using UnityEngine;

namespace HKMultiplayer.Game.Players;

/// <summary>
/// La trampa del banco de la Aldea Distante (<c>RestBench Spider</c>, FSM <c>Bench Control Spider</c>) también se ve con
/// un compañero dentro, sin red. Al sentarse, el juego oculta al caballero y enciende en el banco un caballero envuelto en
/// telaraña (<c>Knight Anim</c> y <c>Webbed</c>), pero solo en el PC de quien cae: en los demás el compañero desaparecía.
/// <para>
/// En cada PC, si el avatar de un compañero se oculta justo en el sitio del banco, se enciende ese caballero atrapado
/// (y se apaga el banco normal); cuando el compañero vuelve a verse (rompe la telaraña y cae), se deja como estaba. Si es
/// el caballero local el que está atrapado, el banco es suyo y no se toca.
/// </para>
/// </summary>
internal sealed class PartnerSpiderBench {
    private const string SceneName = "Deepnest_Spider_Town";
    private const string BenchName = "RestBench Spider";
    private const string BenchFsm = "Bench Control Spider";

    /// <summary>Estados de la FSM del banco en los que el caballero local no está en él.</summary>
    private static readonly string[] FreeStates = { "Pause Frame", "Init", "Idle", "In Range", "Can Rest?", "Cancel Frame" };

    /// <summary>Lo lejos del banco que puede estar el avatar oculto para contar como atrapado en él.</summary>
    private const float ReachX = 1.5f;

    private const float ReachY = 2.5f;

    private string _scene = string.Empty;
    private bool _searched;
    private GameObject _bench;
    private PlayMakerFSM _fsm;
    private SpriteRenderer _benchSprite;
    private SpriteRenderer _webbed;
    private MeshRenderer _knight;
    private tk2dSpriteAnimator _knightAnimator;

    /// <summary>Si ahora se muestra a un compañero atrapado.</summary>
    public bool Showing { get; private set; }

    private bool _captured;

    /// <summary>La sala local cambió.</summary>
    public void SetScene(string scene) {
        if (scene == _scene) {
            return;
        }

        _scene = scene;
        _searched = false;
        _bench = null;
        Showing = false;
    }

    /// <summary>Al empezar cada frame de avatares: aún no se vio a nadie atrapado.</summary>
    public void Begin() => _captured = false;

    /// <summary>Un avatar de compañero en <paramref name="position"/>, visible o no.</summary>
    public void Touch(Vector3 position, bool visible) {
        if (visible || _scene != SceneName || !Find()) {
            return;
        }

        var bench = _bench.transform.position;
        if (Mathf.Abs(position.x - bench.x) <= ReachX && Mathf.Abs(position.y - bench.y) <= ReachY) {
            _captured = true;
        }
    }

    /// <summary>Al acabar el frame de avatares: muestra o quita al compañero atrapado.</summary>
    public void End() {
        if (_bench == null) {
            return;
        }

        // El banco lo usa el caballero local: es su trampa y su FSM manda
        var localOnIt = _fsm != null && System.Array.IndexOf(FreeStates, _fsm.ActiveStateName) < 0;
        var show = _captured && !localOnIt;
        if (show == Showing) {
            return;
        }

        if (localOnIt && Showing) {
            Showing = false;
            return;
        }

        Showing = show;
        if (_benchSprite != null) {
            _benchSprite.enabled = !show;
        }

        if (_webbed != null) {
            _webbed.enabled = show;
        }

        if (_knight != null) {
            _knight.enabled = show;
        }

        if (show && _knightAnimator != null && _knightAnimator.GetClipByName("Shake") != null) {
            _knightAnimator.Play("Shake");
        }

        Util.Log.Info(show ? "Un compañero está atrapado en el banco de la Aldea Distante" : "El compañero salió del banco trampa");
    }

    private bool Find() {
        if (_bench != null) {
            return true;
        }

        if (_searched) {
            return false;
        }

        _searched = true;
        _bench = GameObject.Find(BenchName);
        if (_bench == null) {
            return false;
        }

        foreach (var fsm in _bench.GetComponents<PlayMakerFSM>()) {
            if (fsm.FsmName == BenchFsm) {
                _fsm = fsm;
            }
        }

        _benchSprite = _bench.GetComponent<SpriteRenderer>();
        _webbed = _bench.transform.Find("Webbed")?.GetComponent<SpriteRenderer>();
        var knight = _bench.transform.Find("Knight Anim");
        _knight = knight != null ? knight.GetComponent<MeshRenderer>() : null;
        _knightAnimator = knight != null ? knight.GetComponent<tk2dSpriteAnimator>() : null;
        return true;
    }
}
