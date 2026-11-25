HKMultiplayer @VERSION@ for macOS - co-op mod for Hollow Knight
===============================================================

One player hosts a coop world and their friends join with the host's address and the game code
(or through Steam): you share the world, see each other in real time and fight the same enemies
and bosses together.

You need:
  - Hollow Knight 1.5.12620 for macOS from Steam (the current version). Tested on Apple Silicon.
  - Every player must have the SAME version of HKMultiplayer (@VERSION@). Mac and Windows players
    can play together as long as the versions match.

The mod uses the game's language (the same 11 languages as the game).

(Instrucciones en español: LEEME.txt)


1. INSTALL
----------

  1. Close Hollow Knight.
  2. Double-click the .zip to extract it, open the folder and double-click
     "Install HKMultiplayer.command". A Terminal window opens and installs the mod.
  3. Open Hollow Knight from Steam: "Multiplayer" appears in the main menu.

macOS blocks the installer the first time because it isn't signed by an Apple developer
("Apple could not verify..."). Click "Done", then open System Settings > Privacy & Security,
scroll down and click "Open Anyway" next to "Install HKMultiplayer.command". The installer is a
short script: you can open it in TextEdit to read what it does.

Or run it from Terminal instead: type "bash " (with a space), drag "Install HKMultiplayer.command"
onto the Terminal window and press Return.

If your game isn't in the usual Steam folder, the installer asks you for it: in Steam,
right-click Hollow Knight > Manage > Browse local files, and drag that folder onto the Terminal
window.

To update to a new version, install the new package on top. If Steam updates Hollow Knight, the
mod disappears: install the package again (once there is a version for the new game version).


2. HOST A WORLD (the one who invites)
-------------------------------------

  1. Main menu > Multiplayer. Type your name. The port is 26950 unless you change it.
  2. "Host a world" > "New world" (or pick one you already have) > "Create and host".
  3. Give your friends your address (IP), the port (26950) and the game code shown on screen
     (like ABCD-EFGH). They are also in Pause > Multiplayer, with a button to copy the code.

With Steam you don't need any of that: your Steam friends can join directly. To invite
someone: Pause > Multiplayer > "Invite Steam friends".

If macOS asks whether "Hollow Knight" may accept incoming network connections, click "Allow".

Which address to give:
  - Same home (same network or wifi): the IP shown in Pause > Multiplayer (usually 192.168.x.x).
  - Over the internet: your public IP, and you have to open UDP port 26950 in your router
    ("Port forwarding": protocol UDP, port 26950, to your Mac's local IP). If you can't, use a
    virtual LAN program such as ZeroTier or Tailscale, or play through Steam.


3. JOIN A WORLD (the guest)
---------------------------

  - Through Steam: accept the host's invite, or click "Join Game" in your Steam friends list.
  - By IP: main menu > Multiplayer > "Join a world": type the host's address and the game code >
    "Join".

Your character is saved in the host's world. If you join that world again, you get it back.


4. WHERE THINGS ARE SAVED
-------------------------

Coop worlds (on the host's Mac), one folder per world:
  ~/Library/Application Support/unity.Team Cherry.Hollow Knight/HKMultiplayer/Mundos

Skins (CustomKnight compatible, a folder with Knight.png each) go in Pause > Multiplayer > Skin >
"Open skins folder".


5. UNINSTALL
------------

In Steam: right-click Hollow Knight > Properties > Installed Files > Verify integrity of game
files. That puts back the original game files, so the Modding API and the mod no longer load.
Your coop worlds and saves are not touched.


6. TROUBLESHOOTING
------------------

  - "Multiplayer" doesn't appear in the menu: install again. If it still doesn't show up, check
    the Modding API log:
      ~/Library/Application Support/unity.Team Cherry.Hollow Knight/ModLog.txt
    (in Finder: Go > Go to Folder..., and paste the path).
  - The guest can't connect: check the address, the port and the code; that you both have the
    same mod version; and, over the internet, that the host opened the UDP port (section 2).
  - Report bugs in an issue on GitHub with a screenshot and your ModLog.txt.


7. LICENSE AND WARRANTY
-----------------------

HKMultiplayer is licensed under the MIT License (LICENSE.txt). It is provided as is, WITHOUT ANY
WARRANTY: use it at your own risk and back up your saves before playing. It is an unofficial fan
project, not affiliated with Team Cherry. Third-party components and their licenses are listed in
THIRD_PARTY_NOTICES.txt.
