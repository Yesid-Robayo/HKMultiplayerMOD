# Changelog

**English** · [Español](CHANGELOG.es.md)

What each HKMultiplayer version added and fixed, newest first. Published versions have their zip, full release notes
in English and Spanish and their SHA256 on the [Releases](https://github.com/Yesid-Robayo/HKMultiplayerMOD/releases)
page. **All players need the same version**: two different versions can't play together.

When a new version is published, it is added at the top with the same content as the *What's new* section of its
release notes.

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
