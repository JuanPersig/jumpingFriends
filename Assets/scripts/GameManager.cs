using System.Collections;
using System.Threading.Tasks;
using Unity.Netcode;
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

    // ¿Terminó la RONDA (no solo tu partida)? Con varios jugadores, que vos
    // te quedes sin vidas no termina nada: pasás a espectador y el mundo
    // sigue. La ronda termina recién cuando queda uno en pie, y eso lo decide
    // el servidor (ver NetworkRoundState / PlayerSlot).
    public bool IsRoundOver { get; private set; }
    public event System.Action RoundOver;

    public void TriggerGameOver()
    {
        if (IsGameOver) return; // evita disparar dos veces si hay doble colisión en el mismo frame
        IsGameOver = true;

        // OJO: acá YA NO se frena el DifficultyManager (Fase 3.5, 26/8). Ese
        // es el reloj compartido de TODA la simulación, así que pararlo
        // congelaba también a los rivales -- en tu pantalla los veías correr
        // en el lugar mientras en la suya seguían avanzando. Ahora lo frena
        // TriggerRoundOver, que corre cuando la ronda termina de verdad.
        GameOver?.Invoke();

        float finalScore = ScoreManager.Instance != null ? ScoreManager.Instance.CurrentScore : 0f;
        Debug.Log($"[GameManager] GAME OVER. Puntaje final: {finalScore:0}");

        // Sin red no hay a quién esperar: tu muerte ES el fin de la ronda.
        // Con red, la decide el servidor cuando queda uno en pie.
        bool networked = NetworkRoundState.Instance != null && NetworkRoundState.Instance.IsNetworked;
        if (!networked) TriggerRoundOver();
    }

    // Lo llama NetworkRoundState cuando el servidor declara terminada la
    // ronda (o TriggerGameOver, sin red). Acá SÍ se frena el reloj
    // compartido: ya no queda nadie corriendo a quien congelar de más.
    public void TriggerRoundOver()
    {
        if (IsRoundOver) return;
        IsRoundOver = true;

        if (DifficultyManager.Instance != null) DifficultyManager.Instance.Stop();

        Debug.Log("[GameManager] Fin de ronda.");
        RoundOver?.Invoke();
    }

    // ¿Esta partida se está jugando EN RED? Lo miran los dos botones de
    // abajo para no romperle la sesión a los demás, y UIManager para decidir
    // si muestra o no el botón "Reiniciar".
    //
    // Se pregunta directo al NetworkManager y NO a NetworkRoundState.
    // IsNetworked (que hace exactamente lo mismo) a propósito: ese objeto
    // vive en Gameplay.unity y se descarga con la escena, así que en medio
    // de una vuelta al menú puede no estar. El NetworkManager es
    // persistente.
    public static bool IsNetworkedSession =>
        NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

    // ¿Somos el host? Solo él puede mover a TODOS de escena
    // (NetworkManager.SceneManager.LoadScene es una operación de servidor),
    // así que es él quien decide cuándo se vuelve a la sala.
    public static bool IsNetworkHost =>
        NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost;

    // "Volver a la sala": termina la partida pero MANTIENE la sesión viva,
    // con todos sus jugadores, y devuelve a todo el mundo a la Sala de
    // Espera. Desde ahí el host arranca otra ronda con "Empezar Partida",
    // sin que nadie tenga que crear ni compartir un código nuevo.
    //
    // Es lo contrario de ReturnToMainMenu(), que SÍ abandona la sala. Los dos
    // caminos conviven a propósito: este es "juguemos otra", aquel es "me voy".
    //
    // TIENE QUE LLAMARLO EL HOST. Un cliente no puede volver por su cuenta:
    // se saldría de la sincronización de escenas de Netcode. Por eso a los
    // demás ni se les muestra el botón (ver UIManager) -- esperan al host,
    // igual que esperan su "Empezar Partida" en la Sala de Espera.
    public void ReturnToLobby()
    {
        if (!IsNetworkHost)
        {
            Debug.LogWarning("[GameManager] 'Volver a la sala' solo puede dispararlo el host -- " +
                              "mover a todos de escena es una operación de servidor.");
            return;
        }

        // Se loguea el status por la misma razón que en
        // RoomFlowController.OnStartGamePressed: LoadScene puede RECHAZAR el
        // pedido sin tirar excepción, y esa falla silenciosa ya costó una
        // cacería entera el 25/8.
        SceneEventProgressStatus status =
            NetworkManager.Singleton.SceneManager.LoadScene("MainMenu", LoadSceneMode.Single);
        Debug.Log($"[GameManager] Volviendo a la sala (la sesión sigue viva) -> {status}");
    }

    // "Reiniciar" existe solo para la partida de UN jugador.
    //
    // En red no puede reiniciar uno solo: recargar la escena por tu cuenta
    // con SceneManager.LoadScene te saca de la sincronización de escenas de
    // Netcode (y si sos el host, además dejás a los clientes en una escena
    // que ya nadie está simulando). Un reinicio sincronizado para toda la
    // sala -- que tendría que decidir el host y disparar con
    // NetworkManager.SceneManager.LoadScene -- es una feature aparte; hasta
    // que exista, en red el único camino de salida es "Menú".
    //
    // UIManager ya oculta el botón cuando hay red; este chequeo es la red de
    // seguridad por si quedó visible (campo sin wirear) o si alguien llama a
    // esto desde otro lado.
    public void RestartGame()
    {
        if (IsNetworkedSession)
        {
            Debug.LogWarning("[GameManager] 'Reiniciar' no está disponible en una partida en red " +
                              "-- recargar la escena por tu cuenta rompería la sesión. Usá " +
                              "'Menú' para volver al menú principal.");
            return;
        }

        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    // Público para el botón "Menú" del panel de Game Over (ver
    // UIManager.OnMainMenuButtonPressed).
    //
    // ABANDONA LA SESIÓN ANTES DE CARGAR LA ESCENA (bug A2, cerrado el 26/8).
    // Antes esto era un SceneManager.LoadScene("MainMenu") pelado, sin pasar
    // por Netcode: la sesión seguía viva, el host dejaba a los demás en una
    // escena que abandonó sin avisar, y volvías al Menú técnicamente
    // conectado/hosteando de fondo. Mismo orden que ya usa
    // RoomFlowController.OnLeaveRoomPressed: primero soltar la sala de
    // verdad, recién después mostrar el menú.
    //
    // Sin red (Gameplay.unity abierta suelta) no hay
    // MultiplayerConnectionManager y esto es exactamente el LoadScene de
    // siempre, un frame más tarde.
    //
    // Los singletons persistentes (NativePoseInputSource, PlayerInputProvider,
    // CharacterSelection) sobreviven el cambio -- no hace falta recalibrar ni
    // volver a elegir personaje al volver a jugar.
    public void ReturnToMainMenu()
    {
        if (returningToMainMenu) return; // doble click en el botón
        returningToMainMenu = true;
        returnStartedAt = Time.realtimeSinceStartup;

        StartCoroutine(LeaveSessionThenLoadMainMenu());
    }

    private bool returningToMainMenu;
    private float returnStartedAt;

    // Topes de espera. Guardarraíl 8 del proyecto: jamás un "esperar hasta
    // que" sin salida -- si el servicio no responde, se vuelve al menú igual
    // en vez de dejar al jugador clavado en el panel de Game Over.
    private const float LeaveSessionTimeoutSeconds = 5f;
    private const float NetcodeShutdownTimeoutSeconds = 2f;

    private IEnumerator LeaveSessionThenLoadMainMenu()
    {
        if (MultiplayerConnectionManager.Instance != null)
        {
            // LeaveSession() nunca tira (atrapa adentro y siempre suelta
            // CurrentSession), así que acá solo hay que esperar a que
            // termine -- no hace falta mirar el resultado.
            Task leaving = MultiplayerConnectionManager.Instance.LeaveSession();

            float waited = 0f;
            while (!leaving.IsCompleted && waited < LeaveSessionTimeoutSeconds)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            if (!leaving.IsCompleted)
            {
                Debug.LogWarning($"[GameManager] Abandonar la sala tardó más de " +
                                  $"{LeaveSessionTimeoutSeconds}s -- se vuelve al menú igual.");
            }
        }

        // Netcode apaga de verdad recién al final del frame
        // (ShutdownInProgress). Cargar la escena en el mismo instante se
        // pisaría con ese apagado, así que se le da el margen que pida.
        float shutdownWait = 0f;
        while (NetworkManager.Singleton != null && NetworkManager.Singleton.ShutdownInProgress &&
               shutdownWait < NetcodeShutdownTimeoutSeconds)
        {
            shutdownWait += Time.unscaledDeltaTime;
            yield return null;
        }

        // DIAGNÓSTICO (26/8): en la primera prueba no quedó rastro de si esta
        // línea llegó a correr o si se cortó antes -- el log mostraba "Sala
        // abandonada" y después nada. Con esto, la próxima vez se sabe de una,
        // y el total dice cuánto tardó la vuelta completa. Sacar cuando el
        // camino esté confirmado.
        Debug.Log($"[GameManager] Volviendo al menú principal " +
                  $"({Time.realtimeSinceStartup - returnStartedAt:0.00}s desde el click).");

        // Por nombre, no por buildIndex -- acá el destino es fijo, así que el
        // nombre es más explícito y no depende de qué posición tenga en Build
        // Settings.
        SceneManager.LoadScene("MainMenu");
    }
}
