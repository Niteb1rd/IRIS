# IRIS

IRIS reads player nameplates in Star Citizen, looks the names up on RSI, and marks players and orgs from your lists with coloured brackets and a side panel. Borderless mode is required.

To download click Releases on the right side of the GitHub page >>>

## Windows warning

IRIS.exe is not code-signed, so Windows SmartScreen may show "Windows protected your PC". Click More info, then Run anyway.

## Quick start

1. Run IRIS-installer.exe, or unzip IRIS-portable.zip to an IRIS folder.
2. Run IRIS.exe. The first time Windows may ask to install the Microsoft .NET Desktop Runtime, accept.
3. Set Star Citizen Window Mode to Borderless (Sorry other modes do not work).
4. In game, use Shift+F12 (default) to toggle On/Off IRIS and Shift+0 (zero, default) to toggle the Interactive Overlay.
5. To change settings, right-click the IRIS tray icon and choose Settings: Change Capture, Tracking, Overlay settings, Import/Export list configs, edit hotkeys.

## Requirements

- Windows 10 version 2004 or later. Windows 11 to hide WGC yellow video capture border
- Microsoft .NET 10 Desktop Runtime
- Star Citizen in Borderless mode.

## How it works

IRIS only reads the picture of the Star Citizen window, like recording software. It never reads game memory, injects code or sends input to the game.

Settings > Capture > Capture Method:
- DD (Desktop Duplication), the default: has better frame rates and tracking. IRIS's overlay cannot be seen in streams, recordings and screenshots.
- WGC (Windows Graphics Capture): allows "Show overlay in screenshots and recordings", so the overlay can appear in streams and recordings. It caps around 50 fps and tracks slightly worse.

For better tracking, turn off Hardware-Accelerated GPU Scheduling (Windows Settings > System > Display > Graphics, then restart the PC). Turning on Nvidia Low Latency Mode or AMD Anti-Lag also offers a benefit.

Names read best with Chromatic Aberration turned off in Star Citizen's graphics settings. IRIS shows a note in the side panel when it finds it on.

Very bright backgrounds, such as a planet in daylight, are the hardest case. IRIS remembers players it has already identified for up to 30 seconds while it can still read their distance, so a bracket that disappears comes back with the right name and colour once the plate can be followed again. New names may not be read until the plate is over something darker.

## CPU, memory and GPU readout

Off by default. Turn it on in Settings > Settings, "Show IRIS's CPU, memory and GPU use on screen". Small numbers then appear just below the top right of the screen, updated once a second. They show what IRIS itself is using, not the game. To move them, open the Interactive Overlay (Shift+0 by default) and drag the highlighted box.

## Lists

- In settings, Players, Orgs and Org-Attribute tabs set who gets a bracket and which colour.
- Orgs and Org-Attribute tabs each have a Color Rank: when several rows match a player, the higher-ranked colour wins.
- Import and Export share lists; a Hosted Config URL lets an org share one list.
- Config\exclusions.txt holds names IRIS ignores; edit it in Notepad.
- "Only check main org" (Orgs and Org-Attribute tabs) ignores a player's affiliate orgs for that tab.
- Players on no lists get the No Match colour (in Org-Attribute tab), Grey by default.

## Hotkeys

Change them in Settings > Hotkeys.

- Shift+F12: Toggle IRIS On/Off.
- Tab: Activate IRIS for 10 seconds (5-30) while the overlay is off
- Shift+0 (zero): Toggle Interactive Overlay (click players to colour them, open a dossier, write notes); Esc or its X button also closes it.

## Mining

- In scan mode, IRIS reads the unknown signal number and shows the corresponding resource (for example "Riccite 2x").
- Settings has Edit Mining Signals Data, which opens Config\mining.txt to change the look-up table; delete that file to go back to defaults.
- A signal above the table maximum shows "Multiple" or "Ore/Multiple".
- Can disable Mining feature in Settings.

## Network

- robertsspaceindustries.com: name lookups.
- Your hosted config URL, only if you set one.
- Update check (can turn off): api.github.com.
- Update download (if you accept): github.com, *.githubusercontent.com.

Nothing else is sent anywhere.

## Updating

With "Check for updates" on (the default), IRIS asks at start when a new version is out and can update itself; it restarts when done. If a new version needs a .NET Desktop Runtime this PC does not have, a message offers to download it when IRIS restarts. Accept, install the runtime from the page that opens, then start IRIS again. Your lists and settings are kept.

By hand: run the new IRIS-installer.exe, or download IRIS-portable.zip and copy its IRIS.exe and \Data folder to your current folder.

## Uninstall

Installed: Windows Settings > Apps > IRIS > Uninstall. Uninstall removes IRIS and all its files, including lists, settings and dossier notes. Copy any you want to keep first.

Portable: quit IRIS and delete its folder.

## Help

Discord: https://discord.gg/tdPPEfhsWj

Logs: tray menu, Open log.

## License

GPL-3.0 (LICENSE.txt). Third-party licenses: THIRD-PARTY-NOTICES.txt.

IRIS is an unofficial Star Citizen fan project, not affiliated with the Cloud Imperium group of companies. All content not authored by its creator belongs to its respective owners. Official site: https://robertsspaceindustries.com
