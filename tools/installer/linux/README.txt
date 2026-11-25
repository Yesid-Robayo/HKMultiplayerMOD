HKMultiplayer @VERSION@ for Linux (EXPERIMENTAL) - co-op mod for Hollow Knight
=============================================================================

One player hosts a coop world and their friends join with the host's address and the game code
(or through Steam): you share the world, see each other in real time and fight the same enemies
and bosses together.

THIS LINUX VERSION IS EXPERIMENTAL: it is ready but nobody has tested it yet. If you try it,
please tell us whether it works (section 3).

You need:
  - Hollow Knight 1.5.12620, the native Linux version from Steam (the current version).
    If you play with Proton (Steam Play running the Windows version), this package is NOT for
    you: use the Windows package and install it by hand as its README.txt explains.
  - Every player must have the SAME version of HKMultiplayer (@VERSION@). Players on Linux,
    Windows and macOS can play together as long as the versions match.

(Instrucciones en español: LEEME.txt)


1. INSTALL
----------

  1. Close Hollow Knight.
  2. Extract the zip, open a terminal in the extracted folder and run:  bash install.sh
     If your game isn't in the usual Steam folder (~/.local/share/Steam), pass it:
       bash install.sh "/path/to/Hollow Knight"
     (In Steam: right-click Hollow Knight > Manage > Browse local files.)
  3. Open Hollow Knight from Steam: "Multiplayer" appears in the main menu.

The installer is a short script you can read before running it. It saves a copy of every game
file it changes in "<game>/HKMultiplayer-backup".

To update to a new version, install the new package on top.


2. UNINSTALL
------------

  bash install.sh --uninstall ["/path/to/Hollow Knight"]

It puts back the game's files from the backup. Steam's "Verify integrity of game files" also
leaves the game as it was. Your coop worlds and saves are not touched.


3. IF SOMETHING FAILS
---------------------

Please open an issue at https://github.com/Yesid-Robayo/HKMultiplayerMOD/issues and attach the
ModLog.txt, which is in ~/.config/unity3d/Team Cherry/Hollow Knight/ModLog.txt
(copy it right after the problem, before opening the game again: it is reset on every start).
You can also use "Report a problem" in the Multiplayer menu.
