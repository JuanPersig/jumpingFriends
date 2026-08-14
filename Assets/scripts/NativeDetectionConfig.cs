// Todos los parámetros ajustables de la detección de movimiento nativa, en
// un solo lugar — igual que config.py en el POC de Python
// (jumping-friends-poc/config.py), del cual estos valores son el punto de
// partida (ya tuneados y probados ahí). Si la detección da falsos positivos
// o se siente poco sensible, este es el único archivo que debería hacer
// falta tocar.
//
// Unidades: las posiciones verticales de MediaPipe vienen normalizadas
// [0.0, 1.0] respecto al alto de la imagen (0 = arriba, 1 = abajo). Casi
// todo acá se expresa como "ratio del torso" (distancia hombro-cadera), no
// en píxeles ni unidades absolutas — así los umbrales no dependen de qué
// tan lejos esté el jugador de la cámara.
public static class NativeDetectionConfig
{
    // ------------------------------------------------------------------
    // Confianza de landmarks
    // ------------------------------------------------------------------
    // Visibilidad mínima promedio (cadera + hombros) para confiar en el
    // dato de un frame. Por debajo de esto, se considera "tracking perdido".
    public const float MinLandmarkVisibility = 0.5f;

    // ------------------------------------------------------------------
    // Calibración
    // ------------------------------------------------------------------
    public const float CalibrationDurationSeconds = 3.0f;   // tiempo mínimo parado quieto para calibrar
    public const int CalibrationMinSamples = 20;             // mínimo de muestras válidas para aceptar la calibración
    public const float CalibrationMaxSeconds = 15.0f;        // tope de seguridad si no se junta buena señal
    public const float CalibrationMaxStdRatio = 0.06f;       // si la cadera "tiembla" más que esto (ratio de
                                                               // torso) durante la calibración, se descarta y
                                                               // se reinicia el conteo

    // ------------------------------------------------------------------
    // Suavizado (reduce ruido/jitter del tracking frame a frame)
    // ------------------------------------------------------------------
    // EMA = Exponential Moving Average. Alpha más alto = reacciona más
    // rápido pero suaviza menos ruido. Alpha más bajo = más estable pero
    // más lag.
    public const float PositionSmoothingAlpha = 0.45f;
    public const float VelocitySmoothingAlpha = 0.55f;
    public const float TorsoLengthSmoothingAlpha = 0.1f;     // el largo del torso debería cambiar lento
                                                               // (solo si el jugador se acerca/aleja)

    // ------------------------------------------------------------------
    // Detección de salto (JUMPING)
    // ------------------------------------------------------------------
    // La cadera debe subir al menos este % del largo del torso respecto a
    // la posición neutral para considerarse un candidato a salto.
    public const float JumpTriggerOffsetRatio = 0.20f;

    // Además del desplazamiento, exige una velocidad ascendente mínima (en
    // ratios de torso por segundo), para que una elevación lenta y
    // sostenida no se confunda con un salto.
    public const float JumpMinUpwardVelocity = 0.55f;

    // Para considerar que el jugador "aterrizó" y volver a STANDING, la
    // cadera tiene que volver a estar por debajo de este offset (más chico
    // que el trigger → histéresis, evita parpadeo cerca del umbral).
    public const float JumpReleaseOffsetRatio = 0.08f;

    // Tiempo mínimo entre dos eventos de salto distintos.
    public const float JumpCooldownSeconds = 0.35f;

    // Si el estado JUMPING nunca vuelve a STANDING solo (ej: el jugador se
    // sale de cuadro en el aire), forzamos el regreso después de este tiempo.
    public const float JumpMaxDurationSeconds = 1.2f;

    // ------------------------------------------------------------------
    // Detección de agache (CROUCHING)
    // ------------------------------------------------------------------
    // La cadera debe bajar al menos este % del largo del torso respecto a
    // la posición neutral para considerarse un candidato a agache.
    public const float CrouchTriggerOffsetRatio = 0.15f;

    // A diferencia del salto, el agache no depende de la velocidad sino de
    // que la posición se sostenga varios frames seguidos.
    public const int CrouchMinHoldFrames = 5;

    // Histéresis de salida: para volver a STANDING, la cadera tiene que
    // subir por encima de este offset y sostenerlo.
    public const float CrouchReleaseOffsetRatio = 0.08f;
    public const int StandMinHoldFrames = 4;

    // ------------------------------------------------------------------
    // Recuperación ante pérdida de tracking
    // ------------------------------------------------------------------
    // Si el tracking se pierde por más de este tiempo estando en JUMPING o
    // CROUCHING, se fuerza la vuelta a STANDING (estado "seguro por
    // defecto") en vez de quedar trabado para siempre.
    public const float TrackingLostResetSeconds = 1.0f;
}
