using System.Collections;
using UnityEngine;

// Compartido por GameIntroSequence.PlayIntro y GameOutroSequence.PlayOutro:
// las dos corrutinas hacían el mismo Lerp de posición + Slerp de rotación
// a lo largo de `duration` segundos, letra por letra salvo el punto de
// partida. `from` se lee UNA sola vez al arrancar -- en la intro es un
// Transform fijo que no se mueve (introCameraStart); en la outro es la
// propia cámara (pasando `cam` como `from`, se captura "donde esté AHORA"
// en vez de un punto fijo). `target` se lee EN VIVO cada frame -- en la
// outro es hijo del jugador y se sigue moviendo mientras cae (ver
// comentario original en GameOutroSequence).
//
// El bucle SIEMPRE espera los `duration` segundos completos, sin importar
// si algún Transform vino sin asignar -- mismo comportamiento que tenía
// GameIntroSequence.PlayIntro antes de unificarse acá (si el llamador
// necesita en cambio saltearse la espera entera cuando falta una
// referencia, como hacía GameOutroSequence, que decida NO llamar a este
// método -- ver el `if` alrededor del llamado en PlayOutro).
internal static class CameraLerpUtility
{
    public static IEnumerator LerpTo(Transform cam, Transform from, Transform target, float duration)
    {
        bool valid = cam != null && from != null && target != null;
        Vector3 startPos = valid ? from.position : Vector3.zero;
        Quaternion startRot = valid ? from.rotation : Quaternion.identity;

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            if (valid)
            {
                float t = Mathf.Clamp01(elapsed / duration);
                cam.position = Vector3.Lerp(startPos, target.position, t);
                cam.rotation = Quaternion.Slerp(startRot, target.rotation, t);
            }
            yield return null;
        }

        if (valid) cam.SetPositionAndRotation(target.position, target.rotation);
    }
}
