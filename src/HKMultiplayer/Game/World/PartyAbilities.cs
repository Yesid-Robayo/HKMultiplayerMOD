using System;
using System.Collections.Generic;
using System.Reflection;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.World;

/// <summary>
/// Habilidades de todo el grupo para lo que el juego carga según ellas. Las habilidades son de cada jugador
/// (<see cref="CharacterData"/>), pero hay partes de sala que se cargan solo si el jugador local tiene una: el combate del
/// Señor Traidor (<c>Fungus3_23_boss</c>) exige la Sombra. Con un jugador que la tenía y otro que no, el jefe existía en
/// un PC y en el otro la sala estaba vacía. Ahora cada jugador avisa a los demás de las suyas y, al decidir si se carga
/// una de esas partes (<see cref="SceneAdditiveLoadConditional"/>), basta con que alguien del grupo la tenga. Para moverse
/// y atacar siguen siendo de cada uno.
/// </summary>
internal sealed class PartyAbilities {
    /// <summary>Las habilidades de un jugador, como bits de <see cref="Fields"/> (jugador → todos).</summary>
    public const MessageType Abilities = (MessageType) 112;

    /// <summary>Habilidades que el juego mira para cargar partes de sala (sí/no; las de nivel cuentan si son más que 0).</summary>
    private static readonly string[] Fields = {
        "hasDash", "hasShadowDash", "hasWalljump", "hasDoubleJump", "hasSuperDash", "hasAcidArmour", "hasKingsBrand",
        "hasDreamNail", "dreamNailUpgraded", "hasDreamGate", "hasSpell"
    };

    private static readonly FieldInfo[] FieldInfos = Array.ConvertAll(Fields,
        name => typeof(PlayerData).GetField(name, BindingFlags.Public | BindingFlags.Instance));

    /// <summary>
    /// Objetos de sala cuya FSM mira una habilidad para preparar la sala misma (y su FSM): con las del grupo, la sala queda
    /// igual en todos los PCs. La pared del Falso Caballero se ve reparada si se tiene el primer hechizo (y si no, sigue la
    /// pared de antes: en un PC sólida y en otro no), la puerta del Abismo y el alud del Borde del Reino miran la Marca del
    /// Rey y el puente del Pico de Cristal, el Corazón de Cristal. El viento de la sala de las Alas de Monarca
    /// (`Abyss_19`) para cuando se tiene el doble salto: en un PC soplaba y en otro no. Las demás lecturas de habilidades
    /// (el ácido, los diálogos, lo que da la habilidad) siguen siendo de cada uno.
    /// </summary>
    private static readonly (string Name, string Fsm)[] RoomFsms = {
        ("Fk Break Wall", "Control"), ("abyss_door", "Control"), ("mine_break_bridge", "Bridge Control"),
        ("Avalanche", "Activate"), ("Windy_Set", "Set Windy")
    };

    /// <summary>Cada cuánto se mira si cambiaron las propias (se envían solo al cambiar o al entrar alguien).</summary>
    private const float CheckInterval = 1f;

    private static PartyAbilities _active;
    private static bool _hooked;

    private readonly Dictionary<ushort, uint> _remote = new();
    private readonly MessageHandler _onAbilities;
    private readonly Action<SessionPlayer> _onJoined;
    private readonly Action<SessionPlayer> _onLeft;
    private GameSession _session;
    private uint _sent;
    private bool _dirty;
    private float _nextCheck;

    static PartyAbilities() {
        MessageRoutes.Register(Abilities, Route.Everyone, reliable: true);
    }

    public PartyAbilities() {
        _onAbilities = OnAbilities;
        _onJoined = _ => _dirty = true;
        _onLeft = player => _remote.Remove(player.Id);
    }

    public void Attach(GameSession session) {
        _session = session;
        _remote.Clear();
        _dirty = true;
        _active = this;
        session.On(Abilities, _onAbilities);
        session.PlayerJoined += _onJoined;
        session.PlayerLeft += _onLeft;
    }

    public void Detach() {
        if (_session != null) {
            _session.On(Abilities, null);
            _session.PlayerJoined -= _onJoined;
            _session.PlayerLeft -= _onLeft;
            _session = null;
        }

        _remote.Clear();
        if (_active == this) {
            _active = null;
        }
    }

    public static void InstallHooks() {
        if (_hooked) {
            return;
        }

        _hooked = true;
        On.HutongGames.PlayMaker.Actions.PlayerDataBoolTest.OnEnter += (orig, self) => {
            var active = _active;
            if (active?._session == null || PlayerData.instance == null || !IsRoomFsm(self.Fsm)) {
                orig(self);
                return;
            }

            var saved = active.ApplyParty(PlayerData.instance);
            try {
                orig(self);
            } finally {
                Restore(PlayerData.instance, saved);
            }
        };

        On.SceneAdditiveLoadConditional.OnEnable += (orig, self) => {
            var active = _active;
            if (active?._session == null || PlayerData.instance == null) {
                orig(self);
                return;
            }

            // Las del grupo solo mientras el cargador decide (lo hace aquí mismo); luego vuelven las propias
            var saved = active.ApplyParty(PlayerData.instance);
            // La parte de un jefe que solo se carga al entrar por una puerta (la arena de los Caballeros Vigía, desde
            // arriba): con un compañero ya en la sala, se carga aunque este jugador entre por otra. Si no, él no veía a los
            // caballeros mientras los demás peleaban contra ellos
            var door = TransitionPoint.lastEntered;
            var asDoor = !string.IsNullOrEmpty(self.doorTrigger) && door != self.doorTrigger &&
                         active.PartnerInRoom(self.gameObject.scene.name);
            if (asDoor) {
                TransitionPoint.lastEntered = self.doorTrigger;
            }

            try {
                orig(self);
            } finally {
                Restore(PlayerData.instance, saved);
                if (asDoor) {
                    TransitionPoint.lastEntered = door;
                }
            }
        };
    }

    private bool PartnerInRoom(string scene) {
        foreach (var player in _session.Players) {
            if (!player.IsLocal && player.Scene == scene) {
                return true;
            }
        }

        return false;
    }

    private static bool IsRoomFsm(HutongGames.PlayMaker.Fsm fsm) {
        var owner = fsm?.GameObject;
        if (owner == null) {
            return false;
        }

        foreach (var (name, fsmName) in RoomFsms) {
            if (fsm.Name == fsmName && owner.name.StartsWith(name, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Cada frame: envía las propias si cambiaron (o si entró alguien).</summary>
    public void Update() {
        if (_session == null || !_session.CanSend || PlayerData.instance == null) {
            return;
        }

        var now = Time.unscaledTime;
        if (!_dirty && now < _nextCheck) {
            return;
        }

        _nextCheck = now + CheckInterval;
        var mask = Mask(PlayerData.instance);
        if (!_dirty && mask == _sent) {
            return;
        }

        _dirty = false;
        _sent = mask;
        var writer = _session.Begin(Abilities);
        writer.WriteUInt(mask);
        _session.End();
    }

    private void OnAbilities(SessionPlayer from, PacketReader reader) {
        var mask = reader.ReadUInt();
        if (_session != null && from.Id != _session.LocalId) {
            _remote[from.Id] = mask;
        }
    }

    private static uint Mask(PlayerData pd) {
        uint mask = 0;
        for (var i = 0; i < FieldInfos.Length; i++) {
            if (FieldInfos[i]?.GetValue(pd) is true) {
                mask |= 1u << i;
            }
        }

        return mask;
    }

    /// <summary>Pone en PlayerData las que tiene alguien del grupo; devuelve las propias para devolverlas después.</summary>
    private uint ApplyParty(PlayerData pd) {
        var own = Mask(pd);
        var party = own;
        foreach (var mask in _remote.Values) {
            party |= mask;
        }

        if (party != own) {
            for (var i = 0; i < FieldInfos.Length; i++) {
                if ((party & ~own & (1u << i)) != 0) {
                    FieldInfos[i]?.SetValue(pd, true);
                }
            }
        }

        return own;
    }

    private static void Restore(PlayerData pd, uint own) {
        for (var i = 0; i < FieldInfos.Length; i++) {
            if ((own & (1u << i)) == 0 && FieldInfos[i]?.GetValue(pd) is true) {
                FieldInfos[i].SetValue(pd, false);
            }
        }
    }

    /// <summary>Resumen para el modo de prueba.</summary>
    public string Describe() {
        var text = $"propias={(PlayerData.instance != null ? Mask(PlayerData.instance) : 0):x} ";
        foreach (var pair in _remote) {
            text += $"{pair.Key}={pair.Value:x} ";
        }

        return text;
    }
}
