using System.Collections;
using UnityEngine;

// Mueve al jugador hacia adelante automáticamente (estilo endless runner) y
// reacciona a los eventos de PlayerInputProvider para saltar/agacharse.
// Igual que TestCubeReactor en la Fase 0: este script NO sabe nada de
// cámara, UDP, ni Python. Solo escucha OnJump / OnCrouch / OnStand.
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
    [Tooltip("Si el jugador saltó hace menos de este tiempo, un obstáculo cuyo " +
             "borde inferior esté por debajo de Low Obstacle Height Threshold " +
             "no le hace perder vida, aunque ya haya aterrizado del salto.")]
    [SerializeField] private float lowObstacleJumpGraceSeconds = 1.2f;
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

    [Header("Animación")]
    [Tooltip("Opcional: si no se asigna, el personaje se mueve igual, solo que sin animar.")]
    [SerializeField] private Animator animator;
    [SerializeField] private string runClipName = "Jog_Fwd_Loop";
    [SerializeField] private string jumpClipName = "Jump_Loop";
    [SerializeField] private string crouchClipName = "Crouch_Fwd_Loop";
    [SerializeField] private float animationCrossFade = 0.1f;

    public PlayerState State { get; private set; } = PlayerState.Running;

    private CapsuleCollider capsule;
    private float originalColliderHeight;
    private float originalColliderCenterY;
    private float groundLocalY;
    private Coroutine actionRoutine;
    private float lastJumpStartTime = -999f;

    private void Awake()
    {
        capsule = GetComponent<CapsuleCollider>();
        originalColliderHeight = capsule.height;
        originalColliderCenterY = capsule.center.y;
        groundLocalY = transform.position.y;

        // Rigidbody kinemático: lo necesitamos para que OnTriggerEnter
        // funcione de forma confiable, pero movemos al jugador a mano
        // (transform), no con física real, porque el salto tiene un arco
        // fijo disparado por un evento binario, no por gravedad continua.
        Rigidbody rb = GetComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;
    }

    private void Start()
    {
        // Se suscribe en Start(), no en OnEnable(): Unity garantiza que
        // TODOS los Awake() de la escena (incluido el de PlayerInputProvider,
        // que asigna Instance) corren antes que cualquier Start(). Con
        // OnEnable() eso no está garantizado entre objetos distintos, y es
        // justo lo que hacía que este script se suscribiera a veces y a
        // veces no (mismo bug que ya habíamos arreglado en TestCubeReactor).
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
        if (DifficultyManager.Instance == null) return;

        transform.position += Vector3.forward * DifficultyManager.Instance.CurrentSpeed * Time.deltaTime;
    }

    private void HandleJump()
    {
        Debug.Log("[RunnerController] JUMP recibido");
        // Solo ignoramos el salto si YA está en el aire (para no reiniciar
        // el arco a mitad de camino). Antes también lo ignorábamos estando
        // agachado, obligando a pasar por un instante "de pie" en el medio
        // -algo que en la vida real pasa muy rápido y la detección no
        // siempre llega a registrar- y eso comía tiempo de reacción real.
        if (State == PlayerState.Jumping) return;

        if (State == PlayerState.Crouching)
        {
            // Saltando directo desde agachado: restauramos el hitbox al
            // instante (sin transición) para no perder tiempo, y arrancamos
            // el salto ya mismo. Ya no hay Transform que restaurar (el
            // agache no toca el Transform, solo el CapsuleCollider).
            capsule.height = originalColliderHeight;
            Vector3 center = capsule.center;
            center.y = originalColliderCenterY;
            capsule.center = center;
        }

        State = PlayerState.Jumping;
        lastJumpStartTime = Time.time;
        PlayAnimation(jumpClipName);
        RestartRoutine(JumpRoutine());
    }

    private void HandleCrouch()
    {
        Debug.Log("[RunnerController] CROUCH recibido");
        if (State != PlayerState.Running) return;
        State = PlayerState.Crouching;
        PlayAnimation(crouchClipName);
        RestartRoutine(ResizeCrouchCollider(originalColliderHeight * crouchHeightScale));
    }

    private void HandleStand()
    {
        Debug.Log("[RunnerController] STAND recibido");
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
        PlayAnimation(runClipName);
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
        PlayAnimation(runClipName);
    }

    // CrossFadeInFixedTime (no CrossFade a secas) porque los clips de este
    // pack están pensados en tiempo real/frames, no normalizado 0-1 — así
    // el tiempo de mezcla (animationCrossFade) se respeta igual sin
    // importar cuán largo sea cada clip.
    private void PlayAnimation(string clipName)
    {
        if (animator == null || string.IsNullOrEmpty(clipName)) return;
        animator.CrossFadeInFixedTime(clipName, animationCrossFade);
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

        bool isLowObstacle = other.bounds.min.y < lowObstacleHeightThreshold;
        bool recentlyJumped = Time.time - lastJumpStartTime <= lowObstacleJumpGraceSeconds;

        if (isLowObstacle && recentlyJumped)
        {
            // Perdón de salto: saltaste hace poco y esto es un obstáculo
            // bajo (se salta, no se agacha) -> no cuenta como choque, aunque
            // en este instante exacto ya hayas aterrizado. Ver comentario
            // grande en "Perdón de salto" arriba para el porqué.
            Debug.Log($"[RunnerController] '{other.name}' esquivado por perdón de salto " +
                      $"(saltaste hace {Time.time - lastJumpStartTime:F2}s)");
        }
        else
        {
            GameManager.Instance?.RegisterObstacleHit();
        }

        // El obstáculo desaparece al chocarlo (o al esquivarlo): es la señal
        // visual más simple de "che, pasó algo" sin necesitar todavía una UI
        // de vidas/daño (eso lo conectamos en la Fase 4, con el resto del
        // sistema de menús reutilizable).
        Destroy(other.gameObject);
    }
}
