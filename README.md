# IRIS

IRIS reads player nameplates in Star Citizen, looks the names up on RSI, and marks players and orgs from your lists with coloured brackets and a side panel.

To download, click Releases >>> download IRIS-1.0.0.zip >>> Unzip and run IRIS.exe

## Windows warning

IRIS.exe is not code-signed, so Windows SmartScreen may show "Windows protected your PC". Click More info, then Run anyway. Antivirus tools may also flag the hotkey keyboard hook. To check your download, compare it with the SHA-256 and VirusTotal link on the release page.

## Quick start

1. Unzip to IRIS folder.
2. Run IRIS.exe. If Windows asks to install the .NET 8 Desktop Runtime, accept.
3. Check Star Citizen is set to Borderless (Sorry other modes do not work).
4. Right-click the IRIS tray icon, choose Settings, and add players and orgs.
5. Press Show Test Overlay to check the panel and boundary, then Save.

## Requirements

- Windows 10 version 2004 or later. Windows 11 is needed to hide the yellow capture border.
- .NET 8 Desktop Runtime (Windows offers it on first start).
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
- api.github.com: update check, only if turned on (off by default).

Nothing else is sent anywhere.

## Updating

Download the new zip. Copy new IRIS.exe and \Data folder to your current folder. This keeps your configs and cache.

## Help

Discord: https://discord.gg/tdPPEfhsWj

Logs: tray menu, Open log.

## License

GPL-3.0 (LICENSE.txt). Third-party licenses: THIRD-PARTY-NOTICES.txt.

IRIS is an unofficial Star Citizen fan project, not affiliated with the Cloud Imperium group of companies. All content not authored by its creator belongs to its respective owners. Official site: https://robertsspaceindustries.com
