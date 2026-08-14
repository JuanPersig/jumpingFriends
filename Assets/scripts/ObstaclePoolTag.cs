using UnityEngine;

// Marca de qué prefab salió una instancia reutilizable de ObstacleSpawner,
// para poder devolverla al pool correcto. Se agrega una sola vez, la
// primera vez que el obstáculo se instancia de cero (ver
// ObstacleSpawner.GetFromPoolOrInstantiate).
public class ObstaclePoolTag : MonoBehaviour
{
    public GameObject SourcePrefab;
}
