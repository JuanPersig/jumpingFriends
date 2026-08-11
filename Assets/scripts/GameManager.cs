using UnityEngine;
using UnityEngine.SceneManagement;

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    [Header("Vidas")]
    [SerializeField] private int startingLives = 3;

    public bool IsGameOver { get; private set; }
    public int Lives { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        Lives = startingLives;
    }

    // Se llama una vez por cada obstáculo que el jugador choca (ver
    // RunnerController.OnTriggerEnter). Resta una vida; recién cuando se
    // acaban dispara el Game Over. Así un choque no termina la partida de
    // una, como pedía el diseño de 3 vidas.
    public void RegisterObstacleHit()
    {
        if (IsGameOver) return;

        Lives = Mathf.Max(0, Lives - 1);
        Debug.Log($"[GameManager] Choque. Vidas restantes: {Lives}");

        if (Lives <= 0)
        {
            TriggerGameOver();
        }
    }

    public void TriggerGameOver()
    {
        if (IsGameOver) return; // evita disparar dos veces si hay doble colisión en el mismo frame
        IsGameOver = true;

        if (DifficultyManager.Instance != null) DifficultyManager.Instance.Stop();

        float finalScore = ScoreManager.Instance != null ? ScoreManager.Instance.CurrentScore : 0f;
        Debug.Log($"[GameManager] GAME OVER. Puntaje final: {finalScore:0}");

        // Placeholder: acá conectamos la UI real de Game Over más adelante
        // (Fase 4, cuando armemos el sistema de menús reutilizable).
    }

    public void RestartGame()
    {
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }
}
