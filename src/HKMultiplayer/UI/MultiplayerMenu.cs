using System;
using System.Collections;
using System.Linq;
using GlobalEnums;
using HKMultiplayer.Game;
using HKMultiplayer.Game.Features;
using HKMultiplayer.Game.World;
using HKMultiplayer.Net.Crypto;
using HKMultiplayer.Util;
using UnityEngine;
using UnityEngine.UI;

namespace HKMultiplayer.UI;

/// <summary>
/// Menú "Multijugador" del menú principal: botón bajo "Iniciar partida" (copia del botón del juego) y pantalla con
/// páginas: inicio (nombre y puerto), hostear (hasta 4 mundos coop, borrar, nuevo), nuevo mundo y unirse.
/// </summary>
internal sealed class MultiplayerMenu : GameStyledMenu {
    public static MultiplayerMenu Instance { get; private set; }

    private const int StartPage = 0;
    private const int HostPage = 1;
    private const int NewWorldPage = 2;
    private const int JoinPage = 3;
    private const int DeletePage = 4;

    private InputField _name;
    private InputField _port;
    private InputField _newWorldName;
    private InputField _address;
    private InputField _code;
    private Text _joinStatus;
    private GameObject _steamList;
    private MenuOptionHorizontal _steamAccess;
    private Text _steamTitle;
    private GameObject _recentList;
    private Text _recentTitle;
    private Text _hostStatus;
    private GameObject _worldList;
    private MenuButton _newWorldButton;
    private Text _limitText;
    private Text _deleteQuestion;
    private CoopWorldInfo _pendingDelete;
    private Text _updateText;
    private MenuButton _updateButton;
    private MenuOptionHorizontal _reviveVolume;
    private string _updateUrl;

    private static readonly string[] YesNo = { "Sí", "No" };
    private static readonly string[] YesNoEnglish = { "Yes", "No" };

    private static MenuButton _mainMenuButton;

    /// <summary>Añade el botón y la pantalla si el menú principal aún no los tiene. Barato: llamar cada frame.</summary>
    public static void EnsureInstalled() {
        if (_mainMenuButton != null) {
            return;
        }

        var ui = UIManager.instance;
        if (ui == null || ui.mainMenuScreen == null || ui.extrasMenuScreen == null) {
            return;
        }

        try {
            Build(ui);
        } catch (Exception e) {
            Log.Error($"No se pudo crear el menú Multijugador: {e}");
            _mainMenuButton = ui.mainMenuButtons.startButton;
        }
    }

    private static void Build(UIManager ui) {
        var startButton = ui.mainMenuButtons.startButton;
        var buttonObject = Instantiate(startButton.gameObject, startButton.transform.parent);
        buttonObject.name = "MultiplayerButton";
        buttonObject.transform.SetSiblingIndex(startButton.transform.GetSiblingIndex() + 1);
        var button = buttonObject.GetComponent<MenuButton>();
        MakeCustom(button, Lang.T("Multijugador", "Multiplayer"), () => Instance?.Open());
        var buttonList = startButton.transform.parent.GetComponent<MenuButtonList>();
        if (buttonList != null) {
            buttonList.AddSelectable(button, 1);
            buttonList.RecalculateNavigation();
        }

        var listRect = (RectTransform) startButton.transform.parent;
        listRect.sizeDelta += new Vector2(0f, ((RectTransform) startButton.transform).sizeDelta.y + 40f);

        Instance = CreateScreen<MultiplayerMenu>(ui, "MultiplayerMenuScreen", Lang.T("Multijugador", "Multiplayer"));
        Instance.BuildPages();
        _mainMenuButton = button;
        Log.Info("Menú Multijugador añadido al menú principal");
    }

    private void BuildPages() {
        var settings = HKMultiplayerMod.Settings;

        var start = NewPage(StartPage);
        AddText(start, Lang.T("Juega con amigos en tu red o por internet.", "Play with friends on your network or over the internet."), 40);
        AddSpace(start, 20);
        _name = AddInput(start, Lang.T("Tu nombre", "Your name"), settings.PlayerName, 24, InputField.ContentType.Standard);
        _port = AddInput(start, Lang.T("Puerto", "Port"), settings.Port.ToString(), 5, InputField.ContentType.IntegerNumber);
        AddSpace(start, 30);
        AddButton(start, Lang.T("Hostear un mundo", "Host a world"), () => ShowPage(HostPage));
        AddButton(start, Lang.T("Unirse a un mundo", "Join a world"), () => ShowPage(JoinPage));
        AddSpace(start, 16);
        _updateText = AddText(start, string.Empty, 28);
        _updateButton = AddButton(start, string.Empty, OpenUpdatePage, 34);
        AddOption(start, Lang.T("Buscar actualizaciones", "Check for updates"), Lang.Labels(YesNo, YesNoEnglish),
            settings.CheckForUpdates ? 0 : 1, index => {
                HKMultiplayerMod.Settings.CheckForUpdates = index == 0;
                HKMultiplayerMod.SaveSettings();
                if (index == 0) {
                    UpdateCheck.CheckAgain(UIManager.instance);
                }

                RefreshUpdate();
            });
        UpdateCheck.Changed += RefreshUpdate;
        RefreshUpdate();
        // De este PC: también se cambia en la pausa, en «Skin y sonido»
        _reviveVolume = AddOption(start, Lang.T("Volumen de revivir", "Revive volume"), ReviveAudio.VolumeLabels(),
            ReviveAudio.VolumeIndex(), index => SetOptionValue(_reviveVolume, ReviveAudio.SetVolume(index)));
        // Quién entra por Steam en las partidas que hosteas (sin Steam, la opción no sale: ver OnPageShowing)
        _steamAccess = AddOption(start, Lang.T("Partidas por Steam", "Steam games"),
            Lang.Labels(new[] { "Amigos", "Solo invitados", "Nadie" }, new[] { "Friends", "Invited only", "Nobody" }),
            Mathf.Clamp(settings.SteamAccess, 0, 2), index => {
                HKMultiplayerMod.Settings.SteamAccess = index;
                HKMultiplayerMod.SaveSettings();
            });

        var host = NewPage(HostPage);
        AddText(host, Lang.F("Elige un mundo coop (hasta {0}). Tus partidas normales no se tocan.", "Pick a coop world (up to {0}). Your normal saves are not touched.", WorldStore.MaxWorlds), 36);
        AddSpace(host, 10);
        _worldList = new GameObject("Worlds", typeof(RectTransform), typeof(VerticalLayoutGroup));
        _worldList.transform.SetParent(host.transform, false);
        var listLayout = _worldList.GetComponent<VerticalLayoutGroup>();
        listLayout.childControlHeight = true;
        listLayout.childControlWidth = true;
        listLayout.childForceExpandHeight = false;
        AddSpace(host, 10);
        _newWorldButton = AddButton(host, Lang.T("Nuevo mundo", "New world"), () => ShowPage(NewWorldPage));
        _limitText = AddText(host, Lang.F("Ya tienes {0} mundos: borra uno para crear otro.", "You already have {0} worlds: delete one to create another.", WorldStore.MaxWorlds), 34);
        _hostStatus = AddText(host, string.Empty, 34);
        AddSpace(host, 10);
        // Dónde están los mundos: para hacer copias de seguridad o pasarlos a otro PC
        var where = AddText(host, Lang.F("Los mundos se guardan en este PC, en:\n{0}", "Worlds are saved on this PC, in:\n{0}", WorldStore.Root.Replace('/', '\\')), 30);
        where.GetComponent<LayoutElement>().preferredHeight = 30 * 3.4f;
        AddButton(host, Lang.T("Abrir carpeta de los mundos", "Open worlds folder"), OpenWorldsFolder, fontSize: 34);

        var newWorld = NewPage(NewWorldPage);
        AddText(newWorld, Lang.T("Un mundo coop empieza como una partida nueva y lo guarda tu PC.", "A coop world starts like a new game and is saved on your PC."), 38);
        AddSpace(newWorld, 20);
        _newWorldName = AddInput(newWorld, Lang.T("Nombre del mundo", "World name"), string.Empty, 40, InputField.ContentType.Standard);
        AddSpace(newWorld, 10);
        AddOption(newWorld, Lang.T("Modo", "Mode"), ModeNames(), 0, index => {
            _newWorldMode = (CoopWorldMode) index;
            _modeHelp.text = ModeHelp(_newWorldMode);
        });
        _modeHelp = AddText(newWorld, ModeHelp(CoopWorldMode.Classic), 30);
        AddSpace(newWorld, 20);
        AddButton(newWorld, Lang.T("Desde una partida", "From a save file"), () => {
            ShowPage(FromSavePage);
            RefreshSaves();
        });
        AddButton(newWorld, Lang.T("Crear y hostear", "Create and host"), () => {
            var world = WorldStore.Create(_newWorldName.text, _newWorldMode);
            if (world == null) {
                ShowPage(HostPage);
                return;
            }

            _newWorldName.text = string.Empty;
            UIManager.instance.StartCoroutine(HostRoutine(world));
        });

        BuildFromSavePage();

        var join = NewPage(JoinPage);
        // Amigos de Steam con una partida abierta: un botón para cada uno (se rellena al abrir la página)
        _steamTitle = AddText(join, Lang.T("Amigos de Steam", "Steam friends"), 40, title: true);
        _steamList = new GameObject("SteamFriends", typeof(RectTransform), typeof(VerticalLayoutGroup));
        _steamList.transform.SetParent(join.transform, false);
        var steamLayout = _steamList.GetComponent<VerticalLayoutGroup>();
        steamLayout.childControlHeight = true;
        steamLayout.childControlWidth = true;
        steamLayout.childForceExpandHeight = false;
        AddSpace(join, 16);
        // Partidas por IP en las que ya entró: un botón para volver sin escribir dirección ni código (issue #12)
        _recentTitle = AddText(join, Lang.T("Partidas recientes", "Recent games"), 40, title: true);
        _recentList = new GameObject("RecentGames", typeof(RectTransform), typeof(VerticalLayoutGroup));
        _recentList.transform.SetParent(join.transform, false);
        var recentLayout = _recentList.GetComponent<VerticalLayoutGroup>();
        recentLayout.childControlHeight = true;
        recentLayout.childControlWidth = true;
        recentLayout.childForceExpandHeight = false;
        AddSpace(join, 16);
        AddText(join, Lang.T("Por IP: pide al host su dirección y el código de la partida.", "By IP: ask the host for their address and the game code."), 38);
        AddSpace(join, 20);
        // Con mando no se puede escribir en los campos (sin el teclado de Steam): lo que el host mandó por el chat se pega.
        // Encima de los campos, para que sea lo primero que se resalta con el mando al abrir la página
        AddButton(join, Lang.T("Pegar dirección y código", "Paste address and code"), PasteInvite);
        _address = AddInput(join, Lang.T("Dirección del host", "Host address"), settings.LastAddress, 64, InputField.ContentType.Standard);
        _code = AddInput(join, Lang.T("Código", "Code"), string.Empty, 9, InputField.ContentType.Custom);
        _code.onValidateInput = (_, _, c) => c == '-' || char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '\0';
        AddSpace(join, 30);
        AddButton(join, Lang.T("Unirse", "Join"), Join);
        _joinStatus = AddText(join, string.Empty, 34);
        AddText(join, Lang.T("Tu personaje se guarda en el mundo del host, en su PC (al volver a unirte lo recuperas).", "Your character is saved in the host's world, on their PC (you get it back when you join again)."), 28);

        var delete = NewPage(DeletePage);
        _deleteQuestion = AddText(delete, string.Empty, 44, title: true);
        AddText(delete, Lang.T("Se borra de este PC con todo su progreso y no se puede deshacer.", "It is deleted from this PC with all its progress and cannot be undone."), 36);
        AddSpace(delete, 30);
        AddButton(delete, Lang.T("Cancelar", "Cancel"), () => ShowPage(HostPage));
        AddButton(delete, Lang.T("Borrar para siempre", "Delete forever"), () => {
            if (_pendingDelete != null) {
                WorldStore.Delete(_pendingDelete.Id);
                _pendingDelete = null;
            }

            ShowPage(HostPage);
        });

        ShowPage(StartPage, instant: true);
    }

    // =====================================================================================================
    // Versiones
    // =====================================================================================================

    /// <summary>
    /// Lo que se sabe de las versiones: una nueva para descargar (todos tienen que llevar la misma para jugar juntos),
    /// las novedades de la recién instalada, o que es la más nueva.
    /// </summary>
    private void RefreshUpdate() {
        if (_updateText == null) {
            return;
        }

        var current = HKMultiplayerMod.Version;
        string text;
        string button = null;
        _updateUrl = null;
        if (UpdateCheck.NewerAvailable) {
            text = Lang.F("Hay una versión nueva: la {0} (tienes la {1}). Para jugar juntos, todos necesitan la misma.", "A new version is out: {0} (you have {1}). To play together, everyone needs the same one.", UpdateCheck.Latest, current);
            button = Lang.F("Descargar la {0}", "Download {0}", UpdateCheck.Latest);
            _updateUrl = UpdateCheck.LatestUrl;
        } else if (UpdateCheck.JustUpdated) {
            text = Lang.F("Actualizado a la {0}.", "Updated to {0}.", current);
            button = Lang.F("Novedades de la {0}", "What's new in {0}", current);
            _updateUrl = UpdateCheck.CurrentNotesUrl;
        } else if (UpdateCheck.Latest != null) {
            text = Lang.F("Tienes la versión más nueva ({0}).", "You have the newest version ({0}).", current);
        } else {
            text = Lang.F("Versión {0}", "Version {0}", current);
        }

        _updateText.text = text;
        if (button != null) {
            SetButtonText(_updateButton, button);
        }

        if (_updateButton.gameObject.activeSelf != (button != null)) {
            _updateButton.gameObject.SetActive(button != null);
        }
    }

    private void OpenUpdatePage() {
        if (!string.IsNullOrEmpty(_updateUrl)) {
            Log.Info($"Abriendo {_updateUrl}");
            Application.OpenURL(_updateUrl);
        }
    }

    // =====================================================================================================
    // Navegación
    // =====================================================================================================

    private void Open() {
        if (Busy) {
            return;
        }

        ShowPage(StartPage, instant: true);
        UIManager.instance.StartCoroutine(OpenRoutine());
    }

    /// <summary>Igual que el juego al ir a "Extras": se apaga el título y el menú principal y entra esta pantalla.</summary>
    private IEnumerator OpenRoutine() {
        Busy = true;
        var ui = UIManager.instance;
        var input = GameManager.instance.inputHandler;
        input.StopUIInput();
        ui.StartCoroutine(FadeSprite(ui.gameTitle, 0f));
        ui.subtitleFSM.SendEvent("FADE OUT");
        yield return ui.StartCoroutine(ui.FadeOutCanvasGroup(ui.mainMenuScreen));
        ui.currentDynamicMenu = Screen;
        SetMenuState?.Invoke(ui, new object[] { MainMenuState.DYNAMIC_MENU });
        yield return ui.StartCoroutine(ui.ShowMenu(Screen));
        input.StartUIInput();
        Busy = false;
        SelectFirst();
    }

    protected override void OnBack() {
        if (Busy) {
            return;
        }

        switch (CurrentPage) {
            case StartPage:
                SaveFields();
                UIManager.instance.StartCoroutine(LeaveRoutine());
                break;
            case NewWorldPage:
            case DeletePage:
                ShowPage(HostPage);
                break;
            case FromSavePage:
                ShowPage(NewWorldPage);
                break;
            default:
                ShowPage(StartPage);
                break;
        }
    }

    private IEnumerator LeaveRoutine() {
        Busy = true;
        var ui = UIManager.instance;
        yield return ui.StartCoroutine(ui.HideMenu(Screen));
        ui.UIGoToMainMenu();
        Busy = false;
    }

    protected override void OnPageShowing(int page) {
        if (page == StartPage) {
            _steamAccess.gameObject.SetActive(Steam.SteamLink.Available);
            SetOptionValue(_reviveVolume, ReviveAudio.VolumeIndex());
        } else if (page == HostPage) {
            RefreshWorlds();
        } else if (page == JoinPage) {
            RefreshRecentGames();
            RefreshSteamFriends();
        }
    }

    /// <summary>Amigos de Steam que tienen una partida de HKMultiplayer abierta (sin Steam, la sección no sale).</summary>
    private void RefreshSteamFriends() {
        foreach (Transform child in _steamList.transform) {
            child.gameObject.SetActive(false);
            Destroy(child.gameObject);
        }

        var steam = Steam.SteamLink.Platform;
        _steamTitle.gameObject.SetActive(steam != null);
        _steamList.SetActive(steam != null);
        if (steam == null) {
            return;
        }

        System.Collections.Generic.List<Steam.SteamFriendGame> games;
        try {
            games = steam.FriendGames();
        } catch (Exception e) {
            Log.Warn($"No se pudo leer la lista de amigos de Steam: {e.Message}");
            games = new System.Collections.Generic.List<Steam.SteamFriendGame>();
        }

        foreach (var game in games.Take(4)) {
            var lobby = game.Lobby;
            AddButton(_steamList, Lang.F("Unirse a {0}", "Join {0}", game.Name), () => JoinSteam(lobby), 40);
        }

        if (games.Count == 0) {
            AddText(_steamList, Lang.T("Ningún amigo tiene una partida abierta. También pueden invitarte desde Steam.",
                "No friend has a game open. They can also invite you from Steam."), 30);
        }

        SetFirst(JoinPage, null);
    }

    /// <summary>
    /// «Partidas recientes»: un botón por cada una (la más reciente arriba). Si el campo del código está vacío y la dirección
    /// es la de la más reciente, se rellena con su código: el host abre ese mundo siempre con el mismo.
    /// </summary>
    private void RefreshRecentGames() {
        foreach (Transform child in _recentList.transform) {
            child.gameObject.SetActive(false);
            Destroy(child.gameObject);
        }

        var recent = (HKMultiplayerMod.Settings.RecentGames ?? new System.Collections.Generic.List<Settings.RecentGame>())
            .Where(g => g != null && !string.IsNullOrEmpty(g.Address) && RoomCode.IsValid(RoomCode.Normalize(g.RoomCode)))
            .Take(Settings.GlobalSettings.MaxRecentGames).ToList();
        _recentTitle.gameObject.SetActive(recent.Count > 0);
        _recentList.SetActive(recent.Count > 0);
        foreach (var game in recent) {
            var target = game;
            var who = string.IsNullOrEmpty(game.HostName) ? game.Address : $"{game.HostName} ({game.Address})";
            AddButton(_recentList, Lang.F("Volver con {0}", "Rejoin {0}", who), () => JoinRecent(target), 40);
        }

        if (recent.Count > 0 && string.IsNullOrEmpty(_code.text) && _address.text == recent[0].Address) {
            _code.text = MultiplayerRunner.FormatCode(RoomCode.Normalize(recent[0].RoomCode));
        }
    }

    private void JoinRecent(Settings.RecentGame game) {
        _address.text = game.Address;
        _port.text = game.Port.ToString();
        _code.text = MultiplayerRunner.FormatCode(RoomCode.Normalize(game.RoomCode));
        Join();
    }

    private void JoinSteam(ulong lobby) {
        SaveFields();
        if (!CoopSaves.CanStart) {
            _joinStatus.text = Lang.T("Únete desde el menú principal", "Join from the main menu");
            return;
        }

        MultiplayerRunner.Instance.JoinSteamLobby(lobby, _name.text);
    }

    private const int FromSavePage = 5;

    /// <summary>Lista de las ranuras del juego en la página «Desde una partida» (se rellena al abrirla).</summary>
    private GameObject _saveList;

    private Text _saveStatus;

    private void BuildFromSavePage() {
        var page = NewPage(FromSavePage);
        AddText(page, Lang.T("Se copia la partida elegida: la original no cambia y lo que juegues en coop se queda en el mundo.",
            "The chosen save is copied: the original doesn't change and what you play in co-op stays in the world."), 34);
        AddSpace(page, 10);
        _saveList = new GameObject("Saves", typeof(RectTransform), typeof(VerticalLayoutGroup));
        _saveList.transform.SetParent(page.transform, false);
        var layout = _saveList.GetComponent<VerticalLayoutGroup>();
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        _saveStatus = AddText(page, string.Empty, 32);
    }

    /// <summary>Las 4 ranuras del juego, cada una con su botón (las vacías o perdidas en Alma de Acero no se ofrecen).</summary>
    private void RefreshSaves() {
        foreach (Transform child in _saveList.transform) {
            child.gameObject.SetActive(false);
            Destroy(child.gameObject);
        }

        _saveStatus.text = Lang.T("Leyendo las partidas…", "Reading the save files…");
        var pending = 4;
        for (var slot = 1; slot <= 4; slot++) {
            var number = slot;
            WorldStore.ReadSlot(slot, data => {
                AddSaveButton(number, data);
                if (--pending == 0) {
                    _saveStatus.text = _saveList.transform.childCount == 0
                        ? Lang.T("No hay partidas que copiar.", "There are no save files to copy.")
                        : string.Empty;
                    SetFirst(FromSavePage, null);
                }
            });
        }
    }

    private void AddSaveButton(int slot, SaveGameData data) {
        var pd = data?.playerData;
        if (pd == null || pd.permadeathMode == 2) {
            return;
        }

        var mode = pd.bossRushMode ? Lang.T("  ·  Buscador de Dioses", "  ·  Godseeker")
            : pd.permadeathMode == 1 ? Lang.T("  ·  Alma de Acero", "  ·  Steel Soul")
            : string.Empty;
        var label = Lang.F("Partida {0}", "Save {0}", slot) +
                    $"  ·  {FormatTime(pd.playTime)}  ·  {AreaName(pd.mapZone.ToString())}{mode}";
        var button = AddButton(_saveList, label, () => {
            var name = _newWorldName.text.Trim().Length > 0 ? _newWorldName.text : Lang.F("Partida {0}", "Save {0}", slot);
            var world = WorldStore.CreateFromSave(name, data);
            if (world == null) {
                ShowPage(HostPage);
                return;
            }

            _newWorldName.text = string.Empty;
            UIManager.instance.StartCoroutine(HostRoutine(world));
        }, 38, 1080);
        button.transform.SetSiblingIndex(Mathf.Min(slot - 1, _saveList.transform.childCount - 1));
    }

    /// <summary>Modo elegido para el mundo nuevo y su explicación bajo el selector.</summary>
    private CoopWorldMode _newWorldMode;

    private Text _modeHelp;

    private static string[] ModeNames() => new[] {
        Lang.T("Clásico", "Classic"), Lang.T("Alma de Acero", "Steel Soul"), Lang.T("Buscador de Dioses", "Godseeker")
    };

    private static string ModeHelp(CoopWorldMode mode) => mode switch {
        CoopWorldMode.SteelSoul => Lang.T("Quien cae se puede levantar; si caen todos a la vez, el mundo se pierde.",
            "Downed players can be revived; if everyone goes down at once, the world is lost."),
        CoopWorldMode.Godseeker => Lang.T("Solo Dios Hogar, con todo el equipo desde el principio.",
            "Only Godhome, with all the gear from the start."),
        _ => Lang.T("La partida normal.", "The normal game.")
    };

    /// <summary>Lo que se ve del modo en la lista de mundos.</summary>
    private static string ModeLabel(CoopWorldInfo world) => world.Lost ? Lang.T("  ·  perdido", "  ·  lost")
        : world.Mode switch {
            CoopWorldMode.SteelSoul => Lang.T("  ·  Alma de Acero", "  ·  Steel Soul"),
            CoopWorldMode.Godseeker => Lang.T("  ·  Buscador de Dioses", "  ·  Godseeker"),
            _ => string.Empty
        };

    private void RefreshWorlds() {
        foreach (Transform child in _worldList.transform) {
            child.gameObject.SetActive(false);
            Destroy(child.gameObject);
        }

        var worlds = WorldStore.List();
        foreach (var world in worlds) {
            var label = (world.IsNew
                ? Lang.F("{0}  ·  nuevo", "{0}  ·  new", world.Name)
                : $"{world.Name}  ·  {FormatTime(world.PlayTime)}  ·  {AreaName(world.Area)}") + ModeLabel(world);
            var target = world;
            var row = AddRow(_worldList, world.Name, 84, 10);
            var host = AddButton(row, label, () => UIManager.instance.StartCoroutine(HostRoutine(target)), 40, 1080);
            // Un mundo de Alma de Acero perdido ya no se juega: solo se puede borrar
            host.interactable = !world.Lost;
            AddButton(row, Lang.T("Borrar", "Delete"), () => {
                _pendingDelete = target;
                _deleteQuestion.text = Lang.F("¿Borrar el mundo «{0}»?", "Delete the world \"{0}\"?", target.Name);
                ShowPage(DeletePage);
            }, 32, 240);
        }

        if (worlds.Count == 0) {
            AddText(_worldList, Lang.T("Aún no tienes mundos coop.", "You don't have any coop worlds yet."), 36);
        }

        var full = worlds.Count >= WorldStore.MaxWorlds;
        _newWorldButton.gameObject.SetActive(!full);
        _limitText.gameObject.SetActive(full);
        SetFirst(HostPage, null);
    }

    // =====================================================================================================
    // Acciones
    // =====================================================================================================

    private bool TryPort(out int port) => int.TryParse(_port.text, out port) && port is > 0 and < 65536;

    private IEnumerator HostRoutine(CoopWorldInfo world) {
        if (Busy) {
            yield break;
        }

        SaveFields();
        if (!TryPort(out var port)) {
            _hostStatus.text = Lang.T("El puerto no es válido", "The port is not valid");
            yield break;
        }

        if (!CoopSaves.CanStart) {
            _hostStatus.text = Lang.T("Vuelve al menú principal para abrir un mundo coop", "Go back to the main menu to open a coop world");
            yield break;
        }

        Busy = true;
        var ui = UIManager.instance;
        yield return ui.StartCoroutine(ui.HideMenu(Screen));
        Busy = false;
        var runner = MultiplayerRunner.Instance;
        if (!runner.HostWorld(world, _name.text, port)) {
            // No se pudo: se vuelve a mostrar el menú con el motivo
            _hostStatus.text = runner.StatusMessage;
            yield return ui.StartCoroutine(ui.ShowMenu(Screen));
        }
    }

    /// <summary>Abre en el explorador la carpeta de los mundos coop (la crea si aún no existe).</summary>
    private static void OpenWorldsFolder() {
        try {
            System.IO.Directory.CreateDirectory(WorldStore.Root);
            Application.OpenURL("file:///" + WorldStore.Root.Replace('\\', '/'));
        } catch (Exception e) {
            Log.Warn($"No se pudo abrir la carpeta de los mundos ({WorldStore.Root}): {e.Message}");
        }
    }

    private void Join() {
        SaveFields();
        if (!CoopSaves.CanStart) {
            _joinStatus.text = Lang.T("Únete desde el menú principal", "Join from the main menu");
            return;
        }

        if (!TryPort(out var port)) {
            _joinStatus.text = Lang.T("El puerto no es válido", "The port is not valid");
            return;
        }

        MultiplayerRunner.Instance.Join(_address.text, port, _code.text, _name.text);
    }

    /// <summary>
    /// Rellena la dirección, el puerto y el código con lo copiado (p. ej. "203.0.113.7:26950 ABCD-EFGH" del chat): basta
    /// con uno de ellos, y lo que no venga se deja como estaba.
    /// </summary>
    private void PasteInvite() {
        var clipboard = GUIUtility.systemCopyBuffer;
        if (!TryParseInvite(clipboard, out var address, out var port, out var code)) {
            _joinStatus.text = Lang.T("No hay una dirección ni un código copiados", "No address or code copied");
            return;
        }

        if (address != null) {
            _address.text = address;
        }

        if (port is { } value) {
            _port.text = value.ToString();
        }

        if (code != null) {
            _code.text = MultiplayerRunner.FormatCode(code);
        }

        SaveFields();
        _joinStatus.text = code != null && address != null
            ? Lang.T("Dirección y código pegados", "Address and code pasted")
            : code != null
                ? Lang.T("Código pegado", "Code pasted")
                : Lang.T("Dirección pegada", "Address pasted");
        var pasted = new[] { address != null ? "dirección" : null, port != null ? "puerto" : null, code != null ? "código" : null };
        Log.Info($"Pegado del portapapeles: {string.Join(", ", pasted.Where(p => p != null))}");
    }

    /// <summary>
    /// Saca de un texto la dirección del host (con su puerto si lo lleva: "host:puerto" o "[IPv6]:puerto") y el código de
    /// la partida. Cada palabra cuenta como código si tiene su formato y no parece una dirección.
    /// </summary>
    internal static bool TryParseInvite(string text, out string address, out int? port, out string code) {
        address = null;
        port = null;
        code = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 512) {
            return false;
        }

        foreach (var raw in text.Split(new[] { ' ', '\t', '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)) {
            var token = raw.Trim();
            var looksLikeAddress = token.IndexOf('.') >= 0 || token.IndexOf(':') >= 0 ||
                                   token.Equals("localhost", StringComparison.OrdinalIgnoreCase);
            var normalized = Net.Crypto.RoomCode.Normalize(token);
            if (!looksLikeAddress && code == null && Net.Crypto.RoomCode.IsValid(normalized)) {
                code = normalized;
                continue;
            }

            if (!looksLikeAddress || address != null) {
                continue;
            }

            var host = token;
            var portText = (string) null;
            if (token.StartsWith("[", StringComparison.Ordinal)) {
                // IPv6 con puerto: [::1]:26950
                var close = token.IndexOf(']');
                if (close > 1) {
                    host = token.Substring(1, close - 1);
                    if (close + 1 < token.Length && token[close + 1] == ':') {
                        portText = token.Substring(close + 2);
                    }
                }
            } else if (token.IndexOf(':') == token.LastIndexOf(':') && token.IndexOf(':') > 0) {
                // host:puerto (con varios ":" es una IPv6 sin puerto)
                var colon = token.IndexOf(':');
                host = token.Substring(0, colon);
                portText = token.Substring(colon + 1);
            }

            if (host.Length == 0 || host.Length > 64) {
                continue;
            }

            address = host;
            if (portText != null && int.TryParse(portText, out var value) && value is > 0 and < 65536) {
                port = value;
            }
        }

        return address != null || code != null;
    }

    private void Update() {
        FollowLanguage();
        if (CurrentPage == JoinPage && _joinStatus != null && MultiplayerRunner.Instance != null) {
            var runner = MultiplayerRunner.Instance;
            var status = runner.Session is { IsActive: true } && runner.World.JoinStatus.Length > 0
                ? runner.World.JoinStatus
                : runner.StatusMessage;
            if (_joinStatus.text != status) {
                _joinStatus.text = status;
            }
        }
    }

    private void SaveFields() {
        var settings = HKMultiplayerMod.Settings;
        if (!string.IsNullOrWhiteSpace(_name.text)) {
            settings.PlayerName = _name.text.Trim();
        }

        settings.LastAddress = _address.text.Trim();
        if (TryPort(out var port)) {
            settings.Port = port;
        }

        HKMultiplayerMod.SaveSettings();
    }

    // =====================================================================================================
    // Textos
    // =====================================================================================================

    private static IEnumerator FadeSprite(SpriteRenderer sprite, float target) {
        if (sprite == null) {
            yield break;
        }

        var color = sprite.color;
        var start = color.a;
        for (var t = 0f; t < 1f; t += Time.unscaledDeltaTime * 3.2f) {
            color.a = Mathf.Lerp(start, target, t);
            sprite.color = color;
            yield return null;
        }

        color.a = target;
        sprite.color = color;
    }

    private static string FormatTime(float seconds) {
        var minutes = (int) (seconds / 60);
        return minutes >= 60 ? $"{minutes / 60} h {minutes % 60} min" : $"{minutes} min";
    }

    /// <summary>Nombre de la zona en el idioma del juego si existe, si no el interno.</summary>
    public static string AreaName(string mapZone) {
        if (string.IsNullOrEmpty(mapZone)) {
            return "-";
        }

        try {
            var localized = Language.Language.Get(mapZone, "Map Zones");
            return string.IsNullOrEmpty(localized) || localized.StartsWith("#!#") ? mapZone : localized;
        } catch (Exception) {
            return mapZone;
        }
    }

    /// <summary>Abre el menú.</summary>
    public void TestOpen() => Open();
}
