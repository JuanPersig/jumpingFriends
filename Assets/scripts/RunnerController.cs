using System.Collections;
using UnityEngine;

// Mueve al jugador hacia adelante automáticamente (estilo endless runner) y
// reacciona a los eventos de PlayerInputProvider para saltar/agacharse.
// Este script NO sabe nada de cámara, UDP, ni Python. Solo escucha
// OnJump / OnCrouch / OnStand.
[RequireComponent(typeof(CapsuleCollider))]
[RequireComponent(typeof(Rigidbody))]
public class RunnerController : MonoBehaviour
{
    public enum PlayerState { Running, Jumping, Crouching }

    [Header("Salto")]
    [SerializeField] private float jumpHeight = 1.6f;
    [SerializeField] private float jumpDuration = 0.55f;

    [Header("Perdón de salto (obstáculos bajos)")]
    // Diagnosticado con logs de frame: el jugador NO salta tarde, salta
    // TEMPRANO (apenas ve venir el tronco) — el arco de 0.7s termina y
    // aterriza bien ANTES de llegar físicamente a la posición del Log, y
    // ahí choca ya parado. Un truco de "adelantar el reloj del salto" (lo
    // que había antes acá) no arregla esto — hasta lo empeora, porque
    // acorta el tiempo real que el personaje queda arriba. La solución real
    // es dar crédito por la intención: si saltaste hace poco, un obstáculo
    // BAJO (el Log; no la Barrera, que se agacha) no cuenta como choque,
    // aunque en ese instante ya hayas aterrizado.
    // Rango en vez de valor fijo (mismo criterio que minReactionSeconds y
    // switchTypeChance en ObstacleSpawner): generoso al arrancar, más
    // estricto a medida que sube la velocidad — atado al mismo
    // DifficultyManager.Progress01, así el juego no queda "fácil de punta
    // a punta" solo por tener estos colchones de seguridad.
    [Tooltip("Perdón de salto (segundos) AL ARRANCAR la partida — el más generoso.")]
    [SerializeField] private float lowObstacleJumpGraceSecondsEarly = 1.5f;
    [Tooltip("Perdón de salto (segundos) a velocidad MÁXIMA — el más estricto (exige " +
             "saltar más cerca del obstáculo de verdad).")]
    [SerializeField] private float lowObstacleJumpGraceSecondsLate = 0.6f;
    [Tooltip("Altura (Y del mundo) por debajo de la cual un obstáculo se considera " +
             "'bajo' (se salta, ej. el Log) en vez de 'alto' (se agacha, ej. la " +
             "Barrera). Con la Barrera, saltar SIGUE contando como choque.")]
    [SerializeField] private float lowObstacleHeightThreshold = 1f;

    [Header("Agache")]
    // OJO: este valor ya NO escala el Transform/la malla visual (ver
    // comentario grande en ResizeCrouchCollider). Ahora escala solo el
    // CapsuleCollider.height, para achicar el hitbox y dejar pasar al
    // jugador bajo obstáculos. La pose agachada la muestra la animación
    // Crouch_Fwd_Loop, no un achique del modelo.
    [SerializeField] private float crouchHeightScale = 0.5f;
    [SerializeField] private float crouchTransitionDuration = 0.15f;
    [Tooltip("Tiempo mínimo agachado antes de que un salto pueda cancelar la animación " +
             "de agache. Sin esto, un salto que llega a mitad de la transición corta la " +
             "pose a la mitad y se ve raro. El salto NO se pierde si llega antes: queda " +
             "programado para disparar apenas se cumpla este tiempo.")]
    [SerializeField] private float minCrouchSettleSeconds = 0.15f;

    [Header("Animación")]
    [Tooltip("Opcional: si no se asigna, el personaje se mueve igual, solo que sin animar.")]
    [SerializeField] private Animator animator;
    [SerializeField] private string runClipName = "Jog_Fwd_Loop";
    [SerializeField] private string jumpClipName = "Jump_Loop";
    [SerializeField] private string crouchClipName = "Crouch_Fwd_Loop";
    [SerializeField] private float animationCrossFade = 0.1f;

    [Header("Rotación del modelo (fix visual)")]
    [Tooltip("Rotación Y del modelo visual durante la pose de arranque (agachado/idle, la que " +
             "se ve mientras dura la pantalla de carga) -- se ajusta a mano mirando cómo se ve " +
             "en Play, es independiente de la rotación que necesita la animación de correr.")]
    [SerializeField] private float startPoseModelYRotation = 0f;
    [Tooltip("Rotación Y del modelo visual una vez que arranca a correr de verdad. Distinta de " +
             "la de arriba a propósito: el retargeting Humanoid hace que cada animación pueda " +
             "necesitar su propio ajuste fino para verse derecha.")]
    [SerializeField] private float runningModelYRotation = -181.767f;

    [Header("Muerte")]
    [Tooltip("Cuánto dura, en segundos, el recorrido de la curva de abajo -- lo ideal es que " +
             "coincida con la duración real del clip Death01 (la ves seleccionando el clip " +
             "adentro del FBX en el Project, abajo del preview). Si termina antes de que el " +
             "clip termine de caer, el offset se queda clavado en el último valor de la curva.")]
    [SerializeField] private float deathAnimationDuration = 1.5f;
    [Tooltip("Offset en Y (sumado al piso normal, groundLocalY) A LO LARGO de la animación de " +
             "muerte -- NO es un número fijo: Death01 no es una pose \"en el lugar\" como " +
             "correr/saltar, el personaje se va hundiendo a medida que cae, así que el offset " +
             "correcto va cambiando con el tiempo (cerca de 0 al principio, más negativo al " +
             "final, ya tirado). Ajustá la forma de la curva en el Inspector mirando el " +
             "resultado en Play -- eje X = progreso 0 a 1 de la animación, eje Y = el offset.")]
    [SerializeField] private AnimationCurve deathPoseYOffsetOverTime = AnimationCurve.Linear(0f, 0f, 1f, -0.65f);

    public PlayerState State { get; private set; } = PlayerState.Running;

    private CapsuleCollider capsule;
    private float originalColliderHeight;
    private float originalColliderCenterY;
    private float groundLocalY;
    private Coroutine actionRoutine;
    private float lastJumpStartTime = -999f;
    // Ver HandleCrouch/HandleStand/JumpRoutine: un agache que llega
    // mientras estamos en el aire se guarda acá en vez de perderse.
    private bool queuedCrouchOnLanding = false;
    // Momento en que se entró a Crouching (para minCrouchSettleSeconds) y la
    // corrutina del salto programado cuando llega mientras el agache todavía
    // no se asentó (ver HandleJump). Separada de actionRoutine a propósito:
    // si mientras tanto llega un OnStand, no queremos que cancelar la
    // corrutina de "pararse" se lleve puesto el salto ya decidido.
    private float crouchEnteredTime = -999f;
    private Coroutine pendingJumpRoutine;

    // Ver Awake/Update: la Z se calcula como startZ + distancia recorrida,
    // en vez de irse sumando frame a frame.
    private float startZ;

    // Hashes de los nombres de clip, calculados UNA sola vez en Awake en
    // vez de dejar que Animator.CrossFadeInFixedTime(string, ...) rehaga
    // internamente Animator.StringToHash() cada vez que cambiamos de
    // animación. 0 = "sin clip asignado" (mismo comportamiento que antes
    // con string.IsNullOrEmpty, ver PlayAnimation).
    private int runClipHash;
    private int jumpClipHash;
    private int crouchClipHash;

    private void Awake()
    {
        capsule = GetComponent<CapsuleCollider>();
        originalColliderHeight = capsule.height;
        originalColliderCenterY = capsule.center.y;
        groundLocalY = transform.position.y;

        // La rotación de corrida es el default de cualquier carril (ver
        // Start()). Se deja seteada YA, en Awake, porque PlayerCharacterSpawner
        // puede llamar a SetAnimator() antes de que corra nuestro Start() --
        // el orden de Awake() entre componentes del mismo GameObject no está
        // garantizado -- y ahí SetAnimator ya necesita saber qué re-aplicar.
        currentModelYRotation = runningModelYRotation;

        // Z de arranque, capturada UNA vez. A partir de acá la posición en Z
        // es siempre startZ + distancia recorrida (ver Update) -- absoluta,
        // no acumulada. RoundLaneSetup solo toca la X, así que este valor
        // sigue siendo válido después de que reacomode los carriles.
        startZ = transform.position.z;

        runClipHash = string.IsNullOrEmpty(runClipName) ? 0 : Animator.StringToHash(runClipName);
        jumpClipHash = string.IsNullOrEmpty(jumpClipName) ? 0 : Animator.StringToHash(jumpClipName);
        crouchClipHash = string.IsNullOrEmpty(crouchClipName) ? 0 : Animator.StringToHash(crouchClipName);

        // Rigidbody kinemático: lo necesitamos para que OnTriggerEnter
        // funcione de forma confiable, pero movemos al jugador a mano
        // (transform), no con física real, porque el salto tiene un arco
        // fijo disparado por un evento binario, no por gravedad continua.
        Rigidbody rb = GetComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;

        // TUNNELING -- causa real de "choques que no entran" (diagnosticado
        // 14/8 con el reporte de que el choque parecía llegar segundos
        // tarde: no llegaba tarde, se PERDÍA el choque de esa barrera y
        // recién se registraba con una posterior).
        //
        // Los números: el trigger de la Barrera mide 0.306 unidades de
        // profundidad en Z (BoxCollider size z=1, pero el root del prefab
        // tiene scale z=0.30581). La física corre a 50Hz (timestep 0.02s) y
        // el jugador llega a 16 u/s -> 0.32 unidades por paso de física,
        // MÁS que el grosor del trigger. Con detección discreta (el default)
        // la física solo testea posiciones sueltas: el jugador pasa de
        // "antes" a "después" del trigger sin que ningún paso lo encuentre
        // adentro, y OnTriggerEnter nunca se dispara. Se perdía el choque
        // entero, no solo la animación -- también la vida que correspondía.
        //
        // ContinuousSpeculative es el modo continuo que SÍ soportan los
        // rigidbodies kinemáticos (Continuous/ContinuousDynamic son solo
        // para los dinámicos): en vez de testear posiciones sueltas, barre
        // el collider a lo largo de su recorrido entre paso y paso.
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
    }

    // Llamado por PlayerCharacterSpawner cuando reemplaza el modelo
    // placeholder por el personaje elegido en el menú. La referencia
    // 'animator' del Inspector, sin esto, seguiría apuntando al modelo
    // VIEJO (ya destruido) apenas se hace el swap -- el personaje nuevo se
    // movería bien pero sin animar. Los hashes de clip (runClipHash/
    // jumpClipHash/crouchClipHash) NO hace falta recalcularlos acá: todos
    // los personajes del pack Quaternius comparten el mismo Animator
    // Controller (mismos nombres de estado), solo cambia A QUIÉN se le
    // aplica el CrossFade.
    // La corrección de rotación se RE-APLICA acá (Fase 3.2, 25/8). Antes no
    // hacía falta: había un solo swap, en Awake(), y Start() aplicaba la
    // rotación de corrida encima del modelo ya definitivo.
    //
    // Con el multijugador cada carril swapea DOS veces -- el personaje local
    // en Awake(), y el del dueño real del slot cuando llega por red (ver
    // PlayerSlot). Ese segundo modelo nace después de Start(), así que nunca
    // recibía la corrección y se quedaba con la rotación cruda del FBX.
    // Síntoma reportado: el personaje del otro jugador se veía corriendo
    // rotado en la pantalla del host.
    //
    // Se re-aplica el ÚLTIMO valor pedido, no runningModelYRotation fijo: si
    // el swap cae mientras GameIntroSequence tiene puesta la pose de arranque
    // (ApplyStartPoseRotation), pisarlo con la rotación de corrida rompería
    // la intro.
    public void SetAnimator(Animator newAnimator)
    {
        animator = newAnimator;
        SetModelYRotation(currentModelYRotation);

        // También hay que re-aplicar la ANIMACIÓN, no solo la rotación: un
        // modelo recién instanciado arranca en el estado por defecto de su
        // Animator Controller (correr), así que un swap tardío pisaba la pose
        // de arranque en cuclillas que ya se había puesto. Síntoma reportado
        // el 25/8: el personaje del cliente empezaba parado mientras el del
        // host esperaba agachado.
        //
        // Play() y no CrossFade: es un Animator nuevo, no hay nada de dónde
        // mezclar, y queremos que quede en la pose YA.
        if (animator != null && currentClipHash != 0)
        {
            animator.Play(currentClipHash, 0, 0f);
        }
    }

    private void Start()
    {
        // Rotación de corrida por defecto (ver runningModelYRotation): antes
        // solo la aplicaba GameIntroSequence, que maneja UN solo
        // RunnerController puntual -- cualquier carril extra (los que arma
        // RoundLaneSetup para pruebas locales de varios jugadores) nunca
        // pasaba por ahí y se quedaba con la rotación cruda del modelo, sin
        // corregir. Poniéndolo acá como default, CUALQUIER RunnerController
        // arranca bien orientado sin depender de GameIntroSequence. Para el
        // jugador principal no cambia nada: GameIntroSequence lo sigue
        // pisando con la pose de arranque (ApplyStartPoseRotation) y lo
        // devuelve a este mismo valor al terminar la intro
        // (ApplyRunningRotation) -- sin conflicto, y todo esto pasa tapado
        // por la pantalla de carga negra (StartupLoadingScreen), no se nota
        // ningún salto.
        ApplyRunningRotation();

        // ESTE SCRIPT YA NO SE SUSCRIBE AL INPUT (Fase 3.3, 25/8). Antes cada
        // RunnerController de la escena escuchaba el PlayerInputProvider
        // global, así que un solo salto real movía a los CUATRO carriles a la
        // vez -- perfecto para probar en local, imposible en red.
        //
        // Ahora quien decide es PlayerSlot: se suscribe solo en el carril que
        // es TUYO, lo aplica al instante en tu pantalla, y manda el evento por
        // red para que las demás máquinas lo reproduzcan sobre tu personaje
        // (ver TriggerJump/TriggerCrouch/TriggerStand más abajo).
        //
        // Se mantiene la promesa del encabezado: este script sigue sin saber
        // de dónde viene el input, ni que existe una red. Solo expone "hacé
        // esto" y alguien más decide cuándo.
    }

    private void Update()
    {
        if (GameManager.Instance != null && GameManager.Instance.IsGameOver) return;
        // Pausado hasta que termine la intro de cámara (ver GameIntroSequence).
        if (GameManager.Instance != null && !GameManager.Instance.HasGameplayStarted) return;
        if (DifficultyManager.Instance == null) return;
        // Este carril ya murió en la máquina de su dueño -- ver Frozen.
        if (Frozen) return;

        // Posición ABSOLUTA, no acumulada (Fase 3, 25/8): antes esto era
        // `position += forward * speed * deltaTime`, que va sumando el error
        // de redondeo de cada frame. Dos clientes con distinto framerate
        // acumulan errores distintos y se separan solos, sin que nada avise.
        //
        // Ahora la distancia la calcula DifficultyManager en forma cerrada a
        // partir del tiempo de ronda compartido, así que todos los clientes
        // llegan al mismo número -- y si a uno se le traba un frame, al
        // siguiente vuelve solo a donde corresponde en vez de quedar
        // permanentemente atrasado. Ver el comentario grande de ese script.
        //
        // Solo se toca la Z: la Y la manejan el salto y la muerte, la X la
        // fija RoundLaneSetup según el carril.
        Vector3 pos = transform.position;
        pos.z = startZ + DifficultyManager.Instance.DistanceTravelled;
        transform.position = pos;
    }

    // Un carril apagado no reacciona al input (Fase 3.2, 25/8). Hace falta
    // porque RoundLaneSetup pasó a apagar los slots que sobran TARDE (cuando
    // llega el estado de ronda por red) en vez de en su Awake(): antes, un
    // slot apagado nunca llegaba a correr su Start(), así que jamás se
    // suscribía al input. Ahora sí alcanza a suscribirse, y después lo apagan
    // -- sin este chequeo, el próximo salto real le dispararía una corrutina
    // a un GameObject inactivo ("Coroutine couldn't be started because the
    // game object is inactive").
    //
    // --- Disparadores públicos (los llama PlayerSlot) ---
    //
    // Son la única puerta de entrada a las acciones del personaje. Da igual
    // si el evento nació de la cámara de este jugador o llegó por red desde
    // otra máquina: de acá para adentro el comportamiento es idéntico, con
    // todo lo fino que ya estaba resuelto (perdón de salto, agache encolado
    // mientras estás en el aire, tiempo mínimo de asentado del agache).
    public void TriggerJump() => HandleJump();
    public void TriggerCrouch() => HandleCrouch();
    public void TriggerStand() => HandleStand();

    // ¿Los choques de ESTE carril le restan vidas al jugador de esta máquina?
    //
    // Arranca en true para que una escena sin PlayerSlot (Gameplay.unity
    // abierta suelta) se comporte como siempre. En una partida en red lo
    // maneja PlayerSlot: true solo en tu propio carril.
    //
    // Sin esto, los CUATRO RunnerController de cada máquina le reportaban al
    // mismo GameManager global -- o sea que si el personaje del otro jugador
    // chocaba, vos perdías la vida. Bug real reportado el 25/8.
    //
    // Cada cliente es autoritativo sobre sus PROPIOS choques (decisión
    // tomada en plan-fase3-gameplay-en-red.md, sección 6): es el único que
    // conoce el timing real de su input, y el perdón de salto depende de ese
    // timing. No hay validación del servidor, a propósito.
    public bool ReportsHits { get; set; } = true;

    // Congela este carril donde está, sin tocar nada más. Lo usa PlayerSlot
    // cuando el jugador de OTRA máquina se quedó sin vidas: acá su
    // GameManager local no sabe nada de esa muerte (IsGameOver es false en
    // esta máquina), así que sin esto su personaje seguiría corriendo para
    // siempre en nuestra pantalla mientras en la suya ya está tirado.
    public bool Frozen { get; set; }

    private void HandleJump()
    {
        if (!isActiveAndEnabled) return;

        // Una vez terminado el juego, ignoramos cualquier salto/agache/
        // parado que siga llegando de la detección real -- sin esto, el
        // "cadáver" de GameOutroSequence podía seguir reaccionando a que
        // te muevas de verdad frente a la cámara mientras se reproduce
        // Death01, arruinando la pose.
        if (GameManager.Instance != null && GameManager.Instance.IsGameOver) return;

        // Mismo criterio durante la pantalla de carga / paneo de cámara de
        // arranque (ver GameIntroSequence): si el jugador salta de verdad
        // frente a la cámara ANTES de que termine la intro, State pasaba a
        // Jumping y JumpRoutine empezaba a mover la Y del personaje --
        // peleando contra la pose/cámara que la intro está tratando de
        // asentar, y dejando al personaje "trabado" en una pose rara una
        // vez que el juego arranca de verdad. Bug real, reportado el 17/8.
        if (GameManager.Instance != null && !GameManager.Instance.HasGameplayStarted) return;

        // Solo ignoramos el salto si YA está en el aire (para no reiniciar
        // el arco a mitad de camino). Antes también lo ignorábamos estando
        // agachado, obligando a pasar por un instante "de pie" en el medio
        // -algo que en la vida real pasa muy rápido y la detección no
        // siempre llega a registrar- y eso comía tiempo de reacción real.
        if (State == PlayerState.Jumping) return;

        if (State == PlayerState.Crouching)
        {
            float remaining = minCrouchSettleSeconds - (Time.time - crouchEnteredTime);
            if (remaining > 0f)
            {
                // Todavía no pasó el mínimo para que la pose de agache se
                // vea asentada -- el salto NO se pierde, se programa para
                // el instante justo en que se cumpla (ver comentario en
                // minCrouchSettleSeconds).
                if (pendingJumpRoutine != null) StopCoroutine(pendingJumpRoutine);
                pendingJumpRoutine = StartCoroutine(BeginJumpAfterDelay(remaining));
                return;
            }
        }

        BeginJump();
    }

    private IEnumerator BeginJumpAfterDelay(float delaySeconds)
    {
        yield return new WaitForSeconds(delaySeconds);
        pendingJumpRoutine = null;
        if (State == PlayerState.Jumping) yield break; // ya se disparó por otro camino mientras tanto
        BeginJump();
    }

    private void BeginJump()
    {
        if (State == PlayerState.Crouching)
        {
            // Saltando directo desde agachado: restauramos el hitbox al
            // instante (sin transición) para no perder tiempo. Ya no hay
            // Transform que restaurar (el agache no toca el Transform,
            // solo el CapsuleCollider).
            capsule.height = originalColliderHeight;
            Vector3 center = capsule.center;
            center.y = originalColliderCenterY;
            capsule.center = center;
        }

        State = PlayerState.Jumping;
        lastJumpStartTime = Time.time;
        PlayAnimation(jumpClipHash);
        RestartRoutine(JumpRoutine());
    }

    private void HandleCrouch()
    {
        if (!isActiveAndEnabled) return; // carril apagado, ver comentario en HandleJump
        if (GameManager.Instance != null && GameManager.Instance.IsGameOver) return;
        // Ver comentario grande en HandleJump -- mismo criterio durante la
        // pantalla de carga / paneo de cámara de arranque.
        if (GameManager.Instance != null && !GameManager.Instance.HasGameplayStarted) return;

        if (State == PlayerState.Jumping)
        {
            // A velocidad alta puede llegar un agache mientras TODAVÍA
            // estás en el aire saltando el obstáculo anterior. Antes esto
            // se perdía para siempre (el jugador ya se agachó en la vida
            // real, pero acá no pasaba nada) -> se sentía como que el
            // personaje "dejó de responder". Ahora lo guardamos y se aplica
            // solo, apenas aterriza (ver el final de JumpRoutine).
            queuedCrouchOnLanding = true;
            return;
        }

        if (State != PlayerState.Running) return;
        State = PlayerState.Crouching;
        crouchEnteredTime = Time.time;
        PlayAnimation(crouchClipHash);
        RestartRoutine(ResizeCrouchCollider(originalColliderHeight * crouchHeightScale));
    }

    private void HandleStand()
    {
        if (!isActiveAndEnabled) return; // carril apagado, ver comentario en HandleJump
        if (GameManager.Instance != null && GameManager.Instance.IsGameOver) return;
        // Ver comentario grande en HandleJump -- mismo criterio durante la
        // pantalla de carga / paneo de cámara de arranque.
        if (GameManager.Instance != null && !GameManager.Instance.HasGameplayStarted) return;

        // Si en la vida real el jugador ya se paró de nuevo antes de que
        // termine el salto, cancelamos el agache que habíamos guardado —
        // si no, aterrizaría agachándose por un comando que ya quedó viejo.
        if (State == PlayerState.Jumping)
        {
            queuedCrouchOnLanding = false;
            return;
        }

        // El salto vuelve solo a Running al terminar su propia corrutina;
        // acá solo nos interesa la transición Crouching -> Running.
        if (State != PlayerState.Crouching) return;
        RestartRoutine(StandRoutine());
    }

    private void RestartRoutine(IEnumerator routine)
    {
        if (actionRoutine != null) StopCoroutine(actionRoutine);
        actionRoutine = StartCoroutine(routine);
    }

    private IEnumerator JumpRoutine()
    {
        float elapsed = 0f;
        ApplyJumpHeight(elapsed);

        while (elapsed < jumpDuration)
        {
            elapsed += Time.deltaTime;
            ApplyJumpHeight(elapsed);
            yield return null;
        }

        Vector3 finalPos = transform.position;
        transform.position = new Vector3(finalPos.x, groundLocalY, finalPos.z);
        State = PlayerState.Running;
        PlayAnimation(runClipHash);

        if (queuedCrouchOnLanding)
        {
            // Ahora sí, State ya es Running: HandleCrouch() sigue su
            // camino normal (no vuelve a entrar por la rama de "estoy en
            // el aire" porque ya aterrizamos).
            queuedCrouchOnLanding = false;
            HandleCrouch();
        }
    }

    // Sin(t * PI): sube y baja suave, con el pico en t = jumpDuration/2 -> arco de salto.
    private void ApplyJumpHeight(float elapsed)
    {
        float t = Mathf.Clamp01(elapsed / jumpDuration);
        float heightOffset = jumpHeight * Mathf.Sin(t * Mathf.PI);
        Vector3 pos = transform.position;
        transform.position = new Vector3(pos.x, groundLocalY + heightOffset, pos.z);
    }

    private IEnumerator StandRoutine()
    {
        yield return ResizeCrouchCollider(originalColliderHeight);
        State = PlayerState.Running;
        PlayAnimation(runClipHash);
    }

    // CrossFadeInFixedTime (no CrossFade a secas) porque los clips de este
    // pack están pensados en tiempo real/frames, no normalizado 0-1 — así
    // el tiempo de mezcla (animationCrossFade) se respeta igual sin
    // importar cuán largo sea cada clip. Recibe el HASH precalculado
    // (ver Awake), no el string, para no rehashear en cada llamada.
    // Última animación pedida, para poder re-aplicarla si el modelo se
    // swapea (ver SetAnimator). 0 = ninguna todavía.
    private int currentClipHash;

    private void PlayAnimation(int clipHash)
    {
        if (clipHash == 0) return;
        currentClipHash = clipHash;

        if (animator == null) return;
        animator.CrossFadeInFixedTime(clipHash, animationCrossFade);
    }

    // Público para GameOutroSequence: arranca la animación de muerte Y
    // maneja todo lo que hace falta para que se vea bien.
    //
    // Si el choque final pasó justo en medio de un salto o un agache, la
    // corrutina que lo manejaba (JumpRoutine/StandRoutine/
    // ResizeCrouchCollider) sigue corriendo sola -- no está atada a
    // GameManager.IsGameOver, es aparte del Update() que sí lo chequea --
    // y seguía peleando por la Y del personaje (o el tamaño del collider)
    // al mismo tiempo que Death01 ya estaba crossfadeado encima. Frenamos
    // esa corrutina y volvemos el collider a su tamaño normal ANTES de
    // arrancar.
    //
    // La Y del personaje durante la animación la maneja DeathYOffsetRoutine
    // (más abajo) -- ver el comentario grande en deathPoseYOffsetOverTime
    // sobre por qué hace falta una curva en vez de un offset fijo.
    public void PlayDeathAnimation(string clipName)
    {
        if (actionRoutine != null) { StopCoroutine(actionRoutine); actionRoutine = null; }
        if (pendingJumpRoutine != null) { StopCoroutine(pendingJumpRoutine); pendingJumpRoutine = null; }

        capsule.height = originalColliderHeight;
        Vector3 center = capsule.center;
        center.y = originalColliderCenterY;
        capsule.center = center;

        if (!string.IsNullOrEmpty(clipName))
        {
            PlayCustomAnimation(clipName);
        }

        RestartRoutine(DeathYOffsetRoutine());
    }

    // Sigue la curva deathPoseYOffsetOverTime a lo largo de
    // deathAnimationDuration segundos, ajustando la Y del personaje frame
    // a frame -- root motion sigue apagado a propósito (ver
    // PlayerCharacterSpawner), así que sin esto Unity no mueve la Y sola
    // durante el clip. Al llegar al final se queda clavado en el último
    // valor de la curva (Evaluate con t=1 fuera de rango simplemente
    // repite el último keyframe, no hay que hacer nada especial).
    private IEnumerator DeathYOffsetRoutine()
    {
        float elapsed = 0f;
        while (elapsed < deathAnimationDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / deathAnimationDuration);

            Vector3 pos = transform.position;
            pos.y = groundLocalY + deathPoseYOffsetOverTime.Evaluate(t);
            transform.position = pos;

            yield return null;
        }
    }

    // Público para GameIntroSequence: muestra un clip por NOMBRE (no hash
    // precalculado, porque este no es uno de los 3 que ya cachea Awake) --
    // se usa una sola vez al arrancar la partida, así que rehashear acá no
    // importa. El nombre tiene que coincidir con un estado que ya exista en
    // el Animator Controller (agregalo ahí si hace falta, Unity no lo crea solo).
    public void PlayCustomAnimation(string clipName)
    {
        if (string.IsNullOrEmpty(clipName)) return;
        // Se recuerda igual que en PlayAnimation, para poder re-aplicarla si
        // el modelo se swapea después (ver SetAnimator).
        currentClipHash = Animator.StringToHash(clipName);

        if (animator == null) return;
        animator.CrossFadeInFixedTime(currentClipHash, animationCrossFade);
    }

    // Público para GameIntroSequence: vuelve a la animación de correr
    // normal al terminar la intro (por si PlayCustomAnimation puso otra
    // distinta, ej. una pose de reposo).
    public void ResumeRunAnimation()
    {
        PlayAnimation(runClipHash);
    }

    // Público para GameIntroSequence: aplica la rotación Y ajustada a mano
    // para la pose de arranque (agachado/idle, ver startPoseModelYRotation) --
    // llamar junto con PlayCustomAnimation(introClipName).
    public void ApplyStartPoseRotation()
    {
        SetModelYRotation(startPoseModelYRotation);
    }

    // Público para GameIntroSequence: aplica la rotación Y ajustada a mano
    // para cuando arranca a correr de verdad (ver runningModelYRotation) --
    // llamar junto con ResumeRunAnimation().
    public void ApplyRunningRotation()
    {
        SetModelYRotation(runningModelYRotation);
    }

    // Rota el modelo visual (el mismo GameObject que trae el Animator --
    // PlayerCharacterSpawner/MenuMouseJumper lo swapean entero como una
    // unidad, ver CharacterModelSwapper) sobre su eje Y local, dejando
    // X/Z tal como estén. Fix puntual para compensar que el retargeting
    // Humanoid necesita un ajuste de orientación distinto según la
    // animación que esté sonando en ese momento.

    // Última rotación pedida, para poder re-aplicarla si el modelo se swapea
    // (ver SetAnimator). Arranca en la de corrida, que es el default de
    // cualquier carril -- ver Start().
    private float currentModelYRotation;

    private void SetModelYRotation(float yDegrees)
    {
        currentModelYRotation = yDegrees;

        if (animator == null) return;
        Vector3 euler = animator.transform.localEulerAngles;
        euler.y = yDegrees;
        animator.transform.localEulerAngles = euler;
    }

    // Achica/agranda SOLO el CapsuleCollider (height/center), nunca el
    // Transform ni la malla visual.
    //
    // Antes esto escalaba transform.localScale.y directamente (dejando X/Z
    // en 1), lo cual hacía DESAPARECER por completo al personaje. Causa
    // real, confirmada leyendo Assets/Quaternius/UAL1_Standard.fbx.meta
    // (animationType: 3 = rig Humanoid): el sistema de retargeting Humanoid
    // de Unity asume escala UNIFORME en el GameObject del Animator (o en
    // cualquiera de sus padres, como este "player"). Con localScale
    // no-uniforme (Y != X/Z) el retargeting colapsa la malla. Confirmado
    // aislando la variable: con crouchHeightScale = 1 (escala uniforme) el
    // bug desaparecía; con 0.5 volvía, sin importar Bounds/Update When
    // Offscreen del SkinnedMeshRenderer (esas pistas eran un callejón sin
    // salida, el problema nunca fue culling).
    //
    // La pose agachada ahora la muestra 100% la animación Crouch_Fwd_Loop;
    // acá solo achicamos el hitbox para que el jugador pueda pasar bajo
    // obstáculos, manteniendo el borde INFERIOR del collider fijo (mismo
    // criterio que antes, pero aplicado al collider en vez de al Transform).
    private IEnumerator ResizeCrouchCollider(float targetHeight)
    {
        float elapsed = 0f;
        float startHeight = capsule.height;
        float startCenterY = capsule.center.y;
        float targetCenterY = originalColliderCenterY + (targetHeight - originalColliderHeight) / 2f;

        while (elapsed < crouchTransitionDuration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / crouchTransitionDuration;

            capsule.height = Mathf.Lerp(startHeight, targetHeight, t);
            Vector3 center = capsule.center;
            center.y = Mathf.Lerp(startCenterY, targetCenterY, t);
            capsule.center = center;

            yield return null;
        }

        capsule.height = targetHeight;
        Vector3 finalCenter = capsule.center;
        finalCenter.y = targetCenterY;
        capsule.center = finalCenter;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("Obstacle")) return;

        // Los choques del carril de OTRO jugador no son asunto de esta
        // máquina: los cuenta su propio dueño, en su propia pantalla. Ver
        // ReportsHits.
        if (!ReportsHits) return;

        float progress01 = DifficultyManager.Instance != null ? DifficultyManager.Instance.Progress01 : 0f;
        float lowObstacleJumpGraceSeconds = Mathf.Lerp(lowObstacleJumpGraceSecondsEarly, lowObstacleJumpGraceSecondsLate, progress01);

        bool isLowObstacle = other.bounds.min.y < lowObstacleHeightThreshold;
        bool recentlyJumped = Time.time - lastJumpStartTime <= lowObstacleJumpGraceSeconds;

        // Perdón de salto: si saltaste hace poco y esto es un obstáculo bajo
        // (se salta, no se agacha), NO cuenta como choque, aunque en este
        // instante exacto ya hayas aterrizado. Ver comentario grande en
        // "Perdón de salto" arriba para el porqué.
        bool dodgedByJumpGrace = isLowObstacle && recentlyJumped;
        if (!dodgedByJumpGrace)
        {
            GameManager.Instance?.RegisterObstacleHit();
        }

        // El obstáculo NO se toca acá a propósito: se queda en su lugar y el
        // jugador lo atraviesa. Antes desaparecía al chocarlo (era la señal
        // visual más simple de "che, pasó algo" cuando todavía no había UI de
        // vidas), pero ya existe el HUD de vidas (ver UIManager) para
        // comunicar el choque, así que hacerlo desaparecer solo rompía la
        // continuidad del escenario.
        //
        // Que se quede NO lo deja colgado ni pierde el pooling: ObstacleSpawner
        // .CleanupBehindPlayer() lo devuelve al pool igual apenas queda
        // despawnDistanceBehind unidades atrás, como a cualquier obstáculo que
        // el jugador esquivó sin tocar.
        //
        // Tampoco puede volver a disparar este choque: OnTriggerEnter avisa
        // una sola vez, al ENTRAR, y el jugador solo avanza (nunca retrocede
        // para volver a entrar al mismo trigger).
    }
}
