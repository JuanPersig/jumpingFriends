using UnityEngine;

// Fuente única de verdad de "a qué velocidad va el juego ahora mismo" Y de
// "cuánta distancia se recorrió hasta ahora". El jugador lee la distancia
// para saber dónde pararse; el spawner usa la velocidad indirectamente
// (mismo espaciado en distancia = menos tiempo de reacción a medida que esto
// sube, así la dificultad crece sola).
//
// FORMA CERRADA, NO ACUMULACIÓN (Fase 3, 25/8) -- este es el cambio que hace
// posible el multijugador sin mandar posiciones por la red. Antes la
// velocidad era `CurrentSpeed += acceleration * Time.deltaTime` cada frame, y
// la distancia la acumulaba RunnerController por su cuenta con
// `position += speed * deltaTime`. Dos clientes con distinto framerate
// acumulan errores de redondeo distintos y se van separando solos, sin que
// nada avise.
//
// Ahora velocidad y distancia son FUNCIONES del tiempo transcurrido de ronda
// (que viene de NetworkRoundState, compartido por todos). Dos beneficios:
//   1. Todos los clientes calculan exactamente lo mismo, sin sincronizar nada.
//   2. La posición pasa a ser ABSOLUTA en vez de acumulada -- si a un cliente
//      se le traba un frame (carga de chunk, GC), al frame siguiente vuelve
//      solo a donde corresponde. Antes ese hipo quedaba como error permanente.
//
// REGLA para el futuro: nada de estado de gameplay acumulado frame a frame.
// Si se vuelve a agregar algo así, la deriva vuelve sin avisar.
public class DifficultyManager : Singleton<DifficultyManager>
{
    [Header("Velocidad")]
    [SerializeField] private float startSpeed = 6f;
    [SerializeField] private float maxSpeed = 16f;
    [SerializeField] private float acceleration = 0.15f; // unidades por segundo, al cuadrado

    // Velocidad actual, derivada del tiempo de ronda (ver comentario de arriba).
    public float CurrentSpeed => SpeedAt(EffectiveElapsedSeconds);

    // Distancia recorrida desde el arranque de la ronda -- la integral de la
    // velocidad. La lee RunnerController para ubicarse en Z.
    public float DistanceTravelled => DistanceAt(EffectiveElapsedSeconds);

    // 0 = recién arrancó (CurrentSpeed == startSpeed), 1 = llegó a maxSpeed.
    // Otros sistemas (ObstacleSpawner, RunnerController) lo leen para hacer
    // que su propia dificultad (piso de reacción, variedad de obstáculos,
    // perdón de salto...) también vaya subiendo DENTRO de una misma
    // partida, en vez de ser un número fijo parejo de punta a punta.
    public float Progress01
    {
        get
        {
            if (Mathf.Approximately(maxSpeed, startSpeed)) return 1f;
            return Mathf.Clamp01((CurrentSpeed - startSpeed) / (maxSpeed - startSpeed));
        }
    }

    private bool isRunning = true;

    // Al frenar (Game Over) congelamos el reloj en el valor que tenía en ese
    // instante, en vez de dejar que la fórmula siga avanzando sola -- si no,
    // el "cadáver" seguiría deslizándose hacia adelante durante toda la
    // cinemática de muerte.
    private float stoppedAtElapsed;

    // Último recurso si alguien se olvidó de poner el NetworkRoundState en la
    // escena: seguimos funcionando con reloj local (con deriva, pero jugable)
    // en vez de dejar el juego congelado sin explicar por qué.
    private double fallbackStartTime = -1.0;
    private bool warnedAboutMissingRoundState;

    private float EffectiveElapsedSeconds
    {
        get
        {
            if (!isRunning) return stoppedAtElapsed;

            NetworkRoundState round = NetworkRoundState.Instance;
            if (round != null) return round.ElapsedSeconds;

            if (!warnedAboutMissingRoundState)
            {
                warnedAboutMissingRoundState = true;
                Debug.LogError("[DifficultyManager] No hay ningún NetworkRoundState en la escena -- " +
                                "usando reloj local como respaldo. En multijugador los clientes se " +
                                "van a desincronizar. Agregá el objeto de estado de ronda a Gameplay.unity.");
            }

            if (fallbackStartTime < 0.0) fallbackStartTime = Time.timeAsDouble;
            return Mathf.Max(0f, (float)(Time.timeAsDouble - fallbackStartTime));
        }
    }

    // Segundos que tarda en llegar de startSpeed a maxSpeed. Con acceleration
    // <= 0 la velocidad nunca sube, así que el tramo acelerado dura 0.
    private float TimeToMaxSpeed =>
        acceleration > 0f ? Mathf.Max(0f, (maxSpeed - startSpeed) / acceleration) : 0f;

    private float SpeedAt(float t)
    {
        if (t <= 0f) return startSpeed;
        return Mathf.Min(maxSpeed, startSpeed + acceleration * t);
    }

    // Integral de SpeedAt: mientras acelera es un tramo cuadrático; una vez
    // clavada en maxSpeed, sigue lineal desde donde había quedado.
    private float DistanceAt(float t)
    {
        if (t <= 0f) return 0f;

        float tCap = TimeToMaxSpeed;
        if (t <= tCap)
        {
            return startSpeed * t + 0.5f * acceleration * t * t;
        }

        float distanceWhileAccelerating = startSpeed * tCap + 0.5f * acceleration * tCap * tCap;
        return distanceWhileAccelerating + maxSpeed * (t - tCap);
    }

    // Acá vivía ResetDifficulty() (sacado el 26/8): no lo llamaba nadie.
    // Quedó de cuando reiniciar no recargaba la escena entera; hoy
    // GameManager.RestartGame() recarga Gameplay.unity completa, así que este
    // componente se crea de cero con todos sus campos en su valor inicial y
    // no hay nada que resetear a mano.
    public void Stop()
    {
        if (!isRunning) return;
        stoppedAtElapsed = EffectiveElapsedSeconds;
        isRunning = false;
    }
}
