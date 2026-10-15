# Changelog

**English** · [Español](CHANGELOG.es.md)

What each HKMultiplayer version added and fixed, newest first. Published versions have their zip, full release notes
in English and Spanish and their SHA256 on the [Releases](https://github.com/Yesid-Robayo/HKMultiplayerMOD/releases)
page. **All players need the same version**: two different versions can't play together.

When a new version is published, it is added at the top with the same content as the *What's new* section of its
release notes.

## 0.31.2

- **Uninstalling puts the whole game back as it was**: the Modding API's `TeamCherry.Localization.dll` stayed instead of
  the game's original one coming back (this happened with every installer version). If you already uninstalled an
  earlier version and the game without mods has problems: Steam › right-click Hollow Knight › Properties › Installed
  Files › Verify integrity.
- The package no longer contains paths from the PC it was built on.
- Spanish instructions in neutral Spanish.

## 0.31.1

Fixes to the Steam connection, found with real Steam.

- **"Invite to Game" in the Steam friends list**: outside the game only "Invite to Watch" used to show up; now "Invite
  to Game" shows up while you host, and your friends see "Join Game". Also with "Invited only": there, Steam won't let
  in anyone who clicks "Join Game" without an invite. With "Invited only", the safe way to invite is *Pause ›
  Multiplayer › Invite Steam friends*.
- **Why you can't join**: if you can't join a Steam game, the reason is shown (invite-only, full or already closed)
  instead of a general error.
- When the host closes the game, the guest also leaves their Steam lobby.

## 0.31.0

**Play through Steam, no port forwarding.**

- **Steam lobby**: hosting opens a Steam lobby. Your friends see *Join Game* in their Steam friends list, or your game
  in *Multiplayer › Join a world* with a button to join, without typing an address or a code.
- **Invites**: *Pause › Multiplayer › Invite Steam friends* opens the Steam overlay to invite anyone (also from the
  Steam friends list). On accepting, the mod joins the game on its own; if the game was closed, Steam opens it, and if
  your friend is playing their normal game, they join when they go back to the main menu.
- **Who joins through Steam**: in *Multiplayer*, *Steam games*: **Friends** (any Steam friend), **Invited only**
  (private lobby: only people who get an invite can join) or **Nobody** (IP only).
- **Steam and IP at the same time**: in the same game some players join through Steam and others by IP. If the port
  can't be opened, the game still opens through Steam. Anyone without Steam keeps joining the host by IP.
- **Security**: the Steam connection is encrypted with the game code, just like by IP (Steam only carries the
  packets), and connections are only accepted from friends, lobby members or the host you're joining.
- Automatic reconnection also works through Steam, and the host checks the version before letting anyone in.

## 0.30.0

Every boss fight played again with 4 players, one at a time, and the Pantheon of Hallownest played to its ending.

- **Pantheon of Hallownest ending**: each player finishes the Radiance on their own PC. Everyone strikes the final
  blows with their own attack button and sees the ending cutscene when they finish theirs; the first one to finish
  used to take the others along. Partners' knights and names are hidden during the ending.
- **The Radiance's climb**: the abyss rises for everyone when anyone climbs, and every player respawns on the last
  platform passed instead of falling into the abyss again and again. Its rays, the rising shade and the platforms are
  seen on every PC, its orbs come out where they charge, and it no longer freezes in the air when it changes PC in
  the middle of a teleport.
- **Pure Vessel**: its name and its bubble attack appear for everyone, in the same places.
- **Pantheons**: the partners who go in with the player who starts a Pantheon no longer get a black screen, and a
  player who falls in a Pantheon is revived next to the group in the next boss's room.
- **NPCs, per player**: each player hears every conversation from the start (Zote no longer asks the second player
  why they keep bothering him), and a character who leaves after talking (Zote, Quirrel, Cloth, Tiso...) only leaves
  for the player who talked to them.
- **Health bars**: only the boss that is fighting has one (the six Watcher Knights showed six). The Vengefly King and
  Gruz Mother count as bosses: health bar, extra health and waiting for everyone.
- **Mantis Lords**: two players can challenge them at the same time. **Soul Master**: the fake orb vanishes for
  everyone, everyone falls to the lower floor and its orb comes out where it charges. **Hornet**: the Mothwing Cloak
  falls on every PC and the Dreamers' dream starts as soon as each player picks it up.
- **Enemies that only some PCs have** (a part of the room that didn't load on the room owner's PC) are run by one of
  those players instead of standing still until someone hits them. The **Menderbug** shows up for everyone if it
  shows up for anyone.
- **Installer**: it's now a single program, with no PowerShell underneath, and it has a new icon (a shade). It's the
  same file in every version, so Windows should warn less often over time when opening it. An install made with the
  previous installer is updated and uninstalled the same way.

## 0.29.0

The whole story played with 4 players (four copies of the game at once), from the first bosses to both Hollow Knight
endings and the Radiance, plus the Grimm Troupe, Grey Prince Zote, the Colosseum and a whole Pantheon.

- **Pantheons together**: when a player starts a Pantheon, everyone in the Atrium goes in with them, with the same
  bindings, and the group moves on to each boss together (also out of the rest room). They used to fight it alone.
- **Hollow Knight**: no longer freezes forever in Hornet's scene. Everyone sees the body being absorbed and the Black
  Egg opening, whoever does it. Anyone can use the dream nail to enter the Radiance, which no longer starts on its
  own.
- **Radiance**: it moves on to its next phase, and takes its final hit, from any player, not only the one whose PC runs
  it.
- **Room parts that need an ability** (the Traitor Lord fight, the False Knight's wall, the Abyss door, the Crystal
  Peak bridge...): they load if anyone in the group has the ability, so the room is the same for everyone.
- **Soul Master**: everyone sees the fake orb sequence and falls to the lower floor together; its first phase has a
  health bar.
- **Dream warriors** start for everyone, and **dreams** no longer leave partners standing without control on the way
  out.
- **Cloth** fights the Traitor Lord on every PC; Xero, Flukemarm and the Soul Master's body no longer get stuck on some
  PCs; a sleeping boss no longer keeps changing PC.

## 0.28.0

Games with 3 and 4 players, tested with four copies of the game running at once.

- **Bosses**: a PC that was only watching no longer resumes a boss from an old state (it used to jump back to an
  earlier attack or phase).
- **Elevators, the Colosseum and the Mantis Lords**: when two players use them at the same time, every PC now agrees on
  who runs them (they used to stop for a third player).
- **Boss introductions**: with two at once, the knight stays still until both are over.
- **Quest NPCs**: when someone in another room changes a quest, the NPC is checked again.
- **Downed players**: a downed player no longer keeps following someone who is in a dream, a cutscene or Godhome.
- **Benches**: four players on the same bench, without ending up inside the wall.
- **Reviving**: with two players reviving at once, the notice no longer flickers and the one who finishes second gets
  their soul back.
- **Map**: each partner has their own color and their name.
- **Difficulty**: the extra health of enemies and bosses grows with each partner in the room (1.5× with two players is
  2× with three and 2.5× with four).
- **"Go to"**: several players going to the same partner at once appear in different spots.
- A new player no longer inherits what a player who left was controlling, and the host no longer loses data when it
  relays a lot.

## 0.27.0

- **Bosses no longer cut their attack when they switch player.** Each boss is run by the PC of the nearest player, and
  when the other player came closer the boss switched PC in the middle of an attack and started it again towards the
  other knight. Now it finishes the attack and switches between one attack and the next (at most 4 seconds later).

## 0.26.0

A full review of the mod against the game's code: 172 problems found, all of them fixed or reviewed, and most checked
in the game, which turned up 16 more.

- **Your partner**: the darkness of dark rooms opens around them; their Carefree Melody shield, Baldur Shell, Shade Dash
  recharge and their deaths on spikes, in acid and for real are now shown.
- **Enemies and bosses**: damage that isn't a nail hit (Flukenest, Spore Shroom, Defender's Crest) counts for everyone;
  bosses wait for everyone with no time limit; a boss that changes PC keeps fighting; each broken False Knight armor
  counts as a phase; summons and shots "over time" (Vengefly King, Hornet in Kingdom's Edge, Nosk, the Hollow Knight)
  happen on every PC.
- **Shared world**: the City of Tears bridge, the trams, toll machines, the White Palace elevator, the Colosseum and
  Radiance platforms and the Fog Canyon eggs work the same for everyone. Godhome statues, maximum soul and Millibelle's
  "theft" belong to each player.
- **Revive and session**: a downed player can no longer cast spells or dash; a guest who leaves to the main menu leaves
  the game; inside a Pantheon a guest's character is never saved without its charms; a boss encounter no longer freezes
  if the player running it goes down during the dialogue.
- **Interface and installer**: the partner HUD hides with the inventory and changes language; menus work with a
  controller; the installer handles a Steam update of the game and folders with `[ ]`.
- **Network and security**: the game code is stretched with PBKDF2 and the host doesn't answer anyone who doesn't know
  it; fake packets can no longer exhaust memory or kick a player.

## 0.25.0

Fixes from the third real game between two PCs.

- **Garpedes** (Deepnest): they move along the same path at the same time for both players.
- **Mantis Lords, second phase**: the scythe no longer flies out of the arena first.
- **Darkness in Deepnest**: a player's view no longer goes black when their partner moves away.

## 0.24.1

Fixes from the second real game between two PCs.

- **Mantis Lords**: when retrying the challenge the first Mantis Lord always appears; the challenge prompt no longer
  turns off too early; their trail no longer stays frozen.
- **Bretta and other quest NPCs** no longer disappear in front of a partner who is in the room.
- **Elevators** in Dirtmouth and the stag station: they move for both players.
- **Enemies come back** when resting at a bench and when dying, as in the normal game.
- The shade music stops when the partner's shade is killed, and a downed player's body wears their skin.

## 0.24.0

- **New**: abilities belong to each player; ride the stag together; benches for two; new guests see the opening
  cutscene; loot lands in the same place for everyone; partners on the map only with the Wayward Compass; "paused" in
  the HUD; new version notices in the game; `HKMultiplayer Setup.exe` installer with *What's new*.
- **Fixes from the first real game**: the False Knight dies completely and opens the gates; Elder Baldur shots, Gulka
  spit and Shrumal Ogre vomit on the other PC; no double attacks when an enemy changes PC; the Vengefly King no longer
  stays in the air; effects that stayed on screen; Greenpath grass, lifeblood cocoon and stag bell.

## 0.23.0

Fixes from the first real game between two PCs.

- An error while handling a message no longer cuts the connection for the rest of the game.
- The False Knight appears again after *Reload room*.
- Tiktiks and Crawlids no longer jump around; Goams come out and hide at the same time for everyone.
- A skin's soul orb no longer flickers.

## 0.22.1

- Installer: the text of disabled buttons is readable (white).

## 0.22.0

- **Partners on the map**, red when they are down (host option).
- **Automatic reconnection** for guests, up to 12 attempts (*Esc* cancels it).
- **Bodies always reachable**: a player who dies over a pit, spikes or acid stays on the last safe ground.

## 0.21.2

- Geo rocks drop geo for everyone.
- Faster skins, saved in a cache (up to 256 MB).

## 0.21.1

- First public version.

## Before release (0.2.0 to 0.21.0)

Development versions, without a release.

- **0.21.0**: the mod in Spanish or English depending on the game, and a windowed installer.
- **0.20.x**: quest NPCs no longer desync or advance twice; bosses that change owner, the False Knight's head and dream
  bars.
- **0.19.0**: go to a partner and host settings.
- **0.18.x**: partner charm and companion effects; Grimm, Soul Tyrant, Hollow Knight, Radiance, Failed Champion, False
  Knight and Hive children.
- **0.16.0 to 0.17.0**: shared progress (M5) and the Golden Colosseum without getting stuck.
- **0.15.x**: the other player's effects (M4); the whole Silver Colosseum.
- **0.14.x**: shared breakables and saved objects; automatic test routes; the Colosseum no longer gets stuck between
  waves.
- **0.12.0 to 0.13.0**: shared boss dialogues and fights that wait for everyone.
- **0.9.0 to 0.11.0**: chests, animated hazards in sync and the Collector with his jars.
- **0.7.0 to 0.8.0**: Mantis Lords, enemy handover between PCs, joining in the middle of a fight and the Colosseum.
- **0.4.0 to 0.6.x**: bosses identical on both PCs (damage, arena, effects) and the Crystal Guardian with its beams.
- **0.3.0**: enemies attack both players.
- **0.2.0**: enemy hits and sounds on every PC.
