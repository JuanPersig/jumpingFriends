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

    [Header("Agache")]
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
    private Vector3 originalScale;
    private float groundLocalY;
    private float crouchYOffset; // cuánto baja el centro para que los "pies" no floten al achicarse
    private Coroutine actionRoutine;

    private void Awake()
    {
        capsule = GetComponent<CapsuleCollider>();
        originalColliderHeight = capsule.height;
        originalScale = transform.localScale;
        groundLocalY = transform.position.y;

        // El pivot del capsule está en su centro: si solo achicáramos
        // localScale.y, el personaje se encogería para los dos lados (la
        // "cabeza" baja Y los "pies" suben, atravesando el piso). Para que
        // se vea como agacharse de verdad (pies plantados, solo baja la
        // cabeza), además de achicar la escala bajamos el centro la mitad
        // de la altura que se pierde.
        float heightLost = originalColliderHeight * (1f - crouchHeightScale);
        crouchYOffset = -heightLost / 2f;

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
            // Saltando directo desde agachado: restauramos la altura al
            // instante (sin transición) para no perder tiempo, y arrancamos
            // el salto ya mismo.
            Vector3 scale = transform.localScale;
            scale.y = originalScale.y;
            transform.localScale = scale;

            Vector3 pos = transform.position;
            transform.position = new Vector3(pos.x, groundLocalY, pos.z);
        }

        State = PlayerState.Jumping;
        PlayAnimation(jumpClipName);
        RestartRoutine(JumpRoutine());
    }

    private void HandleCrouch()
    {
        Debug.Log("[RunnerController] CROUCH recibido");
        if (State != PlayerState.Running) return;
        State = PlayerState.Crouching;
        PlayAnimation(crouchClipName);
        RestartRoutine(CrouchRoutine());
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

        while (elapsed < jumpDuration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / jumpDuration;
            // Sin(t * PI): sube y baja suave, con el pico en t = 0.5 -> arco de salto.
            float heightOffset = jumpHeight * Mathf.Sin(t * Mathf.PI);
            Vector3 pos = transform.position;
            transform.position = new Vector3(pos.x, groundLocalY + heightOffset, pos.z);
            yield return null;
        }

        Vector3 finalPos = transform.position;
        transform.position = new Vector3(finalPos.x, groundLocalY, finalPos.z);
        State = PlayerState.Running;
        PlayAnimation(runClipName);
    }

    private IEnumerator CrouchRoutine()
    {
        yield return ResizeCrouch(originalScale.y * crouchHeightScale, crouchYOffset);
        // Se queda en Crouching hasta que llegue HandleStand (el jugador
        // se paró de nuevo frente a la cámara).
    }

    private IEnumerator StandRoutine()
    {
        yield return ResizeCrouch(originalScale.y, 0f);
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

    // Achica/agranda al jugador en Y (localScale) y compensa la posición
    // (yOffset, relativo a groundLocalY) para que no flote ni se hunda.
    // OJO: acá NO tocamos el CapsuleCollider a mano — al escalar el
    // transform, Unity escala el collider junto con la malla visual
    // automáticamente. Si además le cambiáramos height/center a mano,
    // se achicaría el doble.
    private IEnumerator ResizeCrouch(float targetScaleY, float targetYOffset)
    {
        float elapsed = 0f;
        float startScaleY = transform.localScale.y;
        float startYOffset = transform.position.y - groundLocalY;

        while (elapsed < crouchTransitionDuration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / crouchTransitionDuration;

            Vector3 scale = transform.localScale;
            scale.y = Mathf.Lerp(startScaleY, targetScaleY, t);
            transform.localScale = scale;

            float yOffset = Mathf.Lerp(startYOffset, targetYOffset, t);
            Vector3 pos = transform.position;
            transform.position = new Vector3(pos.x, groundLocalY + yOffset, pos.z);

            yield return null;
        }

        Vector3 finalScale = transform.localScale;
        finalScale.y = targetScaleY;
        transform.localScale = finalScale;

        Vector3 finalPos = transform.position;
        transform.position = new Vector3(finalPos.x, groundLocalY + targetYOffset, finalPos.z);
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Obstacle"))
        {
            GameManager.Instance?.RegisterObstacleHit();
            // El obstáculo desaparece al chocarlo: es la señal visual más
            // simple de "che, pasó algo" sin necesitar todavía una UI de
            // vidas/daño (eso lo conectamos en la Fase 4, con el resto del
            // sistema de menús reutilizable).
            Destroy(other.gameObject);
        }
    }
}
