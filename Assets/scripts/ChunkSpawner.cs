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
    [Header("Referencias")]
    [SerializeField] private Transform player;
    [Tooltip("Tus trozos de escenario ya armados como prefabs (piso + árboles/rocas acomodados a mano).")]
    [SerializeField] private GameObject[] chunkPrefabs;

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
    private readonly List<GameObject> activeChunks = new List<GameObject>();
    // El Terrain del último chunk spawneado, para poder conectarlo como
    // "vecino" del próximo (ver SpawnNextChunk). Si tus chunks usan un
    // Plane en vez de Terrain, esto simplemente queda en null y no hace nada.
    private Terrain previousTerrain;

    private void Start()
    {
        nextChunkZ = 0f;
        // Generamos varios chunks antes de arrancar, para no empezar con
        // el camino vacío por delante del jugador.
        for (int i = 0; i < chunksAhead; i++)
        {
            SpawnNextChunk();
        }
    }

    private void Update()
    {
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

    private void SpawnNextChunk()
    {
        if (chunkPrefabs == null || chunkPrefabs.Length == 0) return;

        GameObject prefab = chunkPrefabs[Random.Range(0, chunkPrefabs.Length)];
        Vector3 spawnPos = new Vector3(pathX, 0f, nextChunkZ);
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

        nextChunkZ += chunkLength;
    }

    private void CleanupBehindPlayer()
    {
        for (int i = activeChunks.Count - 1; i >= 0; i--)
        {
            GameObject chunk = activeChunks[i];
            if (chunk == null)
            {
                activeChunks.RemoveAt(i);
                continue;
            }

            // Un chunk se borra recién cuando su PUNTA DE ATRÁS (posición +
            // el largo completo) ya quedó bien lejos del jugador — así no
            // desaparece mientras todavía se ve una parte adelante.
            float chunkBackEdgeZ = chunk.transform.position.z + chunkLength;
            if (chunkBackEdgeZ < player.position.z - despawnDistanceBehind)
            {
                Destroy(chunk);
                activeChunks.RemoveAt(i);
            }
        }
    }
}
