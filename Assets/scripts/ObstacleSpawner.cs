using System.Collections.Generic;
using UnityEngine;

// Genera obstáculos por delante del jugador a intervalos de DISTANCIA fija
// (no de tiempo). Como la velocidad del jugador sube con el tiempo
// (DifficultyManager), el mismo espaciado en distancia se traduce en cada
// vez MENOS tiempo de reacción -> la dificultad crece sola, sin que este
// script necesite saber nada de eso.
//
// OPTIMIZACIÓN: los obstáculos se REUTILIZAN (object pooling) en vez de
// Instantiate/Destroy cada vez. A medida que la partida dura y la velocidad
// sube, los obstáculos aparecen cada vez más seguido -> Instantiate/Destroy
// constante genera basura (GC) todo el tiempo, y una recolección de basura
// grande puede trabar un frame justo en el peor momento (cuando más
// precisión de salto hace falta). Reutilizar evita ese costo entero.
//
// MULTI-CARRIL (17/8): antes era Singleton<T> con un solo "player" -- ahora
// es un "director" único (sigue habiendo UNA sola instancia en la escena
// por diseño, pero YA NO hereda Singleton<T>: nada más en el proyecto
// llamaba a ObstacleSpawner.Instance, así que no hacía falta ese patrón) que
// tira la MISMA decisión de tipo+espaciado una sola vez y la replica como
// una copia física por cada carril activo (lanePlayers). Es a propósito UN
// solo lugar decidiendo, no un spawner por carril con su propio azar: así
// la secuencia queda sincronizada gratis (sin mantener varios RNG iguales)
// y es el punto natural donde más adelante Netcode va a enganchar (el host
// decide acá, los clientes reciben el resultado).
//
// Todos los carriles avanzan en Z al mismo ritmo (todos leen
// DifficultyManager.Instance.CurrentSpeed, que es compartido) -- por eso
// alcanza con UNA sola referencia de progreso en Z (lanePlayers[0]), aunque
// cada carril tenga su propio X.
public class ObstacleSpawner : MonoBehaviour
{
    // Cada obstáculo tiene su propio prefab Y su propia altura de aparición.
    // OJO: Instantiate(prefab, spawnPos, ...) siempre PISA la posición que
    // el prefab tenga guardada — no sirve de nada mover el objeto adentro
    // del prefab y guardar. Por eso la altura se controla acá, por entrada,
    // no en el prefab.
    [System.Serializable]
    public class ObstacleEntry
    {
        public GameObject prefab;
        [Tooltip("Altura (Y) relativa a 'Spawn Height', propia de ESTE obstáculo. " +
                 "Ej: un tronco bajo (se salta) en 0; una barrera alta (te agachás para pasar) en ~1.2.")]
        public float heightOffset = 0f;
    }

    [Header("Referencias")]
    [Tooltip("Un Transform por carril activo (el 'player' de cada RunnerController), en el " +
             "orden que quieras. Para 1 jugador (single-player de hoy), dejá un solo elemento -- " +
             "el comportamiento es idéntico al de antes. El elemento [0] se usa además como " +
             "referencia de progreso en Z (todos avanzan igual, ver comentario de arriba).")]
    [SerializeField] private Transform[] lanePlayers;
    [SerializeField] private ObstacleEntry[] obstaclePrefabs;

    [Header("Spawning")]
    [SerializeField] private float spawnDistanceAhead = 40f;
    [SerializeField] private float despawnDistanceBehind = 15f;
    [SerializeField] private float spawnHeight = 0f; // altura base (piso); cada entrada le suma su heightOffset

    [Header("Espaciado entre obstáculos (curva dinámica)")]
    // Antes esto era un solo valor fijo (12) de punta a punta -- la
    // dificultad igual subía sola porque la velocidad crece mientras el
    // espaciado en distancia se mantenía igual (menos tiempo real entre
    // obstáculos). Ahora, mismo criterio que minReactionSeconds/
    // switchTypeChance: el espaciado en sí también se va achicando con
    // Progress01, así la curva pega más fuerte todavía sobre el final.
    //
    // OJO -- interactúa con el piso de reacción de abajo: el espaciado
    // efectivo NUNCA baja de minReactionSeconds * velocidad actual (ver
    // Update). Si "Late" de acá queda muy por debajo de ese piso a
    // velocidad máxima, el piso es quien termina mandando igual -- para
    // que este ajuste se note de punta a punta, puede hacer falta bajar
    // también Min Reaction Seconds Late.
    [Tooltip("Distancia entre obstáculos AL ARRANCAR la partida — la más generosa.")]
    [SerializeField] private float spacingBetweenObstaclesEarly = 12f;
    [Tooltip("Distancia entre obstáculos a velocidad MÁXIMA — la más exigente.")]
    [SerializeField] private float spacingBetweenObstaclesLate = 8f;

    [Header("Piso de reacción (curva dinámica)")]
    // El espaciado de arriba, aunque ahora también se achica solo con
    // Progress01, sigue siendo una distancia -- y la velocidad
    // (DifficultyManager) sube todo el tiempo -> el tiempo REAL entre
    // obstáculos se achica más rápido todavía de lo que el espaciado por sí
    // solo sugiere (a propósito, es la curva de dificultad). El problema:
    // RunnerController.HandleJump() ignora
    // cualquier input mientras ya está saltando (jumpDuration), así que a
    // velocidad alta el juego puede terminar pidiendo una reacción en menos
    // tiempo del que dura, sin querer, el propio salto -> el personaje se
    // siente "trabado"/sin responder.
    //
    // En vez de un piso FIJO (que resultó demasiado generoso de punta a
    // punta y volvió el juego fácil todo el tiempo), esto ahora es un
    // RANGO: arranca generoso (Early) y se va endureciendo hacia Late a
    // medida que DifficultyManager.Progress01 avanza (0 al arrancar, 1 a
    // velocidad máxima) — mismo progreso que ya usa la velocidad, así que
    // no hace falta timer propio.
    [Tooltip("Piso de reacción (segundos) AL ARRANCAR la partida — el más generoso.")]
    [SerializeField] private float minReactionSecondsEarly = 1.6f;
    [Tooltip("Piso de reacción (segundos) a velocidad MÁXIMA — el más exigente. Si tu " +
             "salto (jumpDuration en RunnerController) o tu agache tardan más que esto, subilo.")]
    [SerializeField] private float minReactionSecondsLate = 0.9f;

    [Header("Variedad de obstáculos (curva dinámica)")]
    // Antes la elección era 100% al azar (Random.Range sin memoria de qué
    // salió antes) — con solo 2 tipos (Log/Barrera), eso da rachas largas
    // del mismo tipo bastante seguido por pura casualidad, y repetir el
    // mismo tipo es más fácil (es la misma acción una y otra vez) que
    // alternar salto/agache, que es lo que realmente le da dificultad. Al
    // igual que el piso de reacción, esto es un rango que endurece con el
    // mismo Progress01: al arrancar se permiten más repeticiones (fácil),
    // a velocidad máxima casi siempre alterna (difícil).
    [Tooltip("Probabilidad de cambiar de tipo AL ARRANCAR la partida — la más baja " +
             "(más repeticiones permitidas, más fácil).")]
    [Range(0f, 1f)]
    [SerializeField] private float switchTypeChanceEarly = 0.4f;
    [Tooltip("Probabilidad de cambiar de tipo a velocidad MÁXIMA — la más alta (casi " +
             "siempre alterna, más difícil). Sin importar este valor, NUNCA salen más " +
             "de 2 obstáculos iguales seguidos (tope duro, no configurable).")]
    [Range(0f, 1f)]
    [SerializeField] private float switchTypeChanceLate = 0.85f;

    private const int MaxConsecutiveSameType = 2;
    private ObstacleEntry lastPickedEntry;
    private int consecutiveSameCount;
    // Calculado una vez por Update() (ver ahí) y reusado en PickNextEntry,
    // para no leer DifficultyManager.Instance.Progress01 dos veces por frame.
    private float currentProgress01;

    private float nextSpawnZ;
    private readonly List<GameObject> activeObstacles = new List<GameObject>();
    // Un pool de instancias desactivadas por cada prefab de origen (Log,
    // Barrera, etc.), listas para reposicionar y reactivar en vez de
    // instanciar de cero.
    private readonly Dictionary<GameObject, Queue<GameObject>> pools = new Dictionary<GameObject, Queue<GameObject>>();

    // Copia "limpia" de obstaclePrefabs sin slots vacíos (o sin prefab asignado). Ver por qué en Start().
    private List<ObstacleEntry> validObstaclePrefabs;

    private void Start()
    {
        if (lanePlayers == null || lanePlayers.Length == 0 || lanePlayers[0] == null)
        {
            Debug.LogError("[ObstacleSpawner] 'Lane Players' está vacío o el elemento [0] no tiene " +
                            "un Transform asignado. El spawner no puede saber de dónde leer el " +
                            "progreso en Z y no va a generar nada.");
            enabled = false;
            return;
        }

        nextSpawnZ = lanePlayers[0].position.z + spawnDistanceAhead;

        // Si en el Inspector agrandaste el array pero dejaste algún slot
        // sin arrastrarle un prefab, ese slot queda como referencia "sin
        // asignar" (no es lo mismo que null en C#: Unity lo detecta aparte).
        // Antes filtrábamos solo "array vacío", pero un slot vacío en un
        // array NO vacío pasaba esa guarda igual, y explotaba recién al
        // hacer Instantiate() con ese slot -> UnassignedReferenceException
        // en cada Update(). Lo filtramos acá, una sola vez, y avisamos una
        // sola vez en vez de miles de veces en la consola.
        validObstaclePrefabs = new List<ObstacleEntry>();
        if (obstaclePrefabs != null)
        {
            foreach (ObstacleEntry entry in obstaclePrefabs)
            {
                if (entry != null && entry.prefab != null) validObstaclePrefabs.Add(entry);
            }
        }

        int missingSlots = (obstaclePrefabs?.Length ?? 0) - validObstaclePrefabs.Count;
        if (missingSlots > 0)
        {
            Debug.LogWarning(
                $"[ObstacleSpawner] {missingSlots} slot(s) de 'Obstacle Prefabs' están vacíos en el Inspector. " +
                "Arrastrá un prefab a cada slot (o reducí el tamaño del array) para no desperdiciar tiradas aleatorias en ellos.");
        }
        if (validObstaclePrefabs.Count == 0)
        {
            Debug.LogError("[ObstacleSpawner] No hay ningún prefab válido en 'Obstacle Prefabs'. El spawner no va a generar nada.");
        }
    }

    private void Update()
    {
        if (GameManager.Instance != null && GameManager.Instance.IsGameOver) return;
        // Pausado hasta que termine la intro de cámara (ver GameIntroSequence).
        if (GameManager.Instance != null && !GameManager.Instance.HasGameplayStarted) return;

        // Progreso 0->1 de la partida (mismo que usa DifficultyManager para
        // subir la velocidad): interpola tanto el espaciado base como el
        // piso de reacción entre Early (generoso) y Late (exigente). A
        // velocidad baja igual puede mandar el espaciado base (aunque ya
        // esté endureciendo) porque el piso, recién arrancando, todavía da
        // menos que esa distancia.
        float currentSpeed = DifficultyManager.Instance != null ? DifficultyManager.Instance.CurrentSpeed : 0f;
        currentProgress01 = DifficultyManager.Instance != null ? DifficultyManager.Instance.Progress01 : 0f;
        float spacingBetweenObstacles = Mathf.Lerp(spacingBetweenObstaclesEarly, spacingBetweenObstaclesLate, currentProgress01);
        float minReactionSeconds = Mathf.Lerp(minReactionSecondsEarly, minReactionSecondsLate, currentProgress01);
        float effectiveSpacing = Mathf.Max(spacingBetweenObstacles, minReactionSeconds * currentSpeed);

        while (nextSpawnZ < lanePlayers[0].position.z + spawnDistanceAhead)
        {
            SpawnObstacleAt(nextSpawnZ);
            nextSpawnZ += effectiveSpacing;
        }

        CleanupBehindPlayer();
    }

    // UNA sola decisión de tipo (PickNextEntry) para este Z, replicada como
    // una instancia física independiente en cada carril -- así todos ven
    // exactamente el mismo obstáculo en el mismo punto, y cada uno choca (o
    // no) el suyo propio sin afectar a los demás carriles.
    private void SpawnObstacleAt(float z)
    {
        if (validObstaclePrefabs == null || validObstaclePrefabs.Count == 0) return;

        ObstacleEntry entry = PickNextEntry();
        foreach (Transform lane in lanePlayers)
        {
            if (lane == null) continue; // carril sin usar todavía (ver Lane Players en el Inspector)
            Vector3 spawnPos = new Vector3(lane.position.x, spawnHeight + entry.heightOffset, z);
            GameObject obstacle = GetFromPoolOrInstantiate(entry.prefab, spawnPos);
            activeObstacles.Add(obstacle);
        }
    }

    // Elige el próximo tipo de obstáculo con memoria de qué salió antes:
    // - Si ya salieron MaxConsecutiveSameType (2) iguales seguidas, fuerza
    //   un tipo distinto sí o sí (tope duro).
    // - Si no, tira los dados con switchTypeChance de probabilidad de
    //   cambiar de tipo (en vez de 100% al azar) -> más intercalado en
    //   general, sin volverse un patrón predecible tipo ABAB fijo.
    private ObstacleEntry PickNextEntry()
    {
        if (validObstaclePrefabs.Count <= 1)
        {
            ObstacleEntry only = validObstaclePrefabs[0];
            lastPickedEntry = only;
            consecutiveSameCount++;
            return only;
        }

        float switchTypeChance = Mathf.Lerp(switchTypeChanceEarly, switchTypeChanceLate, currentProgress01);
        bool mustSwitch = lastPickedEntry != null && consecutiveSameCount >= MaxConsecutiveSameType;
        bool wantsSwitch = lastPickedEntry == null || mustSwitch || Random.value < switchTypeChance;

        ObstacleEntry picked;
        if (wantsSwitch)
        {
            // Sorteo por rechazo: para 2-3 tipos converge casi siempre en
            // la primera vuelta, y evita alocar una lista nueva cada vez.
            do
            {
                picked = validObstaclePrefabs[Random.Range(0, validObstaclePrefabs.Count)];
            } while (picked == lastPickedEntry);
        }
        else
        {
            picked = lastPickedEntry;
        }

        if (picked == lastPickedEntry) consecutiveSameCount++;
        else { consecutiveSameCount = 1; lastPickedEntry = picked; }

        return picked;
    }

    // Saca una instancia reutilizable del pool de ESE prefab si hay alguna
    // esperando; si no, instancia una nueva (y la marca con ObstaclePoolTag
    // para saber a qué pool devolverla más adelante).
    private GameObject GetFromPoolOrInstantiate(GameObject prefab, Vector3 spawnPos)
    {
        if (pools.TryGetValue(prefab, out Queue<GameObject> pool) && pool.Count > 0)
        {
            GameObject reused = pool.Dequeue();
            // Antes usaba Quaternion.identity (sin rotación) a propósito, pero
            // eso pisaba cualquier rotación que el prefab tuviera guardada —
            // por eso acá (igual que al instanciar de cero) respetamos la
            // rotación propia del prefab.
            reused.transform.SetPositionAndRotation(spawnPos, prefab.transform.rotation);
            reused.SetActive(true);
            return reused;
        }

        GameObject obstacle = Instantiate(prefab, spawnPos, prefab.transform.rotation);
        ObstaclePoolTag tag = obstacle.AddComponent<ObstaclePoolTag>();
        tag.SourcePrefab = prefab;
        return obstacle;
    }

    // Hoy lo llama solo CleanupBehindPlayer (el obstáculo quedó atrás del
    // jugador). Antes RunnerController.OnTriggerEnter también lo llamaba al
    // chocarlo, para hacerlo desaparecer — eso se sacó a propósito: los
    // obstáculos ahora se quedan en su lugar y el jugador los atraviesa (el
    // choque se comunica por el HUD de vidas y la animación de Hit_Head, no
    // haciéndolos desaparecer). Sigue siendo público por si otra cosa
    // necesita devolver uno al pool.
    public void ReturnObstacle(GameObject obstacle)
    {
        if (obstacle == null || !obstacle.activeSelf) return; // ya fue devuelto antes, no lo encolamos dos veces

        ObstaclePoolTag tag = obstacle.GetComponent<ObstaclePoolTag>();
        obstacle.SetActive(false);

        if (tag == null || tag.SourcePrefab == null)
        {
            // No debería pasar (todo lo que sale de acá tiene el tag), pero
            // por las dudas: mejor destruirlo que dejarlo desactivado
            // flotando para siempre sin pool al que volver.
            Destroy(obstacle);
            return;
        }

        if (!pools.TryGetValue(tag.SourcePrefab, out Queue<GameObject> pool))
        {
            pool = new Queue<GameObject>();
            pools[tag.SourcePrefab] = pool;
        }
        pool.Enqueue(obstacle);
    }

    // null (destruido) o inactivo (ya devuelto al pool por otro camino) ->
    // solo se saca de la lista, SIN llamar a ReturnObstacle de nuevo (si no,
    // quedaría encolado dos veces en el pool, y dos obstáculos "distintos"
    // futuros terminarían siendo en realidad el mismo GameObject).
    //
    // Hoy ningún otro camino devuelve obstáculos (ver ReturnObstacle), así
    // que esta guarda no debería dispararse nunca — se deja igual porque el
    // costo es un chequeo por frame y el error que evita (dos obstáculos que
    // son el mismo objeto) es silencioso y muy molesto de diagnosticar.
    private void CleanupBehindPlayer()
    {
        SpawnerCleanupUtility.CleanupBehind(
            activeObstacles,
            obstacle => obstacle == null || !obstacle.activeSelf,
            obstacle => obstacle.transform.position.z,
            lanePlayers[0].position.z - despawnDistanceBehind,
            ReturnObstacle);
    }
}
