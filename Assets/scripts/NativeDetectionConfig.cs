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
using UnityEngine;

public static class NativeDetectionConfig
{
    // ------------------------------------------------------------------
    // Sensibilidad ajustable por el jugador (pantalla de Configuración del
    // Menú Principal, ver DetectionSettingsPanel) -- a diferencia de TODO
    // lo demás en este archivo (const, fijo), estos dos son
    // "public static" mutables porque los sliders de la UI los tocan en
    // vivo, y se guardan en PlayerPrefs para que la sensibilidad elegida
    // sobreviva entre partidas. LoadPlayerPrefs() los pisa con el valor
    // guardado (si existe) apenas arranca el juego -- si nunca se tocó
    // ningún slider, se quedan en el valor tuneado de fábrica de abajo.
    public static float JumpTriggerOffsetRatio = 0.20f;
    public static float CrouchTriggerOffsetRatio = 0.15f;

    private const string JumpSensitivityPrefKey = "Detection_JumpTriggerOffsetRatio";
    private const string CrouchSensitivityPrefKey = "Detection_CrouchTriggerOffsetRatio";

    // [RuntimeInitializeOnLoadMethod] corre UNA vez por sesión de juego,
    // antes de que cargue la primera escena -- así, sin importar si el
    // jugador entra directo a Gameplay.unity para probar algo o pasa por
    // MainMenu primero, la sensibilidad guardada ya está aplicada antes de
    // que NativeMovementDetector la lea por primera vez.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void LoadPlayerPrefs()
    {
        JumpTriggerOffsetRatio = PlayerPrefs.GetFloat(JumpSensitivityPrefKey, JumpTriggerOffsetRatio);
        CrouchTriggerOffsetRatio = PlayerPrefs.GetFloat(CrouchSensitivityPrefKey, CrouchTriggerOffsetRatio);
    }

    // Público para DetectionSettingsPanel: se llama cada vez que el
    // jugador mueve alguno de los sliders de sensibilidad.
    public static void SaveSensitivityPrefs()
    {
        PlayerPrefs.SetFloat(JumpSensitivityPrefKey, JumpTriggerOffsetRatio);
        PlayerPrefs.SetFloat(CrouchSensitivityPrefKey, CrouchTriggerOffsetRatio);
        PlayerPrefs.Save();
    }

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
    // Rechazo de picos implausibles (ruido puntual de tracking)
    // ------------------------------------------------------------------
    // Ver IsImplausibleSpike en NativeMovementDetector.cs. Cualquier
    // velocidad implícita (en ratios de torso por segundo) entre dos
    // muestras CRUDAS consecutivas por encima de esto se trata como ruido
    // de tracking, no como movimiento real del jugador, y ese frame se
    // descarta antes de tocar el suavizado.
    //
    // Referencia para elegir el número: JumpMinUpwardVelocity (más abajo)
    // es 0.55 y ya pasa por el EMA de velocidad (que amortigua picos). Para
    // que este filtro nunca le coma un salto explosivo real -- incluso uno
    // mucho más brusco que el mínimo detectado -- el piso queda bien por
    // encima de cualquier velocidad humana plausible en esta escala, y
    // solo atrapa saltos de tracking tipo "el landmark apareció en otro
    // lado del cuadro" (que implican velocidades varias veces mayores).
    public const float MaxPlausibleVelocityRatio = 6.0f;

    // Si el rechazo de arriba se sostiene más que esto, se deja de
    // desconfiar y se acepta el dato tal cual (ver comentario grande en
    // IsImplausibleSpike sobre por qué hace falta este seguro).
    public const float ImplausibleJumpMaxRejectSeconds = 0.25f;

    // ------------------------------------------------------------------
    // Suavizado (reduce ruido/jitter del tracking frame a frame)
    // ------------------------------------------------------------------
    // EMA = Exponential Moving Average. Alpha más alto = reacciona más
    // rápido pero suaviza menos ruido. Alpha más bajo = más estable pero
    // más lag.
    // Subidos un poco (antes 0.45/0.55): con muestras llegando cada ~100ms
    // (ver minSecondsBetweenDetections), suavizar de más se siente como
    // demora extra antes de que el salto real "se note" en la señal — esto
    // no cambia el ritmo de detección ni cuesta CPU, es la misma cuenta.
    public const float PositionSmoothingAlpha = 0.6f;
    public const float VelocitySmoothingAlpha = 0.7f;
    public const float TorsoLengthSmoothingAlpha = 0.1f;     // el largo del torso debería cambiar lento
                                                               // (solo si el jugador se acerca/aleja)

    // ------------------------------------------------------------------
    // Detección de salto (JUMPING)
    // ------------------------------------------------------------------
    // JumpTriggerOffsetRatio (cuánto % del torso tiene que subir la cadera
    // para contar como candidato a salto) se movió arriba de todo -- ya no
    // es const, es ajustable por el jugador (ver Sensibilidad).

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
    // CrouchTriggerOffsetRatio (cuánto % del torso tiene que bajar la
    // cadera para contar como candidato a agache) se movió arriba de todo
    // -- ya no es const, es ajustable por el jugador (ver Sensibilidad).

    // A diferencia del salto, el agache no depende de la velocidad sino de
    // que la posición se sostenga un rato.
    //
    // OJO: esto es DURACIÓN REAL (segundos), no cantidad de frames. Antes
    // era "CrouchMinHoldFrames" (cantidad de muestras), pero acá "frame" no
    // es un frame de Unity (~160fps) sino un resultado nuevo de MediaPipe —
    // y esos llegan como mucho cada minSecondsBetweenDetections (~100ms,
    // ver NativePoseInputSource), aposta, para no pedirle de más a la CPU.
    // Contar "frames" de ese reloj mucho más lento hacía que salir de
    // CROUCHING tardara centenares de ms de más (varias muestras a ~100ms
    // cada una) antes de poder saltar de nuevo — de ahí el "se queda
    // estancado" después de agacharse. Medir tiempo real en vez de cantidad
    // de muestras es correcto sin importar a qué ritmo lleguen.
    public const float CrouchMinHoldSeconds = 0.15f;

    // Histéresis de salida: para volver a STANDING, la cadera tiene que
    // subir por encima de este offset y sostenerlo.
    public const float CrouchReleaseOffsetRatio = 0.08f;
    public const float StandMinHoldSeconds = 0.12f;

    // ------------------------------------------------------------------
    // Recuperación ante pérdida de tracking
    // ------------------------------------------------------------------
    // Si el tracking se pierde por más de este tiempo estando en JUMPING o
    // CROUCHING, se fuerza la vuelta a STANDING (estado "seguro por
    // defecto") en vez de quedar trabado para siempre.
    public const float TrackingLostResetSeconds = 1.0f;
}
