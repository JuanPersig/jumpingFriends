// Versión mínima de un landmark de MediaPipe: solo Y (posición vertical
// normalizada [0,1]) y Visibility (confianza), que es todo lo que usa
// NativeMovementDetector. Existe para que NativeMovementDetector no dependa
// directamente de los tipos del plugin MediaPipeUnityPlugin (NormalizedLandmark) —
// quien lea la cámara (más adelante, la nueva implementación de
// PlayerInputProvider) convierte a esto, y la lógica de detección queda
// igual de desacoplada de la fuente que en el POC de Python.
public readonly struct PoseLandmarkSample
{
    public readonly float Y;
    public readonly float Visibility;

    public PoseLandmarkSample(float y, float visibility)
    {
        Y = y;
        Visibility = visibility;
    }
}
