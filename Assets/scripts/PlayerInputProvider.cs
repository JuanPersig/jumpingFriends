using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

// Formato del mensaje que manda el emisor de Python: {"state": "jumping"}
[System.Serializable]
public class StateMessage
{
    public string state;
}

// Capa intermedia entre "de dónde viene el input" (hoy: UDP local desde Python)
// y "quién lo consume" (los minijuegos). Los minijuegos NUNCA deberían
// escuchar UDP directamente: solo se suscriben a OnJump / OnCrouch / OnStand.
// El día que se agregue multijugador online, esta clase es la única que
// cambia (la fuente del evento pasa de ser UDP local a ser la red), y
// ningún minijuego necesita tocarse.
public class PlayerInputProvider : Singleton<PlayerInputProvider>
{
    [Header("Configuración de red")]
    [SerializeField] private int listenPort = 5555;

    public event Action OnJump;
    public event Action OnCrouch;
    public event Action OnStand;

    private UdpClient udpClient;
    private Thread listenThread;
    private volatile bool isListening = false;

    // Los paquetes llegan en un hilo aparte; los guardamos acá y los
    // procesamos en Update() (hilo principal), porque Unity no permite
    // tocar el motor (ni disparar estos eventos con seguridad) desde
    // un hilo que no sea el principal.
    private readonly Queue<string> pendingMessages = new Queue<string>();
    private readonly object queueLock = new object();

    protected override void Awake()
    {
        base.Awake();
        if (Instance != this) return; // instancia duplicada, ya se está autodestruyendo
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        StartListening();
    }

    private void StartListening()
    {
        try
        {
            udpClient = new UdpClient(listenPort);
            isListening = true;
            listenThread = new Thread(ListenLoop) { IsBackground = true };
            listenThread.Start();
            Debug.Log($"[PlayerInputProvider] Escuchando UDP en puerto {listenPort}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[PlayerInputProvider] No se pudo iniciar el listener UDP: {e.Message}");
        }
    }

    private void ListenLoop()
    {
        IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, listenPort);
        while (isListening)
        {
            try
            {
                byte[] data = udpClient.Receive(ref remoteEndPoint);
                string json = Encoding.UTF8.GetString(data);

                lock (queueLock)
                {
                    pendingMessages.Enqueue(json);
                }
            }
            catch (SocketException se)
            {
                // OJO: antes esto cortaba el loop siempre, asumiendo que
                // SIEMPRE es el cierre esperado del socket. Si el problema
                // es otra cosa (ej: firewall, permisos), nos quedábamos sin
                // saberlo. Ahora lo logueamos igual, salvo que ya estemos
                // cerrando (isListening == false), que ahí sí es el cierre normal.
                if (isListening)
                {
                    Debug.LogError($"[PlayerInputProvider] SocketException inesperada: {se.SocketErrorCode} - {se.Message}");
                }
                break;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PlayerInputProvider] Error recibiendo paquete: {e.GetType().Name} - {e.Message}");
            }
        }
    }

    private void Update()
    {
        string json = null;

        lock (queueLock)
        {
            if (pendingMessages.Count > 0)
            {
                json = pendingMessages.Dequeue();
            }
        }

        if (json != null)
        {
            ProcessMessage(json);
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            StateMessage message = JsonUtility.FromJson<StateMessage>(json);

            switch (message.state)
            {
                case "jumping":
                    OnJump?.Invoke();
                    break;
                case "crouching":
                    OnCrouch?.Invoke();
                    break;
                case "standing":
                    OnStand?.Invoke();
                    break;
                default:
                    Debug.LogWarning($"[PlayerInputProvider] Estado desconocido: '{message.state}'");
                    break;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PlayerInputProvider] No se pudo parsear el mensaje '{json}': {e.Message}");
        }
    }

    private void OnDestroy() => StopListening();
    private void OnApplicationQuit() => StopListening();

    private void StopListening()
    {
        isListening = false;
        udpClient?.Close();
        listenThread?.Join(200);
    }
}
