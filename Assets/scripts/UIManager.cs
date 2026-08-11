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
            livesText.text = $"Vidas: {GameManager.Instance.Lives}";
        }

        if (scoreText != null && ScoreManager.Instance != null)
        {
            scoreText.text = $"Puntaje: {ScoreManager.Instance.CurrentScore:0}";
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
