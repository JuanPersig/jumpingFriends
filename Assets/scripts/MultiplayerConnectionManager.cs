using System;
using System.Collections;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.SceneManagement;

// Punto de entrada a Unity Gaming Services (Authentication + Multiplayer
// Sessions -- el paquete unificado com.unity.services.multiplayer, que
// reemplaza a los viejos Relay/Lobby por separado, deprecados 20/8).
//
// CreateSessionAsync/JoinSessionByCodeAsync ya manejan SOLOS crear/unirse
// al lobby, pedir la asignación de Relay, Y conectar Netcode -- no hace
// falta llamar a NetworkManager.Singleton.StartHost()/StartClient() a mano
// en ningún lado de este script.
//
// Vive en el MISMO GameObject que el componente NetworkManager, en
// MainMenu.unity, persistente (DontDestroyOnLoad) -- igual que
// PlayerInputProvider/CharacterSelection. El patrón de
// singleton está escrito A MANO (Destroy(gameObject) en vez de heredar
// Singleton<T>) por prudencia: no hay evidencia de que compartir GameObject
// con NetworkManager cause el mismo problema que compartirlo con otro
// Singleton<T> (ver el caso NativePoseInputSource/PlayerInputProvider), pero
// tampoco hay certeza de lo contrario, así que se prefiere el camino ya
// probado en vez de asumir que es seguro.
public class MultiplayerConnectionManager : MonoBehaviour
{
    public static MultiplayerConnectionManager Instance { get; private set; }

    [Tooltip("Techo de jugadores por sala (1 a 4, ver el resto del proyecto).")]
    [SerializeField] private int maxPlayers = 4;

    [Header("Debug (probar sin UI todavía)")]
    [Tooltip("Código a usar con el menú contextual 'DEBUG: Unirse a sala de prueba' -- " +
             "pegá acá el código que te dio 'DEBUG: Crear sala de prueba' en otra instancia.")]
    [SerializeField] private string debugJoinCode;

    [Tooltip("PROVISORIO -- para poder abrir DOS instancias del MISMO Build en la misma PC y " +
             "probar sin depender de un amigo real todavía. Causa real (confirmada 25/8): " +
             "AuthenticationService.SignInAnonymouslyAsync() cachea la identidad en " +
             "Application.persistentDataPath, que es EL MISMO para dos instancias del mismo " +
             ".exe en la misma PC -- sin esto, las dos terminan autenticadas como el MISMO " +
             "jugador, y el servicio rechaza que el mismo jugador esté dos veces en la misma " +
             "sala (por eso ni siquiera dejaba unirse). Poné acá un nombre distinto por " +
             "instancia (ej. 'host'/'cliente') ANTES de abrir cada Build -- vacío (default) usa " +
             "el perfil normal, igual que en producción real (cada jugador de verdad tiene su " +
             "propia PC, con su propio persistentDataPath, así que esto nunca hace falta fuera " +
             "de pruebas locales). Acordate de dejarlo vacío antes de un build para compartir.")]
    [SerializeField] private string debugAuthProfile;

    // La sesión activa (como host o como cliente) una vez conectado -- null
    // hasta que CreateSession/JoinSession termine con éxito.
    public ISession CurrentSession { get; private set; }

    // Código de sala para mostrar en la UI -- vacío hasta crear una sesión
    // como host (los clientes no generan código, lo reciben de afuera).
    public string SessionCode => CurrentSession?.Code ?? "";

    // ¿Estamos en una sala ahora mismo? Estática y con el null-check adentro
    // porque la preguntan los scripts del menú en su Start(), cuando todavía
    // no saben si este componente existe (al arrancar el juego de cero no
    // existe; volviendo de una partida sí, es persistente).
    //
    // Vive acá y no en cada uno para que MenuController y RoomFlowController
    // pregunten LO MISMO: los dos deciden qué panel mostrar a partir de esta
    // respuesta, y si cada uno la calculara por su cuenta podrían separarse.
    public static bool HasActiveSession => Instance != null && Instance.CurrentSession != null;

    private bool servicesInitialized;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject); // instancia duplicada (ej. se volvió a cargar MainMenu.unity)
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    // Público, pero normalmente no hace falta llamarlo aparte:
    // CreateSession()/JoinSession() ya lo llaman solos antes de conectar.
    // Separado de Awake() a propósito -- es async, y Awake() no debería
    // arrancar trabajo de red por su cuenta sin que nadie lo haya pedido
    // todavía (mismo criterio que el resto del proyecto: nada de red/cámara
    // arranca antes de que el jugador entre a la pantalla que la necesita).
    public async Task EnsureServicesInitialized()
    {
        if (servicesInitialized) return;

        await UnityServices.InitializeAsync();

        // SwitchProfile tiene que llamarse ANTES de cualquier sign-in (tira
        // excepción si ya estás firmado) -- por eso va acá, apenas
        // terminó InitializeAsync y antes de tocar IsSignedIn/SignIn. Ver
        // el comentario grande en debugAuthProfile para el porqué.
        // Trim() no es cosmético: el servicio solo acepta alfanuméricos, '-'
        // y '_', así que un espacio invisible al final del campo del
        // Inspector tira "Invalid profile name" y deja el build sin poder
        // crear NI unirse a ninguna sala. Pasó de verdad el 26/8 con
        // 'Host ' -- costó un build entero darse cuenta, porque el espacio no
        // se ve en el Inspector y el error no dice cuál es el nombre que
        // molesta.
        string profile = debugAuthProfile != null ? debugAuthProfile.Trim() : "";
        if (!string.IsNullOrEmpty(profile))
        {
            AuthenticationService.Instance.SwitchProfile(profile);

            // Deja rastro en el log: es un valor de PRUEBA que solo tiene
            // sentido con dos instancias en la misma PC. En un build
            // compartido, cada jugador está en su propia máquina y todos
            // arrancarían con el MISMO perfil forzado -- que es justo lo que
            // este campo existe para evitar.
            Debug.LogWarning($"[MultiplayerConnectionManager] Usando el perfil de prueba " +
                              $"'{profile}' (campo 'Debug Auth Profile'). Dejalo VACÍO antes de " +
                              "compartir un build.");
        }

        if (!AuthenticationService.Instance.IsSignedIn)
        {
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
        }

        servicesInitialized = true;
        Debug.Log($"[MultiplayerConnectionManager] Servicios listos. PlayerId: {AuthenticationService.Instance.PlayerId}");
    }

    // Público para el botón "Crear Sala". Devuelve el código de sala para
    // mostrar en pantalla, o null si falló (el error ya queda logueado acá,
    // quien llame solo necesita saber si tuvo éxito o no).
    public async Task<string> CreateSession()
    {
        try
        {
            ResetSessionExitFlags();
            await EnsureServicesInitialized();

            SessionOptions options = new SessionOptions { MaxPlayers = maxPlayers }.WithRelayNetwork();
            CurrentSession = await MultiplayerService.Instance.CreateSessionAsync(options);

            Debug.Log($"[MultiplayerConnectionManager] Sala creada. Código: {CurrentSession.Code}");
            SubscribeToSceneEventDiagnostics();
            return CurrentSession.Code;
        }
        catch (Exception e)
        {
            Debug.LogError($"[MultiplayerConnectionManager] Error creando sala: {e.Message}");
            return null;
        }
    }

    // Público para el botón "Unirse a Sala". Devuelve true si se pudo unir.
    public async Task<bool> JoinSession(string code)
    {
        try
        {
            ResetSessionExitFlags();
            await EnsureServicesInitialized();

            CurrentSession = await MultiplayerService.Instance.JoinSessionByCodeAsync(code);

            Debug.Log($"[MultiplayerConnectionManager] Unido a la sala {CurrentSession.Id}.");
            SubscribeToSceneEventDiagnostics();
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[MultiplayerConnectionManager] Error uniéndose a la sala '{code}': {e.Message}");
            return false;
        }
    }

    // Público para el botón "Abandonar Sala". Deja la sesión actual (como
    // host o como cliente) y desconecta Netcode -- ISession.LeaveAsync() ya
    // hace las dos cosas por dentro, por eso NO se llama a
    // NetworkManager.Singleton.Shutdown() acá (el propio SDK lo advierte:
    // usar Shutdown a mano mientras hay una sesión activa está mal).
    // No hace nada si no hay sesión activa (CurrentSession == null).
    public async Task LeaveSession()
    {
        if (CurrentSession == null) return;

        // Avisa que la desconexión que viene es NUESTRA y a propósito, para
        // que el rescate de más abajo (ReturnToMainMenuOnDisconnect) no la
        // confunda con "me echaron" y mande a cargar el Menú por segunda vez
        // encima de quien ya lo está haciendo.
        leavingOnPurpose = true;

        try
        {
            await CurrentSession.LeaveAsync();
            Debug.Log("[MultiplayerConnectionManager] Sala abandonada.");
        }
        catch (Exception e)
        {
            // Igual soltamos la referencia local -- aunque el LeaveAsync
            // remoto haya fallado (ej. sin conexión), no tiene sentido
            // seguir mostrando la Sala de Espera como si siguiéramos ahí.
            Debug.LogError($"[MultiplayerConnectionManager] Error abandonando la sala: {e.Message}");
        }
        finally
        {
            CurrentSession = null;
        }
    }

    // --- Rescate: te quedaste sin sesión en medio de la partida ---
    //
    // El caso típico es el host tocando "Menú" al terminar la ronda: cierra
    // la sala y los demás quedan en Gameplay.unity, desconectados, con el
    // mundo congelado y sin ningún camino de salida (bug A2, 26/8). También
    // cubre una caída de conexión de verdad.
    //
    // Vive acá y no en GameManager porque este componente es persistente:
    // sigue vivo durante el cambio de escena, que es justo cuando hace falta.
    //
    // OJO CON EL CICLO DE VIDA DE ESTAS DOS BANDERAS: NO se apagan al
    // terminar de salir, sino recién cuando se crea o se entra a una sesión
    // NUEVA (ver ResetSessionExitFlags). Apagarlas antes abría una carrera
    // real: Netcode dispara su OnClientDisconnectCallback DESPUÉS de que
    // LeaveAsync terminó, así que para cuando llegaba el aviso la bandera ya
    // estaba en false y el rescate salía a cargar el Menú encima de una
    // salida que ya estaba en curso.
    private bool leavingOnPurpose;
    private bool returningToMainMenu;

    private void ResetSessionExitFlags()
    {
        leavingOnPurpose = false;
        returningToMainMenu = false;
    }

    [Tooltip("Nombre de la escena del menú principal -- tiene que estar en Build Settings. Se " +
             "usa para volver ahí si te quedaste sin sesión en medio de la partida, y para saber " +
             "cuándo NO hace falta (si ya estás en el menú, no se toca nada).")]
    [SerializeField] private string mainMenuSceneName = "MainMenu";

    private void OnDisconnectedFromSession(ulong clientId)
    {
        if (NetworkManager.Singleton == null) return;

        // ¿Es MI desconexión? El host también recibe este callback por cada
        // cliente que se va, y en ese caso no hay nada que rescatar.
        if (clientId != NetworkManager.Singleton.LocalClientId) return;

        if (leavingOnPurpose || returningToMainMenu) return;

        // En el menú esto ya lo maneja RoomFlowController (Abandonar Sala),
        // que además sabe qué panel mostrar. Acá solo nos interesa la
        // desconexión durante la partida.
        if (SceneManager.GetActiveScene().name == mainMenuSceneName) return;

        string reason = NetworkManager.Singleton.DisconnectReason;
        Debug.LogWarning("[MultiplayerConnectionManager] Te quedaste sin sesión en medio de la " +
                          $"partida (motivo: '{reason}') -- volviendo al menú principal.");

        returningToMainMenu = true;
        StartCoroutine(ReturnToMainMenuOnDisconnect());
    }

    private IEnumerator ReturnToMainMenuOnDisconnect()
    {
        // La sesión remota ya no existe, pero la referencia local sí: sin
        // soltarla, el Menú creería que seguímos en una sala. LeaveSession()
        // atrapa el error esperable de "salir de algo que ya no está" y deja
        // CurrentSession en null igual.
        Task leaving = LeaveSession();

        float waited = 0f;
        while (!leaving.IsCompleted && waited < DisconnectCleanupTimeoutSeconds)
        {
            waited += Time.unscaledDeltaTime;
            yield return null;
        }

        SceneManager.LoadScene(mainMenuSceneName);
    }

    // Tope de espera (guardarraíl 8 del proyecto: nada de "esperar hasta que"
    // sin salida). Si la limpieza no termina, se vuelve al menú igual: mejor
    // eso que dejar al jugador mirando una partida muerta.
    private const float DisconnectCleanupTimeoutSeconds = 3f;

    // DIAGNÓSTICO (25/8, Fase 3): el cliente se queda colgado en la Sala de
    // Espera cuando el host aprieta "Empezar Partida" -- para saber en qué
    // paso exacto se corta la sincronización de escena de Netcode, esto
    // loguea CADA evento (Load, LoadComplete, LoadEventCompleted, etc.)
    // tanto del lado del host como del cliente. Vive ACÁ (no en
    // RoomFlowController) porque este componente es persistente -- sigue
    // vivo después de que RoomFlowController se destruye con el cambio de
    // escena, así que alcanza a loguear eventos que lleguen DESPUÉS de que
    // el host ya cambió de escena (ej. la confirmación de "LoadComplete"
    // del cliente, que llega recién ahí).
    //
    // Se suscribe apenas hay sesión activa (al final de CreateSession/
    // JoinSession, arriba). OJO (25/8): llamar a TrySubscribeSceneEvents()
    // directo acá NO alcanzaba del lado CLIENTE -- confirmado en vivo, el
    // host logueaba todo bien pero el cliente no logueaba NI el Synchronize
    // inicial (que el host SÍ confirmaba que le había llegado): en ese
    // instante exacto, NetworkManager.Singleton.SceneManager todavía podía
    // no existir del lado del cliente, aunque del lado host (que arranca
    // su propio SceneManager desde que es servidor) sí. OnClientConnectedCallback
    // es una señal más confiable de que Netcode terminó de arrancar de
    // verdad (StartHost/StartClient) -- se reintenta ahí también, no solo acá.
    private void SubscribeToSceneEventDiagnostics()
    {
        if (NetworkManager.Singleton == null) return;

        NetworkManager.Singleton.OnClientConnectedCallback -= OnNetcodeClientConnectedDiagnostics;
        NetworkManager.Singleton.OnClientConnectedCallback += OnNetcodeClientConnectedDiagnostics;

        // NUEVO (25/8): dos señales que todavía no estábamos escuchando --
        // OnClientDisconnectCallback (con DisconnectReason) y sobre todo
        // OnTransportFailure, que es justo el tipo de falla silenciosa de
        // bajo nivel (socket, fragmentación, lo que sea) que hasta ahora no
        // veíamos en ningún lado -- el cliente se queda sin recibir nada de
        // Gameplay y nunca supimos si es porque se cortó la conexión de
        // verdad o por otra cosa.
        NetworkManager.Singleton.OnClientDisconnectCallback -= OnNetcodeClientDisconnectedDiagnostics;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnNetcodeClientDisconnectedDiagnostics;
        NetworkManager.Singleton.OnTransportFailure -= OnNetcodeTransportFailureDiagnostics;
        NetworkManager.Singleton.OnTransportFailure += OnNetcodeTransportFailureDiagnostics;

        TrySubscribeSceneEvents(); // por si ya está listo para cuando esto corre (ej. el host)
    }

    private void OnNetcodeClientConnectedDiagnostics(ulong clientId)
    {
        TrySubscribeSceneEvents();
    }

    private void OnNetcodeClientDisconnectedDiagnostics(ulong clientId)
    {
        string reason = NetworkManager.Singleton != null ? NetworkManager.Singleton.DisconnectReason : "";
        Debug.LogWarning($"[MultiplayerConnectionManager] OnClientDisconnectCallback: clientId={clientId} " +
                          $"DisconnectReason='{reason}'");

        // Además de loguear, actúa: si el desconectado sos VOS y estás en
        // medio de una partida, te saca de ahí (ver OnDisconnectedFromSession).
        OnDisconnectedFromSession(clientId);
    }

    private void OnNetcodeTransportFailureDiagnostics()
    {
        Debug.LogError("[MultiplayerConnectionManager] OnTransportFailure -- el transporte de red falló " +
                        "a bajo nivel (ver NetworkManager.OnTransportFailure).");
    }

    private void TrySubscribeSceneEvents()
    {
        if (NetworkManager.Singleton == null || NetworkManager.Singleton.SceneManager == null) return;

        // -= antes de += por si esto se llama más de una vez (ej. reintento
        // desde el callback de arriba) -- no queremos loguear el mismo
        // evento duplicado.
        NetworkManager.Singleton.SceneManager.OnSceneEvent -= LogSceneEvent;
        NetworkManager.Singleton.SceneManager.OnSceneEvent += LogSceneEvent;
    }

    private void LogSceneEvent(SceneEvent sceneEvent)
    {
        Debug.Log($"[MultiplayerConnectionManager] SceneEvent: {sceneEvent.SceneEventType} | escena: " +
                  $"{sceneEvent.SceneName} | clientId: {sceneEvent.ClientId}");
    }

    // --- DEBUG: probar Create/Join sin construir todavía los botones de UI ---
    // Click derecho en el header de este componente en el Inspector, EN
    // PLAY, para disparar cualquiera de los dos. `async void` es aceptable
    // acá a propósito (no en el resto del proyecto): un ContextMenu no
    // tiene a quién devolverle un Task, y esto nunca se llama desde
    // gameplay real, solo a mano por el dev. No hace falta borrar esto
    // después -- queda inofensivo una vez que exista la UI real (Fase 2),
    // sigue sirviendo para debuggear sin pasar por los botones.

    [ContextMenu("DEBUG: Crear sala de prueba")]
    private async void DebugCreateSession()
    {
        string code = await CreateSession();
        Debug.Log(code != null
            ? $"[DEBUG] Sala creada. Código: {code}"
            : "[DEBUG] Falló la creación de sala -- mirá el error de arriba en la Console.");
    }

    [ContextMenu("DEBUG: Unirse a sala de prueba")]
    private async void DebugJoinSession()
    {
        if (string.IsNullOrEmpty(debugJoinCode))
        {
            Debug.LogWarning("[DEBUG] 'Debug Join Code' está vacío -- pegá ahí el código antes de unirte.");
            return;
        }

        bool joined = await JoinSession(debugJoinCode);
        Debug.Log(joined ? "[DEBUG] Unido correctamente." : "[DEBUG] Falló al unirse -- mirá el error de arriba en la Console.");
    }
}
