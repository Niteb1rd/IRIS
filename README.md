# IRIS

IRIS reads player nameplates in Star Citizen, looks the names up on RSI, and marks players and orgs from your lists with coloured brackets and a side panel. Borderless mode is required.

To download click Releases on the right side of the GitHub page >>>

## Windows warning

IRIS.exe is not code-signed, so Windows SmartScreen may show "Windows protected your PC". Click More info, then Run anyway.

## Quick start

1. Run IRIS-installer.exe, or unzip IRIS-portable.zip to an IRIS folder.
2. Run IRIS.exe. The first time Windows may ask to install the Microsoft .NET, accept.
3. Set Star Citizen Window Mode to Borderless (Sorry other modes do not work).
4. Right-click the IRIS tray icon, choose Settings, and add players and orgs.
5. Press Show Boundary and Show Info Panel to check and resize the boundary and the panel, then Save.

## Requirements

- Windows 10 version 2004 or later. Windows 11 to hide WGC yellow video capture border
- Microsoft .NET 8
- Star Citizen in Borderless mode.

## How it works

IRIS only reads the picture of the Star Citizen window, like recording software. It never reads game memory, injects code or sends input to the game.

Settings > Capture > Capture Method:
- DD (Desktop Duplication), the default: has better frame rates and tracking. IRIS's overlay cannot be seen in streams, recordings and screenshots.
- WGC (Windows Graphics Capture): allows "Show overlay in screenshots and recordings", so the overlay can appear in streams and recordings. It caps around 50 fps and tracks slightly worse.

For better tracking, turn off Hardware-Accelerated GPU Scheduling (Windows Settings > System > Display > Graphics, then restart the PC). Turning on Nvidia Low Latency Mode or AMD Anti-Lag also offers a benefit.

## Lists

- In settings, Players, Orgs and Org-Attribute tabs set who gets a bracket and which colour.
- Import and Export share lists; a Hosted Config URL lets an org share one list.
- Config\exclusions.txt holds names IRIS ignores; edit it in Notepad.
- "Only check main org" (Orgs and Org-Attribute tabs) ignores a player's affiliate orgs for that tab.
- Players on no lists get the No Match colour (in Org-Attribute tab), Grey by default.

## Hotkeys

Change them in Settings > Hotkeys.

- Shift+F11: Toggle Overlay.
- Shift+F12: Toggle Info Panel.
- Tab: Activate Overlay for 10 seconds (5-30) while the overlay is off

## Mining

- In scan mode, IRIS reads the unknown signal number and shows the corresponding resource (for example "Riccite 2x").
- Settings has Edit Mining Signals Data, which opens Config\mining.txt to change the look-up table; delete that file to go back to defaults.
- A signal above the table maximum shows "Multiple" or "Ore/Multiple".
- Can disable Mining feature in Settings.
- The resource text requires the info panel to be on.

## Network

- robertsspaceindustries.com: name lookups.
- Your hosted config URL, only if you set one.
- Update check (can turn off): api.github.com.
- Update download (if you accept): github.com, *.githubusercontent.com.

Nothing else is sent anywhere.

## Updating

With "Check for updates" on (the default), IRIS asks at start when a new version is out and can update itself; it restarts when done.

By hand: run the new IRIS-installer.exe, or download IRIS-portable.zip and copy its IRIS.exe and \Data folder to your current folder.

## Uninstall

Installed: Windows Settings > Apps > IRIS > Uninstall. This removes IRIS and all its files, including your lists and settings; export your lists first if you want to keep them.

Portable: quit IRIS and delete its folder.

## Help

Discord: https://discord.gg/tdPPEfhsWj

Logs: tray menu, Open log.

## License

GPL-3.0 (LICENSE.txt). Third-party licenses: THIRD-PARTY-NOTICES.txt.

IRIS is an unofficial Star Citizen fan project, not affiliated with the Cloud Imperium group of companies. All content not authored by its creator belongs to its respective owners. Official site: https://robertsspaceindustries.com
