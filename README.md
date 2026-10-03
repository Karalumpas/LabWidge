# LabWidge

A Windows tray app with a desktop widget that brings together the things a home-lab owner in Denmark keeps an eye on:

- **Electricity price** – the current quarter-hour price from Energi Data Service (DK1/DK2), a chart for today/tomorrow and the cheapest 3 hours.
  The total price includes the grid tariff (fetched automatically from Datahub for your grid company), Energinet's tariffs, electricity tax, your supplier's add-on and VAT.
- **Price alerts** – a notification a set number of minutes before the cheapest 3 hours start, and before power gets expensive.
- **System** – CPU (with a chart), RAM, graphics card and fixed disks. Click a drive to open it. The graphics card shows load and VRAM; NVIDIA cards also show temperature, power draw and fan speed.
- **Network** – external IP, local IP per adapter, gateway, DNS, ping and up/down traffic. Click an address to copy it.
- **Audio** – one button per audio output; a click makes it the Windows default (playback and calls). Shows the volume (scroll to adjust),
  a microphone mute button, and the battery of supported wireless headsets (Corsair HS80 and others using the same protocol).
- **Home Assistant (optional)** – selected lights, switches and sensors, or a full dashboard in a panel.
- **Cloudflare (optional)** – updates A records when the external IP changes, and shows whether your Cloudflare Tunnels and the services behind them are up.
- **Proxmox (optional)** – CPU, RAM and storage of your Proxmox VE server, and start, shut down or reboot VMs and containers.

The tray icon's bolt changes colour with the electricity price (green/yellow/red), and its tooltip shows the price, the next cheap period and the IP.

LabWidge is available in **English and Danish**. The installer asks which language to use, and it can be changed later under
**Settings → Widget → Language**. LabWidge was called IpTrayWidget before version 1.11; existing installations move their settings over automatically.

## Usage
- **Left-click** the tray icon to show or hide the widget. **Right-click** opens the menu.
- **Click a header or its arrow** to collapse the section (a summary is shown in the header).
- **Drag the handle by a header** to change the section order. Moving happens within the same area: top, middle or bottom.
- **Right-click a header** to pin the section to the top or bottom, or to unpin it. Only the middle scrolls;
  the pinned areas cover the content beneath them, and hidden buttons cannot be clicked.
- **Pin the summary only** gives a compact pinned section; click to expand it temporarily and click again to return to the summary.
  If pinned sections take up too much room, they are shown compactly for a while so the middle still has space. Your choices are kept.
- **Drag an edge or corner** to change the widget's width and height. The size is remembered; automatic size can be restored
  in the section menu or under Settings → Widget, where all sections can also be unpinned at once.
- Each section's status distinguishes **Updated**, **Outdated data**, **Fetching** and **No connection**.
  Hover over the header for the time and the error description; the last known electricity price and IP can still be shown with a warning.
- **Double-click** the widget for a compact one-line view – click to expand again.
- Drag the widget to move it.
- **Settings...** gathers electricity price, audio, notifications, widget (language, theme, opacity, sections, startup), Home Assistant, Cloudflare and Proxmox.

## Home Assistant
The widget can show and control up to 20 entities through Home Assistant's REST API.

1. Create a token in Home Assistant under **your profile → Security → Long-lived access tokens**.
2. Enter the address (e.g. `http://192.168.0.10:8123`) and the token under **Settings → Home Assistant**.
3. Click **Load entities** and tick the ones the widget should show.

In the widget, Home Assistant takes up a single line with a summary (e.g. "5 on"). Click the line to open the panel,
so the widget does not grow with the number of entities.

The panel can show two things, controlled by the **Dashboard** field in the settings:

- **A real Home Assistant dashboard** – type its path, e.g. `/test-panel` or `/lovelace/0`. The dashboard is embedded
  with Microsoft Edge WebView2. You sign in to the panel the first time; the session is stored in
  `%LOCALAPPDATA%\LabWidgeData\webview`, so it is remembered afterwards. The window can be moved and resized, and its size is remembered.
- **The list of switches** (empty field) – lights, switches, fans and `input_boolean` get a switch that can be
  clicked; the rest is shown as readings. The panel closes when you click outside it, press Esc or click the line again.

After a click on a switch the state is read back from Home Assistant at once. The answer to a service call
does not always contain the new state – especially not for lights with a transition time – and without reading it back
the row would stay unchanged until the next refresh. Otherwise the state is fetched every 15 seconds while the widget or
the panel is showing.

The dashboard view requires **Microsoft Edge WebView2 Runtime**, which ships with Windows 11 and with Edge on Windows 10.
If it is missing, the panel says so and the list can be used instead. The token is stored in Windows Credential Manager,
and only the entities you chose are fetched – not all of `/api/states`.

## Updates
The app looks for new versions on GitHub Releases – at start, once a day and when the PC wakes from sleep
(if at least 6 hours have passed since the last check). When a newer version exists, it is
downloaded in the background and installed when the mouse and keyboard have not been touched for 10 minutes (this can be turned off –
then a notification is shown instead, and nothing is downloaded until you agree). The installer is only downloaded from `github.com`, and its
SHA-256 checksum is verified against the `digest` GitHub states for the file. It then runs with `--silent`: the app closes,
the files are swapped, and the app starts again with its settings kept. The first time the new version starts, a
notification shows what's new – the text comes from `CHANGELOG.md`, which is built into the app.

The check can be turned off under **Settings → Widget → Startup and updates**, and you can always check manually with
**Check for updates...** in the menu by the clock.

Releases live in [Karalumpas/labwidge-releases](https://github.com/Karalumpas/labwidge-releases/releases), which only contains the installer.
The app checks that repository without signing in.

### Publishing a new version
With Claude Code: type `/release` (skill in `.claude/skills/release`), and the steps below are done for you. Manually:

1. Raise `<Version>` in **both** `LabWidge/LabWidge.csproj` (also `AssemblyVersion`/`FileVersion`) and `Setup/LabWidge.Setup.csproj`.
2. Describe what's new under a `## <version>` heading at the top of `CHANGELOG.md`. The text is shown in the update dialog.
3. Open a pull request – `.github/workflows/build.yml` builds it, runs the tests and checks the versions. Merge to `main`.
   `.github/workflows/release.yml` builds Setup on Windows and publishes `v<version>` in the releases repository. If the version already exists, nothing happens.

The workflow needs the secret `RELEASES_TOKEN` in this repository: a fine-grained personal access token with access to
only `Karalumpas/labwidge-releases` and the permission **Contents: Read and write**.

## Installation
Run **`LabWidge-Setup.exe`** (about 7 MB). The installer:

1. Asks which language to use (English or Danish), preselecting the Windows display language or the one already in use.
2. Checks whether Microsoft .NET 8 Desktop Runtime (or newer) is installed.
3. If it is missing, asks for permission, downloads it from Microsoft (about 56 MB), verifies that the file is signed by Microsoft
   and installs it (Windows asks for administrator rights).
4. Installs the app for the current user in `%LOCALAPPDATA%\LabWidge` and registers it under Settings → Apps,
   where it can also be uninstalled. An older version is closed and updated; settings are kept.

The installer itself runs on .NET Framework 4.8, which is part of Windows 10/11.
The app is framework-dependent (no bundled runtime) and also runs on newer .NET versions (`RollForward=Major`).

Build `dist\LabWidge-Setup.exe` and start the installation:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Build only (e.g. to share Setup): add `-BuildOnly`.

The old installation is kept as a backup until the whole new program folder is in place.
On an error the old version is restored; after an interrupted installation Setup can restore it on the next run.
Setup uses a temporary working directory, so it can also update older widgets that start it from the program folder.
Uninstalling waits for the widget process to exit before the program files are deleted.

The files are not code-signed yet (see [Code signing](#code-signing)), so SmartScreen may show "Windows protected your PC" –
choose "More info" → "Run anyway".
The app is deliberately shipped as a small exe with DLLs next to it: Smart App Control blocks an unsigned single-file exe.

The first time a user starts the app, a **setup guide** is shown:
postal code → price area, grid company → the tariff is found automatically, the supplier's add-on, notifications,
Cloudflare (can be skipped) and the widget's appearance. The guide can be run again from the menu or with `LabWidge.exe --setup`.

## Build & run (development)

```powershell
cd LabWidge
dotnet build
dotnet run
```

Regression tests run from the repository root with `dotnet run --project Tests/LabWidge.Tests.csproj`, and UI integration
tests (which also render preview images to `dist\ui-preview`) with `dotnet run --project Tests/Ui/LabWidge.UiTests.csproj`.
They use simulated network answers and temporary folders and do not change your installation, DNS or settings.
GitHub also runs them on pull requests together with the build of the app and Setup.

The icon (`Assets\app.ico`) is drawn by the app itself and can be recreated with `LabWidge.exe --write-icon Assets\app.ico`.

### Translations
Every user-facing text is written as `L.T("English", "Danish")` where it is used (see `LabWidge/Localization.cs`),
so both languages sit side by side and a missing translation cannot happen. Logs and code comments are in English.

## Data and settings
- Settings: `%APPDATA%\LabWidge\settings.json`. The file is written atomically – it is written
  to `settings.json.tmp`, which is swapped in, while the previous version is kept as `settings.json.bak` and used automatically
  if the main file is damaged. Cloudflare, Home Assistant and Proxmox tokens are kept in Windows Credential Manager.
- Spot prices: `DayAheadPrices`. Tariffs: `DatahubPricelist` – the household tariff is found from the grid company's "Nettarif C" including any discounts.
- Price area from the postal code: 1000–4999 = DK2, the rest = DK1.

## Notes
- The app updates all A records in the zone to the current IP, or only the chosen hosts. When "all" is turned off,
  an empty host list means no records are changed. CNAMEs are not changed.
- Failed DNS updates are retried at the next IP check (normally after 5 minutes), also when the IP address has not changed.

## Code signing
Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/)
(the application is pending). Only files built by GitHub Actions from this source code are signed – see
[CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md).

## Privacy
The app sends no usage data or personal data to the author or anyone else. Which services it contacts and
why is described in [PRIVACY.md](PRIVACY.md).

## License
[MIT](LICENSE) © 2026 Karalumpas
