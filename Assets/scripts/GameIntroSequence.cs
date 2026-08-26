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

    // Público para StartupLoadingScreen: necesita saber cuánto dura la intro
    // para levantar la pantalla negra JUSTO a tiempo, de modo que la intro
    // termine (y el juego arranque) en el instante de red acordado por
    // NetworkRoundState -- el mismo para todos los clientes.
    public float IntroDuration => introDuration;

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
        // Apagamos CameraFollow YA, en Start() -- pase lo que pase después
        // (incluido StartupLoadingScreen tapando todo con la pantalla en
        // negro y arrancando PlayIntro() recién más tarde), así su propio
        // LateUpdate() nunca llega a pelear contra el movimiento que hace
        // este script.
        if (cameraFollow != null) cameraFollow.enabled = false;

        // OJO: ya NO se arranca PlayIntro() acá solo. Si hay un
        // StartupLoadingScreen en la escena (pantalla en negro tapando el
        // arranque hasta que MediaPipe + los chunks iniciales terminaron de
        // cargar), es ÉL quien llama a BeginIntro() cuando corresponde. Si
        // NO hay ningún StartupLoadingScreen en la escena, nadie llama a
        // BeginIntro() y el juego queda congelado en el primer frame para
        // siempre -- a propósito, mismo criterio que GameManager.
        // HasGameplayStarted: mejor un freeze obvio (visible, fácil de
        // diagnosticar) que una intro que a veces no corre sin avisar.
    }

    // Público para StartupLoadingScreen: pone al personaje en la pose de
    // arranque (introClipName, ej. cuclillas) Y salta la cámara a
    // introCameraStart, LO ANTES POSIBLE -- a propósito, SEPARADO de
    // BeginIntro() de abajo, así queda asentado DURANTE el tiempo que dura
    // la pantalla en negro en vez de recién al revelarse.
    //
    // El bug de "personaje cargando de a partes" que se investigó acá NO
    // resultó ser esto (se probó reintentar el CrossFade varias veces y
    // agregar una cámara de precalentado -- ninguna de las dos cambió
    // nada) -- terminó siendo específico de las skins elegidas en el menú
    // (PlayerCharacterSpawner), no de esta secuencia. Se saca esa
    // complejidad de acá para no arriesgar más estabilidad por algo que no
    // era la causa real; el diagnóstico de las skins sigue pendiente por
    // separado.
    public void ApplyStartPose()
    {
        StartCoroutine(ApplyStartPoseRoutine());
    }

    // Todos los carriles de la escena, incluidos los que RoundLaneSetup vaya
    // a apagar después (por eso FindObjectsInactive.Include): la pose de
    // arranque se pide ANTES de que se sepa cuántos jugadores hay, así que
    // conviene ponérsela a todos y que los que sobren se apaguen solos. Es
    // gratis: los apagados no se ven.
    //
    // Se busca UNA sola vez -- los 4 slots están puestos a mano en la escena
    // y nunca se crean ni se destruyen (ver PlayerSlotAssigner).
    private RunnerController[] cachedRunners;

    private RunnerController[] AllRunners()
    {
        if (cachedRunners == null || cachedRunners.Length == 0)
        {
            cachedRunners = FindObjectsByType<RunnerController>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
        }
        return cachedRunners;
    }

    private IEnumerator ApplyStartPoseRoutine()
    {
        // Un frame de por medio antes de pedirle al Animator el estado de
        // arranque -- el Animator del personaje (recién instanciado por
        // PlayerCharacterSpawner en Awake()) todavía se está asentando en
        // su estado por defecto en este instante exacto.
        yield return null;

        if (!string.IsNullOrEmpty(introClipName))
        {
            // A TODOS los carriles, no solo al del Inspector (Fase 3.3,
            // 25/8). 'player' está wireado a mano al slot 0, así que antes
            // solo ese personaje esperaba agachado: en una partida de dos, el
            // otro arrancaba parado en las dos pantallas.
            foreach (RunnerController runner in AllRunners())
            {
                if (runner == null) continue;
                runner.PlayCustomAnimation(introClipName);
                // La rotación que se ve bien para la animación de correr no es
                // la misma que la que se ve bien para la pose de arranque (ver
                // RunnerController.startPoseModelYRotation/runningModelYRotation)
                // -- se ajusta acá, junto con la animación, no antes.
                runner.ApplyStartPoseRotation();
            }
        }

        Transform cam = cameraFollow != null ? cameraFollow.transform : null;
        if (cam != null && introCameraStart != null)
        {
            cam.SetPositionAndRotation(introCameraStart.position, introCameraStart.rotation);
        }
    }

    // Público para StartupLoadingScreen: arranca la cinemática de cámara
    // de arranque (la pose del personaje Y la posición inicial de cámara
    // YA se aplicaron antes, ver ApplyStartPose más arriba -- acá no hace
    // falta volver a tocar ninguna de las dos). Separado de Start() para
    // que la pantalla de carga controle cuándo arranca.
    public void BeginIntro()
    {
        StartCoroutine(PlayIntro());
    }

    private IEnumerator PlayIntro()
    {
        Transform cam = cameraFollow != null ? cameraFollow.transform : null;

        // Compartido con GameOutroSequence.PlayOutro (ver CameraLerpUtility) --
        // acá el punto de partida es el propio introCameraStart, fijo. Espera
        // introDuration completos pase lo que pase (sin gate condicional),
        // aunque falte algún Transform.
        yield return CameraLerpUtility.LerpTo(cam, introCameraStart, introCameraEnd, introDuration);

        // A partir de acá, CameraFollow retoma el control (solo posición,
        // como siempre) y el juego arranca de verdad.
        if (cameraFollow != null) cameraFollow.enabled = true;

        // Si pusimos una animación distinta para la intro, volvemos a la de
        // correr normal -- si no, esto es un no-op inofensivo (misma que ya
        // estaba sonando). La rotación del modelo también vuelve a la que
        // corresponde para correr (ver ApplyStartPoseRotation más arriba).
        foreach (RunnerController runner in AllRunners())
        {
            if (runner == null) continue;
            runner.ResumeRunAnimation();
            runner.ApplyRunningRotation();
        }

        GameManager.Instance?.BeginGameplay();
    }
}
