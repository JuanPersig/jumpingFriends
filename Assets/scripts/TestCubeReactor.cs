using System.Collections;
using UnityEngine;

// Solo sirve para validar visualmente el circuito completo
// (webcam -> Python -> UDP -> Unity). No es parte del juego final.
public class TestCubeReactor : MonoBehaviour
{
    [Header("Referencias")]
    [SerializeField] private Transform cubeTransform;

    [Header("Salto")]
    [SerializeField] private float jumpHeight = 1.5f;
    [SerializeField] private float jumpDuration = 0.3f;

    [Header("Agache")]
    [SerializeField] private float crouchScaleY = 0.5f;
    [SerializeField] private float crouchDuration = 0.2f;

    private Vector3 originalPosition;
    private Vector3 originalScale;
    private Coroutine currentRoutine;

    // Un salto real dispara OnJump y, muy poco después (a veces <300ms),
    // OnStand al aterrizar. Si dejamos que OnStand corte JumpRoutine a
    // mitad de camino, el cubo apenas se mueve antes de volver a bajar —
    // se ve como si "el salto no funcionara" aunque el evento sí llegó.
    // Esta bandera evita que OnStand interrumpa un salto en curso:
    // JumpRoutine ya se encarga de dejar al cubo en originalPosition
    // al terminar, así que no hace falta que StandRoutine lo haga por él.
    private bool isJumping = false;

    private void Awake()
    {
        if (cubeTransform == null) cubeTransform = transform;
        originalPosition = cubeTransform.position;
        originalScale = cubeTransform.localScale;
    }

    private void Start()
    {
        // Se suscribe en Start() y no en OnEnable(): Unity garantiza que
        // TODOS los Awake() de la escena corren antes que cualquier Start(),
        // así que PlayerInputProvider.Instance ya está asignado para acá.
        // Con OnEnable() esto no está garantizado entre objetos distintos.
        if (PlayerInputProvider.Instance == null)
        {
            Debug.LogError("[TestCubeReactor] No se encontró PlayerInputProvider.Instance. " +
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

    private void HandleJump()
    {
        Debug.Log("[TestCubeReactor] JUMP recibido");
        RestartRoutine(JumpRoutine());
    }

    private void HandleCrouch()
    {
        Debug.Log("[TestCubeReactor] CROUCH recibido");
        RestartRoutine(CrouchRoutine());
    }

    private void HandleStand()
    {
        Debug.Log("[TestCubeReactor] STAND recibido");
        if (isJumping)
        {
            // Dejamos que JumpRoutine termine su propia animación de
            // caída en vez de cortarla acá.
            return;
        }
        RestartRoutine(StandRoutine());
    }

    private void RestartRoutine(IEnumerator routine)
    {
        if (currentRoutine != null) StopCoroutine(currentRoutine);
        // Si estábamos a mitad de un salto y se lo interrumpe con otra
        // rutina (ej: llega un CROUCH), la corrutina se corta a la fuerza
        // y su "isJumping = false" del final nunca llega a ejecutarse.
        // Lo reseteamos acá, en el único punto por donde pasa cualquier
        // cambio de rutina, para que no quede trabado en true.
        isJumping = false;
        currentRoutine = StartCoroutine(routine);
    }

    private IEnumerator JumpRoutine()
    {
        isJumping = true;
        float elapsed = 0f;
        Vector3 startPos = cubeTransform.position;
        Vector3 peakPos = originalPosition + Vector3.up * jumpHeight;

        while (elapsed < jumpDuration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / jumpDuration;
            cubeTransform.position = Vector3.Lerp(startPos, peakPos, Mathf.Sin(t * Mathf.PI));
            yield return null;
        }
        cubeTransform.position = originalPosition;
        isJumping = false;
    }

    private IEnumerator CrouchRoutine()
    {
        float elapsed = 0f;
        Vector3 startScale = cubeTransform.localScale;
        Vector3 targetScale = new Vector3(originalScale.x, originalScale.y * crouchScaleY, originalScale.z);

        while (elapsed < crouchDuration)
        {
            elapsed += Time.deltaTime;
            cubeTransform.localScale = Vector3.Lerp(startScale, targetScale, elapsed / crouchDuration);
            yield return null;
        }
        cubeTransform.localScale = targetScale;
    }

    private IEnumerator StandRoutine()
    {
        float elapsed = 0f;
        Vector3 startScale = cubeTransform.localScale;
        Vector3 startPos = cubeTransform.position;

        while (elapsed < crouchDuration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / crouchDuration;
            cubeTransform.localScale = Vector3.Lerp(startScale, originalScale, t);
            cubeTransform.position = Vector3.Lerp(startPos, originalPosition, t);
            yield return null;
        }
        cubeTransform.localScale = originalScale;
        cubeTransform.position = originalPosition;
    }
}
