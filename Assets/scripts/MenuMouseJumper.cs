using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

// Personaje decorativo del menú principal: salta en loop para siempre.
// La ALTURA de cada salto sale de la posición del mouse en pantalla al
// arrancar ese salto (mouse arriba = salto alto; abajo = salto ~0). La
// DURACIÓN no es un número aparte: se calcula a partir de la altura,
// usando la física real de un tiro vertical (ver Jump()) — así un salto
// alto automáticamente dura más, en la proporción justa, y nunca sale la
// combinación fea de "salto altísimo en un instante".
//
// Animación: se deja sostenida la pose de Jump_Loop todo el tiempo (no
// alterna con Sprint_Loop) porque, con el movimiento en loop encadenando
// un salto atrás del otro sin pausa, el personaje nunca llega a "asentarse"
// de pie el tiempo suficiente como para que se note una pose de reposo —
// siempre está en el aire, así que tiene sentido dejarlo con la pose de
// salto todo el rato. Ojo: Player Animator.controller solo tiene armados
// Sprint_Loop / Jump_Loop / Crouch_Fwd_Loop (no Jump_Start ni Jump_Land),
// así que nos quedamos con lo que existe de verdad.
public class MenuMouseJumper : MonoBehaviour
{
    [Tooltip("Única conversión necesaria entre píxeles de pantalla y unidades del mundo: " +
             "altura (unidades del mundo) del salto cuando el mouse está arriba del todo de la pantalla.")]
    [SerializeField] private float heightAtScreenTop = 1.5f;

    [Tooltip("'Gravedad' del salto: más alta = saltos más cortos/bruscos; más baja = más " +
             "largos/flotantes. De acá sale la duración de cada salto (no es un valor aparte).")]
    [SerializeField] private float gravity = 9.8f;

    [Header("Animación (opcional)")]
    [Tooltip("Opcional: si lo asignás, reproduce esta animación en loop, sostenida, todo el tiempo. " +
             "Si hay un personaje elegido en el menú (ver 'Personaje' abajo), esta referencia se " +
             "reemplaza sola por el Animator del modelo elegido -- dejalo asignado igual, es el " +
             "fallback si no hay ninguna selección.")]
    [SerializeField] private Animator animator;
    [SerializeField] private string jumpClipName = "Jump_Loop";

    [Header("Personaje (menú)")]
    [Tooltip("Dónde tiene que aparecer el modelo del personaje elegido en CharacterSelection. " +
             "Dejalo vacío para usar este mismo GameObject (el 'Jumper') como padre -- es lo " +
             "normal si el modelo placeholder actual ya es hijo directo de 'Jumper'. Este mismo " +
             "objeto es el único preview de personaje del menú: no hay pedestal aparte.")]
    [SerializeField] private Transform modelParent;
    [Tooltip("Animator Controller compartido por todos los personajes (arrastrá acá " +
             "'Player Animator.controller'). OJO: un FBX recién importado trae su propio " +
             "Animator SIN ningún Controller asignado -- si no lo ponemos a mano en cada " +
             "swap, el modelo nuevo se queda congelado en su pose original (sin animar). " +
             "UAL1_Standard 'funciona solo' porque en algún momento se le asignó el " +
             "Controller directo al asset; los personajes nuevos no.")]
    [SerializeField] private RuntimeAnimatorController sharedController;

    // Piso de duración: sin esto, un salto con altura 0 (mouse pegado abajo
    // de la pantalla) daría duración 0 -> la corrutina nunca llegaría a
    // "yield"-ear ni una vez -> Unity se cuelga en un loop infinito dentro
    // de un mismo frame. Con este piso, SIEMPRE hay al menos un frame de
    // por medio entre un salto y el siguiente, pase lo que pase.
    private const float MinJumpDuration = 0.05f;

    private float groundY;
    private int jumpClipHash;

    // Transformación LOCAL "neutral" del modelo, capturada UNA sola vez (ver
    // RefreshCharacter) y reutilizada como base fija en cada swap -- sin
    // esto, cada swap tomaba como base el modelo ANTERIOR (que ya tenía
    // aplicado SU PROPIO modelRotationOffset), sumando el offset del
    // personaje nuevo encima -- el resultado se iba desplazando/girando
    // más con cada click de Siguiente/Anterior en vez de partir siempre
    // del mismo punto de referencia.
    private Vector3 baseLocalPos;
    private Quaternion baseLocalRot;
    private Vector3 baseLocalScale;
    private bool baseTransformCaptured;

    private void Awake()
    {
        groundY = transform.position.y;
        jumpClipHash = string.IsNullOrEmpty(jumpClipName) ? 0 : Animator.StringToHash(jumpClipName);
    }

    private void Start()
    {
        // Se aplica ANTES de arrancar el loop de salto, así el personaje
        // correcto ya está puesto (modelo + Animator) desde el primer salto.
        RefreshCharacter();
        StartCoroutine(JumpLoop());
    }

    // Público: MenuController lo llama de nuevo cada vez que el jugador
    // cambia de personaje con las flechas de "Elegir Personaje". Reemplaza
    // el modelo hijo actual por el que esté seleccionado en
    // CharacterSelection.Instance, y re-engancha el Animator para que el
    // salto siga animando con el modelo nuevo.
    //
    // Sin selección (CharacterSelection.Instance null, o sin prefab
    // asignado) -> no toca nada, se queda con el modelo/Animator puestos a
    // mano en el Inspector.
    public void RefreshCharacter()
    {
        CharacterSelection.CharacterOption selected =
            CharacterSelection.Instance != null ? CharacterSelection.Instance.Selected : null;

        if (selected == null || selected.prefab == null) return;

        Transform parent = modelParent != null ? modelParent : transform;

        // Capturamos la transformación LOCAL "neutral" del modelo SOLO la
        // primera vez que esto corre (antes de cualquier swap) -- a partir
        // de ahí queda fija para siempre, sin importar qué personaje haya
        // estado puesto la vez anterior. Ver comentario del campo arriba.
        if (!baseTransformCaptured)
        {
            if (parent.childCount > 0)
            {
                Transform placeholder = parent.GetChild(0);
                baseLocalPos = placeholder.localPosition;
                baseLocalRot = placeholder.localRotation;
                baseLocalScale = placeholder.localScale;
            }
            else
            {
                baseLocalPos = Vector3.zero;
                baseLocalRot = Quaternion.identity;
                baseLocalScale = Vector3.one;
            }
            baseTransformCaptured = true;
        }

        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            Destroy(parent.GetChild(i).gameObject);
        }

        GameObject newModel = Instantiate(selected.prefab, parent);
        newModel.transform.localPosition = baseLocalPos;
        // Corrección propia de ESTE personaje (ver CharacterOption.modelRotationOffset)
        // aplicada sobre la base NEUTRAL fija -- así un FBX con el eje horneado
        // distinto no queda "acostado" aunque anime bien, y cambiar de
        // personaje varias veces seguidas no acumula rotación de más.
        newModel.transform.localRotation = baseLocalRot * Quaternion.Euler(selected.modelRotationOffset);
        newModel.transform.localScale = baseLocalScale;

        animator = newModel.GetComponentInChildren<Animator>();
        if (animator == null)
        {
            Debug.LogWarning($"[MenuMouseJumper] '{selected.prefab.name}' no tiene ningún " +
                              "Animator en su jerarquía -- el Jumper no va a animar.");
        }
        else
        {
            // Un FBX recién importado trae su Animator con 'Apply Root
            // Motion' TILDADO por defecto -- acá el Jumper también mueve su
            // Y a mano (Jump()), así que cualquier movimiento horneado en
            // la raíz del clip se sumaría solo y lo haría girar/derivar.
            animator.applyRootMotion = false;

            // Un FBX recién importado trae su propio Animator SIN Controller
            // asignado -- sin esto, el modelo queda congelado en su pose
            // original (nada lo anima) en vez de pararse en Jump_Loop.
            if (sharedController != null) animator.runtimeAnimatorController = sharedController;

            if (jumpClipHash != 0)
            {
                // Re-aplicamos el estado de salto al toque: si este refresh
                // pasa a mitad de partida (cambiaste de personaje en el
                // menú), el Animator nuevo arranca en su pose por defecto
                // hasta que se lo pidamos explícito acá.
                animator.CrossFadeInFixedTime(jumpClipHash, 0.1f);
            }
        }
    }

    // Un salto atrás del otro, para siempre.
    private IEnumerator JumpLoop()
    {
        // Un frame de por medio antes de pedirle al Animator que cambie de
        // estado: pedírselo directo en Start() no siempre pega (el propio
        // Animator todavía se está asentando en su estado por defecto en
        // ese instante exacto, y a veces pisa el pedido).
        yield return null;
        if (animator != null && jumpClipHash != 0)
        {
            animator.CrossFadeInFixedTime(jumpClipHash, 0.1f);
        }

        while (true)
        {
            yield return Jump();
        }
    }

    // Un solo salto: sube y baja (curva de seno), con la altura Y la
    // duración fijadas al arrancar ESTE salto puntual, calculadas ambas a
    // partir de dónde está el mouse en ese instante.
    private IEnumerator Jump()
    {
        float peakHeight = GetMouseHeight01() * heightAtScreenTop;

        // Tiempo de vuelo de un tiro vertical bajo gravedad constante:
        // v0 = sqrt(2 * g * h) (velocidad inicial necesaria para llegar a
        // esa altura), y t = 2 * v0 / g (tiempo de subida + bajada).
        // Simplificado: t = 2 * sqrt(2h / g).
        float duration = Mathf.Max(MinJumpDuration, 2f * Mathf.Sqrt(2f * peakHeight / gravity));

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / duration;
            float heightOffset = peakHeight * Mathf.Sin(t * Mathf.PI);

            Vector3 pos = transform.position;
            pos.y = groundY + heightOffset;
            transform.position = pos;

            yield return null;
        }

        Vector3 finalPos = transform.position;
        finalPos.y = groundY;
        transform.position = finalPos;
    }

    // 0 = mouse abajo del todo de la pantalla, 1 = arriba del todo.
    private float GetMouseHeight01()
    {
        if (Mouse.current == null) return 0f;
        return Mathf.Clamp01(Mouse.current.position.ReadValue().y / Screen.height);
    }
}
