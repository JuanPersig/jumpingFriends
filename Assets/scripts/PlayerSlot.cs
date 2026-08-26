using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

// Identidad de red de cada uno de los 4 slots de jugador de Gameplay.unity
// (Fase 3.2, paso 2). Responde dos preguntas que hasta ahora nadie podía
// contestar del lado del cliente:
//
//   1. "¿Cuál de los 4 soy yo?" -- PlayerSlotAssigner ya reparte la
//      propiedad de los slots en el servidor, pero ese resultado no lo leía
//      nadie. Acá cada cliente encuentra el suyo preguntando IsOwner.
//   2. "¿Qué personaje eligió el dueño de ESTE slot?" -- antes cada máquina
//      le ponía a TODOS los carriles el personaje elegido localmente, así
//      que se veía el mismo skin duplicado en las dos pantallas.
//
// Va en el MISMO GameObject que el NetworkObject de cada slot (player,
// player2, player3, player4), con su Slot Index puesto a mano: 0, 1, 2, 3.
//
// El personaje se sincroniza por Netcode y NO reusando el
// LobbyPlayerListSync de la Sala de Espera: ese mapea playerId del servicio
// de sesión -> personaje, y acá lo que tenemos es el clientId de Netcode.
// Cruzar esas dos identidades es un problema aparte; publicar el índice
// directamente sobre el slot es más corto y no depende de que la sesión
// siga viva durante la partida.
[RequireComponent(typeof(NetworkObject))]
public class PlayerSlot : NetworkBehaviour
{
    [Tooltip("0 para 'player', 1 para 'player2', 2 para 'player3', 3 para 'player4'. Tiene que " +
             "coincidir con la posición de este slot en los arrays de PlayerSlotAssigner y " +
             "RoundLaneSetup -- son la misma numeración.")]
    [SerializeField] private int slotIndex = -1;

    [Tooltip("El DeathCameraAnchor hijo de ESTE slot -- el punto al que viaja la cámara cuando " +
             "este jugador muere. Cada carril tiene el suyo; arrastrá el que cuelga de este " +
             "mismo GameObject. GameOutroSequence lo lee de acá en runtime, en vez de tener uno " +
             "fijo en el Inspector (antes estaba clavado al slot 0, así que la cinemática de " +
             "muerte se veía siempre sobre el personaje del host).")]
    [SerializeField] private Transform deathCameraAnchor;

    [Tooltip("Nombre EXACTO del estado de muerte en el Animator Controller. Mismo valor que el " +
             "de GameOutroSequence -- se usa cuando este carril muere en OTRA máquina y hay que " +
             "reproducir su caída acá.")]
    [SerializeField] private string deathClipName = "Death01";

    // (Acá vivían Victory Clip Name y Victory Delay Seconds, sacados el 26/8
    // junto con la animación de festejo -- ver OnRoundOver.)

    public int SlotIndex => slotIndex;
    public Transform DeathCameraAnchor => deathCameraAnchor;
    public RunnerController Runner => runner;

    // Índice dentro de CharacterSelection. -1 = el dueño todavía no publicó
    // el suyo; quien lo lea debería esperar, no asumir el 0.
    //
    // Escribe el DUEÑO, lee todo el mundo. Ojo con el momento: al spawnear,
    // los NetworkObject in-scene son propiedad del SERVIDOR, así que el host
    // publica su personaje en los 4 slots. Cuando PlayerSlotAssigner le pasa
    // la propiedad de uno a un cliente, ese cliente publica el suyo y pisa el
    // valor. Converge solo; el valor del host queda como respaldo razonable
    // si el cliente nunca llega a publicar (ej. entró sin pasar por el menú).
    private readonly NetworkVariable<int> characterIndex = new NetworkVariable<int>(
        -1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    // ¿PlayerSlotAssigner ya le dio este carril a alguien de verdad?
    //
    // Hace falta porque "soy el dueño" NO alcanza para decir "este carril es
    // mío": al spawnear, los NetworkObject in-scene son propiedad del
    // SERVIDOR, así que el host es dueño de los CUATRO. Sin esta marca, el
    // host reclamaría también los slots que no le tocan (los que quedan
    // libres nunca cambian de dueño, así que se quedarían suyos para
    // siempre) y Local terminaría apuntando a cualquiera de ellos.
    private readonly NetworkVariable<bool> assigned = new NetworkVariable<bool>(false);

    // Público para PlayerSlotAssigner, que corre solo en el servidor.
    public void MarkAssigned()
    {
        if (!IsServer) return;
        assigned.Value = true;
    }

    // Este carril es mío si me lo asignaron Y soy su dueño.
    private bool IsMine => assigned.Value && IsOwner;

    // ¿El dueño de este carril ya se quedó sin vidas? Lo escribe él y lo leen
    // todos. Sin esto, cada máquina solo se entera de SUS propias muertes: en
    // la pantalla del host el personaje del cliente seguía corriendo aunque
    // el cliente ya hubiera perdido (bug reportado el 25/8).
    private readonly NetworkVariable<bool> dead = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    public bool IsDead => dead.Value;

    // El slot de ESTE cliente, o null si todavía no se resolvió la propiedad.
    public static PlayerSlot Local { get; private set; }

    // Se dispara cuando este cliente descubre cuál es su slot. Lo escuchan
    // los sistemas que hasta ahora apuntaban a mano al slot 0 (cámara,
    // cinemática de muerte).
    public static event System.Action<PlayerSlot> LocalSlotResolved;

    private PlayerCharacterSpawner characterSpawner;
    private RunnerController runner;

    private void Awake()
    {
        if (!all.Contains(this)) all.Add(this);

        characterSpawner = GetComponent<PlayerCharacterSpawner>();
        runner = GetComponent<RunnerController>();

        if (slotIndex < 0)
        {
            Debug.LogError($"[PlayerSlot] '{name}' no tiene Slot Index puesto en el Inspector " +
                            "(quedó en -1). Poné 0/1/2/3 según el carril que le toca.");
        }
    }

    public override void OnNetworkSpawn()
    {
        characterIndex.OnValueChanged += OnCharacterIndexChanged;
        // La asignación llega DESPUÉS del spawn (PlayerSlotAssigner corre en
        // su propio Start), así que hay que reevaluar cuando cambie.
        assigned.OnValueChanged += OnAssignedChanged;
        dead.OnValueChanged += OnDeadChanged;

        ClaimIfMine();
        ApplyNetworkedCharacter();
    }

    // La muerte del jugador de ESTA máquina se avisa al resto poniendo la
    // NetworkVariable. La cinemática local (cámara + animación) la sigue
    // manejando GameOutroSequence, como siempre.
    private void OnLocalGameOver()
    {
        // Vale tanto para la partida en red (mi carril) como para la escena
        // suelta (slot 0). Congelar acá es lo que reemplaza al viejo chequeo
        // global de IsGameOver que tenía RunnerController.Update().
        bool mine = IsSpawned ? IsMine : slotIndex == 0;
        if (!mine) return;

        if (runner != null) runner.Frozen = true;
        if (IsSpawned) dead.Value = true;
    }

    // Fin de ronda: nadie se mueve más, y el ganador festeja.
    //
    // El freeze acá es lo que corta el input del que TODAVÍA no había muerto
    // (bug reportado el 26/8: al terminar la ronda el host seguía respondiendo
    // a saltos y agachadas). RunnerController mira Frozen por carril, y hasta
    // ahora solo se ponía al morir cada uno.
    // Al terminar la ronda no se mueve más nadie. Este freeze es lo que corta
    // el input del que TODAVÍA no había muerto (bug del 26/8: al terminar la
    // ronda el host seguía respondiendo a saltos y agachadas) --
    // RunnerController mira Frozen por carril, y hasta ahora solo se ponía
    // cuando moría cada uno.
    //
    // Acá vivía una animación de festejo para el ganador (Dance_Loop). Se
    // sacó el 26/8 porque no quedaba bien: la ronda termina cuando cae el
    // último, así que el ganador venía de reproducir su propia caída y tenía
    // que levantarse del piso para bailar. Quién ganó SÍ se sigue calculando
    // y replicando (NetworkRoundState.WinnerSlotIndex) para la pantalla de
    // resultados.
    private void OnRoundOver()
    {
        if (runner != null) runner.Frozen = true;
    }

    // --- Cuántos siguen en pie (solo le importa al servidor) ---

    // Todos los slots vivos de la escena. Lista estática porque los 4 están
    // puestos a mano y nunca se crean ni se destruyen (ver PlayerSlotAssigner).
    private static readonly List<PlayerSlot> all = new List<PlayerSlot>();
    public static IReadOnlyList<PlayerSlot> All => all;

    public bool IsAssigned => assigned.Value;

    // El primer rival con vida distinto de este slot, o null si no queda
    // ninguno. Lo usa GameOutroSequence para elegir a quién mirar cuando
    // pasás a espectador. Por orden de carril, que es el criterio simple ya
    // acordado en multiplayer-plan.md (elegir a mano queda para más adelante).
    public static PlayerSlot FirstAliveOther(PlayerSlot except)
    {
        foreach (PlayerSlot slot in all)
        {
            if (slot == null || slot == except) continue;
            if (!slot.IsAssigned || slot.IsDead) continue;
            return slot;
        }
        return null;
    }

    // Cuando muere alguien, el servidor mira si queda alguien en pie. Los
    // clientes no deciden nada: se enteran por NetworkRoundState.
    //
    // LA RONDA TERMINA CUANDO MUEREN TODOS (decidido 26/8), no cuando queda
    // uno. Se evaluó "último en pie" -- era la regla original del 20/8 -- y
    // se cambió porque en ESTE juego no altera quién gana: todos corren la
    // misma pista a la misma velocidad, así que el que sobrevive más es el
    // que llega más lejos, y son la misma persona. Lo que sí cambiaba era la
    // experiencia: con 2 jugadores la primera muerte cortaba la carrera del
    // otro de golpe, y el modo espectador no llegaba a verse nunca.
    //
    // Con esta regla cada uno completa su propia carrera, el espectador tiene
    // sentido, y los dos ven el resultado con distancias comparables.
    private void EvaluateRoundEnd()
    {
        if (!IsServer) return;

        int alive = 0;
        foreach (PlayerSlot slot in all)
        {
            if (slot != null && slot.IsAssigned && !slot.IsDead) alive++;
        }

        if (alive > 0 || NetworkRoundState.Instance == null) return;

        // El último en caer es el que más aguantó, o sea el ganador. Como
        // esto se llama desde el OnDeadChanged del que acaba de morir, ese
        // somos nosotros.
        NetworkRoundState.Instance.DeclareRoundOver(slotIndex);
    }

    private void OnDeadChanged(bool previous, bool current)
    {
        if (!current) return;

        EvaluateRoundEnd();

        // En NUESTRA máquina el muerto ya lo maneja GameOutroSequence (con
        // cámara, animación y todo). Acá solo nos interesa el caso remoto: un
        // carril cuyo dueño perdió en otra pantalla y que, sin esto, seguiría
        // corriendo en la nuestra.
        if (IsMine) return;
        if (runner == null) return;

        runner.Frozen = true;
        runner.PlayDeathAnimation(deathClipName);
    }

    public override void OnNetworkDespawn()
    {
        characterIndex.OnValueChanged -= OnCharacterIndexChanged;
        assigned.OnValueChanged -= OnAssignedChanged;
        dead.OnValueChanged -= OnDeadChanged;
        if (Local == this) Local = null;
        UnsubscribeFromLocalInput();
        base.OnNetworkDespawn();
    }

    private void OnAssignedChanged(bool previous, bool current)
    {
        ClaimIfMine();
    }

    // PlayerSlotAssigner reparte la propiedad DESPUÉS del spawn, así que
    // OnNetworkSpawn por sí solo no alcanza: hay que enterarse también acá.
    public override void OnGainedOwnership()
    {
        ClaimIfMine();
        base.OnGainedOwnership();
    }

    public override void OnLostOwnership()
    {
        // ClaimIfMine se encarga de las dos mitades: suelta Local y deja de
        // escuchar el input. Pasa de verdad cuando el host arranca siendo
        // dueño de los 4 slots y después le cede uno a un cliente.
        ClaimIfMine();
        base.OnLostOwnership();
    }

    private void ClaimIfMine()
    {
        // El personaje se publica apenas somos dueños, aunque la asignación
        // todavía no haya llegado: es idempotente y así el valor viaja lo
        // antes posible.
        if (IsOwner) PublishLocalCharacter();

        // Solo TU carril le resta vidas a TU GameManager. Sin esto, los
        // cuatro RunnerController de cada máquina le reportaban al mismo
        // GameManager global: si chocaba el personaje del otro jugador,
        // perdías la vida vos.
        if (runner != null) runner.ReportsHits = IsMine;

        if (!IsMine)
        {
            // Perdimos el carril (o nunca fue nuestro): dejar de escuchar el
            // input, si lo estábamos haciendo.
            if (Local == this) Local = null;
            UnsubscribeFromLocalInput();
            return;
        }

        bool isNew = Local != this;
        Local = this;
        SubscribeToLocalInput();

        if (isNew)
        {
            Debug.Log($"[PlayerSlot] Este cliente maneja el slot {slotIndex} ({name}).");
            LocalSlotResolved?.Invoke(this);
        }
    }

    private void PublishLocalCharacter()
    {
        if (CharacterSelection.Instance == null) return; // escena abierta suelta, sin pasar por el menú
        characterIndex.Value = CharacterSelection.Instance.SelectedIndex;
    }

    private void OnCharacterIndexChanged(int previous, int current)
    {
        ApplyNetworkedCharacter();
    }

    private void ApplyNetworkedCharacter()
    {
        if (characterIndex.Value < 0) return; // el dueño todavía no publicó
        if (characterSpawner == null) return;

        characterSpawner.ApplyCharacter(characterIndex.Value);
    }

    // --- Input: solo el carril propio escucha, y lo replica al resto ---
    //
    // Antes CADA RunnerController de la escena escuchaba el
    // PlayerInputProvider global, así que un solo salto real movía a los 4
    // carriles a la vez. Ahora se suscribe únicamente el slot que es tuyo.
    //
    // El evento se aplica LOCALMENTE primero y recién después se manda por
    // red: así tu propio personaje responde sin ni un frame de latencia, que
    // es lo único que se siente al jugar. Los demás lo ven unos milisegundos
    // más tarde, que en un juego entre amigos no se nota.

    private bool listeningToLocalInput;

    private void Start()
    {
        // Sin red (Gameplay.unity abierta suelta desde el Editor) nadie va a
        // spawnear nada ni a repartir propiedad, así que OnNetworkSpawn no
        // corre nunca y sin esto el juego quedaría sin input. En ese caso el
        // slot 0 hace de "jugador local", que es justo el único carril que
        // RoundLaneSetup deja activo con la ronda de 1.
        if (GameManager.Instance != null)
        {
            GameManager.Instance.GameOver += OnLocalGameOver;
            GameManager.Instance.RoundOver += OnRoundOver;
        }

        bool networked = NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
        if (networked) return; // con red manda ClaimIfMine, que ya corrió o va a correr

        bool mine = slotIndex == 0;
        if (runner != null) runner.ReportsHits = mine;
        if (mine) SubscribeToLocalInput();
    }

    private void SubscribeToLocalInput()
    {
        if (listeningToLocalInput) return;

        if (PlayerInputProvider.Instance == null)
        {
            Debug.LogError("[PlayerSlot] No se encontró PlayerInputProvider.Instance -- este " +
                            "carril no va a responder al input. ¿Existe ese componente?");
            return;
        }

        PlayerInputProvider.Instance.OnJump += OnLocalJump;
        PlayerInputProvider.Instance.OnCrouch += OnLocalCrouch;
        PlayerInputProvider.Instance.OnStand += OnLocalStand;
        listeningToLocalInput = true;
    }

    private void UnsubscribeFromLocalInput()
    {
        if (!listeningToLocalInput) return;
        listeningToLocalInput = false;

        if (PlayerInputProvider.Instance == null) return;
        PlayerInputProvider.Instance.OnJump -= OnLocalJump;
        PlayerInputProvider.Instance.OnCrouch -= OnLocalCrouch;
        PlayerInputProvider.Instance.OnStand -= OnLocalStand;
    }

    // NO se llama base.OnDestroy() acá a propósito: NetworkBehaviour no
    // declara OnDestroy como virtual, así que esto es un método propio de
    // Unity, no un override.
    private void OnDestroy()
    {
        all.Remove(this);
        UnsubscribeFromLocalInput();
        if (GameManager.Instance != null)
        {
            GameManager.Instance.GameOver -= OnLocalGameOver;
            GameManager.Instance.RoundOver -= OnRoundOver;
        }
        if (Local == this) Local = null; // static: sin esto queda apuntando a un objeto destruido
    }

    private void OnLocalJump()
    {
        runner?.TriggerJump();
        if (IsSpawned) JumpRpc();
    }

    private void OnLocalCrouch()
    {
        runner?.TriggerCrouch();
        if (IsSpawned) CrouchRpc();
    }

    private void OnLocalStand()
    {
        runner?.TriggerStand();
        if (IsSpawned) StandRpc();
    }

    // SendTo.NotOwner: va a todas las máquinas MENOS la que lo originó (que
    // ya lo aplicó local). Cuando lo manda un cliente, Netcode lo rutea solo
    // a través del servidor -- no hace falta un RPC de ida al host y otro de
    // vuelta a los demás.
    [Rpc(SendTo.NotOwner)] private void JumpRpc() => runner?.TriggerJump();
    [Rpc(SendTo.NotOwner)] private void CrouchRpc() => runner?.TriggerCrouch();
    [Rpc(SendTo.NotOwner)] private void StandRpc() => runner?.TriggerStand();
}
