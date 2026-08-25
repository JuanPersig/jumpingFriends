using System.Collections;
using TMPro;
using Unity.Netcode;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.SceneManagement;

// Maneja el flujo de "Crear Sala"/"Unirse a Sala" + la Sala de Espera --
// separado de MenuController (que sigue manejando selección de personaje y
// Configuración) para no sobrecargar un solo script con todo.
//
// Reemplaza al viejo botón "Jugar" de MenuController.OnPlayPressed: crear o
// unirse a una sala pasa a ser el único camino para llegar a Gameplay.unity
// (decisión del usuario, 20/8). El chequeo de "cámara calibrada" que tenía
// OnPlayPressed se reusa acá, tal cual -- sigue haciendo falta la cámara
// andando para poder jugar, sea que crees o te unas a una sala.
public class RoomFlowController : MonoBehaviour
{
    [Header("Paneles")]
    [SerializeField] private GameObject mainPanel;
    [SerializeField] private GameObject joinRoomPanel;
    [SerializeField] private GameObject lobbyWaitingRoomPanel;

    [Header("Crear Sala")]
    [Tooltip("Propio de 'Crear Sala' -- NO reusar joinErrorText acá. Ese texto es hijo de " +
             "JoinRoomPanel en la escena, que está apagado mientras se crea una sala (se ve " +
             "mainPanel en ese momento), así que un error de Crear Sala escrito ahí queda " +
             "seteado pero invisible. Este campo tiene que ser hijo de mainPanel.")]
    [SerializeField] private TMP_Text createErrorText;

    [Header("Unirse a Sala")]
    [SerializeField] private TMP_InputField joinCodeInput;
    [Tooltip("Muestra errores de 'Unirse a Sala' -- ver createErrorText de arriba para por qué " +
             "'Crear Sala' tiene el suyo propio en vez de compartir este.")]
    [SerializeField] private TMP_Text joinErrorText;

    [Header("Sala de Espera")]
    [SerializeField] private TMP_Text roomCodeText;
    [Tooltip("Cuántos Jumpers están REALMENTE visibles ahora mismo -- se actualiza solo, en " +
             "vivo, escuchando LobbyJumperSpawner.VisibleCountChanged. A PROPÓSITO no cuenta " +
             "session.Players.Count directo: alguien puede estar conectado a la sala pero su " +
             "Jumper seguir oculto un instante (esperando su primer dato de personaje) -- con " +
             "session.Players.Count el número se adelantaba a lo que se veía de verdad en " +
             "pantalla. No hace falta tocarlo a mano en ningún otro lado.")]
    [SerializeField] private TMP_Text playerCountText;
    [Tooltip("El mismo LobbyJumperSpawner de WaitingRoomPanel -- de acá sale VisiblePlayerCount " +
             "para el contador de arriba.")]
    [SerializeField] private LobbyJumperSpawner jumperSpawner;
    [Tooltip("Solo el host lo ve activo -- se decide con NetworkManager.Singleton.IsHost en " +
             "ShowLobbyWaitingRoom(), no antes (recién ahí Netcode ya está conectado).")]
    [SerializeField] private GameObject startGameButton;

    [Header("Pantalla de Carga")]
    [Tooltip("Overlay que se muestra mientras Crear Sala/Unirse a Sala esperan la respuesta del " +
             "servicio (CreateSession()/JoinSession() son async y pueden tardar unos segundos) -- " +
             "sin esto no había ningún feedback visual durante esa espera, parecía que el juego " +
             "se había colgado.")]
    [SerializeField] private GameObject loadingPanel;
    [SerializeField] private TMP_Text loadingText;

    [Header("Bloqueo sin cámara calibrada")]
    [Tooltip("Mismo aviso que ya usaba MenuController.OnPlayPressed -- se reusa acá porque " +
             "crear/unirse a una sala es ahora el único camino para jugar. Recomendado: " +
             "poné el MISMO GameObject que ya usás en MenuController (Volver a Main Panel ya " +
             "lo apaga solo al navegar).")]
    [SerializeField] private GameObject cameraNotConfiguredWarning;

    [Tooltip("PROVISORIO -- para probar sin webcam (ej. testeando con 2 instancias locales y " +
             "una sola cámara física). Tildado, salta el chequeo de cámara calibrada en Crear/" +
             "Unirse a Sala como si NativePoseInputSource.IsCalibrated ya fuera true. Acordate " +
             "de destildarlo antes de un build real -- sin esto, cualquiera podría jugar sin " +
             "cámara conectada.")]
    [SerializeField] private bool debugSkipCameraCheck;

    private bool CameraReady =>
        debugSkipCameraCheck ||
        (NativePoseInputSource.Instance != null && NativePoseInputSource.Instance.IsCalibrated);

    // Enganchá esto al botón "Crear Sala" del panel principal.
    public async void OnCreateRoomPressed()
    {
        if (!CameraReady)
        {
            if (cameraNotConfiguredWarning != null) cameraNotConfiguredWarning.SetActive(true);
            return;
        }

        SetCreateError("");
        if (mainPanel != null) mainPanel.SetActive(false);
        ShowLoading("Creando sala");
        string code = await MultiplayerConnectionManager.Instance.CreateSession();
        HideLoading();

        if (code == null)
        {
            if (mainPanel != null) mainPanel.SetActive(true);
            SetCreateError("No se pudo crear la sala. Revisá tu conexión e intentá de nuevo.");
            return;
        }

        ShowLobbyWaitingRoom();
    }

    // Enganchá esto al botón "Unirse a Sala" del panel principal -- solo
    // ABRE el panel con el input de código, todavía no conecta nada.
    public void OnOpenJoinRoom()
    {
        if (!CameraReady)
        {
            if (cameraNotConfiguredWarning != null) cameraNotConfiguredWarning.SetActive(true);
            return;
        }

        if (mainPanel != null) mainPanel.SetActive(false);
        if (joinRoomPanel != null) joinRoomPanel.SetActive(true);
        SetJoinError("");
    }

    // Enganchá esto al botón "Confirmar"/"Unirse" DENTRO del panel de
    // Unirse a Sala.
    public async void OnConfirmJoinRoom()
    {
        if (joinCodeInput == null) return;

        string code = joinCodeInput.text.Trim();
        if (string.IsNullOrEmpty(code))
        {
            SetJoinError("Escribí un código antes de unirte.");
            return;
        }

        SetJoinError("");
        if (joinRoomPanel != null) joinRoomPanel.SetActive(false);
        ShowLoading("Uniéndote a la sala");
        bool joined = await MultiplayerConnectionManager.Instance.JoinSession(code);
        HideLoading();

        if (!joined)
        {
            if (joinRoomPanel != null) joinRoomPanel.SetActive(true);
            SetJoinError("Código incorrecto\nNo te has podido unir");
            return;
        }

        ShowLobbyWaitingRoom();
    }

    // Enganchá esto al botón "Volver" del panel de Unirse a Sala.
    public void OnBackFromJoinRoom()
    {
        if (joinRoomPanel != null) joinRoomPanel.SetActive(false);
        if (mainPanel != null) mainPanel.SetActive(true);
    }

    private void ShowLobbyWaitingRoom()
    {
        if (mainPanel != null) mainPanel.SetActive(false);
        if (joinRoomPanel != null) joinRoomPanel.SetActive(false);
        if (lobbyWaitingRoomPanel != null) lobbyWaitingRoomPanel.SetActive(true);

        if (roomCodeText != null)
        {
            roomCodeText.text = MultiplayerConnectionManager.Instance.CurrentSession?.Code ?? "";
        }

        // NetworkManager.Singleton.IsHost -- NO uso "¿tengo código?" como
        // proxy para "¿soy el host?": un cliente que se UNIÓ también ve el
        // Code de la sesión (es un dato de la sala, no algo exclusivo de
        // quien la creó). IsHost es la fuente correcta, y ya es válida acá
        // porque el SDK conecta Netcode como parte de Create/JoinSession,
        // antes de que este método corra.
        if (startGameButton != null)
        {
            bool isHost = NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost;
            startGameButton.SetActive(isHost);
        }

        UpdatePlayerCountText();
        SubscribeToSessionPlayerEvents();
        if (jumperSpawner != null) jumperSpawner.VisibleCountChanged += UpdatePlayerCountText;

        if (LobbyPlayerListSync.Instance != null) LobbyPlayerListSync.Instance.Subscribe();
    }

    // ISession.PlayerJoined/PlayerHasLeft avisan solos cuando cambia quién
    // está en la sala (entra o sale alguien) -- no hace falta pollear nada
    // a mano, por eso nos suscribimos apenas se muestra la Sala de Espera y
    // nos desuscribimos apenas se abandona (OnLeaveRoomPressed), para no
    // dejar el delegate colgado escuchando una sesión que ya no existe.
    private void SubscribeToSessionPlayerEvents()
    {
        ISession session = MultiplayerConnectionManager.Instance?.CurrentSession;
        if (session == null) return;

        session.PlayerJoined += OnSessionPlayersChanged;
        session.PlayerHasLeft += OnSessionPlayersChanged;
    }

    private void UnsubscribeFromSessionPlayerEvents()
    {
        ISession session = MultiplayerConnectionManager.Instance?.CurrentSession;
        if (session == null) return;

        session.PlayerJoined -= OnSessionPlayersChanged;
        session.PlayerHasLeft -= OnSessionPlayersChanged;
    }

    // Firma Action<string> (playerId) -- no nos interesa QUIÉN entró/salió
    // acá, solo repintar el contador con el Players.Count actualizado.
    private void OnSessionPlayersChanged(string playerId)
    {
        UpdatePlayerCountText();
    }

    private void UpdatePlayerCountText()
    {
        if (playerCountText == null) return;

        ISession session = MultiplayerConnectionManager.Instance?.CurrentSession;
        if (session == null) return;

        // VisiblePlayerCount (cuántos Jumpers se ven de verdad), no
        // session.Players.Count (cuántos están conectados, aunque su
        // Jumper todavía esté oculto esperando el dato) -- así el número
        // nunca se adelanta a lo que se ve en pantalla.
        int visibleCount = jumperSpawner != null ? jumperSpawner.VisiblePlayerCount : session.Players.Count;
        playerCountText.text = $"{visibleCount}/{session.MaxPlayers} jugadores";
    }

    // Enganchá esto al botón "Abandonar Sala" de la Sala de Espera (antes
    // era el botón "Volver" -- ver historial: estaba enganchado por error a
    // MenuController.OnBackToMainMenu, que no sabe nada de
    // lobbyWaitingRoomPanel y solo prendía mainPanel sin apagar la Sala de
    // Espera, así que quedaban los dos paneles visibles a la vez).
    //
    // Ahora, además de ocultar el panel, deja la sesión de verdad
    // (MultiplayerConnectionManager.LeaveSession() desconecta Netcode por
    // dentro) ANTES de mostrar mainPanel -- si mostráramos mainPanel primero
    // y el LeaveAsync tardara, el jugador vería el menú principal mientras
    // técnicamente sigue conectado/hosteando de fondo.
    public async void OnLeaveRoomPressed()
    {
        // Desuscribir ANTES de LeaveSession() -- ese método deja
        // CurrentSession en null al terminar, y a partir de ahí ya no
        // tendríamos cómo encontrar el mismo objeto de sesión para sacarle
        // el listener (quedaría un delegate colgado hasta que el GC lo note).
        UnsubscribeFromSessionPlayerEvents();
        if (jumperSpawner != null) jumperSpawner.VisibleCountChanged -= UpdatePlayerCountText;
        if (LobbyPlayerListSync.Instance != null) LobbyPlayerListSync.Instance.Unsubscribe();

        if (MultiplayerConnectionManager.Instance != null)
        {
            await MultiplayerConnectionManager.Instance.LeaveSession();
        }

        if (lobbyWaitingRoomPanel != null) lobbyWaitingRoomPanel.SetActive(false);
        if (mainPanel != null) mainPanel.SetActive(true);
        SetCreateError("");
    }

    // Red de seguridad, no el camino normal de desuscribirse (ese es
    // OnLeaveRoomPressed, arriba). Este componente vive en el Canvas del
    // Menú -- NO es persistente -- pero session.PlayerJoined/PlayerHasLeft
    // los escuchamos sobre el ISession que sí sobrevive (vive en
    // MultiplayerConnectionManager, DontDestroyOnLoad). Hoy nunca hace
    // falta: la única forma de dejar la Sala de Espera es OnLeaveRoomPressed,
    // que ya desuscribe todo antes de que este objeto se destruya. Pero
    // cuando la Fase 3 haga que "Empezar Partida" cargue Gameplay.unity de
    // verdad (sin pasar por Abandonar Sala -- seguís en la MISMA sesión),
    // este GameObject SÍ se destruye con el cambio de escena, todavía
    // suscripto -- sin este OnDestroy, el próximo PlayerJoined/PlayerHasLeft
    // dispararía OnSessionPlayersChanged sobre un objeto ya destruido
    // (MissingReferenceException al tocar playerCountText.text). Los
    // métodos de abajo ya son idempotentes (chequean null / -= sobre un
    // delegate no suscripto es un no-op), así que no importa si esto corre
    // DESPUÉS de que OnLeaveRoomPressed ya desuscribió todo a mano.
    //
    // A PROPÓSITO no se toca acá LeaveSession() ni
    // LobbyPlayerListSync.Instance.Unsubscribe(): a diferencia de
    // OnLeaveRoomPressed, esto puede dispararse por un cambio de escena
    // mientras seguís conectado a la sala (Fase 3) -- llamar a LeaveSession()
    // acá te sacaría de la sala sin querer, y Unsubscribe() borraría
    // ConnectedPlayerCharacters, que Gameplay.unity todavía va a necesitar
    // leer (qué personaje eligió cada jugador).
    private void OnDestroy()
    {
        UnsubscribeFromSessionPlayerEvents();
        if (jumperSpawner != null) jumperSpawner.VisibleCountChanged -= UpdatePlayerCountText;
    }

    // Enganchá esto al botón "Empezar Partida" de la Sala de Espera (solo
    // visible para el host, ver ShowLobbyWaitingRoom).
    public void OnStartGamePressed()
    {
        // El botón ya está gateado por UI (ShowLobbyWaitingRoom lo apaga
        // para todo el que no sea host), pero el chequeo se repite acá
        // igual: NetworkManager.SceneManager.LoadScene tiene que llamarlo
        // el servidor -- un cliente que de algún modo lo llamara (bug de
        // UI, por ejemplo) no tiene que hacer nada.
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsHost) return;

        // NetworkManager.SceneManager.LoadScene (NO SceneManager.LoadScene
        // a secas, ese es el de UnityEngine.SceneManagement sin red) --
        // carga Gameplay.unity para TODOS los clientes conectados al mismo
        // tiempo, sincronizado. Con un SceneManager.LoadScene plano acá
        // solo cambiarías de escena en TU propio cliente, dejando a los
        // demás colgados en el Menú -- justo el bug que este método
        // reemplaza (ver el TODO que tenía antes de la Fase 3).
        //
        // DIAGNÓSTICO (25/8, cliente se queda colgado en la Sala de Espera):
        // LoadScene() devuelve un SceneEventProgressStatus -- si NO es
        // "Started", el pedido se rechazó de entrada (sin tirar excepción)
        // y antes esto quedaba en silencio total. Se loguea para saber SI
        // el host realmente arrancó el envío del evento de escena a los
        // clientes, o si nunca salió de acá.
        SceneEventProgressStatus status = NetworkManager.Singleton.SceneManager.LoadScene("Gameplay", LoadSceneMode.Single);
        Debug.Log($"[RoomFlowController] NetworkManager.SceneManager.LoadScene(\"Gameplay\") -> {status}");
    }

    private void SetJoinError(string message)
    {
        if (joinErrorText != null) joinErrorText.text = message;
    }

    private void SetCreateError(string message)
    {
        if (createErrorText != null) createErrorText.text = message;
    }

    // Referencia al coroutine de los puntitos animados -- se guarda para
    // poder cortarlo en HideLoading() (si no, seguiría corriendo de fondo
    // escribiendo texto sobre un panel ya apagado).
    private Coroutine loadingDotsCoroutine;

    private void ShowLoading(string message)
    {
        if (loadingPanel != null) loadingPanel.SetActive(true);

        if (loadingDotsCoroutine != null) StopCoroutine(loadingDotsCoroutine);
        loadingDotsCoroutine = StartCoroutine(AnimateLoadingDots(message));
    }

    private void HideLoading()
    {
        if (loadingDotsCoroutine != null)
        {
            StopCoroutine(loadingDotsCoroutine);
            loadingDotsCoroutine = null;
        }
        if (loadingPanel != null) loadingPanel.SetActive(false);
    }

    // Va rotando "Creando sala.", "Creando sala..", "Creando sala..." en
    // loop cada 0.6s -- la animación típica de puntitos de carga, mientras
    // se espera CreateSession()/JoinSession(). A propósito NO incluye un
    // frame sin puntos -- siempre se ve al menos uno, tope 3.
    private IEnumerator AnimateLoadingDots(string baseMessage)
    {
        string[] dotFrames = { ".", "..", "..." };
        int frame = 0;

        while (true)
        {
            if (loadingText != null) loadingText.text = baseMessage + dotFrames[frame % dotFrames.Length];
            frame++;
            yield return new WaitForSeconds(0.6f);
        }
    }
}
