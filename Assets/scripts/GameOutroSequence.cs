using System.Collections;
using UnityEngine;

// Cinemática corta al PERDER la partida -- hermana de GameIntroSequence,
// mismo mecanismo pero al revés: en vez de ir DE un punto fijo A la vista
// normal de juego, va DE la vista normal de juego A un punto fijo (la
// "cámara de muerte"), mientras el jugador reproduce la animación de caída
// (Death01 por default).
//
// "Pausar" el juego acá NO significa Time.timeScale = 0 -- eso también
// congelaría la propia animación de Death01 y el movimiento de esta misma
// cámara, que sí queremos que sigan en tiempo real. No hace falta: apenas
// GameManager.IsGameOver pasa a true, RunnerController/ObstacleSpawner/
// DifficultyManager/ScoreManager ya dejan de correr en su propio Update()
// (mismo chequeo que ya usan, ver esos scripts) -- el "pausado" del
// gameplay ya está resuelto de antes. Este script solo se encarga de la
// parte visual: mover la cámara y disparar la animación.
public class GameOutroSequence : MonoBehaviour
{
    [Header("Cámara de muerte")]
    [Tooltip("La cámara real del juego (el GameObject 'rig' con el componente CameraFollow).")]
    [SerializeField] private CameraFollow cameraFollow;
    [Tooltip("OJO: tiene que ser un Transform HIJO del jugador ('player'), no un punto fijo " +
             "del mundo -- en un endless runner el jugador nunca muere dos veces en el mismo " +
             "lugar, siempre está más adelante. Acomodalo en la Scene view como posición/" +
             "rotación LOCAL relativa al jugador (ej. \"3 unidades adelante, 1 arriba, mirando " +
             "hacia atrás y arriba\") -- mismo truco que ya usa CameraFollow con la Camera de " +
             "verdad, que es hija del rig con un offset local fijo. Al ser hijo, viaja solo con " +
             "el jugador sin que este script tenga que calcular nada.")]
    [SerializeField] private Transform deathCameraAnchor;
    [SerializeField] private float cameraTransitionDuration = 1.5f;

    [Header("Animación de muerte")]
    [Tooltip("El RunnerController del jugador -- igual que en GameIntroSequence, pedírselo " +
             "siempre trae el Animator actual (por si se cambió de personaje en el menú).")]
    [SerializeField] private RunnerController player;
    [Tooltip("Nombre EXACTO del estado en el Animator Controller para la animación de caída. " +
             "Tiene que existir como estado ahí -- si no está, agregalo primero (mismo paso " +
             "que se hizo con Hit_Head).")]
    [SerializeField] private string deathClipName = "Death01";

    [Header("Timing")]
    [Tooltip("Cuánto se sostiene la escena (cámara ya en posición, animación ya jugando) " +
             "antes de mostrar el panel de Game Over.")]
    [SerializeField] private float holdBeforeGameOverPanelSeconds = 1.5f;

    [Header("UI")]
    [Tooltip("Se le llama a ShowGameOverPanel() recién cuando termina la cinemática -- así " +
             "el panel no tapa la animación de caída ni el movimiento de cámara.")]
    [SerializeField] private UIManager uiManager;

    // Evita arrancar la cinemática más de una vez -- Update() sigue viendo
    // IsGameOver == true en todos los frames siguientes.
    private bool hasPlayed;

    private void Update()
    {
        if (hasPlayed) return;
        if (GameManager.Instance == null || !GameManager.Instance.IsGameOver) return;

        hasPlayed = true;
        StartCoroutine(PlayOutro());
    }

    private IEnumerator PlayOutro()
    {
        // Apagamos CameraFollow para que su propio LateUpdate() no pelee
        // cada frame contra el movimiento que hacemos acá -- mismo motivo
        // que GameIntroSequence al arrancar la partida.
        if (cameraFollow != null) cameraFollow.enabled = false;

        // PlayDeathAnimation se encarga de todo: frena cualquier corrutina
        // de salto/agache que siguiera corriendo sola, y anima la Y del
        // personaje a lo largo del clip (ver deathPoseYOffsetOverTime en
        // RunnerController -- un offset fijo no alcanzaba, Death01 se va
        // hundiendo a medida que cae, no es una pose "en el lugar").
        if (player != null)
        {
            player.PlayDeathAnimation(deathClipName);
        }

        Transform cam = cameraFollow != null ? cameraFollow.transform : null;

        if (cam != null && deathCameraAnchor != null)
        {
            Vector3 startPos = cam.position;
            Quaternion startRot = cam.rotation;
            float elapsed = 0f;

            // Leemos deathCameraAnchor.position/.rotation DE NUEVO en cada
            // vuelta del while, no una sola vez al principio -- es hijo del
            // jugador, y PlayDeathAnimation() sigue moviendo su Y en tiempo
            // real mientras el personaje cae (ver DeathYOffsetRoutine en
            // RunnerController), así que el ancla se hunde CON él y el Lerp
            // apunta siempre al lugar correcto, no a una foto vieja de
            // antes de que empezara a caer.
            while (elapsed < cameraTransitionDuration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / cameraTransitionDuration);
                cam.position = Vector3.Lerp(startPos, deathCameraAnchor.position, t);
                cam.rotation = Quaternion.Slerp(startRot, deathCameraAnchor.rotation, t);
                yield return null;
            }

            cam.SetPositionAndRotation(deathCameraAnchor.position, deathCameraAnchor.rotation);
        }

        yield return new WaitForSeconds(holdBeforeGameOverPanelSeconds);

        // A propósito NO se vuelve a prender cameraFollow -- la partida ya
        // terminó, no hay a qué "volver a seguir". Si en algún momento se
        // agrega un botón de Reiniciar que recarga la escena
        // (GameManager.RestartGame ya existe), todo esto se resetea solo.
        uiManager?.ShowGameOverPanel();
    }
}
