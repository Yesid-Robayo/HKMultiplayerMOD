using System.Collections.Generic;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using UnityEngine;

namespace HKMultiplayer.Game.Players;

/// <summary>
/// Sincroniza a los jugadores: envía el estado del caballero local a 60 Hz (menos si está quieto) y dibuja a los
/// remotos que están en la misma sala, interpolados con <see cref="SnapshotBuffer"/>. Sin memoria nueva por frame.
/// </summary>
internal sealed class PlayerSync {
    /// <summary>Si el jugador está quieto, se repite su estado cada tantos ticks (para que se sepa que sigue ahí).</summary>
    private const int IdleResendTicks = 6;

    private sealed class Remote {
        public SessionPlayer Player;
        public readonly SnapshotBuffer Buffer = new();
        public RemoteAvatar Avatar;
    }

    private readonly Dictionary<ushort, Remote> _remotes = new();
    private readonly MessageHandler _onState;

    /// <summary>Efectos de las acciones de cada jugador (tajos, hechizos, dash...).</summary>
    private readonly PlayerEffects _effects = new();
    private GameSession _session;

    // Jugador local
    private HeroController _hero;
    private tk2dSprite _heroSprite;
    private MeshRenderer _heroRenderer;
    private HeroSprites _sprites;
    private uint _tick;
    private PlayerSnapshot _lastSent;
    private uint _lastSentTick;
    private bool _hasLastSent;

    private string _localScene = string.Empty;
    private ushort _localSceneHash;

    private PlayerSnapshot _incoming;
    private Font _font;

    public PlayerSync() {
        _onState = OnState;
    }

    /// <summary>Número de jugadores remotos dibujados en la sala.</summary>
    public int VisibleCount { get; private set; }

    public void Attach(GameSession session) {
        _session = session;
        session.On(MessageType.PlayerState, _onState);
        session.PlayerJoined += OnPlayerJoined;
        session.PlayerLeft += OnPlayerLeft;
        session.PlayerSceneChanged += OnPlayerSceneChanged;
        _effects.Attach(session, this);
        foreach (var player in session.Players) {
            if (!player.IsLocal) {
                OnPlayerJoined(player);
            }
        }
    }

    public void Detach() {
        if (_session != null) {
            _session.On(MessageType.PlayerState, null);
            _session.PlayerJoined -= OnPlayerJoined;
            _session.PlayerLeft -= OnPlayerLeft;
            _session.PlayerSceneChanged -= OnPlayerSceneChanged;
            _session = null;
        }

        // Antes que los avatares: sus efectos van pegados a ellos
        _effects.Detach();
        foreach (var remote in _remotes.Values) {
            remote.Avatar?.Destroy();
        }

        _remotes.Clear();
        _hasLastSent = false;
    }

    /// <summary>Busca un jugador remoto por id (para pruebas y para otras capas).</summary>
    public bool TryGetAvatarPosition(ushort id, out Vector3 position) {
        if (_remotes.TryGetValue(id, out var remote) && remote.Avatar != null) {
            position = remote.Avatar.Position;
            return true;
        }

        position = default;
        return false;
    }

    /// <summary>Objeto del avatar de un jugador remoto dibujado en la sala.</summary>
    /// <summary>Avatar del jugador remoto en la sala (null si no se dibuja).</summary>
    public RemoteAvatar AvatarOf(ushort id) => _remotes.TryGetValue(id, out var remote) ? remote.Avatar : null;

    public bool TryGetAvatarObject(ushort id, out GameObject avatar) {
        avatar = _remotes.TryGetValue(id, out var remote) && remote.Avatar != null ? remote.Avatar.Root : null;
        return avatar != null;
    }

    /// <summary>Si algún jugador remoto está en la sala del local (si no, no hace falta enviar sus efectos).</summary>
    public bool AnyRemoteInScene() {
        if (_localScene.Length == 0) {
            return false;
        }

        foreach (var remote in _remotes.Values) {
            if (remote.Player.Scene == _localScene) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Efectos enviados, recibidos y reproducidos (modo de prueba).</summary>
    public string DescribeEffects() => $"efectos enviados={_effects.Sent} recibidos={_effects.Received} reproducidos={_effects.Played} " +
                                       $"amuletos: {_effects.DescribeCharms()}";

    /// <summary>Primer jugador remoto dibujado en la sala (0 si ninguno).</summary>
    public ushort FirstVisibleId() {
        foreach (var pair in _remotes) {
            if (pair.Value.Avatar != null) {
                return pair.Key;
            }
        }

        return 0;
    }

    /// <summary>Retraso y variación del búfer de un jugador, en ticks (para el estado de pruebas).</summary>
    public bool TryGetBufferStats(ushort id, out double delay, out double jitter) {
        if (_remotes.TryGetValue(id, out var remote)) {
            delay = remote.Buffer.Delay;
            jitter = remote.Buffer.Jitter;
            return true;
        }

        delay = jitter = 0;
        return false;
    }

    /// <summary>Búfer de un jugador (diagnóstico en el modo de prueba).</summary>
    internal SnapshotBuffer BufferOf(ushort id) => _remotes.TryGetValue(id, out var remote) ? remote.Buffer : null;

    /// <summary>La sala local cambió (vacía si no se está jugando).</summary>
    public void SetLocalScene(string scene) {
        if (scene == _localScene) {
            return;
        }

        _localScene = scene;
        _localSceneHash = PlayerSnapshot.HashScene(scene);
        _hasLastSent = false;
        foreach (var remote in _remotes.Values) {
            remote.Buffer.Clear();
            DestroyAvatar(remote);
        }
    }

    // =====================================================================================================
    // Jugador local → red
    // =====================================================================================================

    /// <summary>Envía el estado del caballero local si toca. Llamar en LateUpdate, antes de actualizar la red.</summary>
    public void SendLocalState(double time) {
        if (_localScene.Length > 0) {
            _effects.UpdateLocal();
        }

        var tickNow = (uint) (time * NetConstants.TickRate);
        if (tickNow == _tick) {
            return;
        }

        _tick = tickNow;
        if (_session == null || !_session.CanSend || _localScene.Length == 0 || !RefreshHero()) {
            return;
        }

        var position = _hero.transform.position;
        var state = new PlayerSnapshot {
            Tick = _tick,
            SceneHash = _localSceneHash,
            X = position.x,
            Y = position.y,
            FacingRight = _hero.transform.localScale.x < 0f,
            Visible = _heroRenderer == null || _heroRenderer.enabled,
            Collection = _sprites.IndexOf(_heroSprite.Collection),
            SpriteId = (ushort) _heroSprite.spriteId
        };

        if (_hasLastSent && state.SameLook(_lastSent) && _tick - _lastSentTick < IdleResendTicks) {
            return;
        }

        var writer = _session.Begin(MessageType.PlayerState);
        state.Write(writer);
        _session.End();
        _lastSent = state;
        _lastSentTick = _tick;
        _hasLastSent = true;
    }

    private bool RefreshHero() {
        var hero = HeroController.instance;
        if (hero == null) {
            _hero = null;
            return false;
        }

        if (hero != _hero) {
            _hero = hero;
            _heroSprite = hero.GetComponent<tk2dSprite>();
            _heroRenderer = hero.GetComponent<MeshRenderer>();
            var animator = hero.GetComponent<tk2dSpriteAnimator>();
            if (_sprites == null && animator != null && animator.Library != null) {
                _sprites = new HeroSprites(animator.Library);
            }
        }

        return _heroSprite != null && _sprites != null;
    }

    // =====================================================================================================
    // Red → jugadores remotos
    // =====================================================================================================

    private void OnState(SessionPlayer from, PacketReader reader) {
        _incoming.Read(reader);
        if (!_remotes.TryGetValue(from.Id, out var remote)) {
            return;
        }

        // De otra sala (llegó justo al cambiar): no se dibuja aquí
        if (_incoming.SceneHash != _localSceneHash || _localSceneHash == 0) {
            return;
        }

        // Tras un parón de este PC los paquetes llegan juntos: no es variación de la red
        remote.Buffer.Add(_incoming, Time.unscaledTimeAsDouble, Time.unscaledDeltaTime < 0.1f);
    }

    /// <summary>Dibuja a los remotos de la sala. Llamar en LateUpdate, después de actualizar la red.</summary>
    public void UpdateAvatars() {
        var time = Time.unscaledTimeAsDouble;
        var visible = 0;
        var heroReady = RefreshHero();
        var z = heroReady ? _hero.transform.position.z : 0f;
        var showNames = Features.HostSettings.Current.ShowNames;
        foreach (var remote in _remotes.Values) {
            // Un caído no se dibuja: se ve su cuerpo (Features.Revive) y su caballero va oculto pegado al compañero
            var sameScene = heroReady && _localScene.Length > 0 && remote.Player.Scene == _localScene &&
                            !Features.PartyState.IsDown(remote.Player.Id);
            if (!sameScene) {
                if (remote.Avatar != null) {
                    DestroyAvatar(remote);
                    remote.Buffer.Clear();
                }

                continue;
            }

            if (!remote.Buffer.Sample(time, out var state)) {
                continue;
            }

            remote.Avatar ??= new RemoteAvatar(remote.Player.Name, _hero, Font);
            remote.Avatar.SetNameVisible(showNames);
            remote.Avatar.Apply(state, _sprites, z);
            _effects.UpdateRemote(remote.Player, remote.Avatar, remote.Buffer.RenderTick);
            visible++;
        }

        VisibleCount = visible;
    }

    private Font Font {
        get {
            if (_font == null) {
                // Unity 6 renombró la fuente integrada; se prueban los dos nombres
                _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (_font == null) {
                    _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                }
            }

            return _font;
        }
    }

    private void DestroyAvatar(Remote remote) {
        // Sus efectos se aparcan antes: si no, se destruirían con él
        _effects.Release(remote.Player.Id);
        remote.Avatar?.Destroy();
        remote.Avatar = null;
    }

    private void OnPlayerJoined(SessionPlayer player) {
        if (!player.IsLocal && !_remotes.ContainsKey(player.Id)) {
            _remotes[player.Id] = new Remote { Player = player };
        }
    }

    private void OnPlayerLeft(SessionPlayer player) {
        if (_remotes.TryGetValue(player.Id, out var remote)) {
            DestroyAvatar(remote);
            _remotes.Remove(player.Id);
            _effects.Forget(player.Id);
        }
    }

    private void OnPlayerSceneChanged(SessionPlayer player) {
        if (!player.IsLocal && _remotes.TryGetValue(player.Id, out var remote)) {
            remote.Buffer.Clear();
            DestroyAvatar(remote);
        }
    }
}
