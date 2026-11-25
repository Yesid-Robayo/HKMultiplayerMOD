using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// La sala del Hollow Knight (<c>Room_Final_Boss_Core</c>).
/// <para>
/// Su presentación: quien entra (o recarga la sala) con el combate ya empezado en el PC de un compañero recibe al jefe
/// suelto y peleando, pero su presentación («Battle Start») seguía al principio: el Hollow Knight colgado de las cadenas
/// y otro peleando a la vez (issue #25). Con el jefe ya encendido aquí y la presentación sin empezar, se salta a su final
/// (`Fight Start`: quita al encadenado y la cámara de la presentación) y las cadenas pasan a rotas.
/// </para>
/// <para>
/// Su cuerpo, al vencerlo, tiene que quedar en la arena: es donde el caballero
/// absorbe el vacío con Focus para ver el final. En el issue #25 aparecía fuera de la sala al morir (desaparecía y nadie
/// podía acabar el juego): recibía la posición de otra pieza del jefe porque la lista de piezas no era igual en los dos
/// PCs (arreglado en <c>EnemySync.DetachablePieces</c>). Por si algo más lo dejara fuera, esta es la red de seguridad: si
/// acaba por debajo del suelo de la arena o fuera de la sala, se vuelve a poner sobre el suelo, donde murió el jefe, y se
/// recalcula la zona del Focus. Lo hace cada PC con el suyo: en todos el cuerpo tiene su propia lógica (ver
/// <c>EnemySync.DetachPart</c>).
/// </para>
/// </summary>
internal sealed class FinalBossRoom {
    private const string Scene = "Room_Final_Boss_Core";
    private const string CorpseName = "Boss Corpse";
    private const string CorpseFsm = "Corpse";
    private const string BossName = "Hollow Knight Boss";

    /// <summary>La presentación: el objeto que la lleva, su FSM, sus estados de antes de empezar y el del final.</summary>
    private const string ArenaName = "Boss Control";

    private const string IntroFsm = "Battle Start";
    private const string IntroEnd = "Fight Start";
    private const string FinalChainsVariable = "Final Chains";

    /// <summary>Las cadenas que se rompen a aguijonazos, y el estado de su FSM en el que quedan rotas.</summary>
    private const string ChainPrefix = "hollow_knight_chain_base";

    private const string ChainFsm = "Control";
    private const string ChainBroken = "Disable";

    /// <summary>
    /// Variables de su FSM que su estado inicial calcula desde su x: la zona en la que el caballero tiene que estar para
    /// el Focus (su x ± 12). Al ponerlo en otro sitio se recalculan.
    /// </summary>
    private const string SelfXVariable = "Self X";

    private const string DistanceMinVariable = "Distance Min";
    private const string DistanceMaxVariable = "Distance Max";
    private const float FocusReach = 12f;


    /// <summary>Variable de su FSM con su x al morir limitada a la arena (la usa para la cámara).</summary>
    private const string ArenaXVariable = "CamLock X";

    /// <summary>Límites de la cámara en x, los mismos con los que su FSM limita esa variable.</summary>
    private const float CameraMinX = 34.38f;

    private const float CameraMaxX = 58.12f;

    /// <summary>
    /// Lo que la sala se extiende a cada lado más allá de los límites de la cámara (el jefe muere hasta en x ≈ 66,6 y su
    /// cuerpo cae ahí, que es correcto). Más allá, está fuera.
    /// </summary>
    private const float ArenaBeyondCamera = 14f;

    private const float ArenaMinX = CameraMinX - ArenaBeyondCamera;
    private const float ArenaMaxX = CameraMaxX + ArenaBeyondCamera;

    /// <summary>
    /// Altura desde la que se busca el suelo de la arena hacia abajo (algo por encima de él: el caballero de pie está en
    /// y ≈ 6,4), y hasta dónde.
    /// </summary>
    private const float ProbeY = 10f;

    private const float ProbeDistance = 30f;

    /// <summary>Por debajo del suelo de la arena más que esto, el cuerpo está fuera.</summary>
    private const float BelowFloorMargin = 3f;

    private const int TerrainLayer = 8;

    /// <summary>Cada cuánto se mira: más a menudo hasta que aparece (para apuntar cómo salió).</summary>
    private const float SearchInterval = 0.1f;

    private const float CheckInterval = 0.5f;

    /// <summary>El objeto de la arena de esta visita: si se destruye, se recargó la sala (el nombre de la escena no cambia).</summary>
    private GameObject _arena;

    private GameObject _corpse;

    /// <summary>El jefe, apuntado mientras vive: al morir se apaga donde estaba, que es donde tiene que quedar su cuerpo.</summary>
    private GameObject _boss;

    private float _nextCheck;

    /// <summary>Ya se apuntó en el registro cómo apareció el cuerpo en esta sala.</summary>
    private bool _noted;

    /// <summary>Ya se saltó la presentación en esta visita.</summary>
    private bool _introSkipped;

    /// <summary>Veces que se devolvió el cuerpo a la arena en esta sala (diagnóstico).</summary>
    public int Rescued { get; private set; }

    public void Update(string scene) {
        if (scene != Scene) {
            Reset();
            return;
        }

        var now = Time.unscaledTime;
        if (now < _nextCheck) {
            return;
        }

        // Sala nueva (también al recargar la misma): todo lo apuntado era de la anterior
        if (_arena == null) {
            Reset();
            _arena = GameObject.Find(ArenaName);
            if (_arena == null) {
                return;
            }
        }

        _nextCheck = now + (_noted ? CheckInterval : SearchInterval);
        if (_boss == null) {
            _boss = GameObject.Find(BossName);
        }

        if (!_introSkipped && _boss != null && _boss.activeInHierarchy) {
            SkipIntro();
        }

        // Solo existe activo una vez muerto el jefe, ya suelto de él
        if (_corpse == null || !_corpse.activeInHierarchy) {
            _corpse = GameObject.Find(CorpseName);
        }

        if (_corpse == null || !_corpse.activeInHierarchy || _corpse.transform.parent != null) {
            return;
        }

        var fsm = PlayMakerFSM.FindFsmOnGameObject(_corpse, CorpseFsm);
        // Donde murió el jefe; si no se llegó a ver, la x que su FSM limitó a la arena
        var arenaX = fsm != null ? fsm.FsmVariables.GetFsmFloat(ArenaXVariable)?.Value ?? 0f : 0f;
        var x = Mathf.Clamp(_boss != null ? _boss.transform.position.x : arenaX > 0f ? arenaX : _corpse.transform.position.x,
            ArenaMinX, ArenaMaxX);
        var floor = Physics2D.Raycast(new Vector2(x, ProbeY), Vector2.down, ProbeDistance, 1 << TerrainLayer);
        // Sin suelo debajo, o empezando dentro de algo (distancia 0), no se sabe dónde ponerlo
        if (floor.collider == null || floor.distance <= 0f) {
            return;
        }

        var position = _corpse.transform.position;
        var body = _corpse.GetComponent<Rigidbody2D>();
        if (!_noted) {
            _noted = true;
            Log.Info($"Cuerpo del Hollow Knight en ({position.x:0.0}, {position.y:0.0}), velocidad " +
                     $"{(body != null ? body.linearVelocity : Vector2.zero)}, estado {(fsm != null ? fsm.ActiveStateName : "-")}, " +
                     $"suelo en y={floor.point.y:0.0}");
        }

        if (position.y >= floor.point.y - BelowFloorMargin && position.x >= ArenaMinX && position.x <= ArenaMaxX) {
            return;
        }

        // El fondo de su caja justo sobre el suelo
        var box = _corpse.GetComponent<Collider2D>();
        var bottomOffset = box != null ? position.y - box.bounds.min.y : 0f;
        var target = new Vector3(x, floor.point.y + bottomOffset + 0.05f, position.z);
        _corpse.transform.position = target;
        if (body != null) {
            body.linearVelocity = Vector2.zero;
        }

        if (fsm != null) {
            SetFloat(fsm, SelfXVariable, x);
            SetFloat(fsm, DistanceMinVariable, x - FocusReach);
            SetFloat(fsm, DistanceMaxVariable, x + FocusReach);
            SetFloat(fsm, ArenaXVariable, Mathf.Clamp(x, CameraMinX, CameraMaxX));
        }

        Rescued++;
        Log.Warn($"Cuerpo del Hollow Knight fuera de la arena en ({position.x:0.0}, {position.y:0.0}), " +
                 $"estado {(fsm != null ? fsm.ActiveStateName : "-")}: se pone en el suelo en ({target.x:0.0}, {target.y:0.0})");
    }

    private void Reset() {
        _arena = null;
        _corpse = null;
        _boss = null;
        _noted = false;
        _introSkipped = false;
        Rescued = 0;
    }

    /// <summary>
    /// El jefe ya pelea aquí: si la presentación no ha empezado (se entró con el combate en marcha en otro PC), se salta a
    /// su final y las cadenas quedan rotas. Si ya empezó, sigue sola (es la que lo enciende).
    /// </summary>
    private void SkipIntro() {
        var arena = _arena;
        var intro = PlayMakerFSM.FindFsmOnGameObject(arena, IntroFsm);
        // Recién cargada, aún prepara la sala (`Pause`, `Init`): se mira otra vez después
        if (intro == null || intro.ActiveStateName is "Pause" or "Init" or "") {
            return;
        }

        _introSkipped = true;
        if (intro.ActiveStateName is not ("Idle" or "Revisit")) {
            return;
        }

        Log.Info($"Hollow Knight: el combate ya empezó en otro PC; la presentación ({intro.ActiveStateName}) salta a su final");
        intro.SetState(IntroEnd);
        var chains = intro.FsmVariables.GetFsmGameObject(FinalChainsVariable)?.Value;
        if (chains != null) {
            chains.SetActive(false);
        }

        foreach (var fsm in arena.GetComponentsInChildren<PlayMakerFSM>(true)) {
            if (fsm.FsmName == ChainFsm && fsm.gameObject.name.StartsWith(ChainPrefix, System.StringComparison.Ordinal) &&
                fsm.ActiveStateName != ChainBroken) {
                fsm.SetState(ChainBroken);
            }
        }
    }

    private static void SetFloat(PlayMakerFSM fsm, string name, float value) {
        var variable = fsm.FsmVariables.GetFsmFloat(name);
        if (variable != null) {
            variable.Value = value;
        }
    }
}
