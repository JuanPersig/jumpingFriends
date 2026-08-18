using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

// Conecta los botones del menú de inicio con CharacterSelection y con el
// Jumper (MenuMouseJumper). Enganchá estos métodos públicos a los
// OnClick() de los botones correspondientes en el Inspector — ninguno
// necesita parámetros, así que van directo en la lista de OnClick() sin
// configuración extra.
//
// El menú tiene DOS paneles (dos GameObjects que se prenden/apagan, no dos
// escenas): el principal (título + Jugar + "Elegir Personaje") y el de
// selección (flechas + nombre + Volver). NO hay un pedestal 3D aparte: el
// preview del personaje elegido es el mismo Jumper que salta en el panel
// principal (ver MenuMouseJumper.RefreshCharacter) — un solo personaje
// visible en todo el menú, nunca dos a la vez.
public class MenuController : MonoBehaviour
{
    [Header("Paneles")]
    [Tooltip("Panel principal: título, botón Jugar, botón Elegir Personaje.")]
    [SerializeField] private GameObject mainPanel;
    [Tooltip("Panel de selección: flechas, nombre del personaje, botón Volver.")]
    [SerializeField] private GameObject characterSelectPanel;
    [Tooltip("Panel de Configuración: cámara en vivo, estado detectado, sliders de " +
             "sensibilidad y botón Recalibrar (ver DetectionSettingsPanel).")]
    [SerializeField] private GameObject settingsPanel;
    [Tooltip("Opcional: si lo asignás, la cámara se mueve suavemente al Cylinder (donde " +
             "salta Jumper) al entrar a Elegir Personaje, y vuelve a la vista principal al Volver.")]
    [SerializeField] private MenuCameraMover cameraMover;

    [Header("Selección de personaje")]
    [Tooltip("El Jumper del menú (MenuMouseJumper) -- es el único preview de personaje que " +
             "hay. Opcional: si no lo asignás, cambiar de personaje sigue funcionando, solo " +
             "que el modelo no se actualiza.")]
    [SerializeField] private MenuMouseJumper jumper;
    [Tooltip("Opcional: texto donde mostrar el nombre del personaje elegido.")]
    [SerializeField] private TMP_Text characterNameText;

    [Header("Juego")]
    [Tooltip("Nombre EXACTO de la escena de juego (tal cual aparece en Build Settings).")]
    [SerializeField] private string gameplaySceneName = "SampleScene";
    [Tooltip("Mensaje que avisa 'configurá tu cámara antes de jugar' si el jugador aprieta " +
             "Jugar sin haber calibrado en Configuración todavía. Recomendado: ponelo como " +
             "HIJO de mainPanel -- así se oculta solo apenas se navega a cualquier otro panel, " +
             "sin necesitar lógica extra. Opcional: dejalo vacío para no bloquear nada.")]
    [SerializeField] private GameObject cameraNotConfiguredWarning;

    private void Start()
    {
        ShowMainPanel();
        UpdateNameLabel();
    }

    // Enganchá esto al botón "Elegir Personaje" del panel principal.
    public void OnOpenCharacterSelect()
    {
        if (mainPanel != null) mainPanel.SetActive(false);
        if (characterSelectPanel != null) characterSelectPanel.SetActive(true);
        if (cameraMover != null) cameraMover.ShowCharacterSelectView();
    }

    // Enganchá esto al botón "Configuración" del panel principal.
    public void OnOpenSettings()
    {
        if (mainPanel != null) mainPanel.SetActive(false);
        if (settingsPanel != null) settingsPanel.SetActive(true);
    }

    // Enganchá esto al botón "Volver" del panel de selección Y al del
    // panel de Configuración -- los dos vuelven al mismo lugar.
    public void OnBackToMainMenu()
    {
        ShowMainPanel();
    }

    private void ShowMainPanel()
    {
        if (characterSelectPanel != null) characterSelectPanel.SetActive(false);
        if (settingsPanel != null) settingsPanel.SetActive(false);
        if (mainPanel != null) mainPanel.SetActive(true);
        if (cameraMover != null) cameraMover.ShowMainMenuView();
        // Se apaga cada vez que se vuelve al panel principal, para que no
        // quede colgado de una vez anterior que apretaste Jugar sin cámara.
        if (cameraNotConfiguredWarning != null) cameraNotConfiguredWarning.SetActive(false);
    }

    public void OnNextCharacter()
    {
        CharacterSelection.Instance?.SelectNext();
        if (jumper != null) jumper.RefreshCharacter();
        UpdateNameLabel();
    }

    public void OnPreviousCharacter()
    {
        CharacterSelection.Instance?.SelectPrevious();
        if (jumper != null) jumper.RefreshCharacter();
        UpdateNameLabel();
    }

    public void OnPlayPressed()
    {
        // Bloqueo: no dejar arrancar la partida si el jugador nunca pasó
        // por Configuración a calibrar la cámara (o si calibró y falló --
        // ver NativePoseInputSource.IsCalibrated/HasCameraError). Sin la
        // cámara calibrada, el juego arranca pero nunca responde al salto/
        // agache -- mejor avisar acá, antes de cambiar de escena, que
        // dejarlo entrar a un juego que no va a poder jugar.
        bool cameraReady = NativePoseInputSource.Instance != null && NativePoseInputSource.Instance.IsCalibrated;
        if (!cameraReady)
        {
            if (cameraNotConfiguredWarning != null) cameraNotConfiguredWarning.SetActive(true);
            return;
        }

        SceneManager.LoadScene(gameplaySceneName);
    }

    private void UpdateNameLabel()
    {
        if (characterNameText == null || CharacterSelection.Instance == null) return;
        CharacterSelection.CharacterOption selected = CharacterSelection.Instance.Selected;
        characterNameText.text = selected != null ? selected.displayName : "";
    }
}
