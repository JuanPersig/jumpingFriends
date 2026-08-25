using UnityEngine;
using UnityEngine.SceneManagement;

public class GameManager : Singleton<GameManager>
{
    [Header("Vidas")]
    [SerializeField] private int startingLives = 3;

    [Header("Multijugador")]
    [Tooltip("Cantidad de jugadores de ESTA ronda (1 a 4). Manual por ahora (se sacó el " +
             "selector de botones de Configuración y el RoundSettings que lo respaldaba, " +
             "25/8) -- hasta que la Fase 3 lea la cantidad real de jugadores conectados por " +
             "red, este es un valor fijo de Inspector. Si tocás esto a mano para probar " +
             "localmente varios carriles, mantenelo IGUAL al de RoundLaneSetup (mismo campo, " +
             "duplicado por ahora porque ese script necesita leerlo en su propio Awake() -- ver " +
             "el comentario ahí).")]
    [SerializeField] private int roundPlayerCount = 1;
    public int RoundPlayerCount => roundPlayerCount;

    [Header("Debug")]
    [Tooltip("Debug: tildado, los choques no restan vidas (útil para probar cosas — " +
             "cámara, menú, obstáculos nuevos — sin que la partida se corte). Dejalo " +
             "destildado para jugar normal; no hace falta tocar código para volver a las 3 vidas.")]
    [SerializeField] private bool infiniteLives = false;

    public bool IsGameOver { get; private set; }
    public int Lives { get; private set; }

    // Arranca en false A PROPÓSITO como valor de campo (se aplica antes de
    // CUALQUIER Awake() de la escena, sin importar el orden real entre
    // scripts) -- así el juego queda pausado desde el primer frame hasta
    // que GameIntroSequence llame a BeginGameplay() al terminar la
    // animación de cámara de arranque. RunnerController/DifficultyManager/
    // ObstacleSpawner/ScoreManager chequean esto en su propio Update().
    //
    // OJO: si alguna vez probás Gameplay.unity sin que exista un
    // GameIntroSequence en la escena, el juego queda congelado para
    // siempre (nadie llama a BeginGameplay) -- es a propósito, mejor un
    // freeze obvio que una intro que a veces no corre sin avisar.
    public bool HasGameplayStarted { get; private set; } = false;

    public void BeginGameplay()
    {
        HasGameplayStarted = true;
    }

    protected override void Awake()
    {
        base.Awake();
        if (Instance != this) return; // instancia duplicada, ya se está autodestruyendo
        Lives = startingLives;
    }

    // Se llama una vez por cada obstáculo que el jugador choca (ver
    // RunnerController.OnTriggerEnter). Resta una vida; recién cuando se
    // acaban dispara el Game Over. Así un choque no termina la partida de
    // una, como pedía el diseño de 3 vidas.
    public void RegisterObstacleHit()
    {
        if (IsGameOver) return;

        if (infiniteLives)
        {
            Debug.Log("[GameManager] Choque (Infinite Lives activo, no se resta nada).");
            return;
        }

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

    // Público para el botón "Menú" del panel de Game Over (ver
    // UIManager.OnMainMenuButtonPressed). Por nombre, no por buildIndex --
    // a diferencia de RestartGame (que siempre tiene que ser "la escena
    // actual", sea cual sea), acá el destino es fijo, así que el nombre es
    // más explícito y no depende de qué posición tenga en Build Settings.
    // Los singletons persistentes (NativePoseInputSource, PlayerInputProvider,
    // CharacterSelection) sobreviven el cambio -- no hace falta recalibrar
    // ni volver a elegir personaje al volver a jugar.
    public void ReturnToMainMenu()
    {
        SceneManager.LoadScene("MainMenu");
    }
}
