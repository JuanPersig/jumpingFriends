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

        // Guardamos la transformación LOCAL del modelo placeholder actual
        // (posición/rotación/ESCALA) antes de borrarlo. OJO: Instantiate con
        // el overload (prefab, position, rotation, parent) fija posición y
        // rotación en MUNDO pero NO copia la escala del hijo viejo -- el
        // nuevo modelo queda con la escala de fábrica del prefab, que puede
        // no ser la que se ajustó a mano para este juego (causa real de que
        // el personaje se viera invisible/gigante tras el swap). Repitiendo
        // exactamente los 3 valores locales del placeholder evitamos eso,
        // sin depender de qué escala tenga guardada el prefab.
        Vector3 localPos = Vector3.zero;
        Quaternion localRot = Quaternion.identity;
        Vector3 localScale = Vector3.one;
        if (parent.childCount > 0)
        {
            Transform oldModel = parent.GetChild(0);
            localPos = oldModel.localPosition;
            localRot = oldModel.localRotation;
            localScale = oldModel.localScale;
        }
        else
        {
            Debug.LogWarning("[PlayerCharacterSpawner] No había ningún modelo placeholder " +
                              "de donde copiar posición/rotación/escala -- el personaje nuevo " +
                              "arranca en (0,0,0) escala 1, revisalo si se ve mal.");
        }

        // El modelo placeholder actual es el único hijo esperado acá -- lo
        // sacamos antes de poner el elegido en su lugar.
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            Destroy(parent.GetChild(i).gameObject);
        }

        GameObject newModel = Instantiate(selected.prefab, parent);
        newModel.transform.localPosition = localPos;
        // Corrección propia de ESTE personaje (ver CharacterOption.modelRotationOffset)
        // aplicada encima de la rotación del placeholder anterior -- así un FBX con el
        // eje horneado distinto no queda "acostado" aunque anime bien.
        newModel.transform.localRotation = localRot * Quaternion.Euler(selected.modelRotationOffset);
        newModel.transform.localScale = localScale;

        Animator newAnimator = newModel.GetComponentInChildren<Animator>();
        if (newAnimator == null)
        {
            Debug.LogWarning($"[PlayerCharacterSpawner] '{selected.prefab.name}' no tiene ningún " +
                              "Animator en su jerarquía -- el personaje no va a animar.");
        }
        else
        {
            // Un FBX recién importado trae su Animator con 'Apply Root
            // Motion' TILDADO por defecto -- con eso, cualquier movimiento
            // horneado en la raíz de los clips (Jump_Loop/Sprint_Loop) lo
            // aplica el propio Animator ADEMÁS del movimiento manual que ya
            // hace RunnerController, y el modelo termina girando/derivando
            // solo. RunnerController mueve todo a mano (sin física, sin
            // root motion) por diseño, así que esto siempre va apagado.
            // UAL1_Standard no lo sufre porque en algún momento se lo
            // destildaron directo en el asset.
            newAnimator.applyRootMotion = false;

            if (sharedController != null)
            {
                // Un FBX recién importado trae su propio Animator SIN
                // Controller asignado -- sin esto, el modelo queda
                // congelado en su pose original en vez de correr/saltar/
                // agacharse animado.
                newAnimator.runtimeAnimatorController = sharedController;
            }
        }
        GetComponent<RunnerController>().SetAnimator(newAnimator);
    }
}
