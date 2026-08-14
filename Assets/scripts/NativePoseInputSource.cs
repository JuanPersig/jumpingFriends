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
// Pensado para vivir en el mismo GameObject que PlayerInputProvider
// (persistente, DontDestroyOnLoad), así calibra una sola vez por sesión de
// juego, al arrancar.
public class NativePoseInputSource : MonoBehaviour
{
    [Header("Modelo (debe estar copiado en Assets/StreamingAssets/)")]
    [SerializeField] private string modelFileName = "pose_landmarker_lite.bytes";

    [Header("Webcam")]
    [SerializeField] private int requestedWidth = 640;
    [SerializeField] private int requestedHeight = 480;
    [SerializeField] private int requestedFps = 30;

    [Header("Límite de frecuencia de detección")]
    // Muchas webcams ignoran requestedFps y siguen entregando frames a su
    // propio ritmo nativo -- este límite es NUESTRO, en código, e ignora
    // por completo lo que la cámara reporte. Saltar/agacharse es una señal
    // gruesa (no necesita 30 muestras por segundo), así que limitar acá es
    // la forma más directa de controlar cuánta CPU le pedimos a MediaPipe,
    // sin depender del driver de la cámara.
    [SerializeField] private float minSecondsBetweenDetections = 0.1f;

    private float _lastSubmitTimeSeconds = float.NegativeInfinity;

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
    private List<PoseLandmarkSample> _pendingLandmarks;
    private float _pendingTimestampSeconds;
    private bool _hasPendingResult;
    private float _lastResultTimestampSeconds = float.NegativeInfinity;

    // Evita que OnPoseLandmarkerResult (que corre en un hilo de MediaPipe)
    // siga tocando el recurso nativo una vez que empezamos a cerrarlo.
    private volatile bool _isShuttingDown;

    private void Awake()
    {
        _detector.OnJump += () => PlayerInputProvider.Instance?.RaiseJump();
        _detector.OnCrouch += () => PlayerInputProvider.Instance?.RaiseCrouch();
        _detector.OnStand += () => PlayerInputProvider.Instance?.RaiseStand();
    }

    private IEnumerator Start()
    {
        if (WebCamTexture.devices.Length == 0)
        {
            Debug.LogError("[NativePoseInputSource] No se encontró ninguna cámara. La detección nativa no va a funcionar.");
            yield break;
        }

        _webCamTexture = new WebCamTexture(WebCamTexture.devices[0].name, requestedWidth, requestedHeight, requestedFps);
        _webCamTexture.Play();
        yield return new WaitUntil(() => _webCamTexture.width > 16);

        // Algunas webcams ignoran en silencio la resolución/fps pedidos y
        // entregan la suya propia -- confirmamos acá qué terminó usando de
        // verdad, en vez de asumir que respetó lo pedido.
        Debug.Log($"[NativePoseInputSource] Webcam real: {_webCamTexture.width}x{_webCamTexture.height} (pedido: {requestedWidth}x{requestedHeight} @{requestedFps}fps)");

        string modelPath = Path.Combine(Application.streamingAssetsPath, modelFileName);
        if (!File.Exists(modelPath))
        {
            Debug.LogError($"[NativePoseInputSource] No se encontró el modelo en '{modelPath}'. ¿Falta copiarlo a StreamingAssets?");
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
            yield break;
        }
        _poseLandmarker = createdLandmarker;

        // Pool de varios TextureFrame en vez de uno solo reusado: en modo
        // asincrónico puede haber más de un frame "en vuelo" (ya mandado a
        // MediaPipe, todavía sin resultado), así que no podemos reusar el
        // mismo buffer para el siguiente frame hasta que MediaPipe termine
        // con el anterior. Mismo patrón que usa el sample oficial del plugin.
        _textureFramePool = new TextureFramePool(_webCamTexture.width, _webCamTexture.height, TextureFormat.RGBA32, 10);

        _stopwatch.Start();
        _calibrator.Start();
        _isCalibrated = false;
        Debug.Log("[NativePoseInputSource] Calibrando -- quedate parado quieto frente a la cámara...");

        // Dos corrutinas separadas: mandar frames nuevos a MediaPipe ahora
        // implica esperar una lectura asincrónica de GPU (ver SubmitLoop),
        // y no queremos que esa espera también pause el consumo de
        // resultados ya listos.
        StartCoroutine(SubmitLoop());
        StartCoroutine(ConsumeLoop());
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

        var landmarks = new List<PoseLandmarkSample>(33);
        if (result.poseLandmarks != null && result.poseLandmarks.Count > 0)
        {
            foreach (var landmark in result.poseLandmarks[0].landmarks)
            {
                landmarks.Add(new PoseLandmarkSample(landmark.y, landmark.visibility ?? 0f));
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
        List<PoseLandmarkSample> landmarks;
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
