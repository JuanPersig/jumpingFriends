using System.Collections;
using UnityEngine;

// Secuencia corta de cámara al arrancar la partida: antes de que el juego
// empiece de verdad, la cámara recorre desde un punto de arranque
// (introCameraStart, ej. mostrando al jugador de espaldas ya parado en
// pose de correr) hasta la posición normal de juego (introCameraEnd).
// Mientras tanto, RunnerController/DifficultyManager/ObstacleSpawner/
// ScoreManager quedan pausados (ver GameManager.HasGameplayStarted) --
// nada se mueve, no aparecen obstáculos, no suma puntaje.
//
// No hace falta tocar animación para la pose de correr: el Animator
// Controller ya arranca en su estado por defecto (el de correr) desde el
// primer frame, sin que ningún script se lo pida.
public class GameIntroSequence : MonoBehaviour
{
    [Tooltip("La cámara real del juego (el GameObject 'rig' con el componente CameraFollow).")]
    [SerializeField] private CameraFollow cameraFollow;
    [Tooltip("Transform vacío posicionado a mano donde arranca la cámara al empezar la " +
             "intro (ej. detrás y arriba del jugador, mostrándolo de espaldas).")]
    [SerializeField] private Transform introCameraStart;
    [Tooltip("Transform vacío posicionado donde tiene que terminar la intro. Para que el " +
             "traspaso a CameraFollow no salte, conviene que coincida más o menos con " +
             "dónde CameraFollow ubicaría la cámara apenas arranca el juego.")]
    [SerializeField] private Transform introCameraEnd;
    [SerializeField] private float introDuration = 2.5f;

    [Header("Animación de arranque")]
    [Tooltip("El RunnerController del jugador -- necesario porque, si cambiaste de " +
             "personaje en el menú, el Animator real es uno nuevo (swapeado por " +
             "PlayerCharacterSpawner); pedírselo a RunnerController siempre trae el actual.")]
    [SerializeField] private RunnerController player;
    [Tooltip("Nombre EXACTO del estado del Animator Controller a mostrar durante la intro " +
             "(ej. una pose de reposo/lista para correr). Tiene que existir como estado en " +
             "'Player Animator.controller' -- si no está, agregalo ahí primero. Vacío = no " +
             "cambia nada, se queda con la animación de correr que ya está por defecto.")]
    [SerializeField] private string introClipName = "";

    private void Start()
    {
        // Apagamos CameraFollow mientras dura la intro -- si no, su propio
        // LateUpdate() pelearía cada frame contra el movimiento que hacemos
        // acá, tirando la cámara para el lugar "normal" de entrada.
        if (cameraFollow != null) cameraFollow.enabled = false;

        StartCoroutine(PlayIntro());
    }

    private IEnumerator PlayIntro()
    {
        // Un frame de por medio antes de pedirle al Animator el estado de
        // arranque -- mismo motivo que en MenuMouseJumper: el Animator del
        // personaje (recién instanciado por PlayerCharacterSpawner en
        // Awake()) todavía se está asentando en su estado por defecto en
        // este instante exacto, y a veces el CrossFade pedido demasiado
        // pronto no pega (a veces sí, a veces no, según el timing exacto
        // entre frames -- por eso se veía intermitente, no ligado a un
        // personaje puntual).
        yield return null;

        if (player != null && !string.IsNullOrEmpty(introClipName))
        {
            player.PlayCustomAnimation(introClipName);
        }

        Transform cam = cameraFollow != null ? cameraFollow.transform : null;

        if (cam != null && introCameraStart != null)
        {
            cam.SetPositionAndRotation(introCameraStart.position, introCameraStart.rotation);
        }

        float elapsed = 0f;
        while (elapsed < introDuration)
        {
            elapsed += Time.deltaTime;

            if (cam != null && introCameraStart != null && introCameraEnd != null)
            {
                float t = Mathf.Clamp01(elapsed / introDuration);
                cam.position = Vector3.Lerp(introCameraStart.position, introCameraEnd.position, t);
                cam.rotation = Quaternion.Slerp(introCameraStart.rotation, introCameraEnd.rotation, t);
            }

            yield return null;
        }

        // A partir de acá, CameraFollow retoma el control (solo posición,
        // como siempre) y el juego arranca de verdad.
        if (cameraFollow != null) cameraFollow.enabled = true;

        // Si pusimos una animación distinta para la intro, volvemos a la de
        // correr normal -- si no, esto es un no-op inofensivo (misma que ya
        // estaba sonando).
        if (player != null) player.ResumeRunAnimation();

        GameManager.Instance?.BeginGameplay();
    }
}
