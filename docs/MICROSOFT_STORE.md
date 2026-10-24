# Microsoft Store

LabWidge's GitHub releases are unsigned, and Windows Smart App Control can block them. The Microsoft Store signs
an MSIX package after certification, so the Store version runs wherever the Store does. A Store developer account is free
for individuals.

## What differs in the Store version

The app finds out by itself that it runs from a package (`AppPackage.IsPackaged`) and then:

- **does not update itself.** The Store installs new versions. "Check for updates" and the update settings are hidden.
- **starts at sign-in through the package's startup task** instead of the Run key. If it is turned off under
  Task Manager → Startup apps, Windows does not let the app turn it back on.
- **gets its toast name and logo from the package** instead of a Start menu shortcut.
- **keeps new files under %APPDATA% in the package's own folder** (Windows redirects them). Files an earlier GitHub
  installation left in the ordinary folder are still read, and changed in place, so the settings come along. Under About,
  "Open the folder with settings and the log" opens the package's folder, and a second link the ordinary one while it still has files.
- **includes .NET**, so the package is about 70 MB.

## One-time setup

1. Create the developer account at <https://storedeveloper.microsoft.com> (personal Microsoft account, ID and selfie).
2. In [Partner Center](https://partner.microsoft.com/dashboard/apps-and-games/overview), choose **New product → MSIX or PWA app** and
   reserve the name (e.g. *LabWidge*, or a variant if it is taken).
3. Copy the reserved name and, under **Product management → Product identity**, three more values to repository variables in `Karalumpas/LabWidge`
   (Settings → Secrets and variables → Actions → Variables):

   | Variable | Partner Center field | Example |
   |---|---|---|
   | `STORE_DISPLAY_NAME` | the reserved name | `LabWidge` |
   | `STORE_IDENTITY_NAME` | Package/Identity/Name | `12345Karalumpas.LabWidge` |
   | `STORE_PUBLISHER` | Package/Identity/Publisher | `CN=01234567-89AB-…` |
   | `STORE_PUBLISHER_DISPLAY_NAME` | Package/Properties/PublisherDisplayName | `Karalumpas` |

4. Run the **Release** workflow by hand (Actions → Release → Run workflow). The job *store-package* builds the package and attaches
   the artifact `labwidge-store-package` with `LabWidge-<version>.msix` and a 300 px logo for the listing.

## A submission

In Partner Center, **Start submission** and fill in:

- **Pricing and availability**: free. Under *Discoverability*, choose to make it available only by direct link, or a
  *Private audience* with the Microsoft accounts of the people who should have it.
- **Properties**: category *Utilities & tools*. Privacy policy:
  `https://github.com/Karalumpas/LabWidge/blob/main/PRIVACY.md`.
- **Age ratings**: the questionnaire (no user content, no purchases).
- **Packages**: upload `LabWidge-<version>.msix` from the workflow artifact.
- **Store listings** (English and Danish): description, the logo and at least one screenshot (the PR build's
  `widget-ui-preview` artifact has some).
- **Submission options → restricted capabilities**: `runFullTrust` needs a reason, e.g. *"LabWidge is a desktop tray app
  (WinForms) that reads system information and controls the user's own Home Assistant and Proxmox."*

Certification usually takes from a few hours to three working days. Each new version needs a new submission with the new
package: run the Release workflow (it does that by itself when the version changes) and upload the artifact.

The Store only accepts a package with a higher version than the one live, and the Store and GitHub share version numbers.

The Claude Code skill `.claude/skills/store-submit` does a submission in Partner Center through the user's Chrome: it uploads the
workflow's MSIX (via a small local server, since the browser tool cannot upload files over 10 MB), writes "What's new" in Danish
and English and sends it to certification. The [Microsoft Store Developer CLI](https://learn.microsoft.com/en-us/windows/apps/publish/msstore-dev-cli/commands)
would do it from the command line, but it needs a Microsoft Entra app, which an individual developer account cannot add.

## Moving from the GitHub version

Install the Store version, then uninstall the GitHub version under Settings → Apps → LabWidge and answer **No** when asked
whether to delete your settings. While both are installed, the GitHub version starts at sign-in first and the Store version
stops, because only one LabWidge runs at a time.

## Update strategy (October 2026)

Keep the current two distribution channels and build both from the same versioned source:

| Installation | Update delivery | Release timing |
|---|---|---|
| Microsoft Store MSIX | Microsoft Store | Submit the new MSIX in Partner Center; certification is required before delivery. |
| GitHub EXE | Existing in-app GitHub updater | Available after the GitHub release pipeline finishes; no Store certification. |

Use GitHub for promptly delivering tested fixes. Submit stable Store updates regularly, combining small fixes when useful;
submit critical fixes immediately. Keep version numbers increasing across both channels and never replace an already
published installer at the same version. The existing updater reads GitHub's latest regular release, not prereleases;
an opt-in beta channel would need separate implementation.

Release procedure:

1. Run the regression and UI integration checks, then check the change in a local running app.
2. Increment both app and Setup versions and add the corresponding changelog heading. Check the signing configuration
   described in CODE_SIGNING.md before distributing an EXE; the workflow supports both signed and unsigned releases.
3. Merge the tested release: the existing workflow publishes the GitHub installer and, when Store identity variables are
   configured, builds the `labwidge-store-package` artifact.
4. Upload that MSIX as an update to the existing Partner Center product and submit it for certification.
5. Verify each channel separately after publication. Do not run the EXE updater inside the MSIX installation.

This does not bypass Store approval for existing MSIX users. Remote price data and service responses can change without
replacing the app; the native hover rendering code is part of the installed binary and needs an app update.

An alternative is a Store listing distributed as an EXE/MSI: Microsoft explicitly allows those apps to update existing
users through their own installer. That requires a CA-trusted signing certificate, a suitable Store submission, and a
planned migration for existing MSIX users. It is not a switch to turn on in the current MSIX package. Keep MSIX for now;
reconsider EXE/MSI distribution if immediate updates for all users become a firm requirement.

Microsoft references:
- [MSIX updates](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/publish-update-to-your-app-on-store)
- [EXE/MSI in-app updates](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/publish-update-to-your-app-on-store)
- [Store policies, including signing requirements in 10.2.9](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies)
