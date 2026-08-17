using System.Collections;
using UnityEngine;

// Pantalla en negro simple que tapa el arranque de Gameplay.unity durante
// un tiempo FIJO (minShowSeconds) -- le da margen a que se termine de
// asentar todo (chunks, personaje) antes de revelar nada.
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

        yield return new WaitForSeconds(minShowSeconds);

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
