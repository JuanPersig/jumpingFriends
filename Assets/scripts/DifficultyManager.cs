using UnityEngine;

// Fuente única de verdad de "a qué velocidad va el juego ahora mismo".
// El jugador lee esto para saber cuán rápido correr; el spawner lo usa
// indirectamente (mismo espaciado en distancia = menos tiempo de reacción
// a medida que esto sube, así la dificultad crece sola).
public class DifficultyManager : Singleton<DifficultyManager>
{
    [Header("Velocidad")]
    [SerializeField] private float startSpeed = 6f;
    [SerializeField] private float maxSpeed = 16f;
    [SerializeField] private float acceleration = 0.15f; // unidades por segundo, al cuadrado

    public float CurrentSpeed { get; private set; }

    // 0 = recién arrancó (CurrentSpeed == startSpeed), 1 = llegó a maxSpeed.
    // Otros sistemas (ObstacleSpawner, RunnerController) lo leen para hacer
    // que su propia dificultad (piso de reacción, variedad de obstáculos,
    // perdón de salto...) también vaya subiendo DENTRO de una misma
    // partida, en vez de ser un número fijo parejo de punta a punta.
    public float Progress01
    {
        get
        {
            if (Mathf.Approximately(maxSpeed, startSpeed)) return 1f;
            return Mathf.Clamp01((CurrentSpeed - startSpeed) / (maxSpeed - startSpeed));
        }
    }

    private bool isRunning;

    private void Start()
    {
        ResetDifficulty();
    }

    public void ResetDifficulty()
    {
        CurrentSpeed = startSpeed;
        isRunning = true;
    }

    public void Stop()
    {
        isRunning = false;
    }

    private void Update()
    {
        if (!isRunning) return;
        // Pausado hasta que termine la intro de cámara (ver GameIntroSequence).
        if (GameManager.Instance != null && !GameManager.Instance.HasGameplayStarted) return;
        CurrentSpeed = Mathf.Min(maxSpeed, CurrentSpeed + acceleration * Time.deltaTime);
    }
}
