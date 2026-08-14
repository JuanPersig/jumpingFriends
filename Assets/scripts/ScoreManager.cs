using UnityEngine;

public class ScoreManager : Singleton<ScoreManager>
{
    public float CurrentScore { get; private set; }

    private void Update()
    {
        if (GameManager.Instance != null && GameManager.Instance.IsGameOver) return;
        // Pausado hasta que termine la intro de cámara (ver GameIntroSequence).
        if (GameManager.Instance != null && !GameManager.Instance.HasGameplayStarted) return;
        if (DifficultyManager.Instance == null) return;

        // El puntaje es simplemente la distancia recorrida (velocidad * tiempo).
        CurrentScore += DifficultyManager.Instance.CurrentSpeed * Time.deltaTime;
    }
}
