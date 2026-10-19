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

## Moving from the GitHub version

Install the Store version, then uninstall the GitHub version under Settings → Apps → LabWidge and answer **No** when asked
whether to delete your settings. While both are installed, the GitHub version starts at sign-in first and the Store version
stops, because only one LabWidge runs at a time.
