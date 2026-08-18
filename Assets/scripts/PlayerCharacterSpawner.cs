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

    private void Awake()
    {
        CharacterSelection.CharacterOption selected =
            CharacterSelection.Instance != null ? CharacterSelection.Instance.Selected : null;

        // Sin selección (o sin prefab asignado) -> nos quedamos con el
        // modelo y el Animator que ya estén puestos a mano en el Inspector.
        if (selected == null || selected.prefab == null) return;

        Transform parent = modelParent != null ? modelParent : transform;

        // OJO: Instantiate con el overload (prefab, position, rotation,
        // parent) fija posición y rotación en MUNDO pero NO copia la escala
        // del hijo viejo -- el nuevo modelo queda con la escala de fábrica
        // del prefab, que puede no ser la que se ajustó a mano para este
        // juego (causa real de que el personaje se viera invisible/gigante
        // tras el swap). Por eso se copia a mano la transformación LOCAL
        // completa del placeholder actual (ver CharacterModelSwapper).
        if (parent.childCount == 0)
        {
            Debug.LogWarning("[PlayerCharacterSpawner] No había ningún modelo placeholder " +
                              "de donde copiar posición/rotación/escala -- el personaje nuevo " +
                              "arranca en (0,0,0) escala 1, revisalo si se ve mal.");
        }
        CharacterModelSwapper.LocalTransform baseTransform = CharacterModelSwapper.LocalTransform.FromFirstChildOrIdentity(parent);

        // Swap de modelo + Animator compartido con MenuMouseJumper (mismos
        // gotchas de FBX: root motion, Controller sin asignar) -- ver
        // CharacterModelSwapper. RunnerController mueve todo a mano (sin
        // física, sin root motion) por diseño, de ahí que el swap siempre
        // apague applyRootMotion.
        Animator newAnimator = CharacterModelSwapper.Swap(parent, selected, baseTransform, sharedController, "PlayerCharacterSpawner");
        GetComponent<RunnerController>().SetAnimator(newAnimator);
    }
}
