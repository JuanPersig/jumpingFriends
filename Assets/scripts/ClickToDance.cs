using UnityEngine;

// Decorativo: click sobre el personaje (el sentado, u otro cualquiera con
// este script) y alterna entre su pose normal y Dance_Loop (otro clip que
// ya viene en UAL1_Standard.fbx, junto con los de Sitting_*). Segundo
// click, vuelve a la pose de antes -- un toggle simple, no una animación
// de un solo uso.
//
// Camino PRINCIPAL: ToggleDance() público, enganchado al OnClick() de un
// Button de UI invisible puesto encima del personaje en pantalla -- más
// simple y confiable que un Collider 3D + OnMouseDown, que depende de que
// el Collider calce con la pose animada, de triggers, de layers, etc.
// Válido acá porque la cámara del menú es estática (MenuCameraMover se
// queda quieta apuntando siempre al mismo lugar), así que la posición en
// pantalla del personaje no cambia nunca.
//
// OnMouseDown() se deja de yapa por si en algún momento el Collider 3D
// funciona bien solo -- no molesta si no se usa.
public class ClickToDance : MonoBehaviour
{
    [SerializeField] private Animator animator;
    [Tooltip("Nombre del parámetro Bool en el Animator Controller -- tiene que existir " +
             "ahí con este mismo nombre, si no SetBool no hace nada (sin error visible).")]
    [SerializeField] private string danceParameter = "IsDancing";

    private bool isDancing;

    // Público -- enganchalo directo al OnClick() del Button invisible.
    public void ToggleDance()
    {
        isDancing = !isDancing;
        if (animator != null) animator.SetBool(danceParameter, isDancing);
    }

    private void OnMouseDown()
    {
        ToggleDance();
    }
}
