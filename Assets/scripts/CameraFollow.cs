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

    [Header("Multijugador (mapas de varios carriles)")]
    [Tooltip("Destildado (default): sigue el X del target, igual que siempre -- así queda el " +
             "single-player de hoy, sin tocar nada. Tildado: ignora el X del target y usa 'Fixed " +
             "Center X' fijo -- pensado para mapas de varios carriles, donde 'target' solo sirve " +
             "para leer el Z (todos los carriles avanzan igual, ver ObstacleSpawner) pero ningún " +
             "carril en particular debería tironear la cámara hacia su propio X.")]
    [SerializeField] private bool useFixedCenterX = false;
    [Tooltip("X del centro de la arena para ESTE mapa (normalmente 0 si los carriles están " +
             "ubicados simétricos alrededor de 0). Solo se usa si 'Use Fixed Center X' está tildado.")]
    [SerializeField] private float fixedCenterX = 0f;

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

        float x = useFixedCenterX ? fixedCenterX : target.position.x;
        Vector3 desiredPosition = new Vector3(x, lockedHeight, target.position.z);

        // Mismo suavizado independiente del framerate que ya usábamos.
        float t = 1f - Mathf.Exp(-followSmoothness * Time.deltaTime);
        transform.position = Vector3.Lerp(transform.position, desiredPosition, t);

        // Nada de rotación acá — la cámara (hija de este objeto) mantiene
        // siempre la rotación local que le pusiste a mano en el Editor.
    }
}
