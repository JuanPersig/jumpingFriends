using UnityEngine;

// Contador de FPS mínimo para diagnóstico -- dibuja el número directo en
// pantalla con OnGUI, sin depender de ningún Canvas ni UI existente.
// Agregalo a cualquier GameObject de la escena para verlo; sacalo cuando ya
// no haga falta (es solo una herramienta de debug, no pretende quedar en
// el juego final).
public class FpsCounter : MonoBehaviour
{
    [SerializeField] private float updateIntervalSeconds = 0.5f;

    private float _accumulatedTime;
    private int _frameCount;
    private float _currentFps;
    private GUIStyle _style;

    private void Update()
    {
        _accumulatedTime += Time.unscaledDeltaTime;
        _frameCount++;

        if (_accumulatedTime >= updateIntervalSeconds)
        {
            _currentFps = _frameCount / _accumulatedTime;
            _accumulatedTime = 0f;
            _frameCount = 0;
        }
    }

    private void OnGUI()
    {
        // OnGUI puede dispararse más de una vez por frame (Layout +
        // Repaint) -- creamos el GUIStyle una sola vez en vez de en cada
        // llamada, para no generar basura constante con una herramienta
        // que encima está pensada para diagnosticar problemas de rendimiento.
        _style ??= new GUIStyle(GUI.skin.label) { fontSize = 24 };
        _style.normal.textColor = _currentFps < 30f ? Color.red : (_currentFps < 55f ? Color.yellow : Color.green);
        GUI.Label(new Rect(10, 10, 200, 40), $"FPS: {_currentFps:0}", _style);
    }
}
