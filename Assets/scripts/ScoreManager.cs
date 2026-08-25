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
// DifficultyManager.Stop() (que dispara GameManager.TriggerGameOver) congela
// la distancia, así que el puntaje también queda clavado al morir sin que
// haga falta chequear IsGameOver acá.
public class ScoreManager : Singleton<ScoreManager>
{
    public float CurrentScore =>
        DifficultyManager.Instance != null ? DifficultyManager.Instance.DistanceTravelled : 0f;
}
