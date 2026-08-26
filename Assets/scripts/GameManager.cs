using UnityEngine;
using UnityEngine.SceneManagement;

public class GameManager : Singleton<GameManager>
{
    [Header("Vidas")]
    [SerializeField] private int startingLives = 3;

    [Header("Multijugador")]
    [Tooltip("RESPALDO para cuando NO hay red: cuántos carriles activar si abrís Gameplay.unity " +
             "suelta desde el Editor, sin pasar por una sala. Con una sala activa este valor se " +
             "IGNORA -- manda la cantidad real de jugadores conectados (NetworkRoundState). Ya no " +
             "hace falta mantenerlo sincronizado a mano con ningún otro script: RoundLaneSetup y " +
             "ChunkSpawner leen esta misma propiedad, no un campo propio.")]
    [SerializeField] private int roundPlayerCount = 1;

    // Fuente ÚNICA de "cuántos jugadores hay en esta ronda" (Fase 3.2, 25/8).
    // Antes esto devolvía el campo de Inspector de arriba, y RoundLaneSetup
    // tenía ADEMÁS su propio campo duplicado que había que mantener igual a
    // mano. Ahora los dos leen de acá, y acá manda la red cuando la hay.
    //
    // OJO CON EL MOMENTO EN QUE SE LEE: los NetworkObject in-scene recién
    // spawnean DESPUÉS de que la escena termina de cargar, así que durante
    // los Awake() de la escena esto todavía devuelve el respaldo. Quien
    // necesite el valor real tiene que esperar a que NetworkRoundState se
    // resuelva -- ver RoundLaneSetup, que es hoy el único que decide cuándo.
    public int RoundPlayerCount
    {
        get
        {
            NetworkRoundState round = NetworkRoundState.Instance;
            if (round != null && round.IsResolved) return round.PlayerCount;
            return roundPlayerCount;
        }
    }

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

    // Se dispara UNA vez, cuando el jugador de ESTA máquina se queda sin
    // vidas. Lo escucha PlayerSlot para avisarles a las demás máquinas que su
    // personaje ya está muerto -- si no, en la pantalla de los otros seguiría
    // corriendo tan campante (bug reportado el 25/8).
    public event System.Action GameOver;

    public void TriggerGameOver()
    {
        if (IsGameOver) return; // evita disparar dos veces si hay doble colisión en el mismo frame
        IsGameOver = true;

        if (DifficultyManager.Instance != null) DifficultyManager.Instance.Stop();

        GameOver?.Invoke();

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
