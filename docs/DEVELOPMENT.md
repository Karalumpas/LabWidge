# Developing LabWidge

LabWidge is a .NET 8 WinForms app (`LabWidge/`) with a .NET Framework 4.8 installer (`Setup/`), regression and UI tests
(`Tests/`) and a small Cloudflare Worker for ENTSO-E prices (`worker/`). Everything is drawn by hand with GDI+ – there are no
UI frameworks beyond WinForms and WebView2 (for Home Assistant dashboards).

## Build and run

```powershell
cd LabWidge
dotnet build
dotnet run
```

Build the installer `dist\LabWidge-Setup.exe` and start it (add `-BuildOnly` to only build it):

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

The app is framework-dependent (no bundled runtime) and also runs on newer .NET versions (`RollForward=Major`). It is shipped
as a small exe with DLLs next to it on purpose: Smart App Control blocks an unsigned single-file exe.
The icon (`Assets\app.ico`) is drawn by the app itself and can be recreated with `LabWidge.exe --write-icon Assets\app.ico`.

## Tests

| What | Command |
|---|---|
| Regression tests (prices, DNS, installer, layout) | `dotnet run --project Tests/LabWidge.Tests.csproj` |
| UI integration tests, previews and README screenshots | `dotnet run --project Tests/Ui/LabWidge.UiTests.csproj` |
| Every electricity price area against the real sources | `dotnet Tests/bin/Debug/<tfm>/LabWidge.Tests.dll --live` |
| The price Worker | `cd worker && npm test` |

The tests use simulated network answers and temporary folders and do not change your installation, DNS or settings.
GitHub runs them on every pull request (`.github/workflows/build.yml`) together with the build of the app and Setup, and
uploads the rendered previews as the `widget-ui-preview` artifact.

The screenshots in `docs/images` come from `dist/ui-preview/showcase`, rendered by the UI tests with made-up data – copy them
over when the look changes. If Windows Smart App Control blocks a freshly built test exe locally, run its DLL with `dotnet <dll>`.

## Electricity prices

- Countries, price areas, units and VAT: `LabWidge/Countries.cs` (shared with the installer, so it stays C# that builds for .NET Framework 4.8).
- Sources and their parsers: `LabWidge/SpotPriceSources.cs`. Each parser is pure and covered by a regression test with a sample answer.
- Danish tariffs: `DatahubPricelist` – the household tariff is found from the grid company's "Nettarif C" including any discounts.
  Price area from the postal code: 1000–4999 = DK2, the rest = DK1.
- Countries without a free keyless source will use the Worker in [`worker/`](../worker/README.md), which serves ENTSO-E prices with
  one shared token and a cache.

## Translations

Every user-facing text is written as `L.T("English", "Danish")` where it is used (see `LabWidge/Localization.cs`), so both
languages sit side by side and a missing translation cannot happen. Logs and code comments are in English.

## Settings and data

The widget sections are bundled internal plugins. See [PLUGINS.md](PLUGINS.md) for registration, views, settings and lifecycle.

- Settings: `%APPDATA%\LabWidge\settings.json`, written atomically via `settings.json.tmp`; the previous version is kept as
  `settings.json.bak` and used automatically if the main file is damaged.
- Tokens (Cloudflare, Home Assistant, Proxmox) are kept in Windows Credential Manager.
- The Home Assistant dashboard session: `%LOCALAPPDATA%\LabWidgeData\webview`.

## Installer

The installer asks for language and country, installs the .NET 8 Desktop Runtime from Microsoft if it is missing (the file's
Microsoft signature is verified), and installs the app for the current user in `%LOCALAPPDATA%\LabWidge`. The old installation is
kept as a backup until the whole new program folder is in place; on an error it is restored, and after an interrupted installation
Setup restores it on the next run. Setup uses a temporary working directory, so it can update widgets that start it from the
program folder. Uninstalling waits for the widget process to exit before the program files are deleted.

## Updates

The app checks [Karalumpas/labwidge-releases](https://github.com/Karalumpas/labwidge-releases/releases) without signing in – at
start, once a day and when the PC wakes from sleep (if 6 hours have passed). A newer installer is downloaded only from `github.com`,
its SHA-256 is checked against the `digest` GitHub states for the file, and it runs with `--silent` when the mouse and keyboard
have been idle for 10 minutes. The first start of a new version shows what's new from `CHANGELOG.md`, which is built into the app.

## Publishing a new version

With Claude Code: type `/release` (skill in `.claude/skills/release`). Manually:

1. Raise `<Version>` in **both** `LabWidge/LabWidge.csproj` (also `AssemblyVersion`/`FileVersion`) and `Setup/LabWidge.Setup.csproj`.
2. Describe what's new under a `## <version>` heading at the top of `CHANGELOG.md`. The text is shown in the update dialog.
3. Open a pull request – the build workflow builds it, runs the tests and checks the versions. Merge to `main`.
   `.github/workflows/release.yml` builds Setup and publishes `v<version>` in the releases repository. If the version already exists, nothing happens.

The release workflow needs the secret `RELEASES_TOKEN`: a fine-grained personal access token with access to only
`Karalumpas/labwidge-releases` and the permission **Contents: Read and write**.

Public releases also require the free SignPath signing setup in [CODE_SIGNING.md](CODE_SIGNING.md).
The workflow signs the app before packaging it, then signs Setup, and refuses to publish unsigned files.
