using UnityEngine;

// Guarda qué personaje eligió el jugador en el menú de inicio, y
// sobrevive el cambio de escena (MainMenu -> SampleScene) igual que
// PlayerInputProvider (mismo patrón: singleton + DontDestroyOnLoad).
//
// Si nunca pasaste por el menú (ej: abriste SampleScene directo desde el
// Editor para probar algo puntual), Instance va a ser null en esa escena
// -> todo lo que dependa de la selección debería tratar eso como "usá el
// personaje que ya está puesto a mano", no como un error.
public class CharacterSelection : Singleton<CharacterSelection>
{
    [System.Serializable]
    public class CharacterOption
    {
        public string displayName;
        public GameObject prefab;
        [Tooltip("Corrección de rotación (grados, local) que se le aplica al modelo SOLO " +
                 "de este personaje al instanciarlo. Algunos FBX importados traen un eje " +
                 "distinto horneado en su armature (Blender Z-up vs Unity Y-up) y aparecen " +
                 "'acostados' aunque animen bien -- el retargeting Humanoid corrige las " +
                 "poses de los músculos, pero no la orientación general del modelo. Si un " +
                 "personaje se ve acostado, probá primero con X = 90. UAL1_Standard no lo " +
                 "necesita, dejalo en (0,0,0).")]
        public Vector3 modelRotationOffset = Vector3.zero;
    }

    [Tooltip("Un elemento por personaje disponible para elegir en el menú. " +
             "Para sumar uno nuevo: importalo, marcalo como Humanoid, y arrastralo acá.")]
    [SerializeField] private CharacterOption[] characters;

    private int selectedIndex = 0;

    public int Count => characters?.Length ?? 0;
    public int SelectedIndex => selectedIndex;

    // Null si no hay ningún personaje cargado en la lista (array vacío).
    public CharacterOption Selected =>
        Count > 0 ? characters[Mathf.Clamp(selectedIndex, 0, Count - 1)] : null;

    // Personaje en un índice arbitrario (no necesariamente el elegido
    // localmente) -- lo usa LobbyJumperSpawner para mostrar el personaje
    // que OTRO jugador de la sala eligió, no el propio. Clampeado igual
    // que Selected, por si el índice sincronizado por red no coincide con
    // la cantidad de personajes de este build (ej. builds desactualizados).
    public CharacterOption Get(int index) =>
        Count > 0 ? characters[Mathf.Clamp(index, 0, Count - 1)] : null;

    protected override void Awake()
    {
        base.Awake();
        if (Instance != this) return; // instancia duplicada, ya se está autodestruyendo
        DontDestroyOnLoad(gameObject);
    }

    public void SelectNext()
    {
        if (Count <= 1) return;
        selectedIndex = (selectedIndex + 1) % Count;
    }

    public void SelectPrevious()
    {
        if (Count <= 1) return;
        selectedIndex = (selectedIndex - 1 + Count) % Count;
    }
}
