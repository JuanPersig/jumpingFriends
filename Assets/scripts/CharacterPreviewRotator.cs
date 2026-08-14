using UnityEngine;

// Gira lentamente el pedestal de preview del menú (efecto "vitrina"),
// para que el personaje elegido no se vea parado y estático. Va en el
// mismo Transform que usa CharacterPreviewSpawner como spawnPoint, así
// gira junto con el modelo que se instancie ahí adentro.
public class CharacterPreviewRotator : MonoBehaviour
{
    [SerializeField] private float degreesPerSecond = 30f;

    private void Update()
    {
        transform.Rotate(Vector3.up, degreesPerSecond * Time.deltaTime, Space.World);
    }
}
