using UnityEngine;

// Cámara detrás del personaje, estilo Temple Run. Sigue con un pequeño
// suavizado para que no se sienta "pegada" al jugador.
public class CameraFollow : MonoBehaviour
{
    [SerializeField] private Transform target;
    [SerializeField] private Vector3 offset = new Vector3(0f, 3.5f, -6f);
    [SerializeField] private float followSmoothness = 8f;

    // Altura de referencia del jugador "parado" (sin el offset del salto).
    // RunnerController mueve target.position.y directamente para el arco
    // del salto (groundLocalY + heightOffset). Si la cámara siguiera esa Y
    // tal cual, subiría y bajaría CON el jugador en cada salto, y el salto
    // se ve raro/sin fuerza porque ambos se mueven juntos en vez de que el
    // jugador suba relativo a una cámara estable. Por eso guardamos la
    // altura de piso una sola vez al arrancar y la cámara la ignora.
    private float lockedHeight;
    private bool initialized;

    private void Start()
    {
        if (target != null)
        {
            lockedHeight = target.position.y + offset.y;
            initialized = true;
        }
    }

    private void LateUpdate()
    {
        if (target == null) return;
        if (!initialized)
        {
            lockedHeight = target.position.y + offset.y;
            initialized = true;
        }

        Vector3 desiredPosition = new Vector3(
            target.position.x + offset.x,
            lockedHeight,
            target.position.z + offset.z);

        // 1 - e^(-k*dt) en vez de "k*dt" a secas: el Lerp con un factor
        // crudo no es independiente del framerate (a FPS bajo, k*dt puede
        // superar 1 y la cámara "teletransportarse" en vez de suavizar,
        // lo cual se sentía como parte de lo "bugeado"). Esta fórmula da
        // el mismo suavizado exponencial sin importar el framerate.
        float t = 1f - Mathf.Exp(-followSmoothness * Time.deltaTime);
        transform.position = Vector3.Lerp(transform.position, desiredPosition, t);

        Vector3 lookAtPoint = new Vector3(target.position.x, lockedHeight - offset.y + 1.2f, target.position.z);
        transform.LookAt(lookAtPoint);
    }
}
