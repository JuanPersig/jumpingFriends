using UnityEngine;

// Fuente única de verdad de "a qué velocidad va el juego ahora mismo".
// El jugador lee esto para saber cuán rápido correr; el spawner lo usa
// indirectamente (mismo espaciado en distancia = menos tiempo de reacción
// a medida que esto sube, así la dificultad crece sola).
public class DifficultyManager : MonoBehaviour
{
    public static DifficultyManager Instance { get; private set; }

    [Header("Velocidad")]
    [SerializeField] private float startSpeed = 6f;
    [SerializeField] private float maxSpeed = 16f;
    [SerializeField] private float acceleration = 0.15f; // unidades por segundo, al cuadrado

    public float CurrentSpeed { get; private set; }

    private bool isRunning;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

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
        CurrentSpeed = Mathf.Min(maxSpeed, CurrentSpeed + acceleration * Time.deltaTime);
    }
}
