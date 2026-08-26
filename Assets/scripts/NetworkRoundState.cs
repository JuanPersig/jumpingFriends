using Unity.Netcode;
using UnityEngine;

// Fuente única de verdad de CUÁNDO arranca la ronda, CON QUÉ semilla de azar
// y CUÁNTOS jugadores hay. Es la base de toda la Fase 3: en vez de mandarse
// posiciones por la red, todos los clientes simulan lo mismo a partir de
// estos tres datos (ver plan-fase3-gameplay-en-red.md, sección 2).
//
// Vive en Gameplay.unity en un GameObject propio con un NetworkObject
// ("In-Scene Placed NetworkObject", mismo criterio ya decidido para los 4
// slots de jugador -- ver PlayerSlotAssigner).
//
// FUNCIONA TAMBIÉN SIN RED, a propósito: si abrís Gameplay.unity directo
// desde el Editor (sin pasar por el Menú ni por una sala), no hay ningún
// NetworkManager conectado y OnNetworkSpawn nunca corre. En ese caso esto se
// resuelve solo con reloj local y una semilla al azar. Es el mismo criterio
// que ya usaban PlayerCharacterSpawner ("si no hay selección, no toca nada")
// y CameraFollow ("sin tabla, comportamiento de siempre"): probar la escena
// suelta tiene que seguir siendo posible.
public class NetworkRoundState : NetworkBehaviour
{
    public static NetworkRoundState Instance { get; private set; }

    [Tooltip("Cuántos segundos después de cargar la escena arranca a correr la simulación. " +
             "Tiene que alcanzar para que el cliente MÁS LENTO termine de cargar y de mostrar " +
             "su pantalla negra + intro de cámara. Si te queda corto no se rompe nada (el que " +
             "llega tarde aparece ya en movimiento, la posición es absoluta), pero se ve feo.")]
    [SerializeField] private float startDelaySeconds = 7f;

    // Momento (en tiempo de RED) en que la simulación empieza a correr.
    // -1 = todavía no lo decidió nadie.
    private readonly NetworkVariable<double> gameplayStartTime = new NetworkVariable<double>(-1.0);
    private readonly NetworkVariable<int> obstacleSeed = new NetworkVariable<int>(0);
    private readonly NetworkVariable<int> playerCount = new NetworkVariable<int>(1);

    // Quién ganó, por índice de carril. -1 = todavía no se sabe.
    //
    // Es el que llegó MÁS LEJOS, que en este juego es exactamente el que
    // aguantó más: todos corren la misma pista a la misma velocidad, así que
    // "el último en morir" y "el que más distancia hizo" son el mismo
    // jugador. Por eso alcanza con anotar quién cayó último, sin comparar
    // puntajes.
    //
    // OJO CON EL ORDEN: esta va declarada ANTES que roundOver a propósito.
    // Netcode aplica las NetworkVariable en el orden en que están declaradas
    // en la clase, y quien reacciona al fin de ronda lee el ganador. Si
    // roundOver se aplicara primero, su OnValueChanged saldría con el ganador
    // todavía en -1 y nadie festejaría.
    private readonly NetworkVariable<int> winnerSlot = new NetworkVariable<int>(-1);

    // ¿Ya terminó la ronda? La decide el SERVIDOR cuando no queda nadie en
    // pie (ver PlayerSlot.EvaluateRoundEnd). Es distinto de "yo me morí": el
    // que pierde primero pasa a espectador y el mundo sigue para los demás.
    private readonly NetworkVariable<bool> roundOver = new NetworkVariable<bool>(false);

    public bool IsRoundOver => roundOver.Value;
    public int WinnerSlotIndex => winnerSlot.Value;

    // Público para PlayerSlot, que es quien lleva la cuenta de vivos. Solo
    // corre en el servidor: los clientes se enteran por las NetworkVariable.
    public void DeclareRoundOver(int winnerSlotIndex)
    {
        if (!IsServer || roundOver.Value) return;

        winnerSlot.Value = winnerSlotIndex;
        roundOver.Value = true;
        Debug.Log($"[NetworkRoundState] Ronda terminada. Ganó el carril {winnerSlotIndex} " +
                  "(el que llegó más lejos).");
    }

    // Valores de respaldo para el modo sin red (ver comentario de arriba).
    private double offlineStartTime = -1.0;
    private int offlineSeed;

    // Público: varios sistemas necesitan distinguir "partida en red" de
    // "Gameplay.unity abierta suelta desde el Editor" para elegir el camino
    // correcto (ej. si al morir el jugador local se termina la ronda de una,
    // o si hay que esperar a que caigan los demás).
    public bool IsNetworked =>
        NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

    // Reloj compartido. Con red es el tiempo de servidor (los clientes lo
    // estiman solos, Netcode ya se encarga); sin red, el tiempo local.
    private double CurrentTime =>
        IsNetworked ? NetworkManager.Singleton.ServerTime.Time : Time.timeAsDouble;

    private double StartTime =>
        IsNetworked ? gameplayStartTime.Value : offlineStartTime;

    public int ObstacleSeed => IsNetworked ? obstacleSeed.Value : offlineSeed;

    // Nunca menos de 1: un 0 acá dejaría a RoundLaneSetup sin ningún carril
    // que activar, y el juego arrancaría vacío sin decir por qué.
    public int PlayerCount => IsNetworked ? Mathf.Max(1, playerCount.Value) : 1;

    // ¿Ya sabemos cuándo arranca la ronda? Falso mientras el cliente todavía
    // no recibió el dato del servidor.
    public bool IsResolved => StartTime >= 0.0;

    // Segundos transcurridos DESDE que arrancó la simulación. Clampeado en 0
    // hacia atrás a propósito: antes del arranque todos los jugadores tienen
    // que estar quietos en su posición inicial, no en una posición negativa.
    public float ElapsedSeconds
    {
        get
        {
            if (!IsResolved) return 0f;
            return Mathf.Max(0f, (float)(CurrentTime - StartTime));
        }
    }

    // Momento de red en que la simulación arranca -- lo usa
    // StartupLoadingScreen para levantar la pantalla negra a tiempo en todos
    // los clientes a la vez.
    public double GameplayStartTime => StartTime;

    // Cuánto falta para el arranque. Negativo = ya arrancó. Infinito = el
    // dato todavía no llegó (así cualquier comparación "¿falta poco?" da
    // false sola, sin necesitar un chequeo aparte de IsResolved).
    public float SecondsUntilStart =>
        IsResolved ? (float)(StartTime - CurrentTime) : float.PositiveInfinity;

    private void Awake()
    {
        // Singleton a mano (NO Singleton<T>): ese helper hace
        // Destroy(gameObject) sobre el duplicado, y acá el GameObject
        // también lleva el NetworkObject -- llevárselo puesto rompería la
        // sincronización entera. Ver guardarraíl 6 del proyecto.
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;

        // Respaldo sin red, listo desde el primer frame: si nunca llega un
        // OnNetworkSpawn (escena abierta suelta), esto ya alcanza para que
        // todo el resto del juego funcione igual que antes.
        offlineSeed = Random.Range(int.MinValue, int.MaxValue);
        offlineStartTime = Time.timeAsDouble + startDelaySeconds;
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            obstacleSeed.Value = Random.Range(int.MinValue, int.MaxValue);
            playerCount.Value = NetworkManager.Singleton.ConnectedClientsIds.Count;
            gameplayStartTime.Value = NetworkManager.Singleton.ServerTime.Time + startDelaySeconds;
        }

        // DIAGNÓSTICO (Fase 3.1): loguea el estado resuelto en TODOS los
        // pares, no solo en el servidor. Es la única forma de comprobar que
        // el cliente recibió exactamente la misma semilla y el mismo instante
        // de arranque -- sin esto no hay manera de saber si el determinismo
        // está enganchado o si cada uno está simulando lo suyo.
        //
        // En el servidor los valores ya están puestos arriba, así que loguea
        // de una. En un cliente puede que todavía no hayan llegado, así que
        // además se engancha al cambio.
        LogResolvedStateOnce();
        gameplayStartTime.OnValueChanged += OnStartTimeReplicated;
        roundOver.OnValueChanged += OnRoundOverReplicated;
    }

    public override void OnNetworkDespawn()
    {
        gameplayStartTime.OnValueChanged -= OnStartTimeReplicated;
        roundOver.OnValueChanged -= OnRoundOverReplicated;
        base.OnNetworkDespawn();
    }

    private void OnRoundOverReplicated(bool previous, bool current)
    {
        if (current) GameManager.Instance?.TriggerRoundOver();
    }

    private void OnStartTimeReplicated(double previous, double current)
    {
        LogResolvedStateOnce();
    }

    private bool hasLoggedResolvedState;

    private void LogResolvedStateOnce()
    {
        if (hasLoggedResolvedState || !IsResolved) return;
        hasLoggedResolvedState = true;

        string role = IsServer ? "HOST" : "CLIENTE";
        Debug.Log($"[NetworkRoundState] ({role}) Ronda armada: {PlayerCount} jugador(es), " +
                  $"semilla {ObstacleSeed}, arranca en t={StartTime:0.000} " +
                  $"(faltan {SecondsUntilStart:0.00}s).");
    }

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }
}
