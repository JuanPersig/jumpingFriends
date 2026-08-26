using System.Collections;
using UnityEngine;

// Activa, en Gameplay.unity, la cantidad exacta de "carriles" (jugadores)
// que le corresponde a esta ronda -- sin crear ni destruir ningún
// GameObject en el proceso. Los hasta 4 GameObjects "player" posibles YA
// están armados de antemano en la escena (ver playerSlots); este script
// solo prende los primeros N, los reposiciona en el X que le toca a cada
// uno según el mapa de ese tamaño, y apaga el resto.
//
// QUIÉN MANDA A QUIÉN (cambió en la Fase 3.2, 25/8 -- leer antes de tocar
// el orden de arranque de esta escena):
//
// Antes esto resolvía en Awake(), apoyándose en que Unity garantiza que
// TODOS los Awake() terminan antes que CUALQUIER Start(): así
// ObstacleSpawner, que leía los carriles en su propio Start(), siempre los
// encontraba listos.
//
// Ese contrato ya no se puede sostener. La cantidad real de jugadores viaja
// por red (NetworkRoundState) y los NetworkObject in-scene recién spawnean
// DESPUÉS de que la escena termina de cargar -- o sea, en Awake() el dato
// todavía no existe. Confirmado en vivo: el cliente recibe el estado de
// ronda ~2s después que el host.
//
// Así que la dependencia se invirtió: este script ya no resuelve solo al
// arrancar, sino cuando el estado de ronda está listo, y RECIÉN AHÍ avisa a
// ObstacleSpawner (SetLanePlayers) y a CameraFollow (SetActivePairIndex).
// Ninguno de los dos lee nada por su cuenta: esperan a que se les avise.
//
// Sin red (Gameplay.unity abierta suelta desde el Editor) el camino es el
// mismo, solo que el estado de ronda se resuelve solo con valores locales y
// la espera dura un frame.
public class RoundLaneSetup : MonoBehaviour
{
    [System.Serializable]
    public class LaneLayout
    {
        [Tooltip("Para cuántos jugadores es este layout (1 a 4).")]
        public int playerCount = 1;
        [Tooltip("X exacto de cada carril activo, medido a mano contra la geometría real del " +
                 "chunk de ese tamaño (ver el procedimiento de medición/centrado ya usado para " +
                 "el mapa de 2). Tiene que tener EXACTAMENTE 'Player Count' elementos, en el " +
                 "mismo orden que Player Slots.")]
        public float[] laneXPositions;
    }

    [Tooltip("Los hasta 4 GameObjects 'player' pre-armados en la escena, SIEMPRE en el mismo " +
             "orden (slot 0, slot 1, ...). Nunca se crean ni se destruyen -- este script solo " +
             "prende/apaga y reposiciona en X.")]
    [SerializeField] private Transform[] playerSlots;
    [Tooltip("Un layout por cantidad de jugadores soportada. Si falta el de la ronda actual, se " +
             "usa el de mayor 'Player Count' disponible que no la supere (con aviso en consola) " +
             "-- mismo criterio de fallback que ChunkSpawner.ResolveChunkPrefabsForRound, así los " +
             "dos quedan consistentes solos sin coordinarse a propósito.")]
    [SerializeField] private LaneLayout[] laneLayoutsByPlayerCount;
    [SerializeField] private ObstacleSpawner obstacleSpawner;

    // El campo 'roundPlayerCount' que vivía acá se BORRÓ en la Fase 3.2:
    // era un duplicado de GameManager.roundPlayerCount que había que
    // mantener igual a mano, y existía solo porque este script resolvía en
    // Awake() y no podía depender de GameManager en ese momento. Ahora
    // resuelve tarde (ver SetupWhenRoundStateReady), así que lee la única
    // fuente buena: GameManager.RoundPlayerCount.

    [Header("Cámara (parejas, solo importa con 4 jugadores)")]
    [Tooltip("Índice DENTRO de Player Slots (0-3) que corresponde al jugador de ESTE cliente -- " +
             "el mismo que ya tenés wireado como 'Target' en CameraFollow y como 'Player' en " +
             "GameIntroSequence (mantenelos consistentes entre los tres). Con 4 jugadores, se usa " +
             "para calcular la pareja (slot 0-1 -> Pareja 0, slot 2-3 -> Pareja 1) y elegir el " +
             "centro de cámara correcto -- ver CameraFollow.CameraSettings.pairIndex. Cuando " +
             "exista Netcode de verdad (Fase 3), este índice va a venir de qué carril te asignó " +
             "la red, no de un valor fijo acá.")]
    [SerializeField] private int localPlayerSlotIndex = 0;
    [SerializeField] private CameraFollow cameraFollow;

    // Tope de espera del estado de ronda. Sin esto, un fallo de red dejaría
    // la escena congelada para siempre y sin ningún carril activo, en negro y
    // sin explicación (guardarraíl 8 del proyecto: jamás un "esperar hasta
    // que" sin salida). Al vencerse se arma la ronda igual, con el respaldo
    // local de GameManager.
    private const float RoundStateWaitTimeoutSeconds = 10f;

    private void Awake()
    {
        StartCoroutine(SetupWhenRoundStateReady());
    }

    private IEnumerator SetupWhenRoundStateReady()
    {
        // UN FRAME DE MARGEN, Y NO ES OPCIONAL (bug real, 25/8). Las
        // corrutinas empiezan a correr sincrónicamente en el mismo Awake()
        // que las lanza, y el orden de Awake() entre GameObjects distintos NO
        // está garantizado -- así que acá NetworkRoundState.Instance puede
        // todavía ser null aunque el objeto esté perfecto en la escena.
        //
        // La primera version de esto guardaba Instance en una variable local
        // ANTES de este yield: quedaba en null, la espera de abajo no se
        // ejecutaba nunca, y la ronda se armaba al instante con el respaldo
        // de 1 jugador. Sintoma: arena de 2 jugadores (ChunkSpawner si
        // esperaba bien, porque arranca desde Start()) pero un solo carril.
        //
        // Con este yield ya pasaron TODOS los Awake() y Start() de la escena,
        // asi que si Instance sigue en null es porque el objeto realmente no
        // esta, no porque no le toco arrancar todavia.
        yield return null;

        NetworkRoundState round = NetworkRoundState.Instance;
        if (round == null)
        {
            Debug.LogError("[RoundLaneSetup] No hay ningún NetworkRoundState en la escena -- " +
                            "armando la ronda con el valor de respaldo de GameManager. En " +
                            "multijugador los carriles no van a coincidir con los de los demás.");
            SetupRound();
            yield break;
        }

        float waited = 0f;
        while (!round.IsResolved && waited < RoundStateWaitTimeoutSeconds)
        {
            waited += Time.deltaTime;
            yield return null;
        }

        if (!round.IsResolved)
        {
            Debug.LogWarning($"[RoundLaneSetup] El estado de ronda no llegó en " +
                              $"{RoundStateWaitTimeoutSeconds}s -- armando la ronda con el valor de " +
                              "respaldo de GameManager. Los carriles pueden no coincidir con los " +
                              "de los demás jugadores.");
        }

        SetupRound();
    }

    private void SetupRound()
    {
        if (playerSlots == null || playerSlots.Length == 0)
        {
            Debug.LogError("[RoundLaneSetup] 'Player Slots' está vacío -- no hay ningún jugador " +
                            "para activar.");
            return;
        }

        // Fuente única: GameManager.RoundPlayerCount, que devuelve la cantidad
        // REAL de conectados cuando hay sala, y el valor de Inspector cuando
        // se abre la escena suelta. Antes este script tenía su propio campo
        // duplicado que había que mantener igual a mano.
        int roundPlayerCount = GameManager.Instance != null ? GameManager.Instance.RoundPlayerCount : 1;

        LaneLayout layout = ResolveLayout(roundPlayerCount);
        if (layout == null)
        {
            Debug.LogError($"[RoundLaneSetup] No hay ningún layout utilizable para " +
                            $"{roundPlayerCount} jugador(es). Revisá 'Lane Layouts By Player Count'.");
            return;
        }

        Debug.Log($"[RoundLaneSetup] Armando ronda de {roundPlayerCount} jugador(es) " +
                  $"(layout de {layout.playerCount}).");

        int activeCount = Mathf.Min(layout.playerCount, playerSlots.Length);
        Transform[] activeLanes = new Transform[activeCount];

        for (int i = 0; i < playerSlots.Length; i++)
        {
            if (playerSlots[i] == null) continue;

            bool active = i < activeCount;
            playerSlots[i].gameObject.SetActive(active);

            if (active)
            {
                Vector3 pos = playerSlots[i].position;
                pos.x = layout.laneXPositions[i];
                playerSlots[i].position = pos;
                activeLanes[i] = playerSlots[i];
            }
        }

        if (obstacleSpawner != null) obstacleSpawner.SetLanePlayers(activeLanes);

        // 2 carriles por pareja (pensado para 4 jugadores: slot 0-1 =
        // Pareja 0, slot 2-3 = Pareja 1). Con 1/2/3 jugadores esto da
        // siempre pairIndex=0 (localPlayerSlotIndex nunca pasa de 1 en esos
        // casos), que es justo el valor por defecto de las configuraciones
        // existentes de CameraFollow -- no les cambia nada.
        if (cameraFollow != null)
        {
            int pairIndex = localPlayerSlotIndex / 2;
            cameraFollow.SetActivePairIndex(pairIndex);
        }
    }

    // Mismo criterio de fallback que ChunkSpawner.ResolveChunkPrefabsForRound:
    // exacto si existe, si no el de mayor Player Count que no supere a la
    // ronda actual -- para no dejar el juego sin jugadores por un layout
    // que todavía no armaste, y para que el mapa (ChunkSpawner) y los
    // carriles (acá) siempre terminen de acuerdo en cuántos jugadores hay
    // de verdad, sin tener que coordinarse explícitamente entre sí.
    private LaneLayout ResolveLayout(int roundPlayerCount)
    {
        LaneLayout exactMatch = null;
        LaneLayout bestFallback = null;

        foreach (LaneLayout layout in laneLayoutsByPlayerCount)
        {
            if (layout == null || layout.laneXPositions == null ||
                layout.laneXPositions.Length < layout.playerCount) continue;

            if (layout.playerCount == roundPlayerCount) exactMatch = layout;
            if (layout.playerCount <= roundPlayerCount &&
                (bestFallback == null || layout.playerCount > bestFallback.playerCount))
            {
                bestFallback = layout;
            }
        }

        LaneLayout chosen = exactMatch ?? bestFallback;
        if (chosen != null && exactMatch == null)
        {
            Debug.LogWarning($"[RoundLaneSetup] No hay un layout armado específicamente para " +
                              $"{roundPlayerCount} jugador(es) -- usando el de " +
                              $"{chosen.playerCount} como reemplazo. Armá el layout " +
                              "correspondiente cuando puedas.");
        }
        return chosen;
    }
}
