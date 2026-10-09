using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.UI;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Lo que se hace desde la página "Compañeros" del panel: dar geo a un compañero (si el host lo permite), desatascarse
/// (al último suelo seguro) y recargar la sala. Las dos últimas se hacen al quitar la pausa.
/// </summary>
internal sealed class PartyActions {
    /// <summary>Regalo de geo, ya descontado al que lo da (→ host, con el destinatario y la cantidad).</summary>
    public const MessageType GeoGift = (MessageType) 85;

    /// <summary>Geo para ti: de quién, cuánto y si es una devolución (host → jugador).</summary>
    public const MessageType GeoReceived = (MessageType) 86;

    private enum Pending {
        None,
        Unstuck,
        Reload
    }

    private readonly MessageHandler _onGift;
    private readonly MessageHandler _onReceived;
    private GameSession _session;
    private Revive _revive;
    private Pending _pending;

    /// <summary>Geo recibido sin caballero (en una carga); se entrega al volver.</summary>
    private int _pendingGeo;

    static PartyActions() {
        MessageRoutes.Register(GeoGift, Route.Server, reliable: true);
        MessageRoutes.Register(GeoReceived, Route.Server, reliable: true);
    }

    public PartyActions() {
        _onGift = OnGift;
        _onReceived = OnReceived;
    }

    public void Attach(GameSession session, Revive revive) {
        _session = session;
        _revive = revive;
        _pending = Pending.None;
        session.On(GeoGift, _onGift);
        session.On(GeoReceived, _onReceived);
    }

    public void Detach() {
        if (_session != null) {
            _session.On(GeoGift, null);
            _session.On(GeoReceived, null);
            _session = null;
        }

        _pending = Pending.None;
    }

    // =====================================================================================================
    // Geo
    // =====================================================================================================

    /// <summary>Da geo a un compañero. Devuelve el error o null si se envió.</summary>
    public string TryGiftGeo(ushort targetId, int amount) {
        var pd = PlayerData.instance;
        var hero = HeroController.instance;
        if (_session == null || !_session.CanSend) {
            return "No estás conectado a ninguna partida";
        }

        if (!HostSettings.Current.AllowGeoGifts) {
            return "El host desactivó regalar geo";
        }

        if (pd == null || hero == null) {
            return "Ahora no se puede regalar geo";
        }

        if (amount <= 0) {
            return "Elige cuánto geo dar";
        }

        if (pd.geo < amount) {
            return $"No tienes {amount} geo (tienes {pd.geo})";
        }

        hero.TakeGeo(amount);
        var writer = _session.Begin(GeoGift);
        writer.WriteUShort(targetId);
        writer.WriteInt(amount);
        _session.End();
        Log.Info($"Regalo de {amount} geo a {PartyState.Current?.Get(targetId)?.Name ?? targetId.ToString()}");
        return null;
    }

    private void OnGift(SessionPlayer from, PacketReader reader) {
        var targetId = reader.ReadUShort();
        var amount = reader.ReadInt();
        if (_session == null || !_session.IsHost || amount <= 0 || amount > 1000000) {
            return;
        }

        var target = PartyState.Current?.Get(targetId);
        var accepted = HostSettings.Current.AllowGeoGifts && target != null && targetId != from.Id;
        if (accepted) {
            Log.Info($"{from.Name} regala {amount} geo a {target.Name}");
            SendGeo(targetId, from.Name, amount, refund: false);
        } else {
            SendGeo(from.Id, target?.Name ?? "tu compañero", amount, refund: true);
        }
    }

    private void SendGeo(ushort to, string name, int amount, bool refund) {
        if (to == _session.LocalId) {
            HandleGeo(name, amount, refund);
            return;
        }

        var player = PartyState.Current?.Get(to)?.Player;
        if (player?.Connection == null) {
            return;
        }

        var writer = _session.BeginTo(player, GeoReceived);
        writer.WriteString(name);
        writer.WriteInt(amount);
        writer.WriteBool(refund);
        _session.EndTo();
    }

    private void OnReceived(SessionPlayer from, PacketReader reader) {
        var name = reader.ReadString(64);
        var amount = reader.ReadInt();
        var refund = reader.ReadBool();
        if (_session != null && !_session.IsHost && from.Id == GameSession.HostPlayerId && amount > 0) {
            HandleGeo(name, amount, refund);
        }
    }

    private void HandleGeo(string name, int amount, bool refund) {
        GiveGeo(amount);
        Notice.Show(refund ? $"No se pudo dar el geo a {name}: se te devolvió" : $"{name} te regaló {amount} geo", 5f);
        Log.Info(refund ? $"Geo devuelto ({amount})" : $"{name} regaló {amount} geo");
    }

    private void GiveGeo(int amount) {
        var hero = HeroController.instance;
        if (hero != null) {
            hero.AddGeo(amount);
        } else {
            _pendingGeo += amount;
        }
    }

    // =====================================================================================================
    // Desatascar y recargar sala
    // =====================================================================================================

    /// <summary>Desatascarse al quitar la pausa. Devuelve el error o null.</summary>
    public string RequestUnstuck() {
        if (_revive != null && _revive.IsDowned) {
            return "Estás caído: espera a que te revivan";
        }

        _pending = Pending.Unstuck;
        return null;
    }

    /// <summary>Recargar la sala al quitar la pausa. Devuelve el error o null.</summary>
    public string RequestReload() {
        if (_revive != null && _revive.IsDowned) {
            return "Estás caído: espera a que te revivan";
        }

        _pending = Pending.Reload;
        return null;
    }

    /// <summary>Llamar cada frame: hace lo pendiente en cuanto se juega (sin pausa ni cambio de sala).</summary>
    public void Update() {
        var hero = HeroController.instance;
        if (_pendingGeo > 0 && hero != null) {
            hero.AddGeo(_pendingGeo);
            _pendingGeo = 0;
        }

        var gm = GameManager.instance;
        if (_pending == Pending.None || gm == null || hero == null || gm.isPaused ||
            gm.gameState != GlobalEnums.GameState.PLAYING || gm.IsInSceneTransition) {
            return;
        }

        var action = _pending;
        _pending = Pending.None;
        if (_revive != null && _revive.IsDowned) {
            return;
        }

        if (action == Pending.Reload) {
            Log.Info("Recargar sala desde el panel");
            if (_revive == null || !_revive.ReloadCurrentRoom()) {
                Notice.Show("Ahora no se puede recargar la sala", 3f);
            }

            return;
        }

        // Al último suelo seguro de la sala (el mismo punto al que devuelven los pinchos)
        var target = PlayerData.instance.hazardRespawnLocation;
        Log.Info($"Desatascar: a ({target.x:0.0}, {target.y:0.0})");
        hero.transform.position = target;
        var body = hero.GetComponent<Rigidbody2D>();
        if (body != null) {
            body.linearVelocity = Vector2.zero;
        }

        Notice.Show("Volviste al último suelo seguro", 3f);
    }
}
