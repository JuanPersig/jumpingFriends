using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Pantalla de Configuración del Menú Principal: le muestra al jugador su
// propia webcam en vivo (para que pueda acomodar la cámara o su propia
// posición) y un texto de estado (Parado/Saltando/Agachado) que confirma
// que la detección de verdad lo está reconociendo -- SIN esqueleto ni
// landmarks dibujados, a propósito, para mantenerlo simple.
//
// El NativePoseInputSource que usa (poseInputSource) es el ÚNICO que
// existe en todo el juego -- desde el 17/8 es un singleton persistente
// (DontDestroyOnLoad, ver ese script) que sobrevive el cambio a
// Gameplay.unity sin recrearse ni recalibrar de nuevo.
//
// A PROPÓSITO arranca APAGADO en la escena (no bien carga MainMenu.unity):
// la detección (webcam + calibración) solo tiene que empezar cuando el
// jugador entra ACÁ, a Configuración -- nunca antes. Este panel es quien
// la prende la primera vez (OnEnable) y la deja corriendo de ahí en
// adelante (Start() en NativePoseInputSource solo corre una vez; volver a
// des/activar el GameObject después NO reinicia nada, así que no hace
// falta apagarla de nuevo al salir del panel).
public class DetectionSettingsPanel : MonoBehaviour
{
    [Header("Cámara en vivo")]
    [SerializeField] private NativePoseInputSource poseInputSource;
    [SerializeField] private RawImage cameraPreview;
    [Tooltip("La webcam entrega la imagen 'como la ve', no en espejo -- para que el jugador " +
             "se vea como en un espejo (más natural para acomodarse), la mostramos con la X " +
             "invertida acá nada más. Puramente visual, no afecta a la detección real.")]
    [SerializeField] private bool mirrorPreview = true;

    [Header("Estado detectado")]
    [SerializeField] private TMP_Text stateLabel;

    [Header("Sensibilidad")]
    [SerializeField] private Slider jumpSensitivitySlider;
    [SerializeField] private Slider crouchSensitivitySlider;
    [Tooltip("Rango de NativeDetectionConfig.JumpTriggerOffsetRatio que cubre el slider -- " +
             "X = extremo MENOS sensible (0 en el slider), Y = extremo MÁS sensible (1 en el " +
             "slider). El valor tuneado de fábrica es 0.20, en algún punto intermedio.")]
    [SerializeField] private Vector2 jumpRatioRange = new Vector2(0.35f, 0.10f);
    [Tooltip("Mismo criterio que arriba pero para CrouchTriggerOffsetRatio (tuneado: 0.15).")]
    [SerializeField] private Vector2 crouchRatioRange = new Vector2(0.25f, 0.08f);

    [Header("Recalibrar")]
    [SerializeField] private Button recalibrateButton;

    private void OnEnable()
    {
        // Primera vez que se abre Configuración: arranca la webcam +
        // calibración. Si ya estaba prendida (se volvió a entrar al
        // panel), esto es un no-op inofensivo.
        if (poseInputSource != null) poseInputSource.gameObject.SetActive(true);

        if (jumpSensitivitySlider != null)
        {
            jumpSensitivitySlider.SetValueWithoutNotify(
                Mathf.InverseLerp(jumpRatioRange.x, jumpRatioRange.y, NativeDetectionConfig.JumpTriggerOffsetRatio));
            jumpSensitivitySlider.onValueChanged.AddListener(OnJumpSensitivityChanged);
        }
        if (crouchSensitivitySlider != null)
        {
            crouchSensitivitySlider.SetValueWithoutNotify(
                Mathf.InverseLerp(crouchRatioRange.x, crouchRatioRange.y, NativeDetectionConfig.CrouchTriggerOffsetRatio));
            crouchSensitivitySlider.onValueChanged.AddListener(OnCrouchSensitivityChanged);
        }
        if (recalibrateButton != null) recalibrateButton.onClick.AddListener(OnRecalibratePressed);
    }

    private void OnDisable()
    {
        if (jumpSensitivitySlider != null) jumpSensitivitySlider.onValueChanged.RemoveListener(OnJumpSensitivityChanged);
        if (crouchSensitivitySlider != null) crouchSensitivitySlider.onValueChanged.RemoveListener(OnCrouchSensitivityChanged);
        if (recalibrateButton != null) recalibrateButton.onClick.RemoveListener(OnRecalibratePressed);
    }

    private void Update()
    {
        if (cameraPreview != null && poseInputSource != null && poseInputSource.PreviewTexture != null
            && cameraPreview.texture != poseInputSource.PreviewTexture)
        {
            cameraPreview.texture = poseInputSource.PreviewTexture;
            Vector3 scale = cameraPreview.rectTransform.localScale;
            scale.x = mirrorPreview ? -Mathf.Abs(scale.x) : Mathf.Abs(scale.x);
            cameraPreview.rectTransform.localScale = scale;
        }

        UpdateStateLabel();
    }

    private void UpdateStateLabel()
    {
        if (stateLabel == null || poseInputSource == null) return;

        if (poseInputSource.PreviewTexture == null)
        {
            stateLabel.text = "Iniciando cámara...";
            return;
        }

        if (poseInputSource.IsWaitingToStartCalibration)
        {
            stateLabel.text = "Preparate -- acomodate frente a la cámara";
            return;
        }

        if (!poseInputSource.IsCalibrated)
        {
            stateLabel.text = "Calibrando -- quedate parado quieto";
            return;
        }

        switch (poseInputSource.CurrentState)
        {
            case MovementState.Jumping:
                stateLabel.text = "SALTANDO";
                break;
            case MovementState.Crouching:
                stateLabel.text = "AGACHADO";
                break;
            default:
                stateLabel.text = "Parado";
                break;
        }
    }

    private void OnJumpSensitivityChanged(float t)
    {
        NativeDetectionConfig.JumpTriggerOffsetRatio = Mathf.Lerp(jumpRatioRange.x, jumpRatioRange.y, t);
        NativeDetectionConfig.SaveSensitivityPrefs();
    }

    private void OnCrouchSensitivityChanged(float t)
    {
        NativeDetectionConfig.CrouchTriggerOffsetRatio = Mathf.Lerp(crouchRatioRange.x, crouchRatioRange.y, t);
        NativeDetectionConfig.SaveSensitivityPrefs();
    }

    private void OnRecalibratePressed()
    {
        poseInputSource?.Recalibrate();
    }
}
