using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Services.Multiplayer;
using UnityEngine;

// Sincroniza qué personaje eligió cada jugador de la sala -- hasta ahora
// CharacterSelection.SelectedIndex era 100% local, nadie más se enteraba de
// qué elegiste. Esto usa las Player Properties del servicio de sesión (el
// mismo ISession que ya maneja MultiplayerConnectionManager) -- corre a
// nivel del servicio de Lobby/Sesión, NO de Netcode, así que funciona ya
// desde la Sala de Espera en MainMenu.unity, antes de que exista ningún
// NetworkObject de Gameplay.unity.
//
// Persistente (DontDestroyOnLoad) y singleton escrito a mano, mismo patrón
// que MultiplayerConnectionManager -- porque este dato también va a hacer
// falta en Gameplay.unity más adelante (Fase 3/4: cada slot de jugador
// mostrando el personaje real que esa persona eligió), no solo acá en el
// menú. Vive en el MISMO GameObject que MultiplayerConnectionManager/
// NetworkManager, por consistencia con ese script.
//
// Guarda el ÍNDICE (CharacterSelection.SelectedIndex) como string, no el
// nombre ni una referencia al prefab -- se asume que todos los clientes
// corren el mismo build, con la misma lista de personajes en el mismo
// orden en su propio CharacterSelection.
public class LobbyPlayerListSync : MonoBehaviour
{
    public static LobbyPlayerListSync Instance { get; private set; }

    private const string CharacterPropertyKey = "character";

    // Se dispara cada vez que cambia algo de la lista (alguien publicó su
    // personaje, entró o salió gente) -- para que la UI (o lo que sea que
    // consuma esto) sepa cuándo repintar, sin tener que pollear.
    public event Action PlayerListChanged;

    // playerId (el mismo Id que usa Unity Authentication/ISession) ->
    // índice de personaje elegido. Si un jugador todavía no publicó nada
    // (ej: se acaba de conectar y el Save todavía no viajó), simplemente
    // no aparece acá -- quien lea esto debería tratar "no está" como "usá
    // el personaje por defecto" (índice 0), no como un error.
    private readonly Dictionary<string, int> connectedPlayerCharacters = new();
    public IReadOnlyDictionary<string, int> ConnectedPlayerCharacters => connectedPlayerCharacters;

    private ISession session;

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

    // Llamalo apenas se entra a la Sala de Espera (ShowLobbyWaitingRoom) --
    // empieza a escuchar los cambios de los demás Y publica tu selección
    // actual (por si la elegiste ANTES de crear/unirte a la sala, que es
    // el orden normal del menú).
    public async void Subscribe()
    {
        session = MultiplayerConnectionManager.Instance?.CurrentSession;
        if (session == null) return;

        // -= ANTES DE += Y NO ES COSMÉTICO (26/8). Desde que se puede volver
        // del Gameplay a la Sala de Espera sin abandonar la sala, esto se
        // llama otra vez sobre la MISMA sesión: al salir del menú nunca nos
        // desuscribimos, a propósito (ver RoomFlowController.OnDestroy, que
        // deja este objeto vivo porque Gameplay todavía necesita leer qué
        // personaje eligió cada uno). Sin esto, cada vuelta sumaría un
        // listener más y los eventos se procesarían N veces.
        session.PlayerPropertiesChanged -= OnPlayerPropertiesChanged;
        session.PlayerPropertiesChanged += OnPlayerPropertiesChanged;
        session.PlayerJoined -= OnPlayerJoined;
        session.PlayerJoined += OnPlayerJoined;
        // PlayerHasLeft (no el PlayerLeaving obsoleto, ni el PlayerLeft
        // deprecado) -- se dispara DESPUÉS de que session.Players ya se
        // actualizó sin esa persona. Sin esto, cuando alguien se iba no
        // había ningún aviso directo que refrescara la lista a tiempo --
        // quedaba mostrando datos viejos (la skin de otro jugador) hasta
        // que algo más disparaba un refresh de casualidad.
        session.PlayerHasLeft -= OnPlayerHasLeft;
        session.PlayerHasLeft += OnPlayerHasLeft;

        RefreshFromSession();
        await PublishLocalCharacterSelection();
    }

    // Llamalo al abandonar la sala (OnLeaveRoomPressed) -- misma razón que
    // el resto de las suscripciones a la sesión: sin esto quedaría un
    // delegate colgado escuchando una sesión que ya no existe.
    public void Unsubscribe()
    {
        if (session == null) return;

        session.PlayerPropertiesChanged -= OnPlayerPropertiesChanged;
        session.PlayerJoined -= OnPlayerJoined;
        session.PlayerHasLeft -= OnPlayerHasLeft;
        session = null;

        connectedPlayerCharacters.Clear();
        PlayerListChanged?.Invoke();
    }

    // Público por si en algún momento hace falta republicar a mano (ej: si
    // más adelante se permite cambiar de personaje DESDE la Sala de
    // Espera, algo que hoy la UI no ofrece todavía).
    public async Task PublishLocalCharacterSelection()
    {
        if (session?.CurrentPlayer == null)
        {
            Debug.LogWarning("[LobbyPlayerListSync] PublishLocalCharacterSelection: session o " +
                              "session.CurrentPlayer es null -- no se publicó nada.");
            return;
        }
        if (CharacterSelection.Instance == null)
        {
            Debug.LogWarning("[LobbyPlayerListSync] PublishLocalCharacterSelection: " +
                              "CharacterSelection.Instance es null -- no se publicó nada.");
            return;
        }

        int index = CharacterSelection.Instance.SelectedIndex;
        session.CurrentPlayer.SetProperty(CharacterPropertyKey, new PlayerProperty(index.ToString()));

        try
        {
            await session.SaveCurrentPlayerDataAsync();
        }
        catch (Exception e)
        {
            Debug.LogError($"[LobbyPlayerListSync] Error publicando el personaje elegido: {e.Message}");
        }
    }

    private void OnPlayerPropertiesChanged()
    {
        RefreshFromSession();
    }

    private void OnPlayerJoined(string playerId)
    {
        RefreshFromSession();
    }

    private void OnPlayerHasLeft(string playerId)
    {
        RefreshFromSession();
    }

    private void RefreshFromSession()
    {
        if (session == null) return;

        connectedPlayerCharacters.Clear();
        foreach (IReadOnlyPlayer player in session.Players)
        {
            if (player.Properties != null &&
                player.Properties.TryGetValue(CharacterPropertyKey, out PlayerProperty property) &&
                int.TryParse(property.Value, out int index))
            {
                connectedPlayerCharacters[player.Id] = index;
            }
            // Si no hay dato (o no se pudo parsear), ese jugador simplemente
            // no aparece en el diccionario -- ver comentario en el campo de
            // arriba sobre por qué eso NO es un error.
        }

        PlayerListChanged?.Invoke();
    }
}
