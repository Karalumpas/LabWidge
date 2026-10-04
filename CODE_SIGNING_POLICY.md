# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

> Status: an application to the SignPath Foundation has not yet been submitted. Existing releases through 1.18.0 are unsigned.
> The release pipeline requires signing before publishing a new version. Activation depends on Foundation approval and account configuration.
> See [the setup guide](docs/CODE_SIGNING.md).

## What is signed

Only binaries built from the source code in this repository are signed:

- `LabWidge-Setup-<version>.exe` – the installer
- `LabWidge.exe` and `LabWidge.dll` – the application

Third-party libraries shipped with the application (Microsoft WebView2 SDK, the Windows SDK projection and the WinRT runtime)
are included unchanged. Existing upstream signatures are preserved; these libraries are not signed with the project's certificate.

Every release is built by GitHub Actions from the `main` branch (`.github/workflows/release.yml`) and published at
[Karalumpas/labwidge-releases](https://github.com/Karalumpas/labwidge-releases/releases). Nothing built on a
developer machine is signed.

The application binaries are signed and verified first, then embedded unchanged in Setup. Setup is signed and verified separately.
Missing configuration, rejected signing requests, missing timestamps or invalid signatures stop publication. Each request requires approval
by the project's SignPath approver. Local builds from `install.ps1` remain unsigned development builds.

## Team roles

| Role | Members |
|---|---|
| Authors (commit to the repository) | [Karalumpas](https://github.com/Karalumpas) |
| Reviewers (review changes before merge) | [Karalumpas](https://github.com/Karalumpas) |
| Approvers (approve each signing request) | [Karalumpas](https://github.com/Karalumpas) |

All team members are required to use multi-factor authentication for GitHub and SignPath before production signing is activated.

## Privacy

See [PRIVACY.md](PRIVACY.md). In short: the program only contacts the services listed there, and only to fetch the
data it shows or to perform actions the user has configured. It does not collect or send usage data or personal data.
