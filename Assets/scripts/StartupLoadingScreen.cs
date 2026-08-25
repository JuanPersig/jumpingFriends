using System.Collections;
using UnityEngine;

// Pantalla en negro que tapa el arranque de Gameplay.unity -- le da margen a
// que se termine de asentar todo (chunks, personaje) antes de revelar nada.
//
// CUÁNTO dura depende de si hay red (ver WaitForRevealMoment): con una sala
// activa se levanta justo a tiempo para que la intro termine en el instante
// que acordaron todos los clientes; sin red, un tiempo fijo (minShowSeconds),
// que es el comportamiento de siempre.
//
// A PROPÓSITO ya no espera a que MediaPipe/la webcam confirmen estar
// listos: se probó esa versión (gate sobre NativePoseInputSource.IsReady /
// ChunkSpawner.InitialChunksReady) y el problema es que a veces la webcam
// falla al arrancar (visto en la consola: "Could not start graph" /
// WebCamTexture.Play() que nunca resuelve) -- en ese caso IsReady se queda
// en false PARA SIEMPRE, y el juego quedaba colgado en negro sin ninguna
// salida. Un tiempo fijo es menos "inteligente" pero no se puede colgar.
[RequireComponent(typeof(CanvasGroup))]
public class StartupLoadingScreen : MonoBehaviour
{
    [Tooltip("Se llama cuando termina la espera, ANTES de empezar el fade -- normalmente el " +
             "GameIntroSequence de la escena (que ya no arranca solo).")]
    [SerializeField] private GameIntroSequence introSequence;

    [Header("Timing")]
    [Tooltip("Tiempo fijo que se muestra el negro antes de revelar la intro.")]
    [SerializeField] private float minShowSeconds = 4f;
    [SerializeField] private float fadeOutSeconds = 0.5f;

    private CanvasGroup canvasGroup;

    // Cuándo levantar el negro (Fase 3, 25/8). Antes era siempre
    // minShowSeconds, un reloj puramente LOCAL -- con dos clientes, cada uno
    // arrancaba su ronda en un instante distinto.
    //
    // Ahora, si hay estado de ronda en red, se revela justo a tiempo para que
    // la intro de cámara TERMINE en el instante acordado por todos
    // (NetworkRoundState.GameplayStartTime). Como la intro dura lo mismo en
    // todas las máquinas, los tres momentos -- revelar, terminar la intro y
    // empezar a correr -- quedan alineados sin mandar nada más por la red.
    //
    // Se conserva minShowSeconds como respaldo para cuando NO hay red
    // (Gameplay.unity abierta suelta desde el Editor), que es exactamente el
    // comportamiento de siempre.
    private IEnumerator WaitForRevealMoment()
    {
        NetworkRoundState round = NetworkRoundState.Instance;
        float introDuration = introSequence != null ? introSequence.IntroDuration : 0f;

        if (round == null)
        {
            yield return new WaitForSeconds(minShowSeconds);
            yield break;
        }

        // Tope de seguridad: si el estado de ronda nunca se resuelve, no nos
        // quedamos en negro para siempre (guardarraíl 8 del proyecto). El
        // tope arranca generoso porque la espera legítima ya incluye el
        // margen de carga que puso el servidor.
        float maxWait = minShowSeconds + 20f;
        float waited = 0f;

        while (round.SecondsUntilStart > introDuration && waited < maxWait)
        {
            waited += Time.deltaTime;
            yield return null;
        }

        if (waited >= maxWait)
        {
            Debug.LogWarning("[StartupLoadingScreen] Se venció la espera del arranque de ronda -- " +
                              "revelando igual. La partida puede quedar desfasada de la de los demás.");
        }
    }

    private void Awake()
    {
        canvasGroup = GetComponent<CanvasGroup>();
        canvasGroup.alpha = 1f;
    }

    private void Start()
    {
        StartCoroutine(WaitAndFade());
    }

    private IEnumerator WaitAndFade()
    {
        // Apenas arranca la pantalla en negro: pose de arranque (cuclillas)
        // y cámara ya en introCameraStart, tapadas por el negro (ver
        // ApplyStartPose en GameIntroSequence).
        introSequence?.ApplyStartPose();

        // OJO: a propósito NO se recalibra acá. La detección solo arranca
        // (webcam + calibración) cuando el jugador entra a Configuración
        // en el Menú -- ver NativePoseInputSource/DetectionSettingsPanel.
        // Si esa calibración ya se hizo ahí, tiene que seguir siendo
        // válida acá tal cual llegó; recalibrar de nuevo en Gameplay sería
        // lo mismo que no haber calibrado nada en Configuración.

        yield return WaitForRevealMoment();

        introSequence?.BeginIntro();
        // Un frame de margen para que BeginIntro() arranque su propia
        // corrutina antes de que el fade empiece.
        yield return null;

        float t = 0f;
        while (t < fadeOutSeconds)
        {
            t += Time.deltaTime;
            canvasGroup.alpha = 1f - Mathf.Clamp01(t / fadeOutSeconds);
            yield return null;
        }

        canvasGroup.alpha = 0f;
        gameObject.SetActive(false);
    }
}
