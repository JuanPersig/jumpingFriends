using UnityEngine;

// Mueve la Main Camera del menú suavemente entre dos posiciones/ángulos
// fijos: la vista del panel principal y la vista de "Elegir Personaje"
// (enfocada en el Cylinder). Los dos "objetivos" son Transforms vacíos
// que posicionás VOS a mano en el Editor (arrastrando/rotando en la
// Scene view, como siempre) — este script solo interpola la cámara hacia
// el que esté activo en cada momento. MenuController llama a
// ShowMainMenuView()/ShowCharacterSelectView() al cambiar de panel.
public class MenuCameraMover : MonoBehaviour
{
    [SerializeField] private Transform cameraTransform;
    [Tooltip("Transform vacío posicionado donde tiene que estar la cámara para la vista del panel principal.")]
    [SerializeField] private Transform mainMenuView;
    [Tooltip("Transform vacío posicionado de frente al Cylinder, para la vista de Elegir Personaje.")]
    [SerializeField] private Transform characterSelectView;
    [SerializeField] private float moveSpeed = 4f;

    private Transform currentTarget;

    private void Awake()
    {
        currentTarget = mainMenuView;
    }

    public void ShowMainMenuView()
    {
        currentTarget = mainMenuView;
    }

    public void ShowCharacterSelectView()
    {
        currentTarget = characterSelectView;
    }

    private void Update()
    {
        if (cameraTransform == null || currentTarget == null) return;

        // Mismo suavizado independiente de framerate que ya usa CameraFollow.
        float t = 1f - Mathf.Exp(-moveSpeed * Time.deltaTime);
        cameraTransform.position = Vector3.Lerp(cameraTransform.position, currentTarget.position, t);
        cameraTransform.rotation = Quaternion.Slerp(cameraTransform.rotation, currentTarget.rotation, t);
    }
}
