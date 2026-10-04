# Free Windows code signing

## Current status

The maintainer has not submitted a SignPath Foundation application yet. There is no SignPath account configuration or API token
in GitHub. Releases through 1.18.0 are unsigned. The integration is ready for configuration, but no trusted signature can be
issued until the Foundation approves the project. No paid subscription or certificate purchase is required for an approved OSS project.

## Apply

Use the [SignPath Foundation application](https://signpath.org/apply). The maintainer must supply their contact details and
confirm the project's ownership and eligibility. Do not paste passwords or API tokens into issues or chat.

Project information for the application:

| Field | Value |
|---|---|
| Project | LabWidge |
| Repository | https://github.com/Karalumpas/LabWidge |
| License | MIT |
| Downloads | https://github.com/Karalumpas/labwidge-releases/releases |
| Code signing policy | https://github.com/Karalumpas/LabWidge/blob/main/CODE_SIGNING_POLICY.md |
| Privacy policy | https://github.com/Karalumpas/LabWidge/blob/main/PRIVACY.md |
| Maintainer, reviewer and signing approver | Karalumpas |
| Build platform | GitHub Actions, GitHub-hosted Windows runners |
| Files to sign | LabWidge.exe, LabWidge.dll, LabWidge-Setup.exe |

Description: LabWidge is a free MIT-licensed Windows desktop widget displaying electricity prices, PC and network statistics,
audio controls, and user-configured Home Assistant, Proxmox and Cloudflare services. Users can activate and configure its bundled plugins.

Read the [Foundation conditions](https://signpath.org/terms). They require an eligible open source project, MFA for GitHub and SignPath,
an approver for each signing request, and enforceable build provenance and product metadata. Acceptance is at the Foundation's discretion.
Future proprietary plugins, commercial dual licensing or a paid edition require a fresh eligibility assessment before using this service.

## Configure after approval

1. Enable MFA and set up the Foundation-provided production certificate in SignPath. Do not use a self-signed test certificate for releases.
2. Create the project for `https://github.com/Karalumpas/LabWidge` and configure GitHub build provenance. Restrict production signing to `main`
   and GitHub-hosted builds. Use a production signing policy with manual approval by Karalumpas. Grant the CI account only submission rights.
3. Add two artifact configurations under the project: slug **app** from [../.signpath/app.xml](../.signpath/app.xml), and slug **setup** from
   [../.signpath/setup.xml](../.signpath/setup.xml). Both use the mandatory `version` parameter. Only the three exact project filenames are signed;
   dependency libraries remain unchanged. The root is ZIP because GitHub's artifact uploader wraps the files in ZIP.
4. Under [GitHub Actions settings](https://github.com/Karalumpas/LabWidge/settings/secrets/actions), add the CI API token as the secret
   **SIGNPATH_API_TOKEN**. Add repository variables **SIGNPATH_ORGANIZATION_ID**, **SIGNPATH_PROJECT_SLUG** and
   **SIGNPATH_SIGNING_POLICY_SLUG** using the actual values from SignPath. The existing **RELEASES_TOKEN** stays in place.
5. After merging the integration, create a new patch release (at least **1.18.1**) through the normal reviewed version-bump process.
   1.18.0 already exists and will be skipped; do not replace it. Approve the app request, then the installer request, in SignPath.
   Each action waits up to two hours; if this expires, rerun the failed workflow after checking the signing request's status.

## Build and verification

The release workflow publishes the app, uploads its complete folder, and requests signing of its EXE and DLL. It verifies trusted
Authenticode signatures, timestamps and matching LabWidge product/version metadata. `Build-SetupFromSignedApp.ps1` embeds that signed
folder without rebuilding the app. Setup is then uploaded, signed, and verified before publishing. There is no unsigned fallback.

The scripts cannot test a real Foundation signature until account approval and setup are complete. A successful pull-request build
validates ordinary builds and regression tests, not the external signing service. The first signed release must also be installed and
tested on Windows with Smart App Control enabled. Signing does not promise immediate SmartScreen reputation or bypass an organization's policies.

The normal `install.ps1 -BuildOnly` produces unsigned development installers. Do not upload these as public releases.

References: [GitHub integration](https://docs.signpath.io/trusted-build-systems/github),
[artifact configuration](https://docs.signpath.io/artifact-configuration/), [Foundation conditions](https://signpath.org/terms).
