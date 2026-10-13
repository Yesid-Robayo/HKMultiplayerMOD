using System;
using System.Collections.Generic;
using System.Reflection;
using HKMultiplayer.Net;
using HKMultiplayer.Session;
using HKMultiplayer.Util;
using UnityEngine;

namespace HKMultiplayer.Game.Features;

/// <summary>
/// Compañeros en el mapa: con el mapa abierto (el del inventario o el rápido) cada compañero sale como una brújula de
/// otro color en el sitio de la sala en el que está, como la propia del Wayward Compass, y como ella con el amuleto:
/// solo se ven si los dos lo llevan equipado (verlos siempre, sin amuleto, era demasiado fácil).
/// Si está caído, su icono queda en rojo en su cuerpo. Lo activa el host (<see cref="HostSettings.ShowPartnerOnMap"/>).
/// <para>Cada jugador envía 4 veces por segundo (no fiable, a todos) en qué sala del mapa está y en qué punto de ella,
/// de 0 a 1, calculado como lo hace el juego para su brújula: así los demás no necesitan las medidas de una sala que no
/// tienen cargada. En los sueños, el Palacio Blanco y los Panteones el mapa no muestra brújula, y tampoco al
/// compañero.</para>
/// </summary>
internal sealed class PartnerMap {
    /// <summary>Dónde está un jugador en el mapa (jugador → todos, no fiable).</summary>
    public const MessageType MapPosition = (MessageType) 103;

    private const float SendInterval = 0.25f;

    /// <summary>Sin noticias de un jugador en este tiempo, su icono se quita.</summary>
    private const float StaleSeconds = 3f;

    private const byte StateHidden = 0;
    private const byte StateStanding = 1;
    private const byte StateDowned = 2;

    /// <summary>
    /// Color de cada compañero en pie, por su número de jugador (el mismo en todos los PCs). Antes todos salían con la
    /// misma brújula azul y sin nombre: con 3 o más no se sabía quién era quién.
    /// </summary>
    private static readonly Color[] StandingColors = {
        new(0.45f, 0.8f, 1f, 1f), new(0.5f, 1f, 0.55f, 1f), new(1f, 0.85f, 0.35f, 1f), new(0.85f, 0.6f, 1f, 1f)
    };

    private static readonly Color DownedColor = new(1f, 0.35f, 0.35f, 1f);

    /// <summary>Tamaño del nombre respecto a los textos del propio mapa (los nombres de las zonas).</summary>
    private const float LabelScale = 0.6f;

    /// <summary>Un texto del mapa que se copia para los nombres (misma fuente, capa y escala que los del juego).</summary>
    private GameObject _textTemplate;

    private static readonly FieldInfo SceneWidthField = PrivateField("sceneWidth");
    private static readonly FieldInfo SceneHeightField = PrivateField("sceneHeight");
    private static readonly FieldInfo OriginOffsetXField = PrivateField("originOffsetX");
    private static readonly FieldInfo OriginOffsetYField = PrivateField("originOffsetY");

    private sealed class Entry {
        public byte State;
        public string Scene = string.Empty;
        public float X;
        public float Y;
        public float ReceivedAt;
        public GameObject Icon;

        /// <summary>El nombre sobre la brújula: su objeto y su TextMeshPro (por reflexión, sin referenciar TMPro).</summary>
        public GameObject Label;

        public Component LabelText;

        public string LabelName;

        /// <summary>Estado con el que se tiñó el icono (se vuelve a teñir solo si cambia).</summary>
        public byte TintedState;
    }

    private readonly Dictionary<ushort, Entry> _entries = new();
    private readonly MessageHandler _onPosition;
    private readonly Action<SessionPlayer> _onLeft;
    private GameSession _session;
    private Revive _revive;
    private float _nextSend;

    /// <summary>Lo último enviado: estando caído se repite (el caballero oculto sigue al compañero, el cuerpo no).</summary>
    private byte _sentState;
    private string _sentScene = string.Empty;
    private float _sentX;
    private float _sentY;

    /// <summary>Mapa al que pertenecen los iconos y sus salas por nombre (cambia al cargar otra partida).</summary>
    private GameMap _map;
    private readonly Dictionary<string, GameObject> _rooms = new();

    static PartnerMap() {
        MessageRoutes.Register(MapPosition, Route.Everyone, reliable: false);
    }

    public PartnerMap() {
        _onPosition = OnPosition;
        _onLeft = player => {
            if (_entries.TryGetValue(player.Id, out var entry)) {
                DestroyIcon(entry);
                _entries.Remove(player.Id);
            }
        };
    }

    public void Attach(GameSession session, Revive revive) {
        _session = session;
        _revive = revive;
        _sentState = StateHidden;
        _sentScene = string.Empty;
        session.On(MapPosition, _onPosition);
        session.PlayerLeft += _onLeft;
    }

    public void Detach() {
        if (_session != null) {
            _session.On(MapPosition, null);
            _session.PlayerLeft -= _onLeft;
            _session = null;
        }

        foreach (var entry in _entries.Values) {
            DestroyIcon(entry);
        }

        _entries.Clear();
        _revive = null;
    }

    /// <summary>Llamar cada frame: envía la posición propia y coloca los iconos si el mapa está abierto.</summary>
    public void Update(string scene) {
        if (_session == null) {
            return;
        }

        var now = Time.unscaledTime;
        if (_session.CanSend && now >= _nextSend) {
            _nextSend = now + SendInterval;
            SendLocal(scene);
        }

        UpdateIcons(now);
    }

    // =====================================================================================================
    // Envío
    // =====================================================================================================

    private void SendLocal(string scene) {
        if (_revive is { IsDowned: true }) {
            // Recién caído y aún en la sala del cuerpo: el icono va al cuerpo, que puede no estar donde cayó (el último
            // sitio seguro). Después se queda ahí aunque el caballero oculto siga al compañero a otras salas
            if (_sentState != StateDowned && _revive.OwnBodyScene == scene &&
                TryGetLocalPosition(scene, _revive.OwnBodyPosition, out var bodyScene, out var bodyX, out var bodyY)) {
                _sentState = StateDowned;
                _sentScene = bodyScene;
                _sentX = bodyX;
                _sentY = bodyY;
            } else if (_sentState == StateStanding) {
                _sentState = StateDowned;
            }
        } else if (TryGetLocalPosition(scene, HeroController.instance != null ? HeroController.instance.transform.position : Vector3.zero,
                       out var mapScene, out var x, out var y)) {
            _sentState = StateStanding;
            _sentScene = mapScene;
            _sentX = x;
            _sentY = y;
        } else {
            _sentState = StateHidden;
        }

        // Sin el Wayward Compass este jugador no sale en el mapa de los demás (y él tampoco los ve): para verse, los dos
        // lo llevan. Lo de arriba se sigue calculando: el sitio del cuerpo de un caído solo se toma al caer
        var state = HasCompass() ? _sentState : StateHidden;
        var writer = _session.Begin(MapPosition);
        writer.WriteByte(state);
        if (state != StateHidden) {
            writer.WriteString(_sentScene);
            writer.WriteFloat(_sentX);
            writer.WriteFloat(_sentY);
        }

        _session.End();
    }

    /// <summary>
    /// Sala del mapa y punto dentro de ella (de 0 a 1) de <paramref name="position"/> en la sala local, como los calcula
    /// el juego para su brújula (<c>GameMap.Update</c>): en las salas que no salen en el mapa (tiendas, casas) cuenta la
    /// puerta por la que se entró.
    /// </summary>
    private static bool TryGetLocalPosition(string scene, Vector3 position, out string mapScene, out float x, out float y) {
        mapScene = null;
        x = y = 0f;
        var gm = GameManager.instance;
        var hero = HeroController.instance;
        if (scene.Length == 0 || gm == null || hero == null || gm.gameMap == null || HiddenZone(gm.GetCurrentMapZone())) {
            return false;
        }

        var map = gm.gameMap.GetComponent<GameMap>();
        if (map == null) {
            return false;
        }

        if (map.inRoom) {
            if (map.doorSceneWidth <= 0f || map.doorSceneHeight <= 0f || string.IsNullOrEmpty(map.doorScene)) {
                return false;
            }

            mapScene = map.doorScene;
            x = (map.doorX + map.doorOriginOffsetX) / map.doorSceneWidth;
            y = (map.doorY + map.doorOriginOffsetY) / map.doorSceneHeight;
            return true;
        }

        var width = ReadFloat(SceneWidthField, map);
        var height = ReadFloat(SceneHeightField, map);
        if (width <= 0f || height <= 0f) {
            if (gm.tilemap == null) {
                return false;
            }

            width = gm.tilemap.width;
            height = gm.tilemap.height;
        }

        if (width <= 0f || height <= 0f) {
            return false;
        }

        mapScene = gm.sceneName;
        x = (position.x + ReadFloat(OriginOffsetXField, map)) / width;
        y = (position.y + ReadFloat(OriginOffsetYField, map)) / height;
        return true;
    }

    /// <summary>Zonas en las que el juego no muestra la brújula.</summary>
    private static bool HiddenZone(string zone) => zone is "DREAM_WORLD" or "WHITE_PALACE" or "GODS_GLORY";

    // =====================================================================================================
    // Recepción e iconos
    // =====================================================================================================

    private void OnPosition(SessionPlayer from, PacketReader reader) {
        if (_session == null || from == null || from.Id == _session.LocalId) {
            return;
        }

        var state = reader.ReadByte();
        if (!_entries.TryGetValue(from.Id, out var entry)) {
            entry = new Entry();
            _entries[from.Id] = entry;
        }

        entry.ReceivedAt = Time.unscaledTime;
        entry.State = state;
        if (state == StateHidden) {
            return;
        }

        entry.Scene = reader.ReadString(128);
        entry.X = Mathf.Clamp(reader.ReadFloat(), -0.5f, 1.5f);
        entry.Y = Mathf.Clamp(reader.ReadFloat(), -0.5f, 1.5f);
    }

    /// <summary>Amuleto del Wayward Compass.</summary>
    private const string CompassCharm = "equippedCharm_2";

    /// <summary>Si este jugador lleva el Wayward Compass: sin él tampoco ve su propia brújula.</summary>
    private static bool HasCompass() => PlayerData.instance != null && PlayerData.instance.GetBool(CompassCharm);

    private void UpdateIcons(float now) {
        var gm = GameManager.instance;
        var map = gm != null && gm.gameMap != null ? gm.gameMap.GetComponent<GameMap>() : null;
        if (map != _map) {
            // Otra partida u otro mapa: los iconos eran hijos del anterior
            foreach (var entry in _entries.Values) {
                DestroyIcon(entry);
            }

            _rooms.Clear();
            _textTemplate = null;
            _map = map;
        }

        var open = map != null && map.compassIcon != null && map.flamePins != null && map.flamePins.activeSelf &&
                   map.gameObject.activeInHierarchy && HostSettings.Current.ShowPartnerOnMap && HasCompass() &&
                   !HiddenZone(gm.GetCurrentMapZone());
        foreach (var pair in _entries) {
            var entry = pair.Value;
            var position = Vector3.zero;
            var visible = open && entry.State != StateHidden && now - entry.ReceivedAt < StaleSeconds &&
                          TryGetIconPosition(map, entry, out position);
            if (!visible) {
                if (entry.Icon != null && entry.Icon.activeSelf) {
                    entry.Icon.SetActive(false);
                }

                continue;
            }

            if (entry.Icon == null) {
                entry.Icon = CreateIcon(map);
                if (entry.Icon == null) {
                    continue;
                }

                CreateLabel(map, entry);
                entry.TintedState = StateHidden;
            }

            entry.Icon.transform.localPosition = position;
            if (entry.TintedState != entry.State) {
                entry.TintedState = entry.State;
                var color = entry.State == StateDowned ? DownedColor : StandingColors[(pair.Key - 1) % StandingColors.Length];
                Tint(entry.Icon, color);
                SetLabel(entry, "color", color);
            }

            if (entry.LabelText != null && _session.TryGetPlayer(pair.Key, out var player) && entry.LabelName != player.Name) {
                entry.LabelName = player.Name;
                SetLabel(entry, "text", player.Name);
            }

            if (!entry.Icon.activeSelf) {
                entry.Icon.SetActive(true);
            }
        }
    }

    /// <summary>Donde iría la brújula en esa sala y ese punto (la misma cuenta que <c>GameMap.Update</c>).</summary>
    private bool TryGetIconPosition(GameMap map, Entry entry, out Vector3 position) {
        position = default;
        // Solo si la sala se ve en el mapa abierto: el mapa rápido apaga las demás zonas y las salas sin mapear están
        // apagadas (el icono salía flotando sobre negro)
        var room = FindRoom(map, entry.Scene);
        if (room == null || room.transform.parent == null || !room.activeInHierarchy) {
            return false;
        }

        var renderer = room.GetComponent<SpriteRenderer>();
        if (renderer == null || renderer.sprite == null) {
            return false;
        }

        var size = renderer.sprite.bounds.size;
        var roomPosition = room.transform.localPosition + room.transform.parent.localPosition;
        position = new Vector3(roomPosition.x - size.x / 2f + entry.X * size.x, roomPosition.y - size.y / 2f + entry.Y * size.y,
            -1.1f);
        return true;
    }

    /// <summary>La sala del mapa con ese nombre (hija de una de las zonas), o null si no sale en el mapa.</summary>
    private GameObject FindRoom(GameMap map, string scene) {
        if (string.IsNullOrEmpty(scene)) {
            return null;
        }

        if (_rooms.TryGetValue(scene, out var cached)) {
            return cached;
        }

        GameObject found = null;
        foreach (var area in Areas(map)) {
            if (area == null) {
                continue;
            }

            for (var i = 0; i < area.transform.childCount && found == null; i++) {
                var child = area.transform.GetChild(i).gameObject;
                if (child.name == scene) {
                    found = child;
                }
            }

            if (found != null) {
                break;
            }
        }

        _rooms[scene] = found;
        return found;
    }

    private static GameObject[] Areas(GameMap map) => new[] {
        map.areaAncientBasin, map.areaCity, map.areaCliffs, map.areaCrossroads, map.areaCrystalPeak, map.areaDeepnest,
        map.areaFogCanyon, map.areaFungalWastes, map.areaGreenpath, map.areaKingdomsEdge, map.areaQueensGardens,
        map.areaRestingGrounds, map.areaDirtmouth, map.areaWaterways
    };

    /// <summary>Una copia de la brújula del juego, sin su lógica, junto a ella en el mapa.</summary>
    private static GameObject CreateIcon(GameMap map) {
        try {
            var icon = UnityEngine.Object.Instantiate(map.compassIcon, map.compassIcon.transform.parent);
            icon.name = "HKMultiplayer Partner Compass";
            foreach (var fsm in icon.GetComponentsInChildren<PlayMakerFSM>(true)) {
                UnityEngine.Object.Destroy(fsm);
            }

            icon.SetActive(false);
            return icon;
        } catch (Exception e) {
            Log.Warn($"No se pudo crear el icono del compañero en el mapa: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// El nombre del compañero sobre su brújula: una copia de un texto del propio mapa (el nombre de una zona), con su
    /// fuente, su capa y su tamaño. Un TextMesh con la fuente de Unity no se veía sobre el mapa.
    /// </summary>
    private void CreateLabel(GameMap map, Entry entry) {
        try {
            if (_textTemplate == null) {
                foreach (var component in map.GetComponentsInChildren<Component>(true)) {
                    if (component != null && component.GetType().Name == "TextMeshPro") {
                        _textTemplate = component.gameObject;
                        break;
                    }
                }
            }

            if (_textTemplate == null) {
                return;
            }

            var label = UnityEngine.Object.Instantiate(_textTemplate, _textTemplate.transform.parent);
            label.name = "HKMultiplayer Partner Name";
            // Fuera lo que no es el texto: el componente de idioma le volvería a poner el nombre de la zona
            Component text = null;
            foreach (var component in label.GetComponentsInChildren<Component>(true)) {
                var type = component.GetType().Name;
                if (type == "TextMeshPro" && component.gameObject == label) {
                    text = component;
                } else if (component is Behaviour && type is not ("TextMeshPro" or "TMP_SubMesh")) {
                    UnityEngine.Object.Destroy(component);
                }
            }

            for (var i = label.transform.childCount - 1; i >= 0; i--) {
                var child = label.transform.GetChild(i).gameObject;
                if (child.GetComponent("TMP_SubMesh") == null) {
                    UnityEngine.Object.Destroy(child);
                }
            }

            // El mismo tamaño en pantalla que los textos del mapa (algo menor), en el sitio de la brújula: el texto del
            // mapa se dibuja por encima de su punto, así que queda justo sobre ella
            label.transform.SetParent(entry.Icon.transform, true);
            label.transform.localScale *= LabelScale;
            var iconPosition = entry.Icon.transform.position;
            label.transform.position = new Vector3(iconPosition.x, iconPosition.y, label.transform.position.z);
            label.SetActive(true);
            entry.Label = label;
            entry.LabelText = text;
            entry.LabelName = null;
        } catch (Exception e) {
            Log.Warn($"No se pudo crear el nombre del compañero en el mapa: {e.Message}");
        }
    }

    private static void SetLabel(Entry entry, string property, object value) {
        if (entry.LabelText == null) {
            return;
        }

        try {
            entry.LabelText.GetType().GetProperty(property)?.SetValue(entry.LabelText, value, null);
        } catch (Exception e) {
            Log.Warn($"No se pudo cambiar el nombre del compañero en el mapa ({property}): {e.Message}");
            entry.LabelText = null;
        }
    }

    private static void Tint(GameObject icon, Color color) {
        foreach (var renderer in icon.GetComponentsInChildren<SpriteRenderer>(true)) {
            renderer.color = color;
        }

        foreach (var sprite in icon.GetComponentsInChildren<tk2dBaseSprite>(true)) {
            sprite.color = color;
        }
    }

    private static void DestroyIcon(Entry entry) {
        if (entry.Icon != null) {
            UnityEngine.Object.Destroy(entry.Icon);
        }

        entry.Icon = null;
        entry.Label = null;
        entry.LabelText = null;
    }

    private static FieldInfo PrivateField(string name) =>
        typeof(GameMap).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);

    private static float ReadFloat(FieldInfo field, GameMap map) => field != null ? (float) field.GetValue(map) : 0f;

    /// <summary>Resumen para el modo de prueba.</summary>
    public string Describe() {
        var text = new System.Text.StringBuilder();
        text.Append($"enviado={_sentState}:{_sentScene}({_sentX:0.00},{_sentY:0.00}) ");
        var gm = GameManager.instance;
        var map = gm != null && gm.gameMap != null ? gm.gameMap.GetComponent<GameMap>() : null;
        text.Append($"mapa={(map == null ? "no" : $"pins={map.flamePins != null && map.flamePins.activeSelf} " +
                                                      $"activo={map.gameObject.activeInHierarchy}")} brújula={HasCompass()} ");
        foreach (var pair in _entries) {
            var entry = pair.Value;
            var room = map != null ? FindRoom(map, entry.Scene) : null;
            var sprite = room != null ? room.GetComponent<SpriteRenderer>() : null;
            text.Append($"{pair.Key}={entry.State}:{entry.Scene}({entry.X:0.00},{entry.Y:0.00}) icono=" +
                        $"{(entry.Icon != null && entry.Icon.activeSelf ? "visible" : "oculto")} " +
                        $"sala={(room == null ? "no" : $"activa={room.activeInHierarchy} sprite={sprite != null && sprite.sprite != null}")} " +
                        $"hace={Time.unscaledTime - entry.ReceivedAt:0.0}s nombre=" +
                        $"{(entry.Label == null ? "no" : $"{entry.LabelName}@{(Vector2) entry.Label.transform.position}")} " +
                        $"brújula@{(entry.Icon != null ? (Vector2) entry.Icon.transform.position : Vector2.zero)} ");
        }

        return text.ToString();
    }
}
