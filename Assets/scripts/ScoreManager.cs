using UnityEngine;

// El puntaje es, literalmente, la distancia recorrida -- así que en vez de
// integrarlo a mano frame a frame se lee directo de DifficultyManager, que ya
// la calcula en forma cerrada a partir del tiempo de ronda compartido.
//
// Antes esto era `CurrentScore += CurrentSpeed * Time.deltaTime`, o sea otro
// acumulador más: dos clientes con distinto framerate terminaban la misma
// ronda con puntajes distintos aunque hubieran corrido exactamente lo mismo.
// Con la distancia derivada, el puntaje queda idéntico en todas las máquinas
// gratis -- algo que va a hacer falta sí o sí cuando exista la pantalla de
// resultados/podio (ver multiplayer-plan.md, sección 7).
//
// SE CONGELA AL MORIR, a mano (Fase 3.5, 26/8). Antes alcanzaba con que
// GameManager frenara el DifficultyManager al perder, y el puntaje quedaba
// clavado solo. Ahora ese reloj sigue corriendo mientras los rivales siguen
// jugando -- si no snapshotearamos acá, tu puntaje seguiría subiendo mientras
// mirás la partida de espectador, ya muerto.
public class ScoreManager : Singleton<ScoreManager>
{
    // null = seguís vivo, el puntaje se lee en vivo de la distancia.
    private float? scoreAtDeath;

    public float CurrentScore =>
        scoreAtDeath ?? (DifficultyManager.Instance != null ? DifficultyManager.Instance.DistanceTravelled : 0f);

    private void Start()
    {
        if (GameManager.Instance != null) GameManager.Instance.GameOver += FreezeScore;
    }

    private void OnDestroy()
    {
        if (GameManager.Instance != null) GameManager.Instance.GameOver -= FreezeScore;
    }

    private void FreezeScore()
    {
        if (scoreAtDeath.HasValue) return;
        scoreAtDeath = DifficultyManager.Instance != null ? DifficultyManager.Instance.DistanceTravelled : 0f;
    }
}
