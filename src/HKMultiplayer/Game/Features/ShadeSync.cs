using System.Collections;
using System.Collections.Generic;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.UI;
using HKMultiplayer.Util;
using UnityEngine;
using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Sombras compartidas: en cada PC se crea una copia de la sombra de cada compañero (la del juego, con su lógica,
/// leyendo los datos de su dueño: vida, hechizos, aguijón; sin la muerte por aguijón onírico). Cada PC mueve su copia,
/// así que no tiene por qué estar en el mismo sitio en los dos. Si alguien mata una copia, el dueño mata su propia
/// sombra por la vía normal y recupera geo y alma, aunque esté en otra sala ("Recuperaste tu sombra gracias a X"). El
/// host acepta solo el primer aviso (el geo no se cobra dos veces). La sombra de quien no está conectado no se ve.
/// </summary>
internal sealed class ShadeSync {
    /// <summary>Sombra de un jugador (jugador → host → los demás, con el dueño delante).</summary>
    public const MessageType ShadeState = (MessageType) 82;

    /// <summary>El jugador local mató la copia de la sombra de otro (→ host).</summary>
    public const MessageType ShadeKilled = (MessageType) 83;

    /// <summary>Un compañero mató tu sombra: recupérala (host → dueño, con el nombre de quien la mató).</summary>
    public const MessageType KillYourShade = (MessageType) 84;

    /// <summary>Datos enteros de la partida que lee la lógica de la sombra ("Shade Control").</summary>
    private static readonly string[] StatNames = {
        "shadeHealth", "shadeMP", "shadeFireballLevel", "shadeQuakeLevel", "shadeScreamLevel", "shadeSpecialType",
        "nailDamage", "royalCharmState"
    };

    private const float PollSeconds = 1f;

    /// <summary>Prefijo de las copias: empieza como la sombra del juego para que no se tomen por enemigos compartidos.</summary>
    private const string CopyName = "Hollow Shade Coop ";

    /// <summary>La sombra de un jugador tal como la cuenta su dueño.</summary>
    private sealed class State {
        public ushort Owner;
        public bool HasShade;
        public string Scene = string.Empty;
        public float X;
        public float Y;
        public readonly int[] Stats = new int[StatNames.Length];
        public string MapZone = string.Empty;

        /// <summary>Copia en esta sala (null si no hay).</summary>
        public GameObject Copy;

        public void Write(PacketWriter writer) {
            writer.WriteBool(HasShade);
            writer.WriteString(Scene);
            writer.WriteFloat(X);
            writer.WriteFloat(Y);
            writer.WriteByte((byte) Stats.Length);
            foreach (var stat in Stats) {
                writer.WriteInt(stat);
            }

            writer.WriteString(MapZone);
        }

        public void Read(PacketReader reader) {
            HasShade = reader.ReadBool();
            Scene = reader.ReadString(128);
            X = reader.ReadFloat();
            Y = reader.ReadFloat();
            var count = reader.ReadByte();
            for (var i = 0; i < count; i++) {
                var value = reader.ReadInt();
                if (i < Stats.Length) {
                    Stats[i] = value;
                }
            }

            MapZone = reader.ReadString(64);
        }
    }

    /// <summary>Copias que existen ahora y de quién son (las usan los ganchos de su lógica y de su muerte).</summary>
    private static readonly Dictionary<GameObject, State> Copies = new();

    private static ShadeSync _active;
    private static bool _hooked;

    private readonly Dictionary<ushort, State> _states = new();
    private readonly State _own = new();
    private readonly State _incoming = new();
    private readonly List<GameObject> _scratch = new();
    private readonly MessageHandler _onState;
    private readonly MessageHandler _onKilled;
    private readonly MessageHandler _onKillYours;
    private readonly System.Action<SessionPlayer> _onJoined;
    private readonly System.Action<SessionPlayer> _onLeft;
    private GameSession _session;
    private float _nextPoll;
    private bool _ownSent;
    private bool _recovering;

    static ShadeSync() {
        MessageRoutes.Register(ShadeState, Route.Server, reliable: true);
        MessageRoutes.Register(ShadeKilled, Route.Server, reliable: true);
        MessageRoutes.Register(KillYourShade, Route.Server, reliable: true);
    }

    public ShadeSync() {
        _onState = OnState;
        _onKilled = OnKilled;
        _onKillYours = OnKillYours;
        _onJoined = OnPlayerJoined;
        _onLeft = OnPlayerLeft;
    }

    /// <summary>Si el objeto es la copia de la sombra de un compañero.</summary>
    public static bool IsRemoteShade(GameObject shade) => shade != null && Copies.ContainsKey(shade);

    public void Attach(GameSession session) {
        _session = session;
        _active = this;
        _ownSent = false;
        _states.Clear();
        session.On(ShadeState, _onState);
        session.On(ShadeKilled, _onKilled);
        session.On(KillYourShade, _onKillYours);
        session.PlayerJoined += _onJoined;
        session.PlayerLeft += _onLeft;
    }

    public void Detach() {
        if (_session != null) {
            _session.On(ShadeState, null);
            _session.On(ShadeKilled, null);
            _session.On(KillYourShade, null);
            _session.PlayerJoined -= _onJoined;
            _session.PlayerLeft -= _onLeft;
            _session = null;
        }

        foreach (var state in _states.Values) {
            DestroyCopy(state);
        }

        _states.Clear();
        if (_active == this) {
            _active = null;
        }
    }

    // =====================================================================================================
    // Ganchos: la copia lee los datos de su dueño y su muerte avisa al dueño
    // =====================================================================================================

    /// <summary>Se llama al cargar el mod, antes de que el juego compile lo que llama a estos métodos.</summary>
    public static void InstallHooks() {
        if (_hooked) {
            return;
        }

        _hooked = true;
        On.HutongGames.PlayMaker.Actions.GetPlayerDataInt.OnEnter += (orig, self) => {
            if (TryGetStat(self.Fsm.GameObject, self.intName.Value, out var value)) {
                self.storeValue.Value = value;
                self.Finish();
                return;
            }

            orig(self);
        };
        On.HutongGames.PlayMaker.Actions.GetPlayerDataString.OnEnter += (orig, self) => {
            if (self.stringName.Value == "shadeMapZone" && self.Fsm.GameObject != null &&
                Copies.TryGetValue(self.Fsm.GameObject, out var state) && state.MapZone.Length > 0) {
                self.storeValue.Value = state.MapZone;
                self.Finish();
                return;
            }

            orig(self);
        };

        // La copia no muere por la vía normal (daría el geo y el alma al jugador local): se avisa al dueño
        On.HealthManager.Die += (orig, self, direction, attackType, ignoreEvasion) => {
            if (Copies.TryGetValue(self.gameObject, out var state)) {
                _active?.OnCopyKilled(self.gameObject, state);
                return;
            }

            orig(self, direction, attackType, ignoreEvasion);
        };
    }

    private static bool TryGetStat(GameObject owner, string name, out int value) {
        value = 0;
        if (owner == null || !Copies.TryGetValue(owner, out var state)) {
            return false;
        }

        for (var i = 0; i < StatNames.Length; i++) {
            if (StatNames[i] == name) {
                value = state.Stats[i];
                return true;
            }
        }

        return false;
    }

    // =====================================================================================================
    // Cada frame
    // =====================================================================================================

    public void Update(string scene) {
        if (_session == null) {
            return;
        }

        var gm = GameManager.instance;
        if (gm == null || HeroController.instance == null || gm.IsInSceneTransition || scene.Length == 0) {
            return;
        }

        if (Time.unscaledTime >= _nextPoll) {
            _nextPoll = Time.unscaledTime + PollSeconds;
            ReportOwnShade();
        }

        foreach (var state in _states.Values) {
            // El host apunta también la suya (para validar los avisos), pero esa es la del juego
            var shouldExist = state.HasShade && state.Scene == scene && state.Owner != _session.LocalId;
            if (shouldExist && state.Copy == null) {
                Spawn(state);
            } else if (!shouldExist && state.Copy != null) {
                DestroyCopy(state);
            }
        }
    }

    /// <summary>Informa al host de la sombra propia cuando aparece, desaparece o cambia.</summary>
    private void ReportOwnShade() {
        var pd = PlayerData.instance;
        if (pd == null || !_session.CanSend) {
            return;
        }

        var hasShade = pd.soulLimited && !string.IsNullOrEmpty(pd.shadeScene) && pd.shadeScene != "None";
        var changed = !_ownSent || hasShade != _own.HasShade;
        if (hasShade) {
            changed |= pd.shadeScene != _own.Scene || !Mathf.Approximately(pd.shadePositionX, _own.X) ||
                       !Mathf.Approximately(pd.shadePositionY, _own.Y) || (pd.shadeMapZone ?? string.Empty) != _own.MapZone;
            for (var i = 0; i < StatNames.Length; i++) {
                var value = pd.GetInt(StatNames[i]);
                changed |= value != _own.Stats[i];
                _own.Stats[i] = value;
            }
        }

        if (!changed) {
            return;
        }

        _own.HasShade = hasShade;
        _own.Scene = hasShade ? pd.shadeScene : string.Empty;
        _own.X = pd.shadePositionX;
        _own.Y = pd.shadePositionY;
        _own.MapZone = pd.shadeMapZone ?? string.Empty;
        _ownSent = true;
        Log.Info(hasShade ? $"Sombra propia en {_own.Scene} ({_own.X:0.0}, {_own.Y:0.0})" : "Sin sombra propia");
        var writer = _session.Begin(ShadeState);
        writer.WriteUShort(_session.LocalId);
        _own.Write(writer);
        _session.End();
    }

    /// <summary>Crea la copia con la lógica normal de la sombra (ataca al caballero de este PC), sin la muerte por aguijón onírico.</summary>
    private void Spawn(State state) {
        var prefab = GameManager.instance.sm != null ? GameManager.instance.sm.hollowShadeObject : null;
        if (prefab == null) {
            return;
        }

        // Dentro de un contenedor inactivo, para apuntarla antes de que su lógica lea los datos de la partida
        var holder = new GameObject("Sombra de compañero (preparando)");
        holder.SetActive(false);
        var shade = Object.Instantiate(prefab, holder.transform);
        shade.name = CopyName + state.Owner;
        foreach (var fsm in shade.GetComponents<PlayMakerFSM>()) {
            if (fsm.FsmName == "Dreamnail Kill") {
                fsm.enabled = false;
            }
        }

        Copies[shade] = state;
        shade.transform.SetParent(null, false);
        UnitySceneManager.MoveGameObjectToScene(shade, UnitySceneManager.GetActiveScene());
        shade.transform.SetPositionAndRotation(FreePosition(new Vector3(state.X, state.Y, prefab.transform.position.z)),
            Quaternion.identity);
        Object.Destroy(holder);
        shade.SetActive(true);
        state.Copy = shade;
        Log.Info($"Copia de la sombra de {NameOf(state.Owner)} en ({state.X:0.0}, {state.Y:0.0})");
    }

    /// <summary>Si la sombra propia ya ocupa ese punto, la copia se aparta un poco para que no se vean encimadas.</summary>
    private Vector3 FreePosition(Vector3 position) {
        var pd = PlayerData.instance;
        var scene = UnitySceneManager.GetActiveScene().name;
        for (var i = 0; i < 8; i++) {
            var taken = pd.soulLimited && pd.shadeScene == scene &&
                        Vector2.Distance(new Vector2(pd.shadePositionX, pd.shadePositionY), position) < 1f;
            foreach (var other in _states.Values) {
                taken |= other.Copy != null && Vector2.Distance(other.Copy.transform.position, position) < 1f;
            }

            if (!taken) {
                break;
            }

            position.x += 1.5f;
        }

        return position;
    }

    private static void DestroyCopy(State state) {
        if (state.Copy == null) {
            // Destruida con su sala: se quita del registro
            RemoveStale();
            return;
        }

        Copies.Remove(state.Copy);
        Object.Destroy(state.Copy);
        state.Copy = null;
    }

    private static void RemoveStale() {
        if (Copies.Count == 0) {
            return;
        }

        var stale = _active?._scratch;
        if (stale == null) {
            return;
        }

        stale.Clear();
        foreach (var shade in Copies.Keys) {
            if (shade == null) {
                stale.Add(shade);
            }
        }

        foreach (var shade in stale) {
            Copies.Remove(shade);
        }
    }

    /// <summary>El jugador local mató la copia: se quita y el host avisa al dueño.</summary>
    private void OnCopyKilled(GameObject shade, State state) {
        Copies.Remove(shade);
        state.Copy = null;
        // Hasta que el dueño confirme, no se vuelve a crear
        state.HasShade = false;
        RestoreMusic(shade);
        Object.Destroy(shade);
        Log.Info($"Copia de la sombra de {NameOf(state.Owner)} derrotada aquí");
        if (_session != null && _session.CanSend) {
            var writer = _session.Begin(ShadeKilled);
            writer.WriteUShort(state.Owner);
            _session.End();
        }
    }

    /// <summary>
    /// La música vuelve a la normal, como en la muerte de la sombra (su estado "Killed" pasa el audio a "Away"). Al
    /// acercarse, la zona de música de la sombra (su hijo "Music Control") lo pasa a "Close" y solo lo devuelve al salir
    /// de ella o al morir la sombra; la copia se destruye sin pasar por su muerte y la música de sombra seguía sonando.
    /// Si otra sombra cercana (la propia) tiene su música puesta, se deja.
    /// </summary>
    private static void RestoreMusic(GameObject shade) {
        foreach (var fsm in PlayMakerFSM.FsmList) {
            if (fsm != null && fsm.FsmName == "Music Control" && fsm.ActiveStateName == "Shade" &&
                !fsm.transform.IsChildOf(shade.transform)) {
                return;
            }
        }

        foreach (var fsm in shade.GetComponents<PlayMakerFSM>()) {
            if (fsm.FsmName != "Shade Control") {
                continue;
            }

            foreach (var state in fsm.FsmStates) {
                if (state.Name != "Killed") {
                    continue;
                }

                foreach (var action in state.Actions) {
                    if (action is HutongGames.PlayMaker.Actions.TransitionToAudioSnapshot transition &&
                        transition.snapshot.Value is UnityEngine.Audio.AudioMixerSnapshot snapshot) {
                        snapshot.TransitionTo(transition.transitionTime.Value);
                        Log.Info("La música de la sombra vuelve a la normal");
                        return;
                    }
                }
            }
        }
    }

    // =====================================================================================================
    // Red
    // =====================================================================================================

    private void OnState(SessionPlayer from, PacketReader reader) {
        var owner = reader.ReadUShort();
        _incoming.Read(reader);
        if (_session == null) {
            return;
        }

        if (_session.IsHost) {
            owner = from.Id;
        } else if (from.Id != GameSession.HostPlayerId) {
            return;
        }

        if (owner != _session.LocalId) {
            Store(owner, _incoming);
        }

        if (_session.IsHost) {
            // El host también apunta la del dueño (para validar después) y la reenvía
            if (owner == _session.LocalId) {
                Store(owner, _incoming);
            }

            ForwardState(_states[owner], from.Id);
        }
    }

    private void Store(ushort owner, State data) {
        if (!_states.TryGetValue(owner, out var state)) {
            state = new State { Owner = owner };
            _states[owner] = state;
        }

        // Una sombra nueva se puede volver a reclamar
        if (data.HasShade && (!state.HasShade || state.Scene != data.Scene)) {
            _claimed.Remove(owner);
        }

        var moved = state.HasShade != data.HasShade || state.Scene != data.Scene || !Mathf.Approximately(state.X, data.X) ||
                    !Mathf.Approximately(state.Y, data.Y);
        state.HasShade = data.HasShade;
        state.Scene = data.Scene;
        state.X = data.X;
        state.Y = data.Y;
        state.MapZone = data.MapZone;
        System.Array.Copy(data.Stats, state.Stats, state.Stats.Length);
        // Si cambió de sitio o ya no existe, la copia actual sobra (se rehace con los datos nuevos)
        if (moved) {
            DestroyCopy(state);
        }
    }

    /// <summary>El host reenvía la sombra de un jugador a todos menos a su dueño.</summary>
    private void ForwardState(State state, ushort except) {
        foreach (var player in _session.Players) {
            if (player.Connection == null || player.Id == except || player.Id == state.Owner) {
                continue;
            }

            var writer = _session.BeginTo(player, ShadeState);
            writer.WriteUShort(state.Owner);
            state.Write(writer);
            _session.EndTo();
        }
    }

    private void OnKilled(SessionPlayer from, PacketReader reader) {
        var owner = reader.ReadUShort();
        // Solo vale el primer aviso por sombra: el geo no se cobra dos veces
        if (_session == null || !_session.IsHost || !_states.TryGetValue(owner, out var state) || !_claimed.Add(owner)) {
            return;
        }

        Log.Info($"{from.Name} mató la sombra de {NameOf(owner)}");
        state.HasShade = false;
        DestroyCopy(state);
        // A los demás: ya no hay sombra; al dueño: que la recupere
        ForwardState(state, 0);
        if (owner == _session.LocalId) {
            RecoverOwnShade(from.Name);
            return;
        }

        var target = PartyState.Current?.Get(owner)?.Player;
        if (target?.Connection != null) {
            var writer = _session.BeginTo(target, KillYourShade);
            writer.WriteString(from.Name);
            _session.EndTo();
        }
    }

    /// <summary>Dueños cuya sombra ya se dio por muerta (hasta que informen de una nueva).</summary>
    private readonly HashSet<ushort> _claimed = new();

    private void OnKillYours(SessionPlayer from, PacketReader reader) {
        var killer = reader.ReadString(64);
        if (_session != null && !_session.IsHost && from.Id == GameSession.HostPlayerId) {
            RecoverOwnShade(killer);
        }
    }

    private void OnPlayerJoined(SessionPlayer player) {
        if (_session == null || !_session.IsHost || player.Connection == null) {
            return;
        }

        // Quien entra recibe las sombras que ya había (la del host incluida)
        foreach (var state in _states.Values) {
            if (!state.HasShade || state.Owner == player.Id) {
                continue;
            }

            var writer = _session.BeginTo(player, ShadeState);
            writer.WriteUShort(state.Owner);
            state.Write(writer);
            _session.EndTo();
        }
    }

    private void OnPlayerLeft(SessionPlayer player) {
        if (!_states.TryGetValue(player.Id, out var state)) {
            return;
        }

        // La sombra de quien no está conectado no se ve
        state.HasShade = false;
        DestroyCopy(state);
        _states.Remove(player.Id);
        _claimed.Remove(player.Id);
    }

    // =====================================================================================================
    // Recuperar la propia
    // =====================================================================================================

    private void RecoverOwnShade(string killer) {
        var pd = PlayerData.instance;
        if (pd == null || !pd.soulLimited || _recovering) {
            return;
        }

        Log.Info($"{killer} mató nuestra sombra: se recupera el geo y el alma");
        if (HostSettings.Current.ShowNotices) {
            Notice.Show(Lang.T($"Recuperaste tu sombra gracias a {killer}", $"You got your shade back thanks to {killer}"), 5f);
        }
        MultiplayerRunner.Instance.StartCoroutine(KillOwnShade());
    }

    /// <summary>
    /// Mata la sombra propia por la vía normal del juego (devuelve el geo, quita la grieta del alma). Si no está en esta
    /// sala, se crea un momento junto al caballero para matarla ahí.
    /// </summary>
    private IEnumerator KillOwnShade() {
        _recovering = true;
        try {
            var shade = FindOwnShade();
            if (shade == null) {
                var prefab = GameManager.instance.sm != null ? GameManager.instance.sm.hollowShadeObject : null;
                var hero = HeroController.instance;
                if (prefab == null || hero == null) {
                    yield break;
                }

                var position = hero.transform.position;
                var spawned = Object.Instantiate(prefab, new Vector3(position.x, position.y + 1.5f, prefab.transform.position.z),
                    Quaternion.identity);
                // Su lógica se prepara antes de matarla
                yield return new WaitForSeconds(0.3f);
                shade = spawned != null ? spawned.GetComponent<HealthManager>() : null;
            }

            if (shade != null) {
                shade.Die(0f, AttackTypes.Nail, true);
            }

            // Se informa enseguida de cómo quedó (si no se pudo, la sombra sigue y los demás vuelven a verla)
            yield return new WaitForSeconds(1f);
            _ownSent = false;
            _nextPoll = 0f;
        } finally {
            _recovering = false;
        }
    }

    private static HealthManager FindOwnShade() {
        foreach (var health in Object.FindObjectsByType<HealthManager>(FindObjectsSortMode.None)) {
            if (health.gameObject.name.StartsWith("Hollow Shade", System.StringComparison.Ordinal) &&
                !Copies.ContainsKey(health.gameObject)) {
                return health;
            }
        }

        return null;
    }

    private static string NameOf(ushort id) => PartyState.Current?.Get(id)?.Name ?? id.ToString();

    // =====================================================================================================
    // Modo de prueba
    // =====================================================================================================

    /// <summary>Mata la primera copia de sombra ajena de la sala como si la golpeara el jugador.</summary>
    public bool TestKillCopy() {
        foreach (var shade in Copies.Keys) {
            if (shade != null) {
                shade.GetComponent<HealthManager>().Die(0f, AttackTypes.Nail, true);
                return true;
            }
        }

        return false;
    }

    public string Describe() {
        var text = new System.Text.StringBuilder();
        text.Append($"propia={(_own.HasShade ? $"{_own.Scene}({_own.X:0.0},{_own.Y:0.0})" : "-")} copias aquí={Copies.Count} ");
        foreach (var state in _states.Values) {
            text.Append($"[{NameOf(state.Owner)}: {(state.HasShade ? $"{state.Scene}({state.X:0.0},{state.Y:0.0})" : "-")}" +
                        $"{(state.Copy != null ? " copia" : "")}] ");
        }

        return text.ToString();
    }
}
