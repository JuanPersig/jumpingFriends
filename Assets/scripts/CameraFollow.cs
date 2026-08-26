using UnityEngine;

// Sigue al jugador en X/Z manteniendo una altura de piso estable (ignora
// el vaivén de saltos/agaches, igual que antes).
//
// Este script va en un GameObject "rig" (vacío, sin cámara). La cámara
// real es HIJA de ese rig. Para single-player (sin ninguna entrada en
// Settings By Player Count), esa cámara hija mantiene SIEMPRE la posición/
// rotación/FOV que le pusiste a mano en el Editor -- este script nunca los
// toca en ese caso, mismo comportamiento de siempre. Para mapas de varios
// carriles, ver Settings By Player Count más abajo.
public class CameraFollow : MonoBehaviour
{
    [SerializeField] private Transform target;
    [SerializeField] private float followSmoothness = 8f;

    [Tooltip("La cámara real, HIJA de este rig -- hace falta la referencia acá (no solo su " +
             "Transform) para poder ajustarle FOV además de posición/rotación. Si la dejás " +
             "vacía, 'Settings By Player Count' simplemente no hace nada (ni error).")]
    [SerializeField] private Camera childCamera;

    // Una configuración completa por cantidad de jugadores -- mismo patrón
    // que ChunkSpawner.ChunkSet / RoundLaneSetup.LaneLayout. Agrupar todo
    // junto (centro, posición/rotación/FOV de cámara) en vez de tablas
    // separadas evita que se desalineen por índice entre sí.
    [System.Serializable]
    public class CameraSettings
    {
        [Tooltip("Para cuántos jugadores es esta configuración (1 a 4).")]
        public int playerCount = 1;
        [Tooltip("Para separar en \"mitades\" con la MISMA cantidad de jugadores -- ej. con 4 " +
                 "jugadores, Pareja 0 = centrada entre carriles 1-2, Pareja 1 = centrada entre " +
                 "carriles 3-4 (una cámara sola abarcando los 4 queda muy lejos/chica). Dejalo en " +
                 "0 para 1/2/3 jugadores -- no necesitan más de una configuración, y así el " +
                 "comportamiento de esos tamaños no cambia nada.")]
        public int pairIndex = 0;
        [Tooltip("X del centro de la arena para este tamaño de mapa (reemplaza seguir el X real " +
                 "del target -- pensado para que ningún carril en particular tironee la cámara).")]
        public float centerX = 0f;
        [Tooltip("Posición LOCAL de la cámara hija para este tamaño (distancia/altura respecto " +
                 "al rig) -- los mismos valores que ves en el Inspector al moverla a mano en Play.")]
        public Vector3 cameraLocalPosition;
        [Tooltip("Rotación LOCAL (Euler, grados) de la cámara hija para este tamaño.")]
        public Vector3 cameraLocalRotation;
        [Tooltip("Field Of View de la cámara para este tamaño (grados). Mapas más anchos " +
                 "probablemente necesiten un FOV mayor para entrar todos los carriles en cuadro.")]
        public float fieldOfView = 60f;
    }

    [Header("Multijugador (mapas de varios carriles)")]
    [Tooltip("Una configuración por cantidad de jugadores. VACÍO = comportamiento de siempre " +
             "(sigue el X real de 'target', cámara hija tal cual quedó puesta a mano -- pensado " +
             "para single-player). Se resuelve UNA sola vez en Start() según " +
             "GameManager.RoundPlayerCount, con el mismo criterio de respaldo que " +
             "ChunkSpawner/RoundLaneSetup: si falta la de la ronda actual, usa la de mayor " +
             "'Player Count' disponible que no la supere (con aviso en consola).")]
    [SerializeField] private CameraSettings[] settingsByPlayerCount;

    // En qué "pareja" está el jugador de este cliente (0 o 1, ver pairIndex
    // de CameraSettings) -- solo importa con 4 jugadores. Se resuelve
    // AUTOMÁTICO: RoundLaneSetup ya sabe qué slot (de los hasta 4 "player"
    // pre-armados en la escena) es el jugador local -- el mismo que
    // 'target' de acá abajo -- y lo empuja acá con SetActivePairIndex()
    // en su propio Awake(), ANTES de que Start() de este script lo necesite
    // (Unity garantiza que todos los Awake() de la escena terminan antes
    // que cualquier Start()). Sin ningún RoundLaneSetup en la escena (ej.
    // Gameplay.unity abierta directa, un solo jugador), se queda en 0 -- el
    // mismo valor que ya usan las configuraciones de 1/2/3 jugadores, así
    // que no les cambia nada.
    private int activePairIndex = 0;

    // Público para RoundLaneSetup, que ahora lo llama TARDE (cuando el estado
    // de ronda llegó por red), no en su Awake() como antes -- por eso acá hay
    // que volver a resolver la configuración, no alcanza con guardar el
    // índice y esperar a Start(): para cuando esto se llama, Start() ya pasó.
    //
    // ResolveSettings() es idempotente (solo lee tablas y escribe posición/
    // rotación/FOV de la cámara hija), así que llamarla dos veces no molesta:
    // la primera, en Start(), deja la cámara con el respaldo local; la
    // segunda, acá, con los datos reales de la sala. Las dos pasan tapadas
    // por la pantalla negra, no se ve ningún salto.
    public void SetActivePairIndex(int pairIndex)
    {
        activePairIndex = pairIndex;
        ResolveSettings();
    }

    // El 'target' del Inspector está wireado a mano al slot 0. En una partida
    // en red eso solo es correcto para uno de los jugadores, así que apenas
    // se sabe cuál es TU carril, la cámara pasa a seguir ese. Sin red no
    // llega nunca este aviso y queda el del Inspector, igual que siempre.
    //
    // Awake/OnDestroy y NO OnEnable/OnDisable a propósito: GameIntroSequence
    // y GameOutroSequence apagan y prenden este componente a mano, y con
    // OnDisable nos desuscribiríamos justo en medio de la partida.
    private void Awake()
    {
        PlayerSlot.LocalSlotResolved += OnLocalSlotResolved;
        if (PlayerSlot.Local != null) OnLocalSlotResolved(PlayerSlot.Local);
    }

    private void OnDestroy()
    {
        PlayerSlot.LocalSlotResolved -= OnLocalSlotResolved;
    }

    private void OnLocalSlotResolved(PlayerSlot slot)
    {
        if (slot == null) return;
        target = slot.transform;
        // initialized guarda la altura del target de antes; recalcularla con
        // el carril nuevo evita un salto vertical si difieren.
        initialized = false;
    }

    // Público para GameOutroSequence: pasar a mirar a un rival vivo después
    // de que te eliminaron. Vuelve a prender el componente porque la
    // cinemática de muerte lo había apagado para mover la cámara a mano.
    //
    // No hace falta tocar 'resolvedCenterX': con dos o más carriles la cámara
    // ya se queda en el centro de la arena y solo sigue el avance en Z, así
    // que mirar a un rival es simplemente seguir SU Z.
    public void SpectateTarget(Transform newTarget)
    {
        if (newTarget == null) return;

        target = newTarget;
        initialized = false;
        enabled = true;
    }

    // Null = sin tabla (o ninguna entrada utilizable) -> seguir target.x como siempre.
    private float? resolvedCenterX;
    private float lockedHeight;
    private bool initialized;

    private void Start()
    {
        ResolveSettings();
    }

    private void ResolveSettings()
    {
        if (settingsByPlayerCount == null || settingsByPlayerCount.Length == 0)
        {
            resolvedCenterX = null;
            return;
        }

        int roundPlayerCount = GameManager.Instance != null ? GameManager.Instance.RoundPlayerCount : 1;

        // Filtra también por pairIndex, no solo por playerCount -- para
        // 1/2/3 jugadores esto no cambia nada (esas entradas quedan con
        // pairIndex=0 igual que activePairIndex por default, así que
        // siempre matchean como antes). Para 4, permite tener dos entradas
        // con el mismo playerCount=4 pero pairIndex distinto, y elegir la
        // que corresponda.
        CameraSettings exactMatch = null;
        CameraSettings bestFallback = null;
        foreach (CameraSettings settings in settingsByPlayerCount)
        {
            if (settings == null || settings.pairIndex != activePairIndex) continue;

            if (settings.playerCount == roundPlayerCount) exactMatch = settings;
            if (settings.playerCount <= roundPlayerCount &&
                (bestFallback == null || settings.playerCount > bestFallback.playerCount))
            {
                bestFallback = settings;
            }
        }

        CameraSettings chosen = exactMatch ?? bestFallback;
        if (chosen != null && exactMatch == null)
        {
            Debug.LogWarning($"[CameraFollow] No hay una configuración armada específicamente " +
                              $"para {roundPlayerCount} jugador(es) -- usando la de " +
                              $"{chosen.playerCount} como reemplazo.");
        }

        resolvedCenterX = chosen?.centerX;

        // Posición/rotación/FOV de la cámara hija se aplican UNA sola vez
        // acá, temprano (antes de que arranque la intro) -- como
        // GameIntroSequence solo mueve el RIG (este Transform), nunca la
        // cámara hija, estos valores quedan fijos y correctos durante toda
        // la intro Y el resto de la partida, sin que haga falta re-aplicarlos.
        if (chosen != null && childCamera != null)
        {
            childCamera.transform.localPosition = chosen.cameraLocalPosition;
            childCamera.transform.localEulerAngles = chosen.cameraLocalRotation;
            childCamera.fieldOfView = chosen.fieldOfView;
        }
    }

    private void LateUpdate()
    {
        if (target == null) return;
        if (!initialized)
        {
            lockedHeight = target.position.y;
            initialized = true;
        }

        float x = resolvedCenterX ?? target.position.x;
        Vector3 desiredPosition = new Vector3(x, lockedHeight, target.position.z);

        // Mismo suavizado independiente del framerate que ya usábamos.
        float t = 1f - Mathf.Exp(-followSmoothness * Time.deltaTime);
        transform.position = Vector3.Lerp(transform.position, desiredPosition, t);

        // Nada de rotación acá — la cámara hija mantiene siempre la
        // rotación que le corresponda (puesta a mano para single-player,
        // o resuelta en Start() para mapas de varios carriles).
    }
}
