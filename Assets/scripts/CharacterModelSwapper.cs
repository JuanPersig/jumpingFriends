using UnityEngine;

// Compartido por MenuMouseJumper (preview del menú) y PlayerCharacterSpawner
// (juego real): los dos hacían el mismo swap de modelo de personaje --
// destruir el modelo hijo actual, instanciar el prefab elegido en
// CharacterSelection, copiar la transformación LOCAL (posición/rotación/
// escala) del placeholder anterior + el modelRotationOffset propio de ESE
// personaje, y re-enganchar el Animator -- letra por letra salvo detalles
// puntuales de cada llamador (qué hacer con el Animator resultante). Los
// gotchas de FBX documentados acá (Instantiate no copia escala, Apply Root
// Motion tildado por defecto, Animator sin Controller) son los mismos para
// los dos casos -- un fix acá se aplica una sola vez para ambos.
public static class CharacterModelSwapper
{
    public readonly struct LocalTransform
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly Vector3 Scale;

        public LocalTransform(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            Position = position;
            Rotation = rotation;
            Scale = scale;
        }

        public static LocalTransform Identity =>
            new LocalTransform(Vector3.zero, Quaternion.identity, Vector3.one);

        // Lee la transformación LOCAL del primer hijo de `parent` (el
        // placeholder actual) para usarla de base en el próximo swap, o
        // Identity si no hay ningún hijo.
        public static LocalTransform FromFirstChildOrIdentity(Transform parent)
        {
            if (parent.childCount == 0) return Identity;
            Transform child = parent.GetChild(0);
            return new LocalTransform(child.localPosition, child.localRotation, child.localScale);
        }
    }

    // Destruye TODOS los hijos actuales de `parent`, instancia
    // `selected.prefab` en su lugar aplicando `baseTransform` (+ el
    // modelRotationOffset propio de `selected`), y re-engancha el Animator
    // (Apply Root Motion apagado; Controller compartido asignado si se pasó
    // uno). Devuelve el Animator encontrado, o null si el prefab no trae
    // ninguno en su jerarquía (ya se loguea la advertencia acá, con
    // `logTag` identificando a quién avisar).
    public static Animator Swap(
        Transform parent,
        CharacterSelection.CharacterOption selected,
        LocalTransform baseTransform,
        RuntimeAnimatorController sharedController,
        string logTag)
    {
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            Object.Destroy(parent.GetChild(i).gameObject);
        }

        GameObject newModel = Object.Instantiate(selected.prefab, parent);
        newModel.transform.localPosition = baseTransform.Position;
        // Corrección propia de ESTE personaje (ver CharacterOption.modelRotationOffset)
        // aplicada sobre la base fija -- así un FBX con el eje horneado distinto
        // no queda "acostado" aunque anime bien, y cambiar de personaje varias
        // veces seguidas no acumula rotación de más.
        newModel.transform.localRotation = baseTransform.Rotation * Quaternion.Euler(selected.modelRotationOffset);
        newModel.transform.localScale = baseTransform.Scale;

        Animator animator = newModel.GetComponentInChildren<Animator>();
        if (animator == null)
        {
            Debug.LogWarning($"[{logTag}] '{selected.prefab.name}' no tiene ningún Animator en su jerarquía -- no va a animar.");
            return null;
        }

        // Un FBX recién importado trae su Animator con 'Apply Root Motion'
        // TILDADO por defecto y SIN Controller asignado -- sin apagar lo
        // primero, cualquier movimiento horneado en la raíz del clip se
        // suma al movimiento manual que ya hace el juego (RunnerController /
        // el salto del Jumper del menú) y lo hace girar/derivar solo; sin
        // asignar lo segundo, el modelo queda congelado en su pose original.
        animator.applyRootMotion = false;
        if (sharedController != null) animator.runtimeAnimatorController = sharedController;

        return animator;
    }
}
