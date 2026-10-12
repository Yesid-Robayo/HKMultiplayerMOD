HKMultiplayer @VERSION@ - co-op mod for Hollow Knight
=====================================================

One player hosts a coop world and their friends join with the host's address and the game code:
you share the world, see each other in real time and fight the same enemies and bosses together.

You need:
  - Hollow Knight 1.5.12620 on Windows (the current Steam version; the GOG one works too).
    Not Mac or Linux: the package's Modding API is Windows only. The Xbox/Game Pass version is not
    tested and the installer doesn't look for it.
  - Every player must have the SAME version of HKMultiplayer installed.

The mod uses the game's language: Spanish if the game is in Spanish; English for any other
language.

(Instrucciones en español: LEEME.txt)


1. INSTALL (with the installer)
-------------------------------

  1. Close Hollow Knight.
  2. Extract the whole .zip into any folder (right-click > Extract All). Don't run it from inside
     the .zip.
  3. Open the folder and double-click "HKMultiplayer Setup.exe".
  4. The installer opens. Check that the game folder is the right one (if it isn't found, click
     "Browse…") and click "Install".

The installer:
  - Looks for the game folder (Steam and the usual paths). If there are several, pick yours from
    the list. To find it by hand: in Steam, right-click Hollow Knight > Manage > Browse local
    files.
  - Backs up the game files it is going to change, in the "HKMultiplayer-backup" folder inside
    the game folder. Don't delete it: uninstalling needs it.
  - Installs the Modding API (which loads mods) and the mod.
  - Removes the old "HollowKnightCoop" mod if you had it: both use the network at the same time
    and can't be installed together.
  - Moves any other mods to the Mods\Disabled folder (mods built for another Modding API version
    can break the game). Uninstalling puts them back.

Then open Hollow Knight from Steam: "Multiplayer" appears in the main menu.

Windows may warn you when opening "HKMultiplayer Setup.exe" ("Do you want to run this file?" or
"Windows protected your PC"): it happens with any unsigned file downloaded from the internet. Click
"Run" (or "More info" > "Run anyway"). The .exe only opens the installer, a PowerShell
script you can read in the "scripts" folder; if you'd rather not use it, install by hand (section 2).

If Windows won't let the installer write to the game folder, right-click "HKMultiplayer Setup.exe"
and choose "Run as administrator".

To update to a new version of the mod, open the installer of the new package and click "Update":
it keeps the game backup from the first install.

Is there a new version? The installer checks GitHub when it opens and, if there is one, a notice at
the top links to its download; "What's new" (bottom right) shows what it brings. The game also tells you on the
main menu, and "Multiplayer" shows a button to download it (you can turn this off right there, in
"Check for updates"). To play together, everyone needs the same version.


2. INSTALL BY HAND (without the installer)
------------------------------------------

If you'd rather not use the installer, or it doesn't work on your PC:

  1. Close Hollow Knight.
  2. Find the game folder: in Steam, right-click Hollow Knight > Manage > Browse local files.
     Inside it is hollow_knight_Data\Managed.
  3. Back up the whole hollow_knight_Data\Managed folder (for example, copy it to your desktop),
     so you can undo it.
  4. If there is a Managed\Mods folder, move any other mods out of it (especially
     "HollowKnightCoop", which can't be installed at the same time as this one).
  5. Copy the contents of the package's "files\hollow_knight_Data\Managed" folder into
     <game folder>\hollow_knight_Data\Managed. When Windows asks, choose "Replace the files in the
     destination".
  6. Open the game: "Multiplayer" appears in the main menu.

If you already have the Modding API for this game version (for example from Lumafly or Scarab),
just copy the package's "files\hollow_knight_Data\Managed\Mods\HKMultiplayer" folder into
<game folder>\hollow_knight_Data\Managed\Mods.

Don't use the installer and Lumafly or Scarab on the same folder at the same time: if you switch
them to "vanilla" or change the Modding API there, they move the same files (Assembly-CSharp.dll
and Mods\Disabled) and uninstalling may not leave the game exactly as it was. If that happens,
Steam fixes it: Properties > Installed Files > Verify integrity.

To remove it by hand: delete the Managed\Mods\HKMultiplayer folder and restore your backup of
Managed (or in Steam: right-click Hollow Knight > Properties > Installed Files > Verify integrity
of game files).


3. HOST A WORLD (the one who invites)
-------------------------------------

  1. Main menu > Multiplayer. Type your name. The port is 26950 unless you change it.
  2. "Host a world" > "New world" (or pick one you already have) > "Create and host".
  3. Give your friends:
       - your address (IP),
       - the port (26950),
       - the game code shown on screen (like ABCD-EFGH).
     The code and the IP are also in Pause > Multiplayer, with a button to copy the code.

Which address to give:
  - Same home (same network or wifi): the IP shown in Pause > Multiplayer (usually 192.168.x.x).
  - Over the internet: your public IP. Search "what is my IP" in your browser. You also need to
    open the port (section 5).


4. JOIN A WORLD (the guest)
---------------------------

  1. Main menu > Multiplayer. Type your name and the same port as the host (26950).
  2. "Join a world": type the host's address and the game code > "Join".

Your character is saved in the host's world, on their PC. If you join that world again, you get
it back.


5. PLAYING OVER THE INTERNET: OPEN THE UDP PORT (host only)
-----------------------------------------------------------

The game uses UDP port 26950 (or the one you set in Multiplayer > Port).

  a) Windows Firewall: the first time you host, Windows may ask whether Hollow Knight can use the
     network. Tick "Private networks" and "Public networks" and click "Allow".
  b) Router: open your router settings (usually http://192.168.1.1 or http://192.168.0.1; the
     user and password are on a sticker on the router) and look for "Port forwarding", "NAT" or
     "Virtual server". Create a rule:
       - Protocol: UDP
       - External and internal port: 26950
       - Device IP: your PC's local IP (the one in Pause > Multiplayer, 192.168.x.x)
     Save the changes.

If you can't change your router settings (or your provider doesn't allow it), use a virtual LAN
program such as Radmin VPN, ZeroTier or Tailscale: the guest joins with the IP that the program
gives the host.

On the same network (same home) you don't need to open anything.


6. WHERE WORLDS ARE SAVED
-------------------------

Coop worlds are saved only on the host's PC, in:

  C:\Users\<your user>\AppData\LocalLow\Team Cherry\Hollow Knight\HKMultiplayer\Mundos

(one folder per world, up to 4). The "Host a world" page has an "Open worlds folder" button. To
back up a world or move it to another PC, copy its folder.

Coop worlds are separate from the game's 4 normal save slots, which are not touched. Guests'
characters are saved inside the host's world.


7. SKINS
--------

Compatible with CustomKnight. Put each skin (a folder with Knight.png) in
  <game folder>\hollow_knight_Data\Managed\Mods\HKMultiplayer\Skins
and pick it in Pause > Multiplayer > Skin. Other players see it even if they don't have it:
they get it automatically when they join (a large skin takes a few seconds the
first time; then it's kept in Mods\HKMultiplayer\SkinCache).


8. UNINSTALL
------------

Close the game, open "HKMultiplayer Setup.exe" (from the same package) and click "Uninstall". It
puts the game back as it was before installing: restores the original files, removes the Modding
API and the mod, and moves back the mods that were set aside and the old mod if you had it.

It doesn't delete your coop worlds, your saves or your skins.

If something is left broken, Steam fixes it: right-click Hollow Knight > Properties > Installed
Files > Verify integrity of game files.


9. TROUBLESHOOTING
------------------

  - The installer doesn't open: right-click "HKMultiplayer Setup.exe" > "Run as administrator",
    or install by hand (section 2).
  - "Multiplayer" doesn't appear in the menu: install again. If it still doesn't show up, check
    the Modding API log:
      C:\Users\<your user>\AppData\LocalLow\Team Cherry\Hollow Knight\ModLog.txt
  - The guest can't connect: check the address, the port and the code; that you both have the
    same mod version; and, over the internet, that the host opened the UDP port (section 5).
  - If Steam updates Hollow Knight to another version, the mod may stop loading until a version of
    the mod for the new one is released. To go back to the game without mods: section 8.


10. LICENSE AND WARRANTY
------------------------

HKMultiplayer is licensed under the MIT License (LICENSE.txt). It is provided as is, WITHOUT ANY
WARRANTY: use it at your own risk and back up your saves before playing. It is an unofficial fan
project, not affiliated with Team Cherry. Third-party components and their licenses are listed in
THIRD_PARTY_NOTICES.txt.
