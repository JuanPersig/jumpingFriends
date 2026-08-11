using System.Collections.Generic;
using UnityEngine;

// Genera obstáculos por delante del jugador a intervalos de DISTANCIA fija
// (no de tiempo). Como la velocidad del jugador sube con el tiempo
// (DifficultyManager), el mismo espaciado en distancia se traduce en cada
// vez MENOS tiempo de reacción -> la dificultad crece sola, sin que este
// script necesite saber nada de eso.
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
    [SerializeField] private Transform player;
    [SerializeField] private ObstacleEntry[] obstaclePrefabs;

    [Header("Spawning")]
    [SerializeField] private float spawnDistanceAhead = 40f;
    [SerializeField] private float spacingBetweenObstacles = 12f;
    [SerializeField] private float despawnDistanceBehind = 15f;
    [SerializeField] private float spawnHeight = 0f; // altura base (piso); cada entrada le suma su heightOffset

    private float nextSpawnZ;
    private readonly List<GameObject> activeObstacles = new List<GameObject>();

    // Copia "limpia" de obstaclePrefabs sin slots vacíos (o sin prefab asignado). Ver por qué en Start().
    private List<ObstacleEntry> validObstaclePrefabs;

    private void Start()
    {
        nextSpawnZ = player.position.z + spawnDistanceAhead;

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

        while (nextSpawnZ < player.position.z + spawnDistanceAhead)
        {
            SpawnObstacleAt(nextSpawnZ);
            nextSpawnZ += spacingBetweenObstacles;
        }

        CleanupBehindPlayer();
    }

    private void SpawnObstacleAt(float z)
    {
        if (validObstaclePrefabs == null || validObstaclePrefabs.Count == 0) return;

        ObstacleEntry entry = validObstaclePrefabs[Random.Range(0, validObstaclePrefabs.Count)];
        Vector3 spawnPos = new Vector3(player.position.x, spawnHeight + entry.heightOffset, z);
        // Antes usaba Quaternion.identity (sin rotación) a propósito, pero
        // eso pisaba cualquier rotación que el prefab tuviera guardada —
        // mismo problema que con la posición. Ahora respeta la rotación
        // propia del prefab: rotalo en modo edición de prefab y guardá, y
        // el spawner lo va a instanciar tal cual quedó.
        GameObject obstacle = Instantiate(entry.prefab, spawnPos, entry.prefab.transform.rotation);
        activeObstacles.Add(obstacle);
    }

    private void CleanupBehindPlayer()
    {
        for (int i = activeObstacles.Count - 1; i >= 0; i--)
        {
            GameObject obstacle = activeObstacles[i];
            if (obstacle == null)
            {
                activeObstacles.RemoveAt(i);
                continue;
            }

            if (obstacle.transform.position.z < player.position.z - despawnDistanceBehind)
            {
                Destroy(obstacle);
                activeObstacles.RemoveAt(i);
            }
        }
    }
}
