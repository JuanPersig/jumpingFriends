using UnityEngine;

// Instancia (y reemplaza, si el jugador cambia de personaje con las
// flechas) el modelo elegido en el pedestal de preview del menú. No sabe
// nada de UI ni de botones — solo mira CharacterSelection.Instance y
// muestra lo que corresponda. MenuController llama a Refresh() cuando
// cambia la selección.
public class CharacterPreviewSpawner : MonoBehaviour
{
    [Tooltip("Transform vacío en el pedestal del menú donde tiene que aparecer el personaje.")]
    [SerializeField] private Transform spawnPoint;

    [Tooltip("Escala LOCAL que se le aplica a CUALQUIER personaje que aparezca acá. OJO: " +
             "Instantiate(prefab, position, rotation, parent) NO copia la escala del prefab " +
             "-- cada modelo nuevo arranca con la escala de fábrica de SU PROPIO FBX, que " +
             "puede variar de personaje a personaje (distintos exports, distintas unidades). " +
             "Fijándola acá a mano, todos los personajes se ven del mismo porte en el " +
             "pedestal sin importar el prefab. Probá 1 primero; si el nuevo personaje se ve " +
             "gigante o desaparece, es justo este el motivo -- ajustá el número y volvé a " +
             "probar en Play.")]
    [SerializeField] private Vector3 previewScale = Vector3.one;

    private GameObject currentInstance;

    private void Start()
    {
        Refresh();
    }

    public void Refresh()
    {
        if (currentInstance != null) Destroy(currentInstance);

        CharacterSelection.CharacterOption selected =
            CharacterSelection.Instance != null ? CharacterSelection.Instance.Selected : null;

        if (selected == null || selected.prefab == null || spawnPoint == null) return;

        currentInstance = Instantiate(selected.prefab, spawnPoint);
        currentInstance.transform.localPosition = Vector3.zero;
        currentInstance.transform.localRotation = Quaternion.identity;
        currentInstance.transform.localScale = previewScale;
    }
}
