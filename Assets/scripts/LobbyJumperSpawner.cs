using System;
using System.Collections.Generic;
using Unity.Services.Multiplayer;
using UnityEngine;

// Muestra un Jumper saltando por cada jugador conectado a la sala, con el
// personaje que cada uno eligió DE VERDAD (vía LobbyPlayerListSync) -- no
// el propio repetido N veces. Es puramente decorativo, igual que el Jumper
// del menú principal -- ninguno reacciona a la cámara real de esa persona
// (decisión ya tomada en el plan de multijugador, sección 6 -- "Sala de
// espera (lobby)"). Pero SOLO el Jumper de session.CurrentPlayer (vos)
// salta siguiendo tu mouse local -- el resto salta con una altura FIJA
// (ver MenuMouseJumper.SetUseMouseForHeight), para no verse todos copiando
// tu propio mouse como si fuera el de ellos.
//
// Enganchado directo al ciclo de vida de GameObject en vez de suscribirse
// a mano (como RoomFlowController con SubscribeToSessionPlayerEvents): este
// componente va puesto en WaitingRoomPanel mismo, así que OnEnable/
// OnDisable ya coinciden exactamente con "se mostró"/"se ocultó" la Sala
// de Espera -- no hace falta que RoomFlowController llame a nada.
public class LobbyJumperSpawner : MonoBehaviour
{
    [Tooltip("Plantilla a instanciar por cada jugador conectado -- un GameObject con " +
             "MenuMouseJumper ya configurado (heightAtScreenTop/gravity/sharedController, " +
             "'Auto Refresh On Start' DESTILDADO), SIN ningún modelo hijo propio -- " +
             "RefreshCharacter(index) le pone el personaje correcto apenas se instancia. " +
             "Dejalo INACTIVO en la Hierarchy: este script lo activa solo al clonarlo.")]
    [SerializeField] private GameObject jumperTemplate;

    [Tooltip("Posiciones fijas en el mundo 3D (hasta 4, una por jugador posible) donde aparece " +
             "cada Jumper -- las posicionás vos a mano en el Editor, cerca de la colchoneta " +
             "actual. Qué jugador va a cada slot lo decide playerSlotAssignments (por Id, " +
             "estable) -- no importa el orden de session.Players.")]
    [SerializeField] private Transform[] jumperSlots;

    [Tooltip("Hacia dónde mira cada Jumper, en grados sobre el eje vertical (Y) -- índice a " +
             "índice con Jumper Slots (el elemento 0 es la rotación del slot 0, etc). A " +
             "PROPÓSITO es un solo ángulo simple y no la rotación completa del slot: la " +
             "corrección para que el personaje quede PARADO (no tumbado) ya viene de " +
             "jumperTemplate y es la misma para todos -- esto solo gira el resultado sobre Y, " +
             "sin arriesgarse a romper esa corrección con un giro raro en X/Z. Podés dejarlo más " +
             "corto que Jumper Slots -- los que falten quedan en 0 (mirando 'de frente').")]
    [SerializeField] private float[] slotFacingYRotationDegrees;

    [Tooltip("Altura de salto FIJA (unidades del mundo) para el Jumper de cada slot que NO sea " +
             "el tuyo -- índice a índice con Jumper Slots, mismo criterio que Slot Facing Y " +
             "Rotation Degrees. Un valor distinto por slot para que los demás jugadores no " +
             "salten todos parejo/aburrido a la misma altura. Sin efecto sobre TU propio slot " +
             "(ese sigue tu mouse, ver MenuMouseJumper.useMouseForHeight). Podés dejarlo más " +
             "corto que Jumper Slots -- los que falten se quedan con el 'Fixed Jump Height' que " +
             "ya tenga jumperTemplate en su propio Inspector.")]
    [SerializeField] private float[] slotFixedJumpHeights;

    [Tooltip("El Jumper decorativo de siempre en el Main Panel (GameObject 'Jumper') -- la " +
             "cámara NO cambia al entrar a la Sala de Espera, así que sigue siendo visible en " +
             "el mismo encuadre. Se apaga mientras se muestran los Jumpers reales de la sala " +
             "(si no, con 1 jugador conectado se ven 2 personajes) y se vuelve a prender al " +
             "salir. Opcional: dejalo vacío si no querés este comportamiento.")]
    [SerializeField] private GameObject ambientMenuJumper;

    // slot (índice en jumperSlots) -> instancia spawneada ahí.
    private readonly Dictionary<int, GameObject> spawnedJumpers = new();

    // playerId -> slot que se le asignó -- ESTABLE una vez asignado, no se
    // reordena solo porque session.Players cambie de orden internamente
    // (no hay garantía de que esa lista mantenga el mismo orden entre
    // refrescos). Sin esto, cuando entraba alguien nuevo y el orden se
    // corría, un jugador nuevo podía terminar heredando temporalmente el
    // slot (y el Jumper YA instanciado, con la skin puesta) de otro
    // jugador existente -- se veía "el que se une tiene la skin del host"
    // hasta que le llegaba su propio dato y recién ahí se corregía.
    private readonly Dictionary<string, int> playerSlotAssignments = new();

    // Cuántos Jumpers están REALMENTE visibles ahora mismo (activeSelf) --
    // no es lo mismo que session.Players.Count: alguien puede estar
    // conectado a la sala pero su Jumper seguir oculto todavía (esperando
    // su primer dato de personaje, ver el "hasCharacterData" de abajo).
    // RoomFlowController usa esto para el contador de la Sala de Espera,
    // en vez de session.Players.Count, para que el número nunca vaya
    // adelantado a lo que se ve de verdad en pantalla.
    public int VisiblePlayerCount
    {
        get
        {
            int count = 0;
            foreach (GameObject instance in spawnedJumpers.Values)
            {
                if (instance != null && instance.activeSelf) count++;
            }
            return count;
        }
    }

    // Se dispara cada vez que RebuildJumpers() termina -- o sea, cada vez
    // que VisiblePlayerCount puede haber cambiado (alguien se hizo visible,
    // alguien se fue). RoomFlowController se suscribe a esto para refrescar
    // el texto del contador en el momento justo.
    public event Action VisibleCountChanged;

    // ------------------------------------------------------------------
    // DEBUG (provisorio): simular jugadores en la Sala de Espera sin
    // necesitar esa cantidad de clientes reales conectados -- para poder
    // acomodar posiciones (Jumper Slots) y alturas (Slot Fixed Jump
    // Heights) con 3/4 jugadores vos solo. Click derecho en el header de
    // este componente, EN PLAY, con WaitingRoomPanel activo -- mismo
    // patrón que MultiplayerConnectionManager.DebugCreateSession/
    // DebugJoinSession. Ver DebugAddSimulatedPlayer más abajo.
    // ------------------------------------------------------------------
    private readonly List<GameObject> debugPreviewJumpers = new();
    private int debugSimulatedPlayerCount;

    private void OnEnable()
    {
        if (ambientMenuJumper != null) ambientMenuJumper.SetActive(false);

        if (LobbyPlayerListSync.Instance != null)
        {
            LobbyPlayerListSync.Instance.PlayerListChanged += RebuildJumpers;
        }
        RebuildJumpers();
    }

    private void OnDisable()
    {
        if (ambientMenuJumper != null) ambientMenuJumper.SetActive(true);

        if (LobbyPlayerListSync.Instance != null)
        {
            LobbyPlayerListSync.Instance.PlayerListChanged -= RebuildJumpers;
        }
        ClearAllJumpers();
        // Para no dejar Jumpers de mentira colgados la próxima vez que se
        // entra de verdad a la Sala de Espera.
        ClearDebugPreviewJumpers();
        debugSimulatedPlayerCount = 0;
    }

    // Click derecho en el header del componente (Inspector) -> este ítem
    // aparece en el menú contextual, EN PLAY. Cada click agrega un Jumper
    // de mentira más (1, 2, 3, 4...) hasta llenar Jumper Slots; al llegar
    // al tope, el siguiente click limpia todo y vuelve a arrancar de cero.
    //
    // A PROPÓSITO totalmente separado del sistema real -- no toca
    // playerSlotAssignments, spawnedJumpers, VisiblePlayerCount ni dispara
    // VisibleCountChanged: solo instancia Jumpers de más, directo en los
    // slots, cada uno con la altura fija de SU slot (ver
    // slotFixedJumpHeights) para poder juzgar de una si la variación entre
    // slots se ve bien, sin arriesgar nada de la lógica real de
    // sincronización. No pensado para usarse con jugadores reales
    // conectados al mismo tiempo (se dibujarían pisados en el mismo slot).
    //
    // Sacá este [ContextMenu] (o dejalo, no hace nada solo -- hace falta
    // click derecho a mano en el Editor) antes de un build real, mismo
    // criterio que RoomFlowController.debugSkipCameraCheck.
    [ContextMenu("DEBUG: Agregar jugador simulado")]
    private void DebugAddSimulatedPlayer()
    {
        int maxSlots = jumperSlots != null ? jumperSlots.Length : 0;
        if (maxSlots <= 0) return;

        // Ya está al tope (o vino de un estado raro por encima del tope) ->
        // el próximo click reinicia desde cero en vez de no hacer nada.
        debugSimulatedPlayerCount = debugSimulatedPlayerCount >= maxSlots ? 0 : debugSimulatedPlayerCount + 1;
        RebuildDebugPreview(debugSimulatedPlayerCount);
    }

    [ContextMenu("DEBUG: Limpiar simulación")]
    private void DebugClearSimulatedPlayers()
    {
        debugSimulatedPlayerCount = 0;
        ClearDebugPreviewJumpers();
    }

    private void RebuildDebugPreview(int count)
    {
        ClearDebugPreviewJumpers();

        if (count <= 0 || jumperTemplate == null || jumperSlots == null) return;

        int characterCount = CharacterSelection.Instance != null ? CharacterSelection.Instance.Count : 0;
        int spawnCount = Mathf.Min(count, jumperSlots.Length);

        for (int slot = 0; slot < spawnCount; slot++)
        {
            if (jumperSlots[slot] == null) continue;

            // Misma corrección de rotación (base del template + giro en Y
            // propio del slot) que usa RebuildJumpers, para que la
            // simulación se vea igual que los Jumpers reales.
            float facingY = (slotFacingYRotationDegrees != null && slot < slotFacingYRotationDegrees.Length)
                ? slotFacingYRotationDegrees[slot]
                : 0f;
            Quaternion rotation = jumperTemplate.transform.rotation * Quaternion.Euler(0f, facingY, 0f);

            GameObject instance = Instantiate(jumperTemplate, jumperSlots[slot].position, rotation);
            instance.SetActive(true);
            debugPreviewJumpers.Add(instance);

            MenuMouseJumper jumper = instance.GetComponent<MenuMouseJumper>();
            if (jumper != null)
            {
                if (characterCount > 0) jumper.RefreshCharacter(slot % characterCount);

                // Ninguno de estos Jumpers es "vos" de verdad (son todos de
                // mentira) -- todos saltan con su altura fija de slot,
                // igual que se vería un jugador remoto real en ese slot.
                jumper.SetUseMouseForHeight(false);
                if (slotFixedJumpHeights != null && slot < slotFixedJumpHeights.Length)
                {
                    jumper.SetFixedJumpHeight(slotFixedJumpHeights[slot]);
                }
            }
        }
    }

    private void ClearDebugPreviewJumpers()
    {
        foreach (GameObject instance in debugPreviewJumpers)
        {
            if (instance != null) Destroy(instance);
        }
        debugPreviewJumpers.Clear();
    }

    private void RebuildJumpers()
    {
        ISession session = MultiplayerConnectionManager.Instance?.CurrentSession;
        if (session == null || jumperTemplate == null || jumperSlots == null)
        {
            ClearAllJumpers();
            return;
        }

        IReadOnlyList<IReadOnlyPlayer> players = session.Players;
        var currentPlayerIds = new HashSet<string>();
        foreach (IReadOnlyPlayer p in players) currentPlayerIds.Add(p.Id);

        // 1) Liberar los slots de jugadores que YA NO están conectados --
        // esto es lo único que debe correr un slot (que alguien se haya
        // ido), nunca un simple reordenamiento interno de session.Players.
        List<string> departedPlayerIds = null;
        foreach (KeyValuePair<string, int> assignment in playerSlotAssignments)
        {
            if (!currentPlayerIds.Contains(assignment.Key))
            {
                (departedPlayerIds ??= new List<string>()).Add(assignment.Key);
            }
        }
        if (departedPlayerIds != null)
        {
            foreach (string playerId in departedPlayerIds)
            {
                int slot = playerSlotAssignments[playerId];
                if (spawnedJumpers.TryGetValue(slot, out GameObject leftover))
                {
                    if (leftover != null) Destroy(leftover);
                    spawnedJumpers.Remove(slot);
                }
                playerSlotAssignments.Remove(playerId);
            }
        }

        // 2) Asignar slot a cualquier jugador que todavía no tenga uno --
        // el primer slot libre, y esa asignación queda fija hasta que esa
        // persona se vaya (paso 1), sin importar en qué posición aparezca
        // de ahí en más dentro de session.Players.
        foreach (IReadOnlyPlayer player in players)
        {
            if (playerSlotAssignments.ContainsKey(player.Id)) continue;

            for (int slot = 0; slot < jumperSlots.Length; slot++)
            {
                bool slotTaken = false;
                foreach (int takenSlot in playerSlotAssignments.Values)
                {
                    if (takenSlot == slot) { slotTaken = true; break; }
                }
                if (!slotTaken)
                {
                    playerSlotAssignments[player.Id] = slot;
                    break;
                }
            }
            // Si no hay slot libre (más jugadores que jumperSlots.Length),
            // ese jugador simplemente se queda sin Jumper visual -- no
            // debería pasar en la práctica (MaxPlayers ya limita esto).
        }

        // 3) Refrescar cada jugador conectado en SU slot asignado.
        foreach (IReadOnlyPlayer player in players)
        {
            if (!playerSlotAssignments.TryGetValue(player.Id, out int slot)) continue;
            if (jumperSlots[slot] == null) continue;

            // A PROPÓSITO distinguimos "no hay dato" de "el dato es 0" --
            // ISession dispara PlayerPropertiesChanged en un orden que no
            // controlamos del todo, y puede haber una ventana chica donde
            // todavía no llegó la property de un jugador recién asignado a
            // este slot. Si en ESE instante forzáramos characterIndex=0
            // (default) sobre un Jumper YA instanciado (de otro jugador,
            // en otra ronda), se vería su skin vieja de más. Ahora: sin
            // dato confirmado, NO tocamos un Jumper que ya existía.
            int characterIndex = 0;
            bool hasCharacterData = LobbyPlayerListSync.Instance != null &&
                LobbyPlayerListSync.Instance.ConnectedPlayerCharacters.TryGetValue(player.Id, out characterIndex);

            bool isNewInstance = !spawnedJumpers.TryGetValue(slot, out GameObject jumperInstance) || jumperInstance == null;
            if (isNewInstance)
            {
                // Base: la rotación del TEMPLATE (la corrección para quedar
                // parado, tuneada una sola vez, igual para todos). Encima,
                // SOLO un giro sobre Y (hacia dónde mira) propio de este
                // slot -- nunca la rotación completa del slot, para no
                // reintroducir el bug de que aparezca tumbado.
                float facingY = (slotFacingYRotationDegrees != null && slot < slotFacingYRotationDegrees.Length)
                    ? slotFacingYRotationDegrees[slot]
                    : 0f;
                Quaternion rotation = jumperTemplate.transform.rotation * Quaternion.Euler(0f, facingY, 0f);

                jumperInstance = Instantiate(jumperTemplate, jumperSlots[slot].position, rotation);
                // A PROPÓSITO arranca INACTIVO -- no lo prendemos todavía.
                // Antes se activaba de una, y lo que sea que tuviera puesto
                // por dentro en ese instante (ej. el hijo "semilla" que se
                // usa para la rotación base) quedaba visible mientras el
                // join real seguía en curso -- se veía como si mostrara la
                // skin de otro jugador. Ahora se activa recién más abajo,
                // cuando confirmamos que ya llegó el dato real.
                jumperInstance.SetActive(false);
                spawnedJumpers[slot] = jumperInstance;

                // Solo TU propio Jumper (el de session.CurrentPlayer) tiene
                // que saltar siguiendo TU mouse local -- el de cualquier
                // otro jugador conectado saltaría copiando tu mouse sin
                // sentido, así que arranca con una altura fija propia (ver
                // MenuMouseJumper.fixedJumpHeight), distinta por slot (ver
                // slotFixedJumpHeights arriba) para que no salten todos
                // parejo. Se decide UNA sola vez, acá, al crear la
                // instancia -- no cambia con el tiempo.
                bool isLocalPlayer = session.CurrentPlayer != null && player.Id == session.CurrentPlayer.Id;
                MenuMouseJumper newJumper = jumperInstance.GetComponent<MenuMouseJumper>();
                if (newJumper != null)
                {
                    newJumper.SetUseMouseForHeight(isLocalPlayer);
                    if (!isLocalPlayer && slotFixedJumpHeights != null && slot < slotFixedJumpHeights.Length)
                    {
                        newJumper.SetFixedJumpHeight(slotFixedJumpHeights[slot]);
                    }
                }
            }

            // Un Jumper sin dato todavía se queda oculto (si es nuevo) o
            // como estaba (si ya existía) -- nunca se le fuerza a mostrar
            // la skin default ni la de otro jugador mientras esperamos.
            if (hasCharacterData)
            {
                MenuMouseJumper jumper = jumperInstance.GetComponent<MenuMouseJumper>();
                if (jumper != null) jumper.RefreshCharacter(characterIndex);
                if (!jumperInstance.activeSelf) jumperInstance.SetActive(true);
            }
        }

        VisibleCountChanged?.Invoke();
    }

    private void ClearAllJumpers()
    {
        foreach (GameObject instance in spawnedJumpers.Values)
        {
            if (instance != null) Destroy(instance);
        }
        spawnedJumpers.Clear();
        playerSlotAssignments.Clear();
        VisibleCountChanged?.Invoke();
    }
}
