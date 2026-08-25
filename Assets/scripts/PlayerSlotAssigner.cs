using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

// Fase 3 del plan de multijugador -- primer script que toca Netcode de
// verdad. Corre SOLO en el servidor (host, no hay dedicated server en este
// proyecto): cuando cada cliente llega a Gameplay.unity, le asigna la
// propiedad (NetworkObject.ChangeOwnership) de uno de los 4 GameObjects
// "player"/"player2"/"player3"/"player4" YA armados a mano en la escena --
// arquitectura ya decidida en multiplayer-plan.md ("In-Scene Placed
// NetworkObjects"): esos 4 nunca se crean ni se destruyen, siempre están
// ahí, cambia solamente A QUIÉN pertenecen.
//
// Vive en Gameplay.unity, NO es persistente (no hace falta, se resuelve una
// sola vez por partida) -- lee NetworkManager.Singleton, que sí es el
// persistente de MainMenu.unity (DontDestroyOnLoad) y ya está conectado
// para cuando esta escena carga (la conexión pasó en la Sala de Espera,
// antes de "Empezar Partida").
public class PlayerSlotAssigner : MonoBehaviour
{
    [Tooltip("Los hasta 4 GameObjects 'player' ya armados a mano en la escena, cada uno con " +
             "su propio componente Network Object -- MISMO orden que RoundLaneSetup.playerSlots " +
             "(mantenelos consistentes entre los dos, aunque son arrays separados).")]
    [SerializeField] private NetworkObject[] playerSlots;

    // clientId -> slot ya asignado. Sirve para dos cosas: no asignarle dos
    // veces al mismo cliente si algo dispara AssignSlot de más, y saber qué
    // slots ya están ocupados al elegir el próximo libre.
    private readonly Dictionary<ulong, int> assignedSlots = new();

    private void Start()
    {
        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("[PlayerSlotAssigner] No hay NetworkManager.Singleton en la escena -- " +
                            "¿se llegó a Gameplay.unity sin pasar por la Sala de Espera?");
            return;
        }

        // Un cliente puro no tiene permiso para llamar a ChangeOwnership (y
        // no le hace falta: recibe el resultado ya replicado por Netcode
        // apenas el servidor lo asigna, sin que este script tenga que hacer
        // nada de su lado).
        if (!NetworkManager.Singleton.IsServer) return;

        // Red de seguridad para cualquier conexión que llegue DESPUÉS de
        // este punto (no debería pasar en la práctica -- el diseño actual
        // no deja entrar gente a mitad de partida -- pero es gratis cubrirse).
        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;

        // Los clientes que se unieron durante la Sala de Espera YA están
        // conectados de antes -- su propio OnClientConnectedCallback
        // disparó en MainMenu.unity, ANTES de que este componente
        // existiera (recién se instancia al cargar Gameplay.unity), así
        // que suscribirse solo al callback de arriba no alcanza: hay que
        // barrer ConnectedClientsIds acá (incluye al propio host) para no
        // dejar a nadie sin slot.
        foreach (ulong clientId in NetworkManager.Singleton.ConnectedClientsIds)
        {
            AssignSlot(clientId);
        }
    }

    private void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
        }
    }

    private void OnClientConnected(ulong clientId)
    {
        AssignSlot(clientId);
    }

    private void AssignSlot(ulong clientId)
    {
        if (assignedSlots.ContainsKey(clientId)) return; // ya tiene uno

        for (int slot = 0; slot < playerSlots.Length; slot++)
        {
            if (playerSlots[slot] == null) continue;
            if (IsSlotTaken(slot)) continue;

            playerSlots[slot].ChangeOwnership(clientId);
            assignedSlots[clientId] = slot;
            Debug.Log($"[PlayerSlotAssigner] Cliente {clientId} -> slot {slot} ({playerSlots[slot].name}).");
            return;
        }

        // No debería pasar: MaxPlayers de la sesión (ver
        // MultiplayerConnectionManager) ya limita a 4 desde antes de llegar
        // acá. Mejor un error visible que un jugador silenciosamente sin
        // personaje.
        Debug.LogError($"[PlayerSlotAssigner] Cliente {clientId} se conectó pero no quedan slots " +
                        "libres (¿más de 4 jugadores?). No se le asignó ningún personaje.");
    }

    private bool IsSlotTaken(int slot)
    {
        foreach (int takenSlot in assignedSlots.Values)
        {
            if (takenSlot == slot) return true;
        }
        return false;
    }
}
