using UnityEngine;

// Al arrancar SampleScene, reemplaza el modelo visual placeholder del
// jugador por el personaje elegido en el menú
// (CharacterSelection.Instance.Selected).
//
// Si no hay ninguna selección (ej: abriste SampleScene directo desde el
// Editor, sin pasar por MainMenu -- CharacterSelection.Instance es null en
// ese caso), este script no toca nada: se queda el modelo que ya esté
// puesto a mano en la escena, para poder seguir probando SampleScene
// suelta sin pasar por el menú cada vez.
//
// El CapsuleCollider/Rigidbody del salto y el agache viven en el GameObject
// "player" (el padre), no en el modelo visual -- así que reemplazar el
// modelo hijo no afecta nada del gameplay/física, solo lo que se ve.
[RequireComponent(typeof(RunnerController))]
public class PlayerCharacterSpawner : MonoBehaviour
{
    [Tooltip("Dónde tiene que aparecer el modelo del personaje. Dejalo vacío " +
             "para usar este mismo GameObject (el 'player') como padre -- es " +
             "lo normal si el modelo placeholder actual (ej. UAL1_Standard) " +
             "ya es hijo directo de 'player'.")]
    [SerializeField] private Transform modelParent;
    [Tooltip("Animator Controller compartido por todos los personajes (arrastrá acá " +
             "'Player Animator.controller'). OJO: un FBX recién importado trae su propio " +
             "Animator SIN ningún Controller asignado -- si no lo ponemos a mano en cada " +
             "swap, el modelo nuevo se queda congelado en su pose original (sin animar). " +
             "UAL1_Standard 'funciona solo' porque en algún momento se le asignó el " +
             "Controller directo al asset; los personajes nuevos no.")]
    [SerializeField] private RuntimeAnimatorController sharedController;

    private Transform ModelParent => modelParent != null ? modelParent : transform;

    // Transformación LOCAL del placeholder ORIGINAL de la escena. Se captura
    // una sola vez, en Awake, y se reusa en cada swap.
    //
    // Capturarla una sola vez NO es un detalle de optimización, es
    // obligatorio: CharacterModelSwapper aplica el modelRotationOffset propio
    // de cada personaje SOBRE esta base. Si la releyéramos del modelo ya
    // swapeado, el offset del personaje anterior quedaría horneado en la base
    // y el siguiente sumaría el suyo encima -- la rotación se iría acumulando
    // swap a swap. Con el multijugador esto pasó a importar de verdad: ahora
    // cada slot puede swapear DOS veces (el personaje local en Awake, y el
    // del dueño real cuando llega por red).
    private CharacterModelSwapper.LocalTransform baseTransform;

    private void Awake()
    {
        // OJO: Instantiate con el overload (prefab, position, rotation,
        // parent) fija posición y rotación en MUNDO pero NO copia la escala
        // del hijo viejo -- el nuevo modelo queda con la escala de fábrica
        // del prefab, que puede no ser la que se ajustó a mano para este
        // juego (causa real de que el personaje se viera invisible/gigante
        // tras el swap). Por eso se copia a mano la transformación LOCAL
        // completa del placeholder actual (ver CharacterModelSwapper).
        if (ModelParent.childCount == 0)
        {
            Debug.LogWarning("[PlayerCharacterSpawner] No había ningún modelo placeholder " +
                              "de donde copiar posición/rotación/escala -- el personaje nuevo " +
                              "arranca en (0,0,0) escala 1, revisalo si se ve mal.");
        }
        baseTransform = CharacterModelSwapper.LocalTransform.FromFirstChildOrIdentity(ModelParent);

        // Personaje elegido LOCALMENTE, como siempre. En una partida en red
        // esto es solo provisorio: PlayerSlot vuelve a llamar a
        // ApplyCharacter() con el personaje del dueño REAL de este carril
        // apenas llega por red. Los dos swaps pasan tapados por la pantalla
        // negra, así que no se ve ningún cambio.
        //
        // Se mantiene igual porque es el único camino cuando NO hay red
        // (Gameplay.unity abierta suelta desde el Editor): ahí nadie va a
        // llamar a ApplyCharacter() nunca.
        if (CharacterSelection.Instance != null)
        {
            ApplyCharacter(CharacterSelection.Instance.SelectedIndex);
        }
    }

    // Público para PlayerSlot: pone en ESTE carril el personaje que eligió su
    // dueño de verdad. Antes cada máquina le ponía a todos los carriles el
    // personaje elegido localmente -- de ahí que se viera el mismo skin
    // duplicado en las dos pantallas.
    public void ApplyCharacter(int characterIndex)
    {
        if (CharacterSelection.Instance == null) return;

        CharacterSelection.CharacterOption selected = CharacterSelection.Instance.Get(characterIndex);

        // Sin selección (o sin prefab asignado) -> nos quedamos con el
        // modelo y el Animator que ya estén puestos a mano en el Inspector.
        if (selected == null || selected.prefab == null) return;

        // Swap de modelo + Animator compartido con MenuMouseJumper (mismos
        // gotchas de FBX: root motion, Controller sin asignar) -- ver
        // CharacterModelSwapper. RunnerController mueve todo a mano (sin
        // física, sin root motion) por diseño, de ahí que el swap siempre
        // apague applyRootMotion.
        Animator newAnimator = CharacterModelSwapper.Swap(ModelParent, selected, baseTransform, sharedController, "PlayerCharacterSpawner");
        GetComponent<RunnerController>().SetAnimator(newAnimator);
    }
}
