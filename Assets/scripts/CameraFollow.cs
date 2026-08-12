using UnityEngine;

// Sigue al jugador en X/Z manteniendo una altura de piso estable (ignora
// el vaivén de saltos/agaches, igual que antes). A PROPÓSITO no controla
// el ángulo/rotación de la cámara para nada — ese es el cambio clave.
//
// Este script va en un GameObject "rig" (vacío, sin cámara). La cámara
// real es HIJA de ese rig, con la posición/rotación local que le hayas
// puesto a mano en el Editor (arrastrando/rotando en la Scene view hasta
// que se vea bien). Como este script nunca toca esa rotación, lo que ves
// en el Editor es EXACTAMENTE lo que vas a ver en el juego — sin
// necesidad de calcular offsets a mano ni pelear con un LookAt automático
// que reinterpreta el ángulo cada frame.
public class CameraFollow : MonoBehaviour
{
    [SerializeField] private Transform target;
    [SerializeField] private float followSmoothness = 8f;

    private float lockedHeight;
    private bool initialized;

    private void LateUpdate()
    {
        if (target == null) return;
        if (!initialized)
        {
            lockedHeight = target.position.y;
            initialized = true;
        }

        Vector3 desiredPosition = new Vector3(target.position.x, lockedHeight, target.position.z);

        // Mismo suavizado independiente del framerate que ya usábamos.
        float t = 1f - Mathf.Exp(-followSmoothness * Time.deltaTime);
        transform.position = Vector3.Lerp(transform.position, desiredPosition, t);

        // Nada de rotación acá — la cámara (hija de este objeto) mantiene
        // siempre la rotación local que le pusiste a mano en el Editor.
    }
}
