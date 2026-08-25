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

    [Header("Altura de salto: mouse vs. fija")]
    [Tooltip("Tildado (default, comportamiento de siempre): la altura de CADA salto sale del " +
             "mouse LOCAL de este cliente (ver Height At Screen Top) -- correcto para TU propio " +
             "personaje (el Jumper del menú principal, o tu propio slot en la Sala de Espera). " +
             "Destildalo para los Jumpers de OTROS jugadores en la Sala de Espera (ver " +
             "LobbyJumperSpawner.SetUseMouseForHeight) -- no tiene sentido que el personaje de " +
             "otra persona salte copiando TU mouse; en ese caso usa Fixed Jump Height de abajo.")]
    [SerializeField] private bool useMouseForHeight = true;
    [Tooltip("Altura de salto (unidades del mundo, mismo rango que Height At Screen Top) cuando " +
             "'Use Mouse For Height' está destildado -- fija, no depende de ningún mouse.")]
    [SerializeField] private float fixedJumpHeight = 1f;

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
    [Tooltip("Tildado (default): al arrancar, se pone solo el personaje ELEGIDO LOCALMENTE " +
             "(CharacterSelection.Instance.Selected) -- es lo que ya hacía este script siempre. " +
             "Destildalo para los Jumpers que arma LobbyJumperSpawner en la Sala de Espera: esos " +
             "representan a OTRO jugador de la sala, así que no tienen que arrancar mostrando tu " +
             "propia selección -- RefreshCharacter(int) los va a poner apenas se instancian.")]
    [SerializeField] private bool autoRefreshOnStart = true;
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
        // Si autoRefreshOnStart está apagado (Jumpers de LobbyJumperSpawner),
        // nos quedamos sin modelo hasta que llamen a RefreshCharacter(int)
        // a mano -- el loop de salto arranca igual, no depende de esto.
        if (autoRefreshOnStart) RefreshCharacter();
    }

    // El loop de salto arranca acá, no en Start(): Start() corre UNA sola
    // vez en toda la vida del objeto, pero OnEnable() corre cada vez que se
    // reactiva con SetActive(true) -- que es justo lo que hace
    // LobbyJumperSpawner con el Jumper decorativo del menú (lo apaga al
    // entrar a la Sala de Espera, lo prende de nuevo al salir). Con el loop
    // solo en Start(), al reactivarlo la corrutina (cortada sola al
    // desactivarse) nunca se reiniciaba, y el Animator quedaba pegado en el
    // estado por defecto del controller (Sprint_Loop / corriendo) en vez de
    // volver a Jump_Loop.
    private void OnEnable()
    {
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

        ApplyCharacter(selected);
    }

    // Igual que RefreshCharacter(), pero mostrando un personaje EXPLÍCITO
    // (por índice en CharacterSelection.characters) en vez del elegido
    // localmente -- lo usa LobbyJumperSpawner para representar a otro
    // jugador de la sala, cuyo personaje sincroniza LobbyPlayerListSync.
    public void RefreshCharacter(int characterIndex)
    {
        CharacterSelection.CharacterOption selected =
            CharacterSelection.Instance != null ? CharacterSelection.Instance.Get(characterIndex) : null;

        ApplyCharacter(selected);
    }

    // Público para LobbyJumperSpawner: decide si ESTE Jumper puntual sigue
    // el mouse local (true, el de siempre) o salta con Fixed Jump Height
    // (false) -- ver el comentario grande del campo useMouseForHeight.
    // Llamalo UNA vez, apenas se instancia (no cambia solo con el tiempo).
    public void SetUseMouseForHeight(bool useMouse)
    {
        useMouseForHeight = useMouse;
    }

    // Público para LobbyJumperSpawner: sobreescribe Fixed Jump Height para
    // ESTA instancia puntual -- así cada slot de la Sala de Espera puede
    // saltar a una altura distinta (ver LobbyJumperSpawner.slotFixedJumpHeights)
    // en vez de que todos los Jumpers ajenos salten parejo a la misma altura
    // del Inspector del template. Sin efecto mientras useMouseForHeight esté
    // en true (ese caso ignora fixedJumpHeight por completo, ver Jump()).
    public void SetFixedJumpHeight(float height)
    {
        fixedJumpHeight = height;
    }

    private void ApplyCharacter(CharacterSelection.CharacterOption selected)
    {
        if (selected == null || selected.prefab == null) return;

        Transform parent = modelParent != null ? modelParent : transform;

        // Capturamos la transformación LOCAL "neutral" del modelo SOLO la
        // primera vez que esto corre (antes de cualquier swap) -- a partir
        // de ahí queda fija para siempre, sin importar qué personaje haya
        // estado puesto la vez anterior. Ver comentario del campo arriba.
        if (!baseTransformCaptured)
        {
            CharacterModelSwapper.LocalTransform captured = CharacterModelSwapper.LocalTransform.FromFirstChildOrIdentity(parent);
            baseLocalPos = captured.Position;
            baseLocalRot = captured.Rotation;
            baseLocalScale = captured.Scale;
            baseTransformCaptured = true;
        }

        // Swap de modelo + Animator compartido con PlayerCharacterSpawner
        // (mismos gotchas de FBX) -- ver CharacterModelSwapper.
        animator = CharacterModelSwapper.Swap(
            parent,
            selected,
            new CharacterModelSwapper.LocalTransform(baseLocalPos, baseLocalRot, baseLocalScale),
            sharedController,
            "MenuMouseJumper");

        if (animator != null && jumpClipHash != 0)
        {
            // Re-aplicamos el estado de salto al toque: si este refresh pasa
            // a mitad de partida (cambiaste de personaje en el menú), el
            // Animator nuevo arranca en su pose por defecto hasta que se lo
            // pidamos explícito acá.
            animator.CrossFadeInFixedTime(jumpClipHash, 0.1f);
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
    // duración fijadas al arrancar ESTE salto puntual. La altura sale del
    // mouse local (comportamiento de siempre) SOLO si useMouseForHeight
    // está tildado -- si no, usa fixedJumpHeight tal cual (ver
    // SetUseMouseForHeight, que lo destilda para los Jumpers de otros
    // jugadores en la Sala de Espera).
    private IEnumerator Jump()
    {
        float peakHeight = useMouseForHeight ? GetMouseHeight01() * heightAtScreenTop : fixedJumpHeight;

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
