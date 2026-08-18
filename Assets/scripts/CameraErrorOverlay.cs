using UnityEngine;

// Aviso simple en Gameplay.unity: si la webcam deja de responder a mitad de
// partida (se desenchufó, otro programa la agarró, un error de driver tipo
// "Could not connect pins - RenderStream()"), muestra un cartel para que el
// jugador entienda por qué el personaje dejó de responder al salto/agache,
// en vez de quedarse mudo sin explicación.
//
// A pedido explícito del usuario: esto NO pausa el juego (obstáculos y
// puntaje siguen corriendo mientras dura el aviso) -- es solo informativo.
// Se oculta solo apenas NativePoseInputSource.HasCameraError vuelve a false
// (la propia clase ya reintenta Play() sola en SubmitLoop) -- no hace falta
// ningún botón de "reintentar" acá.
public class CameraErrorOverlay : MonoBehaviour
{
    [Tooltip("Panel/Canvas con el aviso (texto tipo 'Se perdió la conexión con la cámara'). " +
             "Arranca oculto y se prende solo mientras HasCameraError esté en true.")]
    [SerializeField] private GameObject warningPanel;

    private void Start()
    {
        if (warningPanel != null) warningPanel.SetActive(false);
    }

    private void Update()
    {
        if (warningPanel == null) return;

        bool hasError = NativePoseInputSource.Instance != null && NativePoseInputSource.Instance.HasCameraError;
        if (warningPanel.activeSelf != hasError)
        {
            warningPanel.SetActive(hasError);
        }
    }
}
