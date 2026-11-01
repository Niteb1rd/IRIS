# IRIS

IRIS reads player nameplates in Star Citizen, looks the names up on RSI, and marks players and orgs from your lists with coloured brackets and a side panel. Borderless mode is required.

To download, click Releases >>> download IRIS installer or portable zip

## Windows warning

IRIS.exe is not code-signed, so Windows SmartScreen may show "Windows protected your PC". Click More info, then Run anyway.

## Quick start

1. Run IRIS-installer.exe, or unzip IRIS-portable.zip to an IRIS folder.
2. Run IRIS.exe. The first time Windows may ask to install the Microsoft .NET, accept.
3. Windows 11 may ask once to allow IRIS to hide the yellow capture border.
4. Set Star Citizen Window Mode to Borderless (Sorry other modes do not work).
5. Right-click the IRIS tray icon, choose Settings, and add players and orgs.
6. Press Show Test Overlay to check and resize the panel and boundary, then Save.

## Requirements

- Windows 10 version 2004 or later. Windows 11 to hide yellow video capture border
- Microsoft .NET 8
- Star Citizen in Borderless mode.

## How it works

IRIS only reads the picture of the Star Citizen window, like recording software. It never reads game memory, injects code or sends input to the game.

## Lists

- Players, Orgs and Org-Attribute tabs set who gets a bracket and which colour.
- Import and Export share lists; a Hosted Config URL lets an org share one list.
- Config\exclusions.txt holds names IRIS ignores; edit it in Notepad.

## Hotkeys

Toggle the overlay or the info panel with Ctrl, Alt and Shift combinations. IRIS uses a keyboard hook so hotkeys work while the game has focus.

## Network

- robertsspaceindustries.com: name lookups.
- Your hosted config URL, only if you set one.
- If auto-updating: api.github.com. github.com, *.githubusercontent.com

Nothing else is sent anywhere.

## Updating

With "Check for updates" on, IRIS asks at start when a new version is out and can update itself; it restarts when done.

By hand: run the new IRIS-installer.exe, or download IRIS-portable.zip and copy its IRIS.exe and \Data folder to your current folder. Both keep your configs and cache.

## Uninstall

Installed: Windows Settings > Apps > IRIS > Uninstall. This removes IRIS and all its files, including your lists and settings; export your lists first if you want to keep them.

Portable: quit IRIS and delete its folder.

## Help

Discord: https://discord.gg/tdPPEfhsWj

Logs: tray menu, Open log.

## License

GPL-3.0 (LICENSE.txt). Third-party licenses: THIRD-PARTY-NOTICES.txt.

IRIS is an unofficial Star Citizen fan project, not affiliated with the Cloud Imperium group of companies. All content not authored by its creator belongs to its respective owners. Official site: https://robertsspaceindustries.com
