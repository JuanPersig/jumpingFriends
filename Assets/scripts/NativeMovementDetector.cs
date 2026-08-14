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

        float hipY = (leftHip.Y + rightHip.Y) / 2f;
        float shoulderY = (leftShoulder.Y + rightShoulder.Y) / 2f;
        float torsoLength = Mathf.Abs(hipY - shoulderY);

        if (torsoLength < 1e-4f)
        {
            // El jugador está demasiado lejos/cerca o el tracking está
            // roto: un torso de largo ~0 haría explotar cualquier ratio.
            return null;
        }

        return new HipAndTorso(hipY, torsoLength, avgVisibility);
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

    private float _lastJumpTime = -999f;
    private float _jumpEnteredTime;

    private int _crouchCandidateFrames;
    private int _standCandidateFrames;

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

        (float offsetRatio, float velocityRatio) = UpdateSignal(sample.Value.HipY, sample.Value.TorsoLength, timestampSeconds);
        RunStateMachine(offsetRatio, velocityRatio, timestampSeconds);
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
                HandleCrouching(offsetRatio, now);
                break;
        }
    }

    private void HandleStanding(float offsetRatio, float velocityRatio, float now)
    {
        _standCandidateFrames = 0;

        bool inCooldown = (now - _lastJumpTime) < NativeDetectionConfig.JumpCooldownSeconds;
        bool isJumpCandidate =
            offsetRatio >= NativeDetectionConfig.JumpTriggerOffsetRatio
            && velocityRatio >= NativeDetectionConfig.JumpMinUpwardVelocity;

        if (!inCooldown && isJumpCandidate)
        {
            State = MovementState.Jumping;
            _jumpEnteredTime = now;
            _crouchCandidateFrames = 0;
            OnJump?.Invoke();
            return;
        }

        // Candidato a agache: requiere persistencia (varios frames
        // seguidos) para no confundirse con un salto en preparación
        // (contramovimiento hacia abajo antes de despegar) ni con ruido
        // puntual.
        if (offsetRatio <= -NativeDetectionConfig.CrouchTriggerOffsetRatio)
        {
            _crouchCandidateFrames++;
        }
        else
        {
            _crouchCandidateFrames = 0;
        }

        if (_crouchCandidateFrames >= NativeDetectionConfig.CrouchMinHoldFrames)
        {
            State = MovementState.Crouching;
            _crouchCandidateFrames = 0;
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

    private void HandleCrouching(float offsetRatio, float now)
    {
        if (offsetRatio >= -NativeDetectionConfig.CrouchReleaseOffsetRatio)
        {
            _standCandidateFrames++;
        }
        else
        {
            _standCandidateFrames = 0;
        }

        if (_standCandidateFrames >= NativeDetectionConfig.StandMinHoldFrames)
        {
            State = MovementState.Standing;
            _standCandidateFrames = 0;
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
            _crouchCandidateFrames = 0;
            _standCandidateFrames = 0;
            OnStand?.Invoke();
        }
    }
}
