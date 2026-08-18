using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Mediapipe;
using Mediapipe.Tasks.Core;
using Mediapipe.Tasks.Vision.Core;
using Mediapipe.Tasks.Vision.PoseLandmarker;
using Mediapipe.Unity.Experimental;
using UnityEngine.Rendering;
using Stopwatch = System.Diagnostics.Stopwatch;

// Lee la webcam, corre el modelo de pose de MediaPipe (Tasks API,
// CPU-only -- GPU no está soportado en Windows, ver
// C:\Users\juanp\Desktop\JumpingFriendsCode\native-detection-migration-plan.md),
// y alimenta a NativeMovementDetector con los landmarks de cadera/hombros.
// Cuando el detector dispara OnJump/OnCrouch/OnStand, los reenvía al
// PlayerInputProvider real vía RaiseJump/RaiseCrouch/RaiseStand -- así
// RunnerController y el resto del juego no se enteran de que la fuente de
// input cambió de UDP (Python) a esto.
//
// Corre en modo LIVE_STREAM (asincrónico): la inferencia de MediaPipe pasa
// a un hilo aparte en vez de bloquear el hilo principal de Unity con cada
// frame de cámara (probado: en modo sincrónico, con todo el juego real
// corriendo encima, el framerate se iba a ~15fps). El resultado de cada
// frame vuelve por un callback que NO corre en el hilo principal -- se
// guarda ahí bajo lock y se procesa en Update(), mismo patrón que ya usa
// PlayerInputProvider.cs para los mensajes UDP que llegan en su propio hilo.
//
// Singleton persistente (DontDestroyOnLoad) -- IMPORTANTE, cambio del
// 17/8: antes esto NO era un singleton de verdad, y al agregar la pantalla
// de Configuración del Menú (que arranca SU PROPIA cámara/detección para
// mostrar el preview) terminamos con DOS instancias vivas al mismo tiempo
// -- una del Menú, otra de Gameplay.unity -- peleando por la MISMA webcam
// al pasar de una escena a otra. Resultado real, confirmado jugando:
// "ya no responde al salto ni al agacharse" en Gameplay, y una
// recalibración redundante justo al entrar a jugar.
//
// La PRIMERA instancia que se crea (normalmente la del Menú Principal, si
// el jugador pasó por ahí) se vuelve persistente y sobrevive el cambio de
// escena -- Gameplay.unity YA NO necesita crear/calibrar una nueva, la
// reusa tal cual llegó (ya calibrada). Si en cambio abrís Gameplay.unity
// DIRECTO desde el Editor (sin pasar por el Menú), no hay ninguna
// instancia previa -- la de Gameplay.unity pasa a ser la única y funciona
// exactamente igual que siempre.
//
// OJO -- a propósito NO hereda de Singleton<T> (a diferencia de
// PlayerInputProvider/GameManager/etc.): ese patrón destruye TODO el
// GameObject del duplicado (Destroy(gameObject)), y en Gameplay.unity el
// GameObject "InputManager" tiene a PlayerInputProvider VIVIENDO EN EL
// MISMO OBJETO. Con Singleton<T>, el duplicado de acá se detectaba a sí
// mismo y se destruía entero -- llevándose puesto a PlayerInputProvider
// en el mismo golpe, dejando a RunnerController sin nadie a quien
// suscribirse (bug real, encontrado el 17/8: "no responde al salto ni al
// agacharse" apenas se pasaba por el Menú). Acá abajo se implementa el
// mismo patrón a mano, pero destruyendo SOLO este componente
// (Destroy(this)), no el GameObject entero -- así los hermanos de
// GameObject (PlayerInputProvider incluido) quedan intactos.
public class NativePoseInputSource : MonoBehaviour
{
    public static NativePoseInputSource Instance { get; private set; }

    [Header("Modelo (debe estar copiado en Assets/StreamingAssets/)")]
    [SerializeField] private string modelFileName = "pose_landmarker_lite.bytes";

    [Header("Webcam")]
    [SerializeField] private int requestedWidth = 640;
    [SerializeField] private int requestedHeight = 480;
    [SerializeField] private int requestedFps = 30;
    [Tooltip("Si la webcam no arranca a tiempo (ej. otro programa ya la tiene abierta, o un " +
             "error de driver tipo 'Could not connect pins - RenderStream()'), cuántos segundos " +
             "esperar antes de rendirse en vez de quedarse colgado para siempre.")]
    [SerializeField] private float webcamStartTimeoutSeconds = 8f;
    [Tooltip("Si la webcam deja de responder A MITAD DE PARTIDA (se desenchufó, otro programa " +
             "la agarró) y el reintento automático de abajo no la recupera en este tiempo, se " +
             "considera un error real (HasCameraError) en vez de un hipo pasajero de un par de frames.")]
    [SerializeField] private float cameraErrorAfterSeconds = 3f;

    [Header("Límite de frecuencia de detección")]
    // Muchas webcams ignoran requestedFps y siguen entregando frames a su
    // propio ritmo nativo -- este límite es NUESTRO, en código, e ignora
    // por completo lo que la cámara reporte. Saltar/agacharse es una señal
    // gruesa (no necesita 30 muestras por segundo), así que limitar acá es
    // la forma más directa de controlar cuánta CPU le pedimos a MediaPipe,
    // sin depender del driver de la cámara.
    [SerializeField] private float minSecondsBetweenDetections = 0.1f;

    [Header("Calibración")]
    [Tooltip("Cuántos segundos esperar DESPUÉS de que la webcam/MediaPipe ya están listos y " +
             "ANTES de arrancar a calibrar -- le da tiempo al jugador de acomodarse frente a " +
             "la cámara (por ejemplo, en la pantalla de Configuración, donde recién está " +
             "entrando y todavía no se paró en posición) en vez de empezar a calibrar de " +
             "inmediato con él fuera de cuadro o a medio moverse. 0 = calibra apenas está " +
             "todo listo, como antes (el comportamiento de Gameplay.unity no cambia si se deja " +
             "en 0 ahí).")]
    [SerializeField] private float calibrationStartDelaySeconds = 0f;

    private bool _calibrationDelayElapsed;

    private float _lastSubmitTimeSeconds = float.NegativeInfinity;
    // Desde cuándo la webcam viene sin responder (null = está bien ahora
    // mismo) -- ver SubmitLoop. Distinto de HasCameraError: esto arranca a
    // contar apenas se nota el problema, HasCameraError recién se prende si
    // se sostiene más de cameraErrorAfterSeconds (para no mostrarle un
    // cartel de error al jugador por un hipo de un solo frame).
    private float? _webcamDownSinceSeconds;

    private WebCamTexture _webCamTexture;
    private TextureFramePool _textureFramePool;
    private PoseLandmarker _poseLandmarker;
    private readonly Stopwatch _stopwatch = new Stopwatch();

    private readonly NativeMovementDetector _detector = new NativeMovementDetector();
    private readonly Calibrator _calibrator = new Calibrator();
    private bool _isCalibrated;

    // El resultado de cada frame llega de un hilo de MediaPipe, no del hilo
    // principal de Unity. Guardamos solo el ÚLTIMO resultado (no hace falta
    // una cola de todos los frames, como sí la necesita PlayerInputProvider
    // para no perderse ningún evento UDP) y lo consumimos en Update().
    private readonly object _resultLock = new object();
    private IReadOnlyList<PoseLandmarkSample> _pendingLandmarks;
    private float _pendingTimestampSeconds;
    private bool _hasPendingResult;
    private float _lastResultTimestampSeconds = float.NegativeInfinity;

    // Ring buffer chico de arrays reusables (33 landmarks, el tamaño real
    // de un resultado completo de BlazePose) para no alocar una List nueva
    // en OnPoseLandmarkerResult -- corre hasta 10 veces por segundo durante
    // toda la partida. Es seguro porque ConsumePendingResult (hilo
    // principal) siempre termina de leer un resultado dentro del mismo
    // frame en que lo recibe (corre cada WaitForEndOfFrame, <16ms), mucho
    // antes de que este mismo slot del ring vuelva a pisarse (recién
    // LandmarkBufferCount resultados después, ~400ms al ritmo máximo de
    // minSecondsBetweenDetections).
    private const int LandmarkBufferCount = 4;
    private readonly PoseLandmarkSample[][] _landmarkBuffers =
    {
        new PoseLandmarkSample[33], new PoseLandmarkSample[33],
        new PoseLandmarkSample[33], new PoseLandmarkSample[33],
    };
    private int _nextLandmarkBufferIndex;

    // Evita que OnPoseLandmarkerResult (que corre en un hilo de MediaPipe)
    // siga tocando el recurso nativo una vez que empezamos a cerrarlo.
    private volatile bool _isShuttingDown;

    // Público para StartupLoadingScreen: true recién cuando el modelo de
    // MediaPipe terminó de cargar/compilar Y el pool de texturas está
    // listo -- o sea, cuando SubmitLoop/ConsumeLoop ya pueden arrancar de
    // verdad. Si la webcam no existe o el modelo falla en cargar (ver los
    // Debug.LogError de más abajo), esto se queda en false para siempre a
    // propósito -- mejor un loading screen que nunca se destapa (visible,
    // diagnosticable en los logs) que arrancar el juego sin detección.
    public bool IsReady { get; private set; }

    // Público para DetectionSettingsPanel/CameraErrorOverlay (Gameplay):
    // true si la webcam nunca pudo abrir, el modelo no cargó, no hay
    // ninguna cámara conectada, o la webcam dejó de responder en medio de
    // una partida por más de cameraErrorAfterSeconds. Se apaga solo si la
    // cámara vuelve a responder -- no hace falta ningún botón de reintentar,
    // el reintento automático de SubmitLoop ya lo venía haciendo.
    public bool HasCameraError { get; private set; }

    // Públicos para DetectionSettingsPanel (pantalla de configuración del
    // Menú Principal) -- solo LECTURA de estado interno que ya existía, no
    // cambian nada de cómo funciona la detección real.
    public MovementState CurrentState => _detector.State;
    public bool IsCalibrated => _isCalibrated;
    // True mientras dura calibrationStartDelaySeconds (todavía no arrancó
    // ni a calibrar) -- para que la UI pueda mostrar "Preparate..." en vez
    // de "Calibrando..." durante ese rato.
    public bool IsWaitingToStartCalibration => !_isCalibrated && !_calibrationDelayElapsed;
    // La textura cruda de la webcam, para mostrarla en un RawImage a modo
    // de preview -- puede ser null hasta que Start() termine de abrirla.
    public WebCamTexture PreviewTexture => _webCamTexture;

    // Público para el botón "Recalibrar" de DetectionSettingsPanel --
    // mismo reset que ya hace Start() al arrancar, para que el jugador
    // pueda repetir la calibración sin tener que reiniciar la escena.
    public void Recalibrate()
    {
        _isCalibrated = false;
        // Acción explícita del jugador (apretó el botón) -- arranca YA, sin
        // pasar por calibrationStartDelaySeconds otra vez.
        _calibrationDelayElapsed = true;
        _calibrator.Start();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            // Solo este componente -- ver comentario grande arriba sobre
            // por qué NO puede ser Destroy(gameObject) acá.
            Destroy(this);
            return;
        }
        Instance = this;

        DontDestroyOnLoad(gameObject);

        _detector.OnJump += () => PlayerInputProvider.Instance?.RaiseJump();
        _detector.OnCrouch += () => PlayerInputProvider.Instance?.RaiseCrouch();
        _detector.OnStand += () => PlayerInputProvider.Instance?.RaiseStand();
    }

    private IEnumerator Start()
    {
        if (WebCamTexture.devices.Length == 0)
        {
            Debug.LogError("[NativePoseInputSource] No se encontró ninguna cámara. La detección nativa no va a funcionar.");
            HasCameraError = true;
            yield break;
        }

        _webCamTexture = new WebCamTexture(WebCamTexture.devices[0].name, requestedWidth, requestedHeight, requestedFps);
        _webCamTexture.Play();

        // OJO -- antes esto era un WaitUntil SIN límite de tiempo: si la
        // webcam nunca arrancaba de verdad (ej. otro programa ya la tenía
        // abierta, error de driver tipo "Could not connect pins -
        // RenderStream()"), esta corrutina se quedaba esperando PARA SIEMPRE
        // y nada de lo que sigue (calibración, IsReady) llegaba a correr --
        // visto en vivo el 17/8, dejaba la pantalla de Configuración
        // colgada en "Preparate..." sin ningún error visible para el
        // jugador. Con tope de tiempo, si no arranca, avisamos y salimos
        // en vez de colgar todo (mismo criterio que StartupLoadingScreen).
        float elapsedWaitingWebcam = 0f;
        while (_webCamTexture.width <= 16 && elapsedWaitingWebcam < webcamStartTimeoutSeconds)
        {
            elapsedWaitingWebcam += Time.deltaTime;
            yield return null;
        }

        if (_webCamTexture.width <= 16)
        {
            Debug.LogError("[NativePoseInputSource] La webcam no respondió a tiempo. Revisá que no la esté " +
                            "usando otro programa (Zoom, Teams, la app Cámara, OBS, etc.) y que esté bien " +
                            "conectada. La detección nativa no va a funcionar.");
            HasCameraError = true;
            yield break;
        }

        // Algunas webcams ignoran en silencio la resolución/fps pedidos y
        // entregan la suya propia -- confirmamos acá qué terminó usando de
        // verdad, en vez de asumir que respetó lo pedido.
        Debug.Log($"[NativePoseInputSource] Webcam real: {_webCamTexture.width}x{_webCamTexture.height} (pedido: {requestedWidth}x{requestedHeight} @{requestedFps}fps)");

        string modelPath = Path.Combine(Application.streamingAssetsPath, modelFileName);
        if (!File.Exists(modelPath))
        {
            Debug.LogError($"[NativePoseInputSource] No se encontró el modelo en '{modelPath}'. ¿Falta copiarlo a StreamingAssets?");
            HasCameraError = true;
            yield break;
        }

        // Leer el modelo y compilar el grafo de inferencia (CreateFromOptions)
        // en un hilo aparte -- perfilado: esto solo, hecho en el hilo
        // principal, congelaba un frame entero ~150-350ms justo al arrancar
        // (medido en ProfilerCaptures/report_17-42-03.txt). Ninguna de las
        // dos llamadas toca una API de Unity, así que sacarlas del hilo
        // principal es seguro.
        PoseLandmarker createdLandmarker = null;
        System.Exception loadError = null;
        var loadTask = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                byte[] modelBytes = File.ReadAllBytes(modelPath);
                var options = new PoseLandmarkerOptions(
                    baseOptions: new BaseOptions(BaseOptions.Delegate.CPU, modelAssetBuffer: modelBytes),
                    runningMode: RunningMode.LIVE_STREAM,
                    numPoses: 1,
                    resultCallback: OnPoseLandmarkerResult);
                createdLandmarker = PoseLandmarker.CreateFromOptions(options);
            }
            catch (System.Exception e)
            {
                loadError = e;
            }
        });
        yield return new WaitUntil(() => loadTask.IsCompleted);

        if (loadError != null)
        {
            Debug.LogError($"[NativePoseInputSource] Falló la carga del modelo de pose: {loadError}");
            HasCameraError = true;
            yield break;
        }
        _poseLandmarker = createdLandmarker;

        // Pool de varios TextureFrame en vez de uno solo reusado: en modo
        // asincrónico puede haber más de un frame "en vuelo" (ya mandado a
        // MediaPipe, todavía sin resultado), así que no podemos reusar el
        // mismo buffer para el siguiente frame hasta que MediaPipe termine
        // con el anterior. Mismo patrón que usa el sample oficial del plugin.
        _textureFramePool = new TextureFramePool(_webCamTexture.width, _webCamTexture.height, TextureFormat.RGBA32, 10);
        IsReady = true;

        _stopwatch.Start();
        _isCalibrated = false;
        _calibrationDelayElapsed = calibrationStartDelaySeconds <= 0f;

        // Dos corrutinas separadas: mandar frames nuevos a MediaPipe ahora
        // implica esperar una lectura asincrónica de GPU (ver SubmitLoop),
        // y no queremos que esa espera también pause el consumo de
        // resultados ya listos. Arrancan YA, antes del delay de abajo, para
        // que la webcam ya se vea en vivo (ej. el preview de la pantalla de
        // Configuración) mientras el jugador todavía se está acomodando --
        // lo único que se retrasa es el arranque de la calibración en sí.
        StartCoroutine(SubmitLoop());
        StartCoroutine(ConsumeLoop());

        if (calibrationStartDelaySeconds > 0f)
        {
            yield return new WaitForSeconds(calibrationStartDelaySeconds);
        }

        _calibrator.Start();
        _calibrationDelayElapsed = true;
        Debug.Log("[NativePoseInputSource] Calibrando -- quedate parado quieto frente a la cámara...");
    }

    private IEnumerator ConsumeLoop()
    {
        var waitForEndOfFrame = new WaitForEndOfFrame();
        while (true)
        {
            yield return waitForEndOfFrame;
            ConsumePendingResult();
        }
    }

    // Manda el frame de cámara actual a MediaPipe para que lo procese en
    // paralelo -- DetectAsync en sí no espera a que termine la inferencia,
    // pero la lectura de píxeles de la webcam (GPU) a CPU sí necesita
    // esperar, así que esto vive en su propia corrutina.
    private IEnumerator SubmitLoop()
    {
        var waitForEndOfFrame = new WaitForEndOfFrame();
        while (true)
        {
            yield return waitForEndOfFrame;

            // La webcam a veces se PARA SOLA en medio de una carga de
            // escena pesada (confirmado con logs el 17/8: al cruzar de
            // MainMenu a Gameplay.unity, _webCamTexture.isPlaying pasaba a
            // false sin ningún error -- el driver de Windows no la
            // "atiende" durante el hitch de carga, y deja de entregar
            // frames para siempre si nadie la vuelve a pedir). Es el MISMO
            // objeto WebCamTexture (esta instancia es persistente,
            // DontDestroyOnLoad) -- no hace falta recrearlo, solo pedirle
            // Play() de nuevo para que retome.
            if (_webCamTexture != null && !_webCamTexture.isPlaying)
            {
                if (_webcamDownSinceSeconds == null)
                {
                    _webcamDownSinceSeconds = (float)_stopwatch.Elapsed.TotalSeconds;
                    Debug.LogWarning("[NativePoseInputSource] La webcam se detuvo sola -- reintentando Play().");
                }
                else if (!HasCameraError && (float)_stopwatch.Elapsed.TotalSeconds - _webcamDownSinceSeconds.Value >= cameraErrorAfterSeconds)
                {
                    // Se sostiene desde hace rato (no fue un hipo de un solo
                    // frame) -- esto es un desenchufado/error real a mitad
                    // de partida, no algo que se vaya a resolver solo en el
                    // próximo frame.
                    Debug.LogError("[NativePoseInputSource] La webcam sigue sin responder después de varios segundos.");
                    HasCameraError = true;
                }
                _webCamTexture.Play();
                continue;
            }

            if (_webcamDownSinceSeconds != null)
            {
                // Se recuperó (sola, o porque el jugador la reconectó) --
                // apagamos el error automáticamente, sin necesidad de
                // ningún botón de reintentar.
                _webcamDownSinceSeconds = null;
                HasCameraError = false;
            }

            // La webcam solo entrega imagen nueva a su propio fps, pero
            // este loop corre una vez por frame renderizado. Sin este
            // chequeo mandaríamos el mismo frame de cámara varias veces de más.
            if (!_webCamTexture.didUpdateThisFrame)
            {
                continue;
            }

            // Límite propio, independiente de lo que la webcam reporte (ver
            // comentario en minSecondsBetweenDetections) -- así controlamos
            // directamente cuánta CPU le pedimos a MediaPipe por segundo.
            float nowSeconds = (float)_stopwatch.Elapsed.TotalSeconds;
            if (nowSeconds - _lastSubmitTimeSeconds < minSecondsBetweenDetections)
            {
                continue;
            }

            if (!_textureFramePool.TryGetTextureFrame(out TextureFrame textureFrame))
            {
                // Pool agotado (demasiados frames todavía en vuelo) -- nos
                // salteamos este frame de cámara en vez de bloquear esperando.
                continue;
            }

            // Lectura ASINCRÓNICA de GPU a CPU (AsyncGPUReadbackRequest) en
            // vez de ReadTextureOnCPU (sincrónica): la sincrónica obliga a
            // la GPU a pararse y esperar cada vez que se llama (un
            // "stall"), sin importar la resolución de la imagen -- por eso
            // bajar resolución casi no cambiaba el framerate: el costo era
            // por la ESPERA, no por el tamaño de los datos.
            //
            // MediaPipe usa el origen de coordenadas arriba-izquierda;
            // Unity usa abajo-izquierda. Sin este flip vertical, el modelo
            // procesa la imagen "patas para arriba" y la detección no
            // funciona bien.
            AsyncGPUReadbackRequest request = textureFrame.ReadTextureAsync(_webCamTexture, flipHorizontally: false, flipVertically: true);
            yield return new WaitUntil(() => request.done);

            if (request.hasError)
            {
                Debug.LogWarning("[NativePoseInputSource] Falló la lectura asincrónica de la webcam, salteando este frame.");
                continue;
            }

            Image image = textureFrame.BuildCPUImage();
            textureFrame.Release();

            long timestampMs = _stopwatch.ElapsedMilliseconds;
            _lastSubmitTimeSeconds = nowSeconds;
            _poseLandmarker.DetectAsync(image, timestampMs);
        }
    }

    // OJO: esto corre en un hilo interno de MediaPipe, NO en el hilo
    // principal de Unity. No se puede tocar ninguna API de Unity acá salvo
    // las explícitamente documentadas como thread-safe -- solo copiamos
    // datos planos (floats) a una variable compartida bajo lock, y el
    // trabajo real (calibración, máquina de estados, disparar eventos) se
    // hace después en Update(), en el hilo principal.
    private void OnPoseLandmarkerResult(PoseLandmarkerResult result, Image image, long timestampMillisec)
    {
        if (_isShuttingDown)
        {
            return;
        }

        float timestampSeconds = timestampMillisec / 1000f;

        IReadOnlyList<PoseLandmarkSample> landmarks = System.Array.Empty<PoseLandmarkSample>();
        if (result.poseLandmarks != null && result.poseLandmarks.Count > 0)
        {
            var rawLandmarks = result.poseLandmarks[0].landmarks;
            if (rawLandmarks.Count == 33)
            {
                // Caso normal (BlazePose siempre da los 33 landmarks
                // completos): reusamos un slot del ring buffer en vez de
                // alocar acá -- ver comentario de _landmarkBuffers arriba.
                PoseLandmarkSample[] buffer = _landmarkBuffers[_nextLandmarkBufferIndex];
                _nextLandmarkBufferIndex = (_nextLandmarkBufferIndex + 1) % LandmarkBufferCount;
                for (int i = 0; i < 33; i++)
                {
                    buffer[i] = new PoseLandmarkSample(rawLandmarks[i].y, rawLandmarks[i].visibility ?? 0f);
                }
                landmarks = buffer;
            }
            else
            {
                // Caso raro/inesperado (cantidad distinta a 33) -- fallback
                // seguro sin tocar el ring buffer.
                var fallback = new PoseLandmarkSample[rawLandmarks.Count];
                for (int i = 0; i < rawLandmarks.Count; i++)
                {
                    fallback[i] = new PoseLandmarkSample(rawLandmarks[i].y, rawLandmarks[i].visibility ?? 0f);
                }
                landmarks = fallback;
            }
        }

        lock (_resultLock)
        {
            // MediaPipe procesa cada stream de entrada en orden, pero este
            // chequeo es una red de seguridad barata: si por lo que sea un
            // resultado llegara fuera de orden, ignorarlo es mucho más
            // seguro que dejar que NativeMovementDetector reciba un
            // timestamp menor al anterior (rompería el cálculo de dt/velocidad).
            if (timestampSeconds <= _lastResultTimestampSeconds)
            {
                return;
            }

            _pendingLandmarks = landmarks;
            _pendingTimestampSeconds = timestampSeconds;
            _lastResultTimestampSeconds = timestampSeconds;
            _hasPendingResult = true;
        }
    }

    private void ConsumePendingResult()
    {
        IReadOnlyList<PoseLandmarkSample> landmarks;
        float timestampSeconds;

        lock (_resultLock)
        {
            if (!_hasPendingResult)
            {
                return;
            }
            landmarks = _pendingLandmarks;
            timestampSeconds = _pendingTimestampSeconds;
            _hasPendingResult = false;
        }

        // Mientras dura calibrationStartDelaySeconds, _calibrator.Start()
        // todavía no se llamó (ver Start() más arriba) -- ni tocarlo:
        // llamar AddSample/IsReady/TimedOut antes de Start() leería un
        // Stopwatch que nunca arrancó, con cuentas sin sentido.
        if (!_calibrationDelayElapsed)
        {
            return;
        }

        if (!_isCalibrated)
        {
            _calibrator.AddSample(landmarks);
            if (_calibrator.IsReady())
            {
                var calibration = _calibrator.FinalizeCalibration();
                if (calibration != null)
                {
                    _detector.Calibrate(calibration.Value.neutralY, calibration.Value.torsoLength);
                    _isCalibrated = true;
                    Debug.Log("[NativePoseInputSource] Calibración lista -- detección activa.");
                }
            }
            else if (_calibrator.TimedOut)
            {
                Debug.LogWarning("[NativePoseInputSource] Timeout de calibración (nadie en cuadro o tracking inestable) -- reintentando.");
                _calibrator.Start();
            }
            return;
        }

        _detector.Update(landmarks, timestampSeconds);
    }

    private void OnDestroy()
    {
        // También corre para un duplicado recién destruido en Awake() (ver
        // Destroy(this) más arriba) -- ahí todos los campos de abajo
        // siguen en null (esa instancia nunca llegó a Start()), así que
        // todo esto es un no-op inofensivo en ese caso.
        if (Instance == this) Instance = null;

        // Marcamos esto ANTES de tocar el recurso nativo: si justo hay una
        // detección en vuelo en el hilo de MediaPipe, su callback va a
        // verse este flag y salir sin hacer nada, en vez de seguir
        // escribiendo estado mientras Close() ya está liberando el recurso.
        _isShuttingDown = true;

        _poseLandmarker?.Close();
        _textureFramePool?.Dispose();
        if (_webCamTexture != null && _webCamTexture.isPlaying)
        {
            _webCamTexture.Stop();
        }
    }
}
