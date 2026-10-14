<div align="center">

# HKMultiplayer

**A co-op mod for Hollow Knight: play the whole world with your friends, on your local network or over the internet.**

![Version](https://img.shields.io/badge/version-0.29.0-7fb2ff)
![Status](https://img.shields.io/badge/status-beta-f0c86a)
![Hollow Knight](https://img.shields.io/badge/Hollow%20Knight-1.5.12620-e8ecf3)
![Platform](https://img.shields.io/badge/platform-Windows-2f5fb3)
![Languages](https://img.shields.io/badge/languages-english%20%7C%20español-7ddc8f)

**English** · [Español](README.es.md) · [Changelog](CHANGELOG.md)

</div>

> [!WARNING]
> **Beta.** Almost everything has been tested by playing with two copies of the game on the same PC, but no long
> session between two PCs over the internet has been played yet, and some parts are not fully tested (see
> [Testing status](#-testing-status)). **It may have bugs.** Back up your saves before playing and, if something goes
> wrong, open an *issue* with a screenshot and your `ModLog.txt`.

One player **hosts** a coop world and their friends **join** with the host's address and the game code: you share the
world, see each other in real time and fight the same enemies and bosses together. It is designed for two players
(more can join) and written from scratch on top of the Hollow Knight Modding API.

## Contents

- [Features](#-features)
- [Testing status](#-testing-status)
- [Install](#-install)
- [How to play](#-how-to-play)
- [Game settings](#%EF%B8%8F-game-settings)
- [Technology](#-technology)
- [Development](#-development)
- [Credits](#-credits)
- [License and contributions](#-license-and-contributions)

---

## ✨ Features

- 🌍 **Coop worlds**: up to 4 worlds of your own, saved on the host's PC, separate from your normal saves.
- 🔒 **Connection**: local network or internet, with a game code and everything encrypted, no Steam or relay servers;
  a guest who loses the connection gets back in on their own.
- 👥 **Players**: you see your partner in real time with their name, animations, attacks, spells and charms, and on the
  map.
- ⚔️ **Enemies and bosses**: the same on every PC (same health, same hits, same death); bosses wait until you are both
  there.
- 🗺️ **Shared world**: progress, levers, doors, broken walls, elevators, gates, chests and NPC quests, all at once.
- ❤️ **Revive**: if you fall while a partner is still standing, you lie down and they revive you by holding Focus next
  to your body.
- 🌙 **Dreams and cutscenes**: you enter dreams together; endings and credits play at the same time and are skipped by
  vote.
- 🎨 **Skins**: CustomKnight-compatible; others see yours even if they don't have it.
- 🛠️ **Pause panel**: host settings, your partners' health, give geo, go to a partner, unstuck and reload the room.
- 🌐 **Languages**: the mod follows the game's language (Spanish, or English for any other language).
- 📦 **Installer**: a window to install, update and uninstall, which leaves the game as it was.

<details>
<summary><b>🌍 Game and connection</b></summary>

- **Coop worlds** of your own: up to 4, saved on the host's PC in
  `C:\Users\<user>\AppData\LocalLow\Team Cherry\Hollow Knight\HKMultiplayer\Mundos` (one folder per world). They are
  separate from the game's 4 save slots, which are never touched. You create, host and delete them from the menu, and
  the hosting page has a button to open that folder (for backups or to move a world to another PC).
- **Host and join** from the main menu (*Multiplayer*): the host opens a world and shares their **address**, the
  **port** (26950 by default) and the **game code** (`ABCD-EFGH` format). Buttons to copy the code and show or hide the
  IP.
- Works on a **local network and over the internet** (over the internet the host opens the UDP port on their router,
  or you use a virtual LAN such as Radmin VPN, ZeroTier or Tailscale).
- **Each guest's character** is saved in the host's world: it is sent at benches, every 30 seconds and when leaving.
  When you join again you get your character back. If the host closes the game, guests' characters are collected
  first.
- **Automatic reconnection**: if a guest loses the connection to the host (not when the host closes the game), they
  go back to the main menu and try to get back in on their own, up to 12 times, and load the world again with their
  character. *Esc* in the main menu cancels it.
- **On-screen notices**: the game code, who joins and leaves, disconnections and fight waits.
- **Pause panel** (*Pause › Multiplayer*): connected players with their ping, the code, the IP and close or leave the
  game; *Game settings*, *Partners* and *Skin*.

</details>

<details>
<summary><b>👥 Players and partner effects</b></summary>

- You see your partner in real time, with their **name above them**, their animations and facing, with smooth movement
  (interpolation that adapts to the connection).
- **Each player has their own inventory**: health, soul, geo, masks and vessels, nail and nail arts, charms and
  notches, keys, relics, maps, essence, purchases, bench and shade.
- Their actions are seen and heard with the game's own effects:
  - **Nail slashes** (side, up, down and on walls), bigger with Mark of Pride and Longnail and colored by Fury of the
    Fallen.
  - **Nail arts**: charge, Great Slash, Dash Slash and Cyclone Slash.
  - **Spells**: Vengeful Spirit and Shade Soul, Desolate Dive and Descending Dark, Howling Wraiths and Abyss Shriek.
  - **Movement**: dash and Shade Dash, double jump, wall jump, Crystal Heart, dust when running and jumping.
  - **Healing**, **Dream Nail**, **hits taken** and the invulnerability blink.
  - **Charms and companions**: Grubberfly's Elegy, Flukenest, Defender's Crest (and its aura), Thorns of Agony,
    Dreamshield, Sharp Shadow, Glowing Womb, Weaversong and Grimmchild; the Carefree Melody shield and the Baldur
    Shell (when it goes up, blocks and breaks).
  - **Deaths**: on spikes and in acid, and a real death (in Godhome, or when everyone falls); the Shade Dash recharge
    flash.
  - **In dark rooms** the darkness opens around each partner as it does around you: you see the tunnel around them,
    not just a black screen.
  - Grass and cloth move as they walk past.
- Effects are visual only: real damage comes from each player's own hits.

</details>

<details>
<summary><b>⚔️ Enemies and bosses</b></summary>

- Each enemy is run by the PC of the **nearest** player and attacks everyone. Its health, attacks and death are the
  same on every PC; anyone's hits take its health on all of them.
- **Projectiles, spawns and summons** are created on every PC and hurt everyone (shots, crystals, lasers, Soul Master
  orbs, Mawlek balls, Hornet's needle, the Collector's jars, the False Knight's barrels...).
- **Enemy effects and sounds**, hit effects, boss music, the **boss name** when it wakes up and a **boss health bar**
  with its name in the game's language.
- **Boss parts and traps** match on every PC: the Crystal Guardian's laser and ceiling turrets, Hornet's needle, the
  False Knight's head...
- **Wave arenas**: Mantis Lords, the Colosseum of Fools (Bronze, Silver and Gold), Watcher Knights...
- **Boss fights wait until you are both there**, with a notice saying who is missing; meanwhile the boss is
  invulnerable. **Dialogues before a fight** are shown to both players, and each waits for the other to finish reading.
- **Boss introductions** keep both knights still and give control back at the same time.
- If a boss moves to another PC mid-fight (because the other player got closer), it carries on where it was.
- Dead enemies stay dead when you come back to the room, until you rest at a bench (as in the game).

</details>

<details>
<summary><b>🗺️ Shared world</b></summary>

- **Live shared progress**: defeated bosses, doors, levers, broken walls, visited rooms and maps.
- **Abilities are per player** (movement abilities, spells, Dream Nail): each player gets their own and sees the
  pickup scene. Characters saved with an earlier version keep the abilities they already had.
- **Loot lands in the same place** on both PCs: geo from enemies, geo rocks and chests.
- **Stag travel together** (host option): when one player picks a destination, partners standing near them at that
  station ride along.
- **Benches for two**: sitting on the same bench, each player takes one half.
- **Version notices**: the main menu tells you when a new version is out (and *Multiplayer* offers to download it)
  or when the mod was just updated (with what's new). It is one request to GitHub's public release list when the game
  starts; turn it off in *Multiplayer › Check for updates*.
- **New guests see the opening cinematic** (skippable) before joining the host's world.
- **Room objects**: levers, switches, doors, breakable walls and floors, geo rocks, breakables, grass, vines, grub jars,
  stalactites, signs and props with their own logic.
- **Elevators**: run by whoever uses them; the other player sees them move the same way.
- **One-way battle gates**: if they close with one player inside, the other can come in to help but not leave.
- **Chests**: if one player opens one, the other sees it open; what drops is for each player.
- **Individual items** (shiny items, mask shards, vessel fragments, dream orbs): what one player picks up doesn't
  disappear for the other.
- **NPC quests**: Mr. Mushroom, Quirrel, Bretta, Zote, Tiso, Myla, Hornet, the Nailsmith, the Grimm Troupe... The NPC
  moves on or leaves on both PCs and the quest never advances twice.
- **Animated hazards in sync** (the White Palace and Path of Pain saws and spikes).
- Whoever enters a room finds it as it is: what's activated, what's broken, dead enemies and fights in progress.

</details>

<details>
<summary><b>❤️ Co-op features</b></summary>

- **Revive**: if you die while a partner is standing, you don't die, you go down. Your body lies on the floor with
  "† Name" and your camera follows your partner. They get a notice and an arrow to your body; next to it, **holding
  Focus for 2 s** and spending 33 soul revives you with 1 mask. If your partner sits at a bench, downed players revive
  next to them with full health. If everyone goes down, each player dies normally. If you die where nobody could
  stand next to you (in the air over a pit, on spikes or in acid), your body lies on the last safe ground, where the
  game respawns you after a hazard.
- **Shared shades**: everyone sees their partner's shade and can kill it; its owner gets their geo and soul back even
  in another room.
- **Partner HUD** at the top right (name, masks, soul and where they are), their masks above their head when their
  health changes, and **arrows** at the edge of the screen pointing to partners and bodies.
- **Partners on the map**: with the map open, each partner shows up as a blue compass where they are (when both of
  you wear the Wayward Compass), and red on their body if they are down.
- Optional **enemy health bars** above each damaged enemy.
- **Shared cutscenes**: endings, credits and Mr. Mushroom play at the same time even in different rooms; they are
  skipped when everyone votes ("Waiting for X to skip 1/2").
- **Shared dreams**: if one player enters a dream with the Dream Nail, a partner in the same room enters too. When you
  win, both leave; whoever falls or dies leaves alone. Dream bosses, the Nightmare King and the Radiance.
- **Grimmchild**: if one player gets it, the other does too.
- **Skins** compatible with CustomKnight (*Pause › Multiplayer › Skin*): body, spells and effects; sent through the
  mod's own network in the background without delaying hits (the first time; then they're kept in a cache and load
  instantly), and you can turn off seeing other players' skins.
- **Partners** (*Pause › Multiplayer › Partners*): each partner's health, soul and location, **give geo**, **go to**
  them (you appear next to them, even in another room), **unstuck** (back to the last safe ground) and **reload the
  room**.

</details>

---

## 🧪 Testing status

Testing uses two (or three) copies of the game on the same PC, with the mod's test mode and automated routes that walk
through the world checking that both players see the same thing.

✅ **Tested by playing:**

- Connection, coop worlds and guests' characters.
- Enemies in every area.
- Bosses: Gruz Mother, False Knight (also on a bad connection), Hornet, Brooding Mawlek, Soul Master, Dung Defender,
  Nosk, Broken Vessel, Crystal Guardian, Mantis Lords, Uumuu, The Collector, Traitor Lord, Watcher Knights, Grimm, Soul
  Tyrant, Hollow Knight and the Radiance.
- Colosseum of Fools: Bronze, Silver and Gold.
- Dreams: Failed Champion, Lost Kin, Soul Tyrant, White Defender, Grey Prince Zote and the Nightmare King.
- Revive (also the body on the last safe ground, over spikes and acid), shades, partner HUD, partners on the map,
  host settings, cutscenes, skins, go to a partner, NPC quests, vine platforms and automatic reconnection (also
  cancelling it and giving up).
- Install, update and uninstall.
- 0.26.0: the whole mod was reviewed against the game's code. The 172 issues found were fixed or reviewed, and most
  were checked in the game; the testing found and fixed 16 more.
- 0.29.0: the whole story with 4 players (four copies of the game): every boss with real fights, the dreams and the
  dream warriors, the three Dreamers, the Black Egg, the Hollow Knight (both endings) and the Radiance, the Grimm
  Troupe, Grey Prince Zote, the Colosseum of Fools (Bronze, Zote included), Zote and Cloth, and the Pantheon of the
  Master from start to finish.

⚠️ **Partially tested:**

- Hive Knight, Flukemarm, Massive Moss Charger and Vengefly King: they die on both PCs, but their attacks still need
  checking.

❓ **Not tested yet:**

- A session between two PCs over the internet.
- The English translation inside the game.
- From 0.22.0: a partner's map icon inside rooms that are not on the map (shops).
- The orange steam of the False Knight's death (fixed without retesting).
- Menderbug, and the Pantheon endings that end in a cutscene (the 5th).
- Playing with 3 or 4 players: the cases that only happen with three or more were fixed in 0.28.0 and tested with four
  copies of the game on one PC, but not yet between real PCs.

Known limitations:

- A boss attacks the player whose PC runs it, which is the nearest one; that's what makes it attack everyone. It
  changes PC at most every 8 s and, since 0.27.0, only between two attacks (or after 4 s without a pause).
- Your partner and the enemies run by another PC are drawn a few hundredths of a second in the past so they move
  smoothly. Very fast falling objects show it a little.

The full list (in Spanish) is in [docs/arquitectura.md](docs/arquitectura.md#pendientes).

---

## 📦 Install

> [!NOTE]
> You need **Hollow Knight 1.5.12620 on Windows** (the current Steam version; GOG works too). All players must have
> **the same version** of HKMultiplayer. Mac and Linux are not supported, and the Xbox/Game Pass version is not tested.

1. Download `HKMultiplayer-<version>.zip` from the [*Releases*](../../releases) page.
2. Close the game, **extract the whole zip** and double-click **`HKMultiplayer Setup.exe`**.
3. In the window that opens, check the game folder and click **Install**.
4. Open Hollow Knight from Steam: **Multiplayer** appears in the main menu.

Windows may warn you when opening a file downloaded from the internet: click *Run* (or *More info › Run anyway*). The
installer is a PowerShell script you can read in the package's `scripts` folder.

<details>
<summary><b>What the installer does</b></summary>

- Finds the game folder (Steam, its libraries and the usual paths); if there are several, you pick one from the list,
  and if none is found, you use *Browse…*.
- Checks that the game is version 1.5.12620 and closed, and that the package is complete (`SHA256SUMS.txt`).
- Saves a copy of every file it changes in `<game>\HKMultiplayer-backup` and records everything in `manifest.txt`.
- Removes the old **HollowKnightCoop** mod if present and moves other mods to `Mods\Disabled`, telling you which ones
  before installing (mods built for another
  Modding API version can break the game).
- Copies the Modding API and the mod, and verifies the copy. Installing a new version on top is an update.
- When it opens it checks GitHub: if there is a newer version than the package, a notice at the top offers to download
  it. **What's new** (bottom right) shows what that version brings, or this package's one if there is none newer.
- **Uninstall** undoes everything in reverse: the game is left as it was. Your coop worlds, saves and skins are not
  touched.
- Don't mix it with Lumafly or Scarab on the same folder: their "vanilla" switch moves the same files, and uninstalling
  may then not leave the game exactly as it was (Steam's *Verify integrity* fixes it).
- It also works from the console: `start /wait "" "HKMultiplayer Setup.exe" /install "D:\Games\Hollow Knight"` (or
  `/uninstall`).

</details>

Rather not use the installer? The package's `README.txt` (English) and `LEEME.txt` (Spanish) explain the **manual
installation**, how to open the port and what to do if something fails.

---

## 🎮 How to play

1. **Host**: main menu › *Multiplayer* › *Host a world* › *New world* (or pick one) › *Create and host*. Give your
   friends your **IP**, the **port** (26950) and the **code** shown on screen (also in *Pause › Multiplayer*).
2. **Guest**: main menu › *Multiplayer* › *Join a world*, with the address, the port and the code.
3. **Over the internet**: the host opens **UDP port 26950** on their router (or you use Radmin VPN, ZeroTier or
   Tailscale). On the same home network you don't need to open anything.

---

## ⚙️ Game settings

The host sets them in *Pause › Multiplayer › Game settings*; they are saved with each world and guests see them right
away without being able to change them.

| Setting | Values | Default |
|---|---|---|
| Boss health | 1× · 1.25× · 1.5× · 2× · 2.5× · 3× | 1.5× |
| Enemy health | 1× · 1.25× · 1.5× · 2× | 1× |
| Extra health only with a partner in the room | Yes · No | Yes |
| Damage taken | 0.5× · 1× · 1.5× · 2× | 1× |
| Revive | Yes · No | Yes |
| Time to revive | 1 · 2 · 3 · 5 s | 2 s |
| Soul to revive | 0 · 33 · 66 · 99 | 33 |
| Masks on revive | 1 · 2 · 3 · All | 1 |
| Invulnerable after revive | 1 · 2 · 3 · 5 s | 2 s |
| Revive at a partner's bench | Yes · No | Yes |
| Names · Partner health · Arrows to partners · Partners on the map · Notices · Boss bar | Yes · No | Yes |
| Enemy bars | Yes · No | No |
| Gift geo · Allow going to a partner | Yes · No | Yes |
| Skip cutscenes | Vote · Anyone | Vote |
| Ride the stag together | Yes · No | Yes |

The boss and enemy health is the extra health with one partner: each additional partner standing in the room adds the
same again (1.5× is 2× with three players and 2.5× with four).

---

## 🔧 Technology

| Component | Used for |
|---|---|
| **Hollow Knight Modding API** (Unity 6) | Loading the mod and hooking the game. A modified version for 1.5.12620, included in [`modding-api/`](modding-api). |
| **MonoMod** | Hooks into the game's code (`MMHOOK_*.dll`, `MonoMod.RuntimeDetour`). |
| **PlayMaker** | Reading and driving the game's state machines: enemies, bosses, elevators, gates, arenas, dialogues. |
| **BouncyCastle** | Encryption: X25519, HKDF-SHA256 with the game code and ChaCha20-Poly1305 on every packet. |
| **Unity UI** | Menus in the game's style, notices and the partner HUD (with the game's own HUD sprites). |

- **Custom networking over UDP**: reliable and unreliable channels, chunked large transfers (the world when joining),
  heartbeat and clean disconnects, with no per-packet allocations to avoid stutters.
- **Security**: only people who know the game code can join; everything is encrypted and authenticated.
- **No external services**: players connect directly to each other.

---

## 💻 Development

> [!NOTE]
> Code identifiers are in English; code comments, logs and the design notes in `docs/` are in Spanish.

<details>
<summary><b>Repository layout</b></summary>

| Folder | Contents |
|---|---|
| `src/HKMultiplayer/Net/` | UDP transport, channels, encryption and game codes (no Unity, tested outside the game). |
| `src/HKMultiplayer/Session/` | Session: hosting and joining, players, rooms and each room's owner, message routing. |
| `src/HKMultiplayer/Game/Players/` | Player state, interpolation, partner avatars and their effects. |
| `src/HKMultiplayer/Game/World/` | Coop worlds, saving, guests' characters, shared progress and NPC quests. |
| `src/HKMultiplayer/Game/Features/` | Co-op features: player status, host settings, revive, shades, HUD, partners on the map, bars, geo gifts, going to a partner, dreams, cutscenes, skins. |
| `src/HKMultiplayer/Game/Scene/` | Room objects, breakables, elevators and gates, battle controllers, enemies and bosses, animated hazards. |
| `src/HKMultiplayer/UI/` | Menus, notices, language and the co-op HUD canvas. |
| `src/HKMultiplayer/Testing/` | Test mode: file-driven commands to test with several copies of the game. |
| `modding-api/` | Modding API source (hk-modding/api, MIT) for Unity 6: the `hk-beta-branch-unity-6-update` branch of `SFGrenade/api` (PR hk-modding/api#164, commit `468f579`) with our own changes. No game files. |
| `tests/` | Transport tests outside the game and co-op test routes (`tests/routes/`). |
| `tools/` | PC setup (`setup-dev.sh`), build and install (`test-cycle.sh`), multi-copy tests, automated routes, package and installer. |
| `docs/` | [Architecture](docs/arquitectura.md) (Spanish): design, decisions, testing and pending work. |
| `lib/`, `vendor/`, `test-instances/` | *(local only, not committed)* Game and Modding API DLLs, and game copies for testing. |
| `decomp/` | *(local only, not committed)* The game's code decompiled by `tools/decompile.sh`, for reference. |

The game's DLLs belong to Team Cherry and are not in the repository: they are taken from your installed game.

</details>

### Building (also on another PC)

You need Hollow Knight 1.5.12620 installed, the .NET SDK (`dotnet`) and Git Bash.

```bash
git clone https://github.com/Yesid-Robayo/HKMultiplayerMOD.git
cd HKMultiplayerMOD
bash tools/setup-dev.sh "C:/Program Files (x86)/Steam/steamapps/common/Hollow Knight"   # once
bash tools/test-cycle.sh            # builds the mod and installs it in the test copies (--game: also in the game)
dotnet run --project tests/NetTests # transport tests
bash tools/package.sh               # player package: out/package/HKMultiplayer-<version>.zip
```

`setup-dev.sh` copies the game's **unmodified** `Managed` folder to `modding-api/Vanilla`, builds the Modding API and
fills `lib/` and `vendor/`. If the game already has the Modding API, it uses the originals saved by the installer
(`HKMultiplayer-backup`); if there are none, it asks you to verify the game files in Steam. To change the Modding API,
edit `modding-api/` and run it again.

`bash tools/decompile.sh` decompiles the game's code into `decomp/` to see how it does something (needs `ilspycmd`).
PlayMaker FSMs are not in there; see [Código del juego](docs/arquitectura.md#código-del-juego) (Spanish).

### Testing

- **Two players on one PC**: `tools/mp-test-lib.sh` opens two copies of the game (`test-instances/b` and `c`) in test
  mode and sends them commands through a file: host and join, go to a room, move, attack, screenshots from both at
  once, enemy and object state, simulated bad connection (`netsim`)... Commands are listed in
  [docs/arquitectura.md](docs/arquitectura.md#pruebas).
- **Automated routes**: `bash tools/run-routes.sh` walks through the game area by area with a host and a guest and
  checks in every room that both see the same thing (`--pairs 3` splits the areas between 3 pairs). Format:
  [tests/routes/README.md](tests/routes/README.md).
- **Installer**: `bash tools/manual-tests/installer-test.sh <zip>` installs, launches, uninstalls and compares on clean
  copies, without touching the Steam game.

---

## 🙏 Credits

- **[Hollow Knight](https://www.hollowknight.com/)** is by **Team Cherry**. This mod is an unofficial fan project and is
  not affiliated with Team Cherry. The repository contains no game files; like the official Modding API releases, the
  player package includes the game's `Assembly-CSharp.dll` and `TeamCherry.Localization.dll` already patched by the
  Modding API.
- **[Hollow Knight Modding API](https://github.com/hk-modding/api)** (MIT), with the Unity 6 port by
  [SFGrenade](https://github.com/SFGrenade/api).
- **[MonoMod](https://github.com/MonoMod/MonoMod)**, **[Mono.Cecil](https://github.com/jbevain/cecil)**,
  **[unity-scene-repacker](https://github.com/jakobhellermann/unity-scene-repacker)**,
  **[BouncyCastle](https://www.bouncycastle.org/)** and **[Newtonsoft.Json](https://www.newtonsoft.com/json)**, all MIT
  (full notices in [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt)).
- Skins compatible with **CustomKnight**.

---

## 📄 License and contributions

- HKMultiplayer's code is licensed under the **[MIT License](LICENSE)**. It is provided **as is, without any
  warranty**: use it at your own risk and back up your saves. The Modding API in [`modding-api/`](modding-api) keeps its
  own MIT license.
- **Pull requests are not accepted** (they are closed automatically). Bugs and ideas go in an *issue*; see
  [CONTRIBUTING.md](CONTRIBUTING.md).
- Security problems are reported privately: [SECURITY.md](SECURITY.md).
