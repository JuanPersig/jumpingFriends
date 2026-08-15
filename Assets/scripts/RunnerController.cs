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

    [Header("Reacción al chocar la Barrera")]
    [Tooltip("Nombre EXACTO del estado en 'Player Animator.controller' para la reacción de choque " +
             "de cabeza (clip del pack: 'Hit_Head', dentro de UAL1_Standard.fbx). Solo se dispara con " +
             "la Barrera (obstáculo alto, se choca de cabeza) -- el Log se choca con el cuerpo, no " +
             "aplica. Vacío = no reproduce nada (mismo comportamiento que antes de este cambio).")]
    [SerializeField] private string hitHeadClipName = "Hit_Head";
    [Tooltip("Cuánto se sostiene la pose de choque antes de volver a la animación de correr. No sale " +
             "de ningún otro timer del juego -- ajustalo mirando el clip en Play hasta que se vea bien.")]
    [SerializeField] private float hitHeadHoldSeconds = 0.5f;

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
    // Corrutina de la reacción de choque de cabeza (ver PlayHitHeadReaction).
    // Separada de actionRoutine a propósito: es puramente cosmética (no
    // toca State ni el collider), así que no debería competir por la misma
    // corrutina que sí gobierna el salto/agache real.
    private Coroutine hitHeadRoutine;

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
        // 14/8 con el reporte de que la animación de Hit_Head aparecía
        // segundos tarde: no llegaba tarde, se PERDÍA el choque de esa
        // barrera y disparaba recién con una posterior).
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
    public void SetAnimator(Animator newAnimator)
    {
        animator = newAnimator;
    }

    private void Start()
    {
        // Se suscribe en Start(), no en OnEnable(): Unity garantiza que
        // TODOS los Awake() de la escena (incluido el de PlayerInputProvider,
        // que asigna Instance) corren antes que cualquier Start(). Con
        // OnEnable() eso no está garantizado entre objetos distintos, y es
        // justo lo que hacía que este script se suscribiera a veces y a
        // veces no.
        if (PlayerInputProvider.Instance == null)
        {
            Debug.LogError("[RunnerController] No se encontró PlayerInputProvider.Instance. " +
                            "¿Existe ese componente en la escena?");
            return;
        }
        PlayerInputProvider.Instance.OnJump += HandleJump;
        PlayerInputProvider.Instance.OnCrouch += HandleCrouch;
        PlayerInputProvider.Instance.OnStand += HandleStand;
    }

    private void OnDestroy()
    {
        if (PlayerInputProvider.Instance == null) return;
        PlayerInputProvider.Instance.OnJump -= HandleJump;
        PlayerInputProvider.Instance.OnCrouch -= HandleCrouch;
        PlayerInputProvider.Instance.OnStand -= HandleStand;
    }

    private void Update()
    {
        if (GameManager.Instance != null && GameManager.Instance.IsGameOver) return;
        // Pausado hasta que termine la intro de cámara (ver GameIntroSequence).
        if (GameManager.Instance != null && !GameManager.Instance.HasGameplayStarted) return;
        if (DifficultyManager.Instance == null) return;

        transform.position += Vector3.forward * DifficultyManager.Instance.CurrentSpeed * Time.deltaTime;
    }

    private void HandleJump()
    {
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
    private void PlayAnimation(int clipHash)
    {
        if (animator == null || clipHash == 0) return;
        animator.CrossFadeInFixedTime(clipHash, animationCrossFade);
    }

    // Público para GameIntroSequence: muestra un clip por NOMBRE (no hash
    // precalculado, porque este no es uno de los 3 que ya cachea Awake) --
    // se usa una sola vez al arrancar la partida, así que rehashear acá no
    // importa. El nombre tiene que coincidir con un estado que ya exista en
    // el Animator Controller (agregalo ahí si hace falta, Unity no lo crea solo).
    public void PlayCustomAnimation(string clipName)
    {
        if (animator == null || string.IsNullOrEmpty(clipName)) return;
        animator.CrossFadeInFixedTime(Animator.StringToHash(clipName), animationCrossFade);
    }

    // Público para GameIntroSequence: vuelve a la animación de correr
    // normal al terminar la intro (por si PlayCustomAnimation puso otra
    // distinta, ej. una pose de reposo).
    public void ResumeRunAnimation()
    {
        PlayAnimation(runClipHash);
    }

    // Reacción cosmética al chocar la Barrera de cabeza (ver OnTriggerEnter).
    // A propósito NO toca State/actionRoutine/el collider -- el choque ya se
    // procesó (vida restada, obstáculo devuelto al pool) en OnTriggerEnter;
    // esto es solo la animación. Si el jugador salta o se agacha de verdad
    // mientras dura, HandleJump/HandleCrouch pisan esto solos (mismo
    // CrossFade que cualquier otra transición), no hace falta cancelarlo acá.
    private void PlayHitHeadReaction()
    {
        if (string.IsNullOrEmpty(hitHeadClipName)) return;

        PlayCustomAnimation(hitHeadClipName);

        if (hitHeadRoutine != null) StopCoroutine(hitHeadRoutine);
        hitHeadRoutine = StartCoroutine(ResumeAfterHitHead());
    }

    private IEnumerator ResumeAfterHitHead()
    {
        yield return new WaitForSeconds(hitHeadHoldSeconds);
        hitHeadRoutine = null;

        // Solo si seguimos corriendo normal -- si mientras tanto saltaste o
        // te agachaste de verdad, esa animación ya está puesta y no hay que
        // pisarla con la de correr.
        if (State == PlayerState.Running)
        {
            PlayAnimation(runClipHash);
        }
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

            // Reacción de cabeza SOLO con la Barrera: es el único obstáculo
            // que se choca de cabeza (alto, se agacha para pasar) -- el Log
            // se choca con el cuerpo/piernas (bajo, se salta), no tiene
            // sentido la misma reacción ahí.
            if (!isLowObstacle)
            {
                PlayHitHeadReaction();
            }
        }

        // El obstáculo NO se toca acá a propósito: se queda en su lugar y el
        // jugador lo atraviesa. Antes desaparecía al chocarlo (era la señal
        // visual más simple de "che, pasó algo" cuando todavía no había UI de
        // vidas), pero ya existe el HUD de vidas (ver UIManager) y la reacción
        // de Hit_Head para comunicar el choque, así que hacerlo desaparecer
        // solo rompía la continuidad del escenario.
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
