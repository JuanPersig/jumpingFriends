using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Recicla "chunks" de escenario: trozos de piso + decoración (árboles,
// rocas, arbustos) que ARMÁS VOS A MANO en el Editor, arrastrando y viendo
// el resultado en vivo — nada de esto se genera al azar por código, así el
// bioma se ve diseñado y no aleatorio.
//
// Este script solo hace una cosa: cuando el jugador se acerca al final del
// último chunk generado, instancia el siguiente (uno al azar de tu lista)
// justo después, y borra los que quedaron muy atrás. Mismo patrón que
// ObstacleSpawner (spawnear adelante, limpiar atrás), pero para escenario
// en vez de obstáculos — a propósito son sistemas separados: los
// obstáculos necesitan ajustarse por dificultad, el escenario por estética,
// y mezclar ambas cosas en un solo script las volvería difíciles de tunear
// por separado.
public class ChunkSpawner : MonoBehaviour
{
    // Un set de chunks completo por cantidad de jugadores -- cada tamaño de
    // mapa (1 a 4 carriles) es un grupo de prefabs totalmente aparte, hechos
    // a mano en el Editor (ver comentario grande de arriba), NO el mismo
    // chunk redimensionado en vivo. Envuelto en una clase en vez de un
    // array-de-arrays porque el Inspector de Unity no serializa arrays
    // anidados directamente (mismo motivo por el que ObstacleEntry existe
    // como clase en ObstacleSpawner).
    [System.Serializable]
    public class ChunkSet
    {
        [Tooltip("Para cuántos jugadores es este set de chunks (1 a 4).")]
        public int playerCount = 1;
        [Tooltip("Los chunks de ESTE tamaño de mapa, armados a mano (piso + decoración).")]
        public GameObject[] chunkPrefabs;
    }

    [Header("Referencias")]
    [SerializeField] private Transform player;
    [Tooltip("Un set de chunks por cada cantidad de jugadores que quieras soportar. Al arrancar, " +
             "se usa el set cuyo 'Player Count' coincida con GameManager.RoundPlayerCount -- si no " +
             "hay ninguno que coincida exacto, se usa el de mayor 'Player Count' que no la supere " +
             "(con aviso en consola), para no dejar el juego sin escenario por un mapa que todavía " +
             "no armaste.")]
    [SerializeField] private ChunkSet[] chunkSetsByPlayerCount;
    // Resuelto una sola vez en Start() a partir de chunkSetsByPlayerCount --
    // de ahí en adelante todo el resto de este script sigue igual que antes
    // (no le importa de dónde salió el array).
    private GameObject[] chunkPrefabs;

    [Header("Config")]
    [Tooltip("Largo real de cada chunk en Z. TODOS los prefabs deben medir exactamente esto " +
             "(el piso de cada chunk empieza en Z local 0 y termina en Z local = chunkLength), " +
             "para que encastren sin huecos ni superposición.")]
    [SerializeField] private float chunkLength = 40f;
    [Tooltip("Posición X fija donde se alinean todos los chunks (normalmente 0, el centro del camino).")]
    [SerializeField] private float pathX = 0f;
    [Tooltip("Cuántos chunks mantener generados por delante del jugador en todo momento.")]
    [SerializeField] private int chunksAhead = 3;
    [SerializeField] private float despawnDistanceBehind = 20f;

    private float nextChunkZ;
    // BUG real encontrado el 15/8 ("chunks generados uno arriba del otro"):
    // sin este guard, Update() corre TODOS los frames -- incluidos los que
    // pasan mientras SpawnInitialChunks() todavía está a mitad de camino
    // (yieldeando entre cada chunk, a propósito, para repartir el costo).
    // nextChunkZ arranca en 0 por default de C# ANTES de que la corrutina
    // llegue a poner "nextChunkZ = 0f" a mano -- así que Update() alcanzaba
    // a spawnear 0/20/40 por su cuenta usando ese 0 "prestado", y un frame
    // después la corrutina reseteaba nextChunkZ = 0f para arrancar SU
    // propio for, pisando el mismo rango de nuevo -- de ahí los chunks
    // duplicados exactos en la misma posición. Con este guard, Update() no
    // toca nada hasta que la corrutina inicial terminó del todo.
    private bool initialChunksReady;
    // Público para StartupLoadingScreen: mismo flag de arriba, de solo
    // lectura desde afuera.
    public bool InitialChunksReady => initialChunksReady;
    private readonly List<GameObject> activeChunks = new List<GameObject>();
    // El Terrain del último chunk spawneado, para poder conectarlo como
    // "vecino" del próximo (ver SpawnNextChunk). Si tus chunks usan un
    // Plane en vez de Terrain, esto simplemente queda en null y no hace nada.
    private Terrain previousTerrain;

    private void Start()
    {
        ResolveChunkPrefabsForRound();
        StartCoroutine(SpawnInitialChunks());
    }

    // Elige, UNA sola vez al arrancar la ronda, qué set de chunks usar según
    // GameManager.RoundPlayerCount (que ya está decidido antes de que esta
    // escena cargue, ver el comentario en GameManager -- no hace falta
    // volver a mirar esto nunca más durante la partida).
    private void ResolveChunkPrefabsForRound()
    {
        int roundPlayerCount = GameManager.Instance != null ? GameManager.Instance.RoundPlayerCount : 1;

        if (chunkSetsByPlayerCount == null || chunkSetsByPlayerCount.Length == 0)
        {
            Debug.LogError("[ChunkSpawner] 'Chunk Sets By Player Count' está vacío. No hay ningún " +
                            "mapa armado, el spawner no va a generar nada.");
            chunkPrefabs = null;
            return;
        }

        ChunkSet exactMatch = null;
        ChunkSet bestFallback = null; // el de mayor playerCount que no supere a roundPlayerCount
        foreach (ChunkSet set in chunkSetsByPlayerCount)
        {
            if (set == null || set.chunkPrefabs == null || set.chunkPrefabs.Length == 0) continue;

            if (set.playerCount == roundPlayerCount) exactMatch = set;
            if (set.playerCount <= roundPlayerCount &&
                (bestFallback == null || set.playerCount > bestFallback.playerCount))
            {
                bestFallback = set;
            }
        }

        ChunkSet chosen = exactMatch ?? bestFallback;
        if (chosen == null)
        {
            Debug.LogError($"[ChunkSpawner] No hay ningún set de chunks utilizable para " +
                            $"{roundPlayerCount} jugador(es). Revisá 'Chunk Sets By Player Count'.");
            chunkPrefabs = null;
            return;
        }

        if (exactMatch == null)
        {
            Debug.LogWarning($"[ChunkSpawner] No hay un set armado específicamente para " +
                              $"{roundPlayerCount} jugador(es) -- usando el de {chosen.playerCount} " +
                              "como reemplazo. Armá el mapa correspondiente cuando puedas.");
        }

        chunkPrefabs = chosen.chunkPrefabs;
    }

    // Cada chunk trae ~300 objetos anidados (árboles/rocas/decoración) --
    // instanciar los 4 chunks iniciales de un tirón en un solo Start()
    // significaba ~1200 Instantiate() en un solo frame, la causa de la
    // demora gigante al arrancar (medida en vivo). Repartir uno por frame
    // convierte eso en pausas chicas o invisibles, sin cambiar el resultado
    // final: el jugador solo necesita el piso bajo sus pies YA (primer
    // chunk, spawneado antes del primer yield); los chunks de más adelante
    // pueden terminar de aparecer en los frames siguientes sin que se note,
    // porque a la velocidad de arranque faltan varios segundos para llegar
    // a esa zona.
    private IEnumerator SpawnInitialChunks()
    {
        // La semilla puede tardar unos frames en llegar del servidor (las
        // NetworkVariable de NetworkRoundState viajan justo después del
        // spawn). Esperarla ACÁ es gratis: todo esto pasa tapado por la
        // pantalla negra de StartupLoadingScreen, no se ve ninguna demora.
        // Sin esta espera, un cliente que arranca antes de recibir la
        // semilla arma TODO el mapa con la equivocada y ve un escenario
        // distinto al de los demás durante toda la ronda.
        yield return WaitForSeed();

        // Un chunk extra ANTES del arranque (Z negativo) -- solo para que
        // la intro de cámara (GameIntroSequence, que muestra al jugador de
        // espaldas) no se vea con el vacío atrás. Se despareja solo apenas
        // el jugador arranca a correr y se aleja lo suficiente
        // (CleanupBehindPlayer lo trata como a cualquier otro chunk viejo,
        // no hace falta lógica especial para sacarlo).
        SpawnChunkAt(-chunkLength);
        yield return null;

        nextChunkZ = 0f;
        // Generamos varios chunks antes de arrancar, para no empezar con
        // el camino vacío por delante del jugador.
        for (int i = 0; i < chunksAhead; i++)
        {
            SpawnNextChunk();
            yield return null;
        }

        // Recién ACÁ termina de estabilizarse nextChunkZ -- antes de esto,
        // Update() se queda quieto (ver el guard de abajo).
        initialChunksReady = true;
    }

    // AZAR SEMBRADO (Fase 3, 25/8) -- mismo motivo y mismo criterio que en
    // ObstacleSpawner: generador PROPIO, no el UnityEngine.Random global, así
    // todos los clientes arman exactamente el mismo escenario. El XOR es para
    // que este flujo de azar no sea idéntico al de los obstáculos aunque
    // ambos salgan de la misma semilla de ronda.
    private System.Random rng;

    // Tope de espera para no colgar el arranque si el estado de red nunca
    // llega (guardarraíl 8 del proyecto: jamás un "esperar hasta que" sin
    // salida). Si se vence, seguimos con lo que haya -- el escenario puede
    // no coincidir, pero el juego arranca.
    private const float SeedWaitTimeoutSeconds = 5f;

    private IEnumerator WaitForSeed()
    {
        NetworkRoundState round = NetworkRoundState.Instance;

        float waited = 0f;
        while (round != null && !round.IsResolved && waited < SeedWaitTimeoutSeconds)
        {
            waited += Time.deltaTime;
            yield return null;
        }

        if (round == null)
        {
            Debug.LogError("[ChunkSpawner] No hay ningún NetworkRoundState en la escena -- " +
                            "usando una semilla al azar. En multijugador cada cliente va a ver " +
                            "un escenario distinto.");
            rng = new System.Random();
            yield break;
        }

        if (!round.IsResolved)
        {
            Debug.LogWarning($"[ChunkSpawner] El estado de ronda no llegó en {SeedWaitTimeoutSeconds}s -- " +
                              "armando el mapa igual. El escenario puede no coincidir con el de los demás.");
        }

        rng = new System.Random(round.ObstacleSeed ^ 0x5F3A7C1);
    }

    private void Update()
    {
        if (!initialChunksReady) return;

        // Mantenemos siempre "chunksAhead" trozos de margen por delante,
        // sin importar qué tan rápido esté corriendo el jugador (la
        // dificultad/velocidad la maneja DifficultyManager, esto solo
        // reacciona a la posición real).
        while (nextChunkZ < player.position.z + chunkLength * chunksAhead)
        {
            SpawnNextChunk();
        }

        CleanupBehindPlayer();
    }

    // Spawnea el próximo chunk de la secuencia normal (por delante del
    // jugador) y avanza nextChunkZ. Para spawnear en un Z puntual fuera de
    // esa secuencia (ver SpawnChunkAt), usar ese método directo.
    private void SpawnNextChunk()
    {
        SpawnChunkAt(nextChunkZ);
        nextChunkZ += chunkLength;
    }

    // Instancia un chunk en el Z indicado y lo encastra como vecino del
    // último chunk spawneado (si trae Terrain). No toca nextChunkZ -- lo
    // usan tanto SpawnNextChunk (secuencia normal) como el chunk extra de
    // arranque en Start() (Z negativo, fuera de esa secuencia).
    private void SpawnChunkAt(float z)
    {
        if (chunkPrefabs == null || chunkPrefabs.Length == 0) return;

        // rng puede seguir en null si SpawnChunkAt se llamara antes de que
        // WaitForSeed haya terminado -- hoy no pasa (el único camino de
        // entrada es SpawnInitialChunks, que espera primero), pero cubrirse
        // es gratis y evita un NullReference silencioso si algo cambia.
        if (rng == null) rng = new System.Random();

        GameObject prefab = chunkPrefabs[rng.Next(0, chunkPrefabs.Length)];
        Vector3 spawnPos = new Vector3(pathX, 0f, z);
        GameObject chunk = Instantiate(prefab, spawnPos, Quaternion.identity);
        activeChunks.Add(chunk);

        // Si el chunk trae un Terrain (piso con relieve/texturas/árboles
        // pintados), lo conectamos como vecino del chunk anterior. Sin esto,
        // Unity trata a cada Terrain como una isla aislada y se nota un
        // corte de sombreado justo en el borde entre uno y otro — con
        // SetNeighbors, Unity calcula bien la iluminación y el LOD a lo
        // largo de la costura, como si fuera un solo terreno continuo.
        Terrain currentTerrain = chunk.GetComponentInChildren<Terrain>();
        if (currentTerrain != null)
        {
            if (previousTerrain != null)
            {
                // El chunk anterior queda "atrás" (Z más chico) del nuevo,
                // así que el nuevo es el vecino de ARRIBA (+Z) del anterior,
                // y el anterior es el vecino de ABAJO (-Z) del nuevo.
                previousTerrain.SetNeighbors(previousTerrain.leftNeighbor, currentTerrain, previousTerrain.rightNeighbor, previousTerrain.bottomNeighbor);
                currentTerrain.SetNeighbors(currentTerrain.leftNeighbor, currentTerrain.topNeighbor, currentTerrain.rightNeighbor, previousTerrain);
            }
            previousTerrain = currentTerrain;
        }
    }

    // Un chunk se borra recién cuando su PUNTA DE ATRÁS (posición + el largo
    // completo) ya quedó bien lejos del jugador — así no desaparece
    // mientras todavía se ve una parte adelante.
    private void CleanupBehindPlayer()
    {
        SpawnerCleanupUtility.CleanupBehind(
            activeChunks,
            chunk => chunk == null,
            chunk => chunk.transform.position.z + chunkLength,
            player.position.z - despawnDistanceBehind,
            chunk => Destroy(chunk));
    }
}
