using UnityEngine;

public class ScoreManager : MonoBehaviour
{
    public static ScoreManager Instance { get; private set; }

    public float CurrentScore { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Update()
    {
        if (GameManager.Instance != null && GameManager.Instance.IsGameOver) return;
        if (DifficultyManager.Instance == null) return;

        // El puntaje es simplemente la distancia recorrida (velocidad * tiempo).
        CurrentScore += DifficultyManager.Instance.CurrentSpeed * Time.deltaTime;
    }
}
