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

        if (GameManager.Instance != null && GameManager.Instance.IsGameOver)
        {
            ShowGameOverPanel();
        }
    }

    private void UpdateHud()
    {
        if (livesText != null && GameManager.Instance != null)
        {
            int lives = GameManager.Instance.Lives;
            if (lives != lastShownLives)
            {
                lastShownLives = lives;
                livesText.text = $"Vidas: {lives}";
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

    private void ShowGameOverPanel()
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
