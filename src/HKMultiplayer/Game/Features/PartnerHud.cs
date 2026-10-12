using HKMultiplayer.Game.Players;
using HKMultiplayer.Session;
using HKMultiplayer.UI;
using UnityEngine;
using UnityEngine.UI;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// HUD de los compañeros, con uGUI y el estilo del juego:
/// <list type="bullet">
/// <item>En la esquina superior derecha, alineado a la derecha y uno debajo de otro: su nombre con la letra del juego y
/// debajo sus máscaras (los sprites del HUD del juego, a unos 2/3 del tamaño de las propias, también las azules) con un
/// orbe de alma pequeño a su izquierda; debajo, su estado (caído, o la zona si está en otra).</item>
/// <item>Sus máscaras sobre su cabeza (más o menos la mitad que las del HUD), encima de su nombre, unos segundos cuando le
/// cambia la vida.</item>
/// <item>Flechas en el borde de la pantalla hacia los compañeros de la sala que no se ven, y hacia los cuerpos de los
/// caídos.</item>
/// </list>
/// Todo se crea al principio; cada frame solo se mueve y se enciende o apaga, y los textos se rehacen solo al cambiar.
/// </summary>
internal sealed class PartnerHud {
    private const int MaxPartners = 3;
    private const int MaxMasks = 20;
    private const int MaxArrows = 6;

    /// <summary>Lo que se ven sus máscaras sobre su cabeza tras cambiarle la vida.</summary>
    private const float OverheadSeconds = 3f;

    /// <summary>Alto de las máscaras del mini-HUD (unos 2/3 de las del HUD propio, ~48 a 1080 de alto) y de las de encima.</summary>
    private const float MaskHeight = 58f;

    private const float SmallMaskHeight = 44f;

    /// <summary>Separación con el borde de la pantalla (como el HUD propio) y entre compañeros.</summary>
    private static readonly Vector2 Margin = new(64f, 52f);

    private const float SlotHeight = 118f;

    /// <summary>Zona de las salas de paso (cinemáticas del juego): no se muestra.</summary>
    private const byte NoZone = (byte) GlobalEnums.MapZone.NONE;
    private static readonly Color DimMask = new(0.35f, 0.35f, 0.4f, 0.6f);
    private static readonly Color BlueTint = new(0.45f, 0.75f, 1f, 1f);

    /// <summary>Fila de máscaras ya creada (se encienden las que hacen falta).</summary>
    private sealed class MaskRow {
        public RectTransform Root;
        public readonly Image[] Masks = new Image[MaxMasks];

        /// <summary>Alto de cada máscara; el ancho sale de la proporción del sprite.</summary>
        public float Height;

        /// <summary>Alineadas a la derecha de su raíz (mini-HUD) o centradas en ella (sobre la cabeza).</summary>
        public bool AlignRight;

        /// <summary>Ancho total de la fila dibujada.</summary>
        public float Width;

        // Lo último dibujado
        public int Health = -1;
        public int Max = -1;
        public int Blue = -1;
    }

    private sealed class Slot {
        public ushort Id;
        public RectTransform Panel;
        public Image Orb;
        public Image OrbFill;
        public Text Name;
        public Text Status;
        public MaskRow Masks;
        public MaskRow Overhead;
        public string ShownName;
        public int StatusKey = -1;
        public int Soul = -1;
    }

    private sealed class Arrow {
        public RectTransform Root;
        public RectTransform Pointer;
        public Text Label;
        public string ShownLabel;
        public bool ShownBody;
    }

    private readonly Slot[] _slots = new Slot[MaxPartners];
    private readonly Arrow[] _arrows = new Arrow[MaxArrows];
    private RectTransform _root;
    private bool _visible;
    private GameSession _session;
    private PartyState _party;
    private Vitals _vitals;
    private PlayerSync _players;
    private Revive _revive;

    public void Attach(GameSession session, PartyState party, Vitals vitals, PlayerSync players, Revive revive) {
        _session = session;
        _party = party;
        _vitals = vitals;
        _players = players;
        _revive = revive;
    }

    public void Detach() {
        _session = null;
        SetVisible(false);
    }

    private void Build() {
        _root = HudCanvas.NewGroup("Compañeros");
        _root.anchorMin = Vector2.zero;
        _root.anchorMax = Vector2.one;
        _root.offsetMin = _root.offsetMax = Vector2.zero;
        for (var i = 0; i < MaxPartners; i++) {
            var slot = new Slot();
            slot.Panel = HudCanvas.NewGroup($"Compañero {i + 1}", _root);
            slot.Panel.anchorMin = slot.Panel.anchorMax = Vector2.one;
            slot.Panel.anchoredPosition = new Vector2(-Margin.x, -Margin.y - i * SlotHeight);

            slot.Name = HudCanvas.NewText("Nombre", slot.Panel, 30, TextAnchor.MiddleRight, title: true);
            slot.Name.rectTransform.pivot = new Vector2(1f, 0.5f);
            slot.Name.rectTransform.anchoredPosition = new Vector2(0f, -18f);

            slot.Masks = NewMaskRow(slot.Panel, MaskHeight, alignRight: true);
            slot.Masks.Root.anchoredPosition = new Vector2(0f, -56f);

            slot.Orb = HudCanvas.NewImage("Orbe", slot.Panel, HudCanvas.Circle, new Vector2(40f, 40f),
                new Color(0.05f, 0.05f, 0.08f, 0.8f));
            slot.OrbFill = HudCanvas.NewImage("Alma", slot.Orb.rectTransform, HudCanvas.Circle, new Vector2(34f, 34f),
                new Color(0.92f, 0.92f, 0.95f, 0.95f));
            var fillRect = slot.OrbFill.rectTransform;
            fillRect.anchorMin = fillRect.anchorMax = new Vector2(0.5f, 0.5f);
            fillRect.anchoredPosition = Vector2.zero;
            slot.OrbFill.type = Image.Type.Filled;
            slot.OrbFill.fillMethod = Image.FillMethod.Vertical;
            slot.OrbFill.fillOrigin = (int) Image.OriginVertical.Bottom;
            slot.OrbFill.fillAmount = 0f;

            slot.Status = HudCanvas.NewText("Estado", slot.Panel, 24, TextAnchor.MiddleRight);
            slot.Status.rectTransform.pivot = new Vector2(1f, 0.5f);
            slot.Status.rectTransform.anchoredPosition = new Vector2(0f, -92f);
            slot.Status.color = new Color(1f, 1f, 1f, 0.8f);

            slot.Overhead = NewMaskRow(_root, SmallMaskHeight, alignRight: false);
            slot.Overhead.Root.anchorMin = slot.Overhead.Root.anchorMax = Vector2.zero;
            slot.Overhead.Root.gameObject.SetActive(false);
            slot.Panel.gameObject.SetActive(false);
            _slots[i] = slot;
        }

        for (var i = 0; i < MaxArrows; i++) {
            var arrow = new Arrow { Root = HudCanvas.NewGroup($"Flecha {i + 1}", _root) };
            var pointer = HudCanvas.NewImage("Punta", arrow.Root, HudCanvas.Arrow, new Vector2(36f, 36f),
                new Color(1f, 1f, 1f, 0.9f));
            arrow.Pointer = pointer.rectTransform;
            arrow.Label = HudCanvas.NewText("Nombre", arrow.Root, 24, TextAnchor.MiddleCenter);
            arrow.Root.gameObject.SetActive(false);
            _arrows[i] = arrow;
        }

        _root.gameObject.SetActive(false);
    }

    private static MaskRow NewMaskRow(RectTransform parent, float height, bool alignRight) {
        var row = new MaskRow { Root = HudCanvas.NewGroup("Máscaras", parent), Height = height, AlignRight = alignRight };
        for (var i = 0; i < MaxMasks; i++) {
            var mask = HudCanvas.NewImage($"Máscara {i + 1}", row.Root, HudSprites.FullMask, new Vector2(height, height),
                Color.white);
            mask.preserveAspect = false;
            mask.gameObject.SetActive(false);
            row.Masks[i] = mask;
        }

        return row;
    }

    private void SetVisible(bool visible) {
        if (_visible == visible) {
            return;
        }

        _visible = visible;
        if (_root != null) {
            _root.gameObject.SetActive(visible);
        }
    }

    /// <summary>Llamar en LateUpdate, después de dibujar a los remotos.</summary>
    public void Update(string scene) {
        var gm = GameManager.instance;
        var playing = _session != null && _party != null && scene.Length > 0 && gm != null && !gm.isPaused &&
                      !UI.HudCanvas.GameHudHidden() &&
                      gm.gameState == GlobalEnums.GameState.PLAYING && HeroController.instance != null;
        if (!playing) {
            SetVisible(false);
            return;
        }

        if (_root == null) {
            if (!HudSprites.TryLoad()) {
                return;
            }

            Build();
        }

        SetVisible(true);
        var settings = HostSettings.Current;
        var camera = GameCameras.instance != null ? GameCameras.instance.mainCamera : null;
        var slotIndex = 0;
        foreach (var member in _party.Members) {
            if (member.Id == _session.LocalId || slotIndex >= MaxPartners) {
                continue;
            }

            UpdateSlot(_slots[slotIndex++], member, scene, settings.ShowPartnerHealth, camera);
        }

        for (var i = slotIndex; i < MaxPartners; i++) {
            HideSlot(_slots[i]);
        }

        UpdateArrows(scene, settings.ShowArrows, camera);
    }

    private void HideSlot(Slot slot) {
        if (slot.Panel.gameObject.activeSelf) {
            slot.Panel.gameObject.SetActive(false);
        }

        if (slot.Overhead.Root.gameObject.activeSelf) {
            slot.Overhead.Root.gameObject.SetActive(false);
        }

        slot.Id = 0;
    }

    private void UpdateSlot(Slot slot, PartyState.Member member, string scene, bool show, Camera camera) {
        var vitals = _vitals.Get(member.Id);
        if (!show) {
            HideSlot(slot);
            return;
        }

        if (slot.Id != member.Id) {
            slot.Id = member.Id;
            slot.ShownName = null;
            slot.StatusKey = -1;
            slot.Soul = -1;
            slot.Masks.Health = slot.Overhead.Health = -1;
        }

        if (!slot.Panel.gameObject.activeSelf) {
            slot.Panel.gameObject.SetActive(true);
        }

        if (!ReferenceEquals(slot.ShownName, member.Name)) {
            slot.ShownName = member.Name;
            slot.Name.text = member.Name;
        }

        // Estado: caído, o la zona si está en otra (el texto solo se rehace al cambiar)
        var downed = member.Status == PlayerStatus.Downed;
        var localZone = (int) Vitals.CurrentZone();
        var elsewhere = member.Player?.Scene != scene;
        // En pausa se dice aunque esté en la misma sala: su caballero se queda quieto y parecía que se había trabado
        var statusKey = downed ? 1000
            : member.Paused ? 1004
            : elsewhere && member.Status == PlayerStatus.Cutscene ? 1001
            : elsewhere && member.Status == PlayerStatus.Dream ? 1002
            : elsewhere && member.Status == PlayerStatus.StagTravel ? 1003
            : member.Status == PlayerStatus.Joining ? 1005
            // Sin zona (salas de paso como las cinemáticas del juego): nada, no "en None"
            : vitals != null && vitals.Zone != localZone && elsewhere && vitals.Zone != NoZone ? vitals.Zone : -2;
        if (statusKey != slot.StatusKey) {
            slot.StatusKey = statusKey;
            slot.Status.text = statusKey switch {
                1000 => Lang.T("caído", "down"),
                1001 => Lang.T("viendo una cinemática", "watching a cutscene"),
                1002 => Lang.T("en un sueño", "in a dream"),
                1003 => Lang.T("viajando en ciervo", "riding the stag"),
                1005 => Lang.T("entrando", "joining"),
                1004 => Lang.T("en pausa", "paused"),
                >= 0 => Lang.T($"en {Zones.Name((byte) statusKey)}", $"in {Zones.Name((byte) statusKey)}"),
                _ => string.Empty
            };
        }

        if (vitals == null) {
            SetMasks(slot.Masks, 0, 0, 0);
            PlaceOrb(slot);
            return;
        }

        if (SetMasks(slot.Masks, downed ? 0 : vitals.Health, vitals.MaxHealth, downed ? 0 : vitals.Blue)) {
            PlaceOrb(slot);
        }

        if (vitals.Soul != slot.Soul) {
            slot.Soul = vitals.Soul;
            slot.OrbFill.fillAmount = vitals.MaxSoul > 0 ? Mathf.Clamp01((float) vitals.Soul / vitals.MaxSoul) : 0f;
        }

        // Sobre su cabeza, unos segundos tras cambiarle la vida
        var point = Vector2.zero;
        var overhead = !downed && camera != null && Time.unscaledTime - vitals.HealthChangedAt < OverheadSeconds &&
                       member.Player?.Scene == scene && _players.TryGetAvatarPosition(member.Id, out var position) &&
                       HudCanvas.WorldToCanvas(camera, position + new Vector3(0f, 2.3f, 0f), out point);
        if (overhead != slot.Overhead.Root.gameObject.activeSelf) {
            slot.Overhead.Root.gameObject.SetActive(overhead);
        }

        if (overhead) {
            slot.Overhead.Root.anchoredPosition = point;
            SetMasks(slot.Overhead, vitals.Health, vitals.MaxHealth, vitals.Blue);
        }
    }

    /// <summary>El orbe de alma, a la izquierda de las máscaras.</summary>
    private static void PlaceOrb(Slot slot) {
        slot.Orb.rectTransform.anchoredPosition = new Vector2(-slot.Masks.Width - 30f, -56f);
    }

    /// <summary>Al cambiar los sprites (modo de prueba) se vuelven a poner.</summary>
    public void RefreshSprites() {
        foreach (var slot in _slots) {
            if (slot != null) {
                slot.Masks.Health = slot.Overhead.Health = -1;
            }
        }
    }

    /// <summary>
    /// Máscaras llenas, huecos de las perdidas y azules detrás. Solo cambia lo que cambió; devuelve si cambió (para
    /// recolocar lo que va al lado).
    /// </summary>
    private static bool SetMasks(MaskRow row, int health, int max, int blue) {
        if (row.Health == health && row.Max == max && row.Blue == blue) {
            return false;
        }

        row.Health = health;
        row.Max = max;
        row.Blue = blue;
        var total = Mathf.Min(max + blue, MaxMasks);
        var width = row.Height * HudSprites.FullAspect;
        var step = width + row.Height * 0.12f;
        row.Width = total > 0 ? step * total - (step - width) : 0f;
        for (var i = 0; i < MaxMasks; i++) {
            var mask = row.Masks[i];
            var active = i < total;
            if (mask.gameObject.activeSelf != active) {
                mask.gameObject.SetActive(active);
            }

            if (!active) {
                continue;
            }

            // Desde la izquierda de la fila; centro de cada máscara
            var x = i * step + width / 2f - (row.AlignRight ? row.Width : row.Width / 2f);
            mask.rectTransform.anchoredPosition = new Vector2(x, 0f);
            if (i >= max) {
                // Azules: la del juego si existe; si no, la normal teñida
                HudSprites.Apply(mask, HudSprites.BlueMask != null ? HudSprites.Kind.Blue : HudSprites.Kind.Full, row.Height);
                mask.color = HudSprites.BlueMask != null ? Color.white : BlueTint;
            } else if (i < health) {
                HudSprites.Apply(mask, HudSprites.Kind.Full, row.Height);
                mask.color = Color.white;
            } else {
                HudSprites.Apply(mask, HudSprites.EmptyMask != null ? HudSprites.Kind.Empty : HudSprites.Kind.Full, row.Height);
                mask.color = HudSprites.EmptyMask != null ? Color.white : DimMask;
            }
        }

        return true;
    }

    // ---- Flechas del borde ----

    private void UpdateArrows(string scene, bool show, Camera camera) {
        var used = 0;
        if (show && camera != null) {
            foreach (var member in _party.Members) {
                if (used >= MaxArrows) {
                    break;
                }

                if (member.Id == _session.LocalId || member.Status == PlayerStatus.Downed || member.Player?.Scene != scene ||
                    !_players.TryGetAvatarPosition(member.Id, out var position)) {
                    continue;
                }

                if (PlaceArrow(_arrows[used], camera, position, member.Name)) {
                    used++;
                }
            }

            foreach (var pair in _revive.Bodies) {
                if (used >= MaxArrows) {
                    break;
                }

                if (pair.Key == _session.LocalId) {
                    continue;
                }

                if (PlaceArrow(_arrows[used], camera, pair.Value.Position, _party.Get(pair.Key)?.Name, body: true)) {
                    used++;
                }
            }
        }

        for (var i = used; i < MaxArrows; i++) {
            if (_arrows[i].Root.gameObject.activeSelf) {
                _arrows[i].Root.gameObject.SetActive(false);
            }
        }
    }

    /// <summary>Si el punto está fuera de pantalla, pone la flecha en el borde apuntando hacia él.</summary>
    private static bool PlaceArrow(Arrow arrow, Camera camera, Vector3 world, string name, bool body = false) {
        var behind = !HudCanvas.WorldToCanvas(camera, world, out var point);
        var size = HudCanvas.Size;
        if (!behind && point.x >= 0f && point.x <= size.x && point.y >= 0f && point.y <= size.y) {
            return false;
        }

        var center = size / 2f;
        var direction = point - center;
        if (behind) {
            direction = -direction;
        }

        if (direction.sqrMagnitude < 1f) {
            return false;
        }

        direction.Normalize();
        const float margin = 56f;
        var half = new Vector2(center.x - margin, center.y - margin);
        var scale = Mathf.Min(half.x / Mathf.Max(Mathf.Abs(direction.x), 0.0001f),
            half.y / Mathf.Max(Mathf.Abs(direction.y), 0.0001f));
        var at = center + direction * scale;
        if (!arrow.Root.gameObject.activeSelf) {
            arrow.Root.gameObject.SetActive(true);
        }

        arrow.Root.anchoredPosition = at;
        arrow.Pointer.localEulerAngles = new Vector3(0f, 0f, Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg);
        arrow.Label.rectTransform.anchoredPosition = -direction * 44f;
        var label = name ?? "?";
        if (!ReferenceEquals(arrow.ShownLabel, label) || arrow.ShownBody != body) {
            arrow.ShownLabel = label;
            arrow.ShownBody = body;
            arrow.Label.text = body ? $"† {label}" : label;
            arrow.Label.color = body ? new Color(1f, 0.85f, 0.85f, 1f) : Color.white;
        }

        return true;
    }
}
