# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

> Status: the application to the SignPath Foundation is pending. Until it is approved, releases are not signed.

## What is signed

Only binaries built from the source code in this repository are signed:

- `LabWidge-Setup-<version>.exe` – the installer
- `LabWidge.exe` and `LabWidge.dll` – the application

Third-party libraries shipped with the application (Microsoft WebView2 SDK, the Windows SDK projection and the WinRT runtime)
are included unchanged, with their original signatures.

Every release is built by GitHub Actions from the `main` branch (`.github/workflows/release.yml`) and published at
[Karalumpas/labwidge-releases](https://github.com/Karalumpas/labwidge-releases/releases). Nothing built on a
developer machine is signed.

## Team roles

| Role | Members |
|---|---|
| Authors (commit to the repository) | [Karalumpas](https://github.com/Karalumpas) |
| Reviewers (review changes before merge) | [Karalumpas](https://github.com/Karalumpas) |
| Approvers (approve each signing request) | [Karalumpas](https://github.com/Karalumpas) |

All team members use multi-factor authentication for GitHub and SignPath.

## Privacy

See [PRIVACY.md](PRIVACY.md). In short: the program only contacts the services listed there, and only to fetch the
data it shows or to perform actions the user has configured. It does not collect or send usage data or personal data.
