using System;
using System.Collections.Generic;

// Compartido por ChunkSpawner y ObstacleSpawner: los dos siguen el mismo
// patrón "spawnear adelante del jugador, limpiar lo que quedó atrás" (ver
// comentarios en esos archivos sobre por qué son sistemas separados pese a
// compartir este loop) -- acá solo se extrae el recorrido en sí, que era
// idéntico letra por letra en ambos. Genérico en T para no acoplarse a
// GameObject: cada spawner decide qué significa "ya inválido" y qué hacer
// al despawnear (Destroy en ChunkSpawner, devolver al pool en ObstacleSpawner).
internal static class SpawnerCleanupUtility
{
    // Recorre `active` en reversa (seguro para RemoveAt mientras se itera):
    // - si `isAlreadyGone(item)` da true, solo lo saca de la lista.
    // - si no, y `getBackEdgeZ(item)` quedó antes de `despawnBelowZ`, llama
    //   `onDespawn(item)` y también lo saca de la lista.
    public static void CleanupBehind<T>(
        List<T> active,
        Func<T, bool> isAlreadyGone,
        Func<T, float> getBackEdgeZ,
        float despawnBelowZ,
        Action<T> onDespawn)
    {
        for (int i = active.Count - 1; i >= 0; i--)
        {
            T item = active[i];
            if (isAlreadyGone(item))
            {
                active.RemoveAt(i);
                continue;
            }

            if (getBackEdgeZ(item) < despawnBelowZ)
            {
                onDespawn(item);
                active.RemoveAt(i);
            }
        }
    }
}
