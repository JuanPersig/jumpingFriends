using TMPro;
using UnityEngine;

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

        if (finalScoreText != null && ScoreManager.Instance != null)
        {
            finalScoreText.text = $"Puntaje final: {ScoreManager.Instance.CurrentScore:0}";
        }
    }

    // Enganchá esto al OnClick() de un botón "Reiniciar" en el panel de
    // Game Over. GameManager.RestartGame() ya existía, pero nadie lo llamaba.
    public void OnRestartButtonPressed()
    {
        GameManager.Instance?.RestartGame();
    }
}
