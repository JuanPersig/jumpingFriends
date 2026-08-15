using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

// Port directo de jumping-friends-poc/movement_detector.py — el corazón de
// la migración a detección nativa (ver
// C:\Users\juanp\Desktop\JumpingFriendsCode\native-detection-migration-plan.md).
// Es matemática pura (EMA, ratios normalizados por torso, máquina de
// estados con histéresis): NO sabe nada de MediaPipe, de la webcam, ni de
// Unity más allá de lo estrictamente necesario. Solo recibe landmarks ya
// calculados (ver PoseLandmarkSample) y dispara eventos, igual que el
// PlayerInputProvider inyectado en la versión Python.
//
// Nombres y estructura calcados del original a propósito, para poder
// comparar ambos archivos lado a lado si algo no cuadra.
//
// 14/8, sesión de mejora de fidelidad: se le sumaron DOS cosas que el POC
// de Python no tenía (ver comentarios en PoseGeometry.ComputeHipAndTorso y
// en IsImplausibleSpike) -- ninguna de las dos toca los umbrales ya
// tuneados de la máquina de estados (JumpTriggerOffsetRatio, etc.), así que
// el "feel" del juego no debería notarse distinto en uso normal; lo que
// cambia es qué tan fiel es la SEÑAL de entrada a esos umbrales.

public enum MovementState
{
    Standing,
    Jumping,
    Crouching,
}

// Cálculo compartido por Calibrator y NativeMovementDetector — equivalente
// a compute_hip_and_torso() en el POC de Python.
internal static class PoseGeometry
{
    public readonly struct HipAndTorso
    {
        public readonly float HipY;
        public readonly float TorsoLength;
        public readonly float Visibility;

        public HipAndTorso(float hipY, float torsoLength, float visibility)
        {
            HipY = hipY;
            TorsoLength = torsoLength;
            Visibility = visibility;
        }
    }

    // Devuelve null si no hay landmarks o si la visibilidad es demasiado
    // baja para confiar en el dato (tracking perdido/parcial).
    public static HipAndTorso? ComputeHipAndTorso(IReadOnlyList<PoseLandmarkSample> landmarks)
    {
        if (landmarks == null || landmarks.Count <= PoseLandmarkIndex.RightHip)
        {
            return null;
        }

        PoseLandmarkSample leftHip = landmarks[PoseLandmarkIndex.LeftHip];
        PoseLandmarkSample rightHip = landmarks[PoseLandmarkIndex.RightHip];
        PoseLandmarkSample leftShoulder = landmarks[PoseLandmarkIndex.LeftShoulder];
        PoseLandmarkSample rightShoulder = landmarks[PoseLandmarkIndex.RightShoulder];

        float avgVisibility = (leftHip.Visibility + rightHip.Visibility + leftShoulder.Visibility + rightShoulder.Visibility) / 4f;
        if (avgVisibility < NativeDetectionConfig.MinLandmarkVisibility)
        {
            return null;
        }

        // Promedio PONDERADO por visibilidad, no 50/50 a secas: si el
        // jugador está de perfil, o un brazo le tapa la cadera un
        // instante, MediaPipe suele seguir devolviendo un Y para ese
        // landmark (no null) pero con visibility baja -- promediarlo
        // igual que al lado bien trackeado arrastra la estimación hacia
        // un dato de mala calidad. Pesar por visibility hace que el lado
        // mejor visto domine, y cuando ambos lados están igual de bien
        // vistos (el caso normal, cámara de frente) da EXACTAMENTE el
        // mismo resultado que el promedio simple de antes.
        float hipY = WeightedAverage(leftHip.Y, leftHip.Visibility, rightHip.Y, rightHip.Visibility);
        float shoulderY = WeightedAverage(leftShoulder.Y, leftShoulder.Visibility, rightShoulder.Y, rightShoulder.Visibility);
        float torsoLength = Mathf.Abs(hipY - shoulderY);

        if (torsoLength < 1e-4f)
        {
            // El jugador está demasiado lejos/cerca o el tracking está
            // roto: un torso de largo ~0 haría explotar cualquier ratio.
            return null;
        }

        return new HipAndTorso(hipY, torsoLength, avgVisibility);
    }

    // Si los dos pesos son ~0 no debería llegar a llamarse (ya se filtró
    // por avgVisibility antes), pero por las dudas cae al promedio simple
    // en vez de dividir por cero.
    private static float WeightedAverage(float valueA, float weightA, float valueB, float weightB)
    {
        float totalWeight = weightA + weightB;
        if (totalWeight < 1e-4f)
        {
            return (valueA + valueB) / 2f;
        }
        return (valueA * weightA + valueB * weightB) / totalWeight;
    }
}

// Recolecta muestras de la posición neutral (parado quieto) del jugador al
// arrancar, y produce una referencia estable. No asume ninguna distancia de
// cámara ni tipo de cuerpo particular: todo lo que produce (neutralY,
// torsoLength) se mide en vivo.
public class Calibrator
{
    private readonly Stopwatch _stopwatch = new Stopwatch();
    private readonly List<float> _hipSamples = new List<float>();
    private readonly List<float> _torsoSamples = new List<float>();

    public void Start()
    {
        _stopwatch.Restart();
        _hipSamples.Clear();
        _torsoSamples.Clear();
    }

    // Reinicia el conteo de tiempo (usado cuando la señal es demasiado
    // ruidosa como para confiar en ella), sin perder las muestras ya
    // buenas que hubiera juntado en frames sueltos.
    public void ResetProgress() => _stopwatch.Restart();

    public float Elapsed => (float)_stopwatch.Elapsed.TotalSeconds;

    public float TimeRemaining => Mathf.Max(0f, NativeDetectionConfig.CalibrationDurationSeconds - Elapsed);

    public bool TimedOut => Elapsed >= NativeDetectionConfig.CalibrationMaxSeconds;

    public int NumSamples => _hipSamples.Count;

    public void AddSample(IReadOnlyList<PoseLandmarkSample> landmarks)
    {
        PoseGeometry.HipAndTorso? sample = PoseGeometry.ComputeHipAndTorso(landmarks);
        if (sample == null)
        {
            return;
        }
        _hipSamples.Add(sample.Value.HipY);
        _torsoSamples.Add(sample.Value.TorsoLength);
    }

    // Lista para finalizar: ya pasó el tiempo mínimo, hay suficientes
    // muestras, y la señal fue razonablemente estable (el jugador estaba
    // parado quieto, no moviéndose).
    public bool IsReady()
    {
        if (Elapsed < NativeDetectionConfig.CalibrationDurationSeconds) return false;
        if (NumSamples < NativeDetectionConfig.CalibrationMinSamples) return false;
        return IsStable();
    }

    private bool IsStable()
    {
        if (NumSamples < 2) return false;
        float stdY = PopulationStdDev(_hipSamples);
        float meanTorso = Mean(_torsoSamples);
        if (meanTorso < 1e-6f) return false;
        return (stdY / meanTorso) <= NativeDetectionConfig.CalibrationMaxStdRatio;
    }

    // Devuelve null si no hay suficientes datos para calibrar (ej: se
    // llegó al timeout sin buena señal). (No se llama "Finalize": ese
    // nombre colisiona con el finalizador de C# y el compilador tira
    // warning CS0465 aunque la firma sea distinta.)
    public (float neutralY, float torsoLength)? FinalizeCalibration()
    {
        if (NumSamples < NativeDetectionConfig.CalibrationMinSamples) return null;
        return (Mean(_hipSamples), Mean(_torsoSamples));
    }

    private static float Mean(List<float> values)
    {
        float sum = 0f;
        foreach (float v in values) sum += v;
        return sum / values.Count;
    }

    private static float PopulationStdDev(List<float> values)
    {
        float mean = Mean(values);
        float sumSquares = 0f;
        foreach (float v in values) sumSquares += (v - mean) * (v - mean);
        return Mathf.Sqrt(sumSquares / values.Count);
    }
}

// Máquina de estados Standing / Jumping / Crouching. Uso:
//   var detector = new NativeMovementDetector();
//   detector.OnJump += () => ...;
//   detector.Calibrate(neutralY, torsoLength);
//   // por cada frame:
//   detector.Update(landmarks, timestampSeconds);
public class NativeMovementDetector
{
    public event Action OnJump;
    public event Action OnCrouch;
    public event Action OnStand;

    public MovementState State { get; private set; } = MovementState.Standing;
    public bool IsCalibrated { get; private set; }
    public bool TrackingOk { get; private set; }

    private float _neutralY;
    private float _torsoLength;

    private float _smoothedY;
    private float _smoothedVelocity;
    private float? _lastTimestamp;

    // Último landmark CRUDO (sin suavizar) aceptado y su timestamp, usados
    // solo por IsImplausibleSpike para detectar picos de ruido antes de que
    // lleguen a tocar el suavizado. Separado de _smoothedY/_lastTimestamp a
    // propósito: si se usara el valor ya suavizado como referencia, el
    // EMA amortiguaría el propio pico y lo haría más difícil de detectar.
    private float? _lastRawY;
    private float? _lastRawTimestamp;

    private float _lastJumpTime = -999f;
    private float _jumpEnteredTime;

    // Momento (timestampSeconds) en que arrancó la posición candidata a
    // agache/parado actual; null = no hay candidato en curso ahora mismo.
    // Ver comentario en NativeDetectionConfig.CrouchMinHoldSeconds sobre
    // por qué esto se mide en tiempo real y no en cantidad de muestras.
    private float? _crouchCandidateSince;
    private float? _standCandidateSince;

    // Última vez que tuvimos una muestra válida (landmarks con confianza
    // suficiente). Se usa para el "seguro" de tracking perdido: ver
    // HandleTrackingLost().
    private float? _lastValidSampleTime;

    public void Calibrate(float neutralY, float torsoLength)
    {
        _neutralY = neutralY;
        _torsoLength = torsoLength;
        _smoothedY = neutralY;
        _smoothedVelocity = 0f;
        _lastTimestamp = null;
        _lastValidSampleTime = null;
        _lastRawY = null;
        _lastRawTimestamp = null;
        State = MovementState.Standing;
        IsCalibrated = true;
    }

    // Procesa un frame nuevo. timestampSeconds debe ser una fuente de
    // tiempo monótona y consistente entre llamadas (ej: Time.realtimeSinceStartup).
    public void Update(IReadOnlyList<PoseLandmarkSample> landmarks, float timestampSeconds)
    {
        if (!IsCalibrated)
        {
            throw new InvalidOperationException("NativeMovementDetector no calibrado. Llamá a Calibrate() primero.");
        }

        PoseGeometry.HipAndTorso? sample = PoseGeometry.ComputeHipAndTorso(landmarks);

        if (sample == null)
        {
            TrackingOk = false;
            // Aunque perdamos tracking, no queremos quedar trabados en
            // Jumping o Crouching para siempre (ej: el jugador se sale de
            // cuadro en el aire, o al agacharse la cadera queda fuera del
            // encuadre de la cámara).
            HandleTrackingLost(timestampSeconds);
            return;
        }

        TrackingOk = true;
        _lastValidSampleTime = timestampSeconds;

        // Landmark visible (pasó el filtro de visibilidad de arriba) pero
        // implausible: MediaPipe a veces "salta" un frame entero a una
        // posición errónea (una mano cruzando delante de la cadera, un
        // cambio de luz) y vuelve solo al frame siguiente. Ver comentario
        // grande en IsImplausibleSpike.
        if (IsImplausibleSpike(sample.Value.HipY, sample.Value.TorsoLength, timestampSeconds))
        {
            return;
        }

        (float offsetRatio, float velocityRatio) = UpdateSignal(sample.Value.HipY, sample.Value.TorsoLength, timestampSeconds);
        RunStateMachine(offsetRatio, velocityRatio, timestampSeconds);
    }

    // ------------------------------------------------------------------
    // Rechazo de picos implausibles (ruido puntual de tracking)
    // ------------------------------------------------------------------
    // Compara el landmark CRUDO de este frame contra el último crudo
    // ACEPTADO (no contra _smoothedY -- el EMA ya amortigua el pico solo,
    // lo que lo haría pasar desapercibido acá). Si el desplazamiento
    // implica una velocidad (en ratios de torso por segundo) por encima de
    // MaxPlausibleVelocityRatio, ningún salto/agache humano real la genera
    // de un frame a otro (~100ms) -- es ruido de tracking, no movimiento.
    // Se descarta ANTES de tocar el suavizado: ni _smoothedY ni
    // _smoothedVelocity se actualizan con un frame así, así que un pico
    // puntual no puede por sí solo cruzar JumpTriggerOffsetRatio ni
    // ensuciar la velocidad usada por HandleStanding.
    //
    // Un frame rechazado NO actualiza _lastRawY/_lastRawTimestamp -- el
    // próximo frame se sigue comparando contra el último dato BUENO
    // conocido, así que el dt, cuando por fin se acepta uno, mide el
    // intervalo real que estuvo "sospechoso" en vez de comparar ruido
    // contra ruido. Seguro aparte (ImplausibleJumpMaxRejectSeconds): si el
    // rechazo se sostiene más de ese tiempo, se deja de desconfiar y se
    // acepta el dato tal cual -- si no, un cambio grande pero REAL (el
    // jugador se recolocó frente a la cámara) quedaría descartado para
    // siempre, comparado contra una referencia cada vez más vieja.
    private bool IsImplausibleSpike(float hipY, float torsoLength, float timestampSeconds)
    {
        if (_lastRawY == null || _lastRawTimestamp == null)
        {
            _lastRawY = hipY;
            _lastRawTimestamp = timestampSeconds;
            return false;
        }

        float dt = timestampSeconds - _lastRawTimestamp.Value;
        if (dt <= 1e-4f)
        {
            return false; // timestamp repetido/desordenado, nada que comparar
        }

        if (dt >= NativeDetectionConfig.ImplausibleJumpMaxRejectSeconds)
        {
            _lastRawY = hipY;
            _lastRawTimestamp = timestampSeconds;
            return false;
        }

        float impliedVelocityRatio = Mathf.Abs(_lastRawY.Value - hipY) / dt / torsoLength;
        if (impliedVelocityRatio > NativeDetectionConfig.MaxPlausibleVelocityRatio)
        {
            return true;
        }

        _lastRawY = hipY;
        _lastRawTimestamp = timestampSeconds;
        return false;
    }

    // ------------------------------------------------------------------
    // Señal: suavizado + velocidad
    // ------------------------------------------------------------------
    private (float offsetRatio, float velocityRatio) UpdateSignal(float hipY, float torsoLength, float timestampSeconds)
    {
        float a = NativeDetectionConfig.PositionSmoothingAlpha;
        float prevSmoothedY = _smoothedY;
        _smoothedY = a * hipY + (1 - a) * _smoothedY;

        float? dt = _lastTimestamp != null ? timestampSeconds - _lastTimestamp.Value : (float?)null;

        if (dt != null && dt.Value > 1e-4f)
        {
            // Y decrece hacia arriba en coordenadas de imagen, así que
            // invertimos el signo para que "velocidad positiva" = subiendo.
            float rawVelocityRatio = (prevSmoothedY - _smoothedY) / dt.Value / _torsoLength;
            float b = NativeDetectionConfig.VelocitySmoothingAlpha;
            _smoothedVelocity = b * rawVelocityRatio + (1 - b) * _smoothedVelocity;
        }

        _lastTimestamp = timestampSeconds;

        // El largo del torso de referencia se actualiza muy lentamente,
        // para tolerar que el jugador se acerque/aleje un poco de la
        // cámara sin tener que recalibrar, pero sin que ruido frame a
        // frame afecte los umbrales.
        float ta = NativeDetectionConfig.TorsoLengthSmoothingAlpha;
        _torsoLength = ta * torsoLength + (1 - ta) * _torsoLength;

        float offsetRatio = (_neutralY - _smoothedY) / _torsoLength;
        return (offsetRatio, _smoothedVelocity);
    }

    // ------------------------------------------------------------------
    // Máquina de estados
    // ------------------------------------------------------------------
    private void RunStateMachine(float offsetRatio, float velocityRatio, float now)
    {
        switch (State)
        {
            case MovementState.Standing:
                HandleStanding(offsetRatio, velocityRatio, now);
                break;
            case MovementState.Jumping:
                HandleJumping(offsetRatio, now);
                break;
            case MovementState.Crouching:
                HandleCrouching(offsetRatio, velocityRatio, now);
                break;
        }
    }

    private void HandleStanding(float offsetRatio, float velocityRatio, float now)
    {
        _standCandidateSince = null;

        bool inCooldown = (now - _lastJumpTime) < NativeDetectionConfig.JumpCooldownSeconds;
        bool isJumpCandidate =
            offsetRatio >= NativeDetectionConfig.JumpTriggerOffsetRatio
            && velocityRatio >= NativeDetectionConfig.JumpMinUpwardVelocity;

        if (!inCooldown && isJumpCandidate)
        {
            State = MovementState.Jumping;
            _jumpEnteredTime = now;
            _crouchCandidateSince = null;
            OnJump?.Invoke();
            return;
        }

        // Candidato a agache: requiere persistencia (sostenerlo un rato)
        // para no confundirse con un salto en preparación (contramovimiento
        // hacia abajo antes de despegar) ni con ruido puntual.
        if (offsetRatio <= -NativeDetectionConfig.CrouchTriggerOffsetRatio)
        {
            _crouchCandidateSince ??= now;
        }
        else
        {
            _crouchCandidateSince = null;
        }

        if (_crouchCandidateSince != null && now - _crouchCandidateSince.Value >= NativeDetectionConfig.CrouchMinHoldSeconds)
        {
            State = MovementState.Crouching;
            _crouchCandidateSince = null;
            OnCrouch?.Invoke();
        }
    }

    private void HandleJumping(float offsetRatio, float now)
    {
        bool landed = offsetRatio <= NativeDetectionConfig.JumpReleaseOffsetRatio;
        bool timedOut = (now - _jumpEnteredTime) > NativeDetectionConfig.JumpMaxDurationSeconds;

        if (landed || timedOut)
        {
            State = MovementState.Standing;
            _lastJumpTime = now;
            OnStand?.Invoke();
        }
    }

    private void HandleCrouching(float offsetRatio, float velocityRatio, float now)
    {
        // Salto directo desde agachado -- BUG real encontrado el 14/8
        // (reportado como "algunos saltos no entran después de agacharse"):
        // antes, la ÚNICA salida de Crouching era pasar primero por
        // Standing (abajo), que pedía sostener el offset cerca de neutral
        // StandMinHoldSeconds (0.12s) seguidos. Si el jugador se agacha y
        // salta en un solo impulso continuo (sin pausar parado en el
        // medio), la cadera cruza esa zona "casi neutral" tan rápido que
        // nunca llega a sostenerla 0.12s -- y mientras tanto, el salto de
        // verdad (que ya venía con velocidad de sobra) no se evaluaba para
        // nada, porque el estado seguía siendo Crouching. Para cuando por
        // fin se declaraba Standing, la velocidad real ya venía bajando y
        // el salto se perdía. Mismos umbrales que un salto desde Standing
        // (JumpTriggerOffsetRatio/JumpMinUpwardVelocity/JumpCooldownSeconds)
        // -- no hace falta relajar nada para que esto dispare correcto.
        bool inCooldown = (now - _lastJumpTime) < NativeDetectionConfig.JumpCooldownSeconds;
        bool isJumpCandidate =
            offsetRatio >= NativeDetectionConfig.JumpTriggerOffsetRatio
            && velocityRatio >= NativeDetectionConfig.JumpMinUpwardVelocity;

        if (!inCooldown && isJumpCandidate)
        {
            State = MovementState.Jumping;
            _jumpEnteredTime = now;
            _standCandidateSince = null;
            OnJump?.Invoke();
            return;
        }

        if (offsetRatio >= -NativeDetectionConfig.CrouchReleaseOffsetRatio)
        {
            _standCandidateSince ??= now;
        }
        else
        {
            _standCandidateSince = null;
        }

        if (_standCandidateSince != null && now - _standCandidateSince.Value >= NativeDetectionConfig.StandMinHoldSeconds)
        {
            State = MovementState.Standing;
            _standCandidateSince = null;
            OnStand?.Invoke();
        }
    }

    // Seguro general: si estamos en Jumping o Crouching y el tracking se
    // pierde por más de TrackingLostResetSeconds, forzamos la vuelta a
    // Standing en vez de quedar trabados ahí para siempre. Standing es el
    // único estado "seguro por defecto": si no sabemos dónde está el
    // jugador, asumimos que no hay input activo.
    private void HandleTrackingLost(float now)
    {
        if (State == MovementState.Standing) return;
        if (_lastValidSampleTime == null) return;

        float lostDuration = now - _lastValidSampleTime.Value;
        if (lostDuration > NativeDetectionConfig.TrackingLostResetSeconds)
        {
            Debug.LogWarning($"[NativeMovementDetector] Tracking perdido {lostDuration:0.0}s estando en {State} -> forzando Standing.");
            State = MovementState.Standing;
            _lastJumpTime = now;
            _crouchCandidateSince = null;
            _standCandidateSince = null;
            OnStand?.Invoke();
        }
    }
}
