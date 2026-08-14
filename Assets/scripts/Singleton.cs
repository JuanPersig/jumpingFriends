using UnityEngine;

// Base genérica para los singletons "uno por escena" del proyecto
// (PlayerInputProvider, GameManager, DifficultyManager, ScoreManager,
// ObstacleSpawner, CharacterSelection): si ya existe otra instancia, esta
// se autodestruye; si no, se registra como Instance. Antes cada script
// repetía este mismo bloque letra por letra.
//
// NO hace DontDestroyOnLoad acá -- eso es una decisión de CADA singleton
// (solo lo necesitan los que sobreviven el cambio de escena, como
// PlayerInputProvider y CharacterSelection), así que lo siguen llamando
// ellos mismos en su propio Awake.
//
// OJO al heredar: Destroy(gameObject) no corta la ejecución del Awake()
// actual (Unity lo destruye recién al final del frame). Si tu Awake()
// override tiene lógica extra (además de registrarse como Instance),
// chequeá "if (Instance != this) return;" después de base.Awake() antes
// de esa lógica extra -- si no, una instancia duplicada (a punto de
// destruirse) la ejecutaría igual. Ver GameManager/PlayerInputProvider/
// CharacterSelection para el patrón.
public abstract class Singleton<T> : MonoBehaviour where T : MonoBehaviour
{
    public static T Instance { get; private set; }

    protected virtual void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this as T;
    }
}
