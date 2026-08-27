using System;
using UnityEngine;

// Capa intermedia entre "de dónde viene el input" y "quién lo consume" (los
// minijuegos). Los minijuegos NUNCA leen la cámara ni la red directamente:
// solo se suscriben a OnJump / OnCrouch / OnStand. Así, el día que cambie la
// fuente del input, esta clase es la única que se toca.
//
// HOY la fuente es NativePoseInputSource (detección de pose nativa, en C#,
// leyendo landmarks de MediaPipe) -- llama a RaiseJump/RaiseCrouch/RaiseStand
// y este componente reparte.
//
// ACÁ VIVÍA UN LISTENER UDP (sacado el 26/8). Era el camino original: un
// proceso de Python aparte hacía la detección y mandaba {"state": "jumping"}
// por UDP al puerto 5555. Cuando la detección se migró a nativo (14/8) ese
// emisor dejó de existir, pero el receptor quedó: ~90 líneas con un
// UdpClient, un hilo de fondo, una cola con lock y parseo de JSON, que ABRÍAN
// UN SOCKET EN CADA ARRANQUE del juego sin que nadie mandara nunca un
// paquete. Aparte de ser código muerto, era un choque potencial con el
// firewall justo en la prueba por Internet.
//
// Si alguna vez hace falta volver a meter una fuente de input externa, el
// lugar correcto sigue siendo este: un componente nuevo que llame a los
// Raise* de abajo, sin que RunnerController ni PlayerSlot se enteren.
public class PlayerInputProvider : Singleton<PlayerInputProvider>
{
    public event Action OnJump;
    public event Action OnCrouch;
    public event Action OnStand;

    // Los dispara la fuente de input activa (hoy NativePoseInputSource). El
    // resto del juego escucha los eventos de arriba sin saber ni le importa
    // quién los levantó.
    public void RaiseJump() => OnJump?.Invoke();
    public void RaiseCrouch() => OnCrouch?.Invoke();
    public void RaiseStand() => OnStand?.Invoke();

    protected override void Awake()
    {
        base.Awake();
        if (Instance != this) return; // instancia duplicada, ya se está autodestruyendo
        DontDestroyOnLoad(gameObject);
    }
}
