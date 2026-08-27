using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Conecta el estado del juego (vidas, game over, puntaje) con textos en
// pantalla. A propósito NO le agrega lógica nueva a GameManager/ScoreManager
// — este script solo LEE esos valores y actualiza la UI. Si mañana cambia
// cómo se calculan las vidas o el puntaje, este script no necesita tocarse.
//
// Usa TMP_Text (TextMeshPro) en vez de UnityEngine.UI.Text (legacy): son
// componentes distintos en Unity, y este proyecto ya está usando TMP.
public class UIManager : MonoBehaviour
{
    [Header("HUD (mientras se juega)")]
    [SerializeField] private TMP_Text livesText;
    [SerializeField] private TMP_Text scoreText;
    [Tooltip("Corazones del sistema de vidas, EN ORDEN (el elemento 0 es la primera vida, " +
             "etc.). Se muestran/ocultan según GameManager.Lives -- no reemplazan a Lives " +
             "Text, conviven con él (dejá Lives Text vacío si solo querés corazones).")]
    [SerializeField] private GameObject[] heartIcons;

    [Header("Pantalla de Game Over")]
    [SerializeField] private GameObject gameOverPanel;
    [SerializeField] private TMP_Text finalScoreText;
    [Tooltip("El botón 'Reiniciar' del panel de Game Over -- arrastrá acá el GameObject entero " +
             "(RestartButton), no su componente Button. Se OCULTA solo en una partida en red: " +
             "reiniciar no puede decidirlo un jugador solo, y recargar la escena por tu cuenta " +
             "rompería la sesión de los demás (ver GameManager.RestartGame). En la partida de un " +
             "jugador se muestra normal. Si lo dejás sin wirear no se rompe nada: el botón queda " +
             "visible pero no hace nada en red, y sale un aviso por consola.")]
    [SerializeField] private GameObject restartButton;
    [Tooltip("Texto del botón en la partida de UN jugador, donde recarga la escena. Se escribe " +
             "sobre el primer texto que cuelgue del botón, así que no hay que wirear nada.")]
    [SerializeField] private string restartLabelSinglePlayer = "Reiniciar";
    [Tooltip("Texto del MISMO botón en una partida en red, donde en vez de reiniciar devuelve a " +
             "todos a la Sala de Espera con la sala y sus jugadores intactos. Solo lo ve el host.")]
    [SerializeField] private string restartLabelNetworked = "Volver a la sala";

    // Último valor efectivamente escrito en cada texto, para no reasignar
    // (ni alocar el string interpolado) cuando el número mostrado no
    // cambió. El puntaje se muestra redondeado (":0"), así que en la
    // práctica solo cambia ~1 vez por segundo, no 60 veces — sin este
    // chequeo estábamos generando basura (GC) todos los frames, para
    // siempre, por texto que la mayoría de las veces ni cambiaba.
    private int lastShownLives = int.MinValue;
    private int lastShownScore = int.MinValue;

    private void Start()
    {
        // El panel de Game Over arranca oculto; recién se muestra cuando
        // GameManager.IsGameOver se pone true (ver Update()).
        if (gameOverPanel != null) gameOverPanel.SetActive(false);
    }

    private void Update()
    {
        UpdateHud();

        // El panel de Game Over YA NO se muestra solo apenas IsGameOver pasa
        // a true -- eso tapaba la cinemática de muerte (GameOutroSequence)
        // apenas arrancaba. Ahora es GameOutroSequence quien llama a
        // ShowGameOverPanel() a mano, recién cuando termina la animación de
        // caída + el movimiento de cámara. Mismo criterio que ya se usa con
        // GameIntroSequence/HasGameplayStarted: si no hay un GameOutroSequence
        // en la escena, el panel de Game Over simplemente no aparece nunca --
        // a propósito, mejor eso bien visible que un freeze silencioso.
    }

    private void UpdateHud()
    {
        // Antes este chequeo vivía adentro del "if (livesText != null ...)",
        // así que si no asignabas Lives Text los corazones tampoco se
        // hubieran actualizado nunca -- separado para que texto y corazones
        // funcionen de forma independiente (podés tener uno solo, el otro,
        // o los dos).
        if (GameManager.Instance != null)
        {
            int lives = GameManager.Instance.Lives;
            if (lives != lastShownLives)
            {
                lastShownLives = lives;
                if (livesText != null) livesText.text = $"Vidas: {lives}";
                UpdateHeartIcons(lives);
            }
        }

        if (scoreText != null && ScoreManager.Instance != null)
        {
            // RoundToInt (no FloorToInt): así redondea igual que el ":0"
            // que usaba el string interpolado original, sin cambiar el
            // número que ve el jugador ni un punto.
            int score = Mathf.RoundToInt(ScoreManager.Instance.CurrentScore);
            if (score != lastShownScore)
            {
                lastShownScore = score;
                scoreText.text = $"Puntaje: {score}";
            }
        }
    }

    // Corazón [i] visible = todavía te queda esa vida. No swapea a un sprite
    // de "corazón vacío" -- el pack que se usó no trae uno confiable (el
    // spritesheet que vino con el corazón lleno parece más una animación de
    // latido que estados de vida, así que no se usó para no adivinar). Si
    // más adelante conseguís un corazón vacío/apagado, esto es lo único que
    // habría que cambiar: en vez de SetActive(false), asignarle ese sprite
    // al Image del corazón perdido.
    private void UpdateHeartIcons(int lives)
    {
        if (heartIcons == null) return;

        for (int i = 0; i < heartIcons.Length; i++)
        {
            if (heartIcons[i] != null) heartIcons[i].SetActive(i < lives);
        }
    }

    // Público: lo llama GameOutroSequence cuando termina la cinemática de
    // muerte (cámara + animación de caída). El guard de abajo
    // (gameOverPanel.activeSelf) ya evitaba mostrarlo dos veces, así que
    // sigue siendo seguro llamarlo más de una vez por las dudas.
    public void ShowGameOverPanel()
    {
        if (gameOverPanel == null || gameOverPanel.activeSelf) return; // ya se está mostrando

        gameOverPanel.SetActive(true);

        // El HUD de "mientras se juega" (vidas/puntaje) ya no tiene sentido
        // una vez que se muestra el panel de Game Over -- el puntaje final
        // se ve en finalScoreText, y las vidas ya no importan.
        if (livesText != null) livesText.gameObject.SetActive(false);
        if (scoreText != null) scoreText.gameObject.SetActive(false);

        if (finalScoreText != null && ScoreManager.Instance != null)
        {
            // Solo el número -- el "Puntaje final fue" ahora es un texto fijo
            // aparte en la escena (GameObject "FinalScore"), no hace falta
            // repetirlo acá.
            finalScoreText.text = $"{ScoreManager.Instance.CurrentScore:0}";
        }

        UpdateRestartButtonVisibility();
    }

    // El botón grande del panel cambia de significado según el contexto,
    // porque en los dos casos quiere decir lo mismo para el jugador
    // ("juguemos otra") aunque por dentro sean cosas distintas:
    //
    //   - Sin red      -> "Reiniciar": recarga la escena, como siempre.
    //   - En red, host -> "Volver a la sala": devuelve a TODOS a la Sala de
    //                     Espera con la sala viva, para arrancar otra ronda
    //                     sin crear ni compartir un código nuevo.
    //   - En red, resto-> oculto: mover a todos de escena es una operación de
    //                     servidor, así que lo decide el host. Igual que con
    //                     "Empezar Partida" en la Sala de Espera.
    //
    // Reusar este botón en vez de agregar uno nuevo evita tocar la escena, y
    // deja el panel con las dos opciones que de verdad importan: seguir
    // jugando, o irse.
    private void UpdateRestartButtonVisibility()
    {
        bool networked = GameManager.IsNetworkedSession;
        bool show = !networked || GameManager.IsNetworkHost;

        if (restartButton == null)
        {
            Debug.LogWarning("[UIManager] 'Restart Button' está sin wirear -- el botón va a " +
                              "quedar siempre visible y con el texto de la escena, incluso para " +
                              "quien no puede usarlo. Arrastrá el GameObject del botón a ese campo.");
            return;
        }

        restartButton.SetActive(show);
        if (!show) return;

        // El texto se busca entre los hijos en vez de pedirlo por Inspector:
        // un botón de Unity siempre lleva su label adentro, así que no hace
        // falta wirear nada y sigue andando si mañana se rehace el botón.
        TMP_Text label = restartButton.GetComponentInChildren<TMP_Text>(true);
        if (label != null)
        {
            label.text = networked ? restartLabelNetworked : restartLabelSinglePlayer;
        }
    }

    // Enganchá esto al OnClick() del botón grande del panel de Game Over.
    // Qué hace depende del contexto -- ver UpdateRestartButtonVisibility.
    public void OnRestartButtonPressed()
    {
        if (GameManager.IsNetworkedSession)
        {
            // Mismo motivo que en "Menú": el cambio de escena de Netcode
            // tarda un instante en propagarse, y sin feedback el botón
            // parece muerto.
            SetGameOverButtonsInteractable(false);
            GameManager.Instance?.ReturnToLobby();
            return;
        }

        GameManager.Instance?.RestartGame();
    }

    // Enganchá esto al OnClick() de un botón "Menú" en el panel de Game Over.
    //
    // OJO CON LA ESPERA (bug encontrado el 26/8 probando esto): volver al menú
    // ya no es instantáneo, porque primero hay que abandonar la sala de verdad
    // y eso es una llamada de red de uno a tres segundos. Sin ningún feedback,
    // el panel se queda IDÉNTICO todo ese rato y el botón parece muerto: el
    // reflejo es volver a apretarlo, o pensar que el juego se colgó.
    //
    // Es el mismo problema que RoomFlowController ya había resuelto con su
    // panel de carga al crear/unirse a una sala. Acá alcanza con apagar los
    // botones: quedan grises al instante (el color de deshabilitado ya está
    // configurado en la escena), así se ve que el click se registró y de paso
    // no se puede encolar un segundo.
    public void OnMainMenuButtonPressed()
    {
        SetGameOverButtonsInteractable(false);
        GameManager.Instance?.ReturnToMainMenu();
    }

    // Se buscan los Button del panel en vez de pedirlos por Inspector: no hace
    // falta wirear nada y funciona igual si más adelante se agrega otro botón
    // al panel. Corre una sola vez, al salir, así que el costo de buscar no
    // importa.
    private void SetGameOverButtonsInteractable(bool value)
    {
        if (gameOverPanel == null) return;

        foreach (Button button in gameOverPanel.GetComponentsInChildren<Button>(true))
        {
            button.interactable = value;
        }
    }
}
