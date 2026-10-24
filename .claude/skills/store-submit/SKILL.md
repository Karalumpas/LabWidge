---
name: store-submit
description: Upload a released LabWidge version to the Microsoft Store and submit it for certification with the Microsoft Store Developer CLI (msstore). Downloads the labwidge-store-package artifact from the Release workflow, updates the "What's new" text, submits and follows the certification status. Use it after /release has published a version, or when the user asks to upload, submit or send the update to the Microsoft Store / Partner Center ("upload til Store", "send til Microsoft Store").
---

# Submit a version to the Microsoft Store

The Release workflow (`.github/workflows/release.yml`) builds the Store package in the job `store-package` and attaches it as the
artifact `labwidge-store-package` (`LabWidge-<version>.msix` and `StoreListingLogo-300.png`). This skill uploads that MSIX to the
existing product in Partner Center and submits it for certification. Microsoft certifies it (usually hours, up to three working
days), and only then do Store users get the update.

Talk to the user in their own language. Release notes sent to the Store are written in English (`en-us`) and Danish (`da-dk`).

**Ask as little as possible.** When the user has approved the release (the merge question in the release skill) or asked for the
Store upload, run the whole flow without further questions. Ask only when something below says so.

## 0. Prerequisites – check, never work around

```powershell
Get-Command msstore -ErrorAction SilentlyContinue      # the CLI is installed
msstore info                                            # configured: shows tenant, seller and client id (never the secret)
gh variable get STORE_PRODUCT_ID --repo Karalumpas/LabWidge   # the Store product id, e.g. 9NXXXXXXXXXX
```

If any is missing, stop and give the user the one-time setup below. **Never** ask for, read, type or store the client secret
yourself: the user runs `msstore reconfigure` in their own terminal, and the CLI keeps the secret in Windows Credential Manager.
The product id is not a secret; when the user gives it, save it with
`gh variable set STORE_PRODUCT_ID --repo Karalumpas/LabWidge --body <id>`.

### One-time setup (the user does this)

1. Install the CLI (it needs the .NET 9 Desktop Runtime, which is installed):
   `winget install "Microsoft Store Developer CLI" --source msstore`
2. Partner Center → Account settings → **Tenants**: associate a Microsoft Entra ID tenant (or create one there).
3. Entra admin center (<https://entra.microsoft.com>) → App registrations → **New registration** (e.g. *LabWidge Store
   publisher*). Under Certificates & secrets create a client secret and copy its value.
4. Partner Center → Account settings → User management → **Microsoft Entra applications** → add that app with the
   **Manager** role.
5. Note the Tenant ID and Client ID (Entra, app overview) and the Seller ID (Partner Center → Account settings →
   Legal info / Developer settings).
6. In their own terminal: `msstore reconfigure --tenantId <tenant> --sellerId <seller> --clientId <client> --clientSecret <secret>`
   (or plain `msstore reconfigure` to be prompted).
7. The Store product id is in Partner Center → the app → Product management → **Product identity** ("Store ID").

**The CLI can only update a product that has been published at least once.** If the first submission has never passed
certification, the user must finish that one in Partner Center by hand (docs/MICROSOFT_STORE.md, "A submission").

## 1. Find the package

The version is the newest released one unless the user names another:

```powershell
git checkout main; git pull
$version = ([xml](Get-Content LabWidge/LabWidge.csproj)).Project.PropertyGroup.Version | ? { $_ } | Select -First 1
gh run list --repo Karalumpas/LabWidge --workflow release.yml --branch main -L 10 --json databaseId,headSha,conclusion
```

Pick the successful run for the merge commit of that version (the commit that changed the version; `git log -1 --format=%H v<version>`
gives the tagged commit). Download into a new, empty folder in the scratchpad:

```bash
gh run download <run-id> --repo Karalumpas/LabWidge --name labwidge-store-package --dir <scratchpad>/store-<version>
```

Check that `LabWidge-<version>.msix` is there, and that the package's own version is right (the Store rejects a version that is
not higher than the published one):

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($msix)
$manifest = [xml](New-Object IO.StreamReader(($zip.Entries | ? FullName -eq 'AppxManifest.xml').Open())).ReadToEnd(); $zip.Dispose()
$manifest.Package.Identity.Version      # must be <version>.0
$manifest.Package.Identity.Name         # must match the repository variable STORE_IDENTITY_NAME
```

If the artifact is missing (the job was skipped because the `STORE_*` variables are not set, or it failed), stop and say so –
never build the MSIX locally for upload: a local build uses placeholder identity values, and releases are always built by the workflow.

## 2. Is a submission already in progress?

```powershell
msstore submission status $productId
```

- No pending submission, or only the last one *Published*: continue.
- A pending **draft** that this flow did not create: `msstore publish` deletes it and starts again from the last published
  submission. Ask the user before continuing – they may have unsaved listing changes in it.
- A submission **in certification** or **pending publication**: stop. A new one can only start when it is done or cancelled in
  Partner Center; tell the user and let them decide.

## 3. Upload, add "What's new", submit

Upload the package into a new draft without submitting it yet:

```powershell
msstore publish $msix -id $productId --noCommit
```

Then set the release notes from the top section of `CHANGELOG.md` (the same text the in-app updater shows). Write the English
bullets as they are for `en-us`, and a faithful Danish translation for `da-dk`. Only touch listings that exist in the submission:

```powershell
$submission = msstore submission get $productId | ConvertFrom-Json
foreach ($lang in $submission.Listings.PSObject.Properties.Name) {
    $notes = if ($lang -like 'da*') { $danish } else { $english }
    $submission.Listings.$lang.BaseListing.ReleaseNotes = $notes
}
msstore submission update $productId ($submission | ConvertTo-Json -Depth 100 -Compress)
```

Release notes are plain text, up to 1500 characters; use "• " bullets and line breaks. If `submission update` fails, the package is
still in the draft: say so, and either submit without notes (only if the user agrees) or leave the draft for them to finish.

Submit for certification:

```powershell
msstore submission publish $productId
msstore submission status $productId
```

## 4. Follow certification

Certification takes hours to days, so do not block on it. Report the status right after submitting. When the user asks later, check
with `msstore submission status $productId` (or `msstore submission poll $productId` in the background for a long wait).

- **Failed / certification failed**: show the report's reasons (`msstore submission get $productId` has them under
  `StatusDetails`) and propose the fix; a code fix is a new version through the release skill.
- **Published**: tell the user the Store version is live. Store users get it automatically over the next hours.

## Rules

- Never handle the client secret, and never put it in a file, a command line you run, or the repository.
- Never upload a package that was not built by the Release workflow, and never upload a version lower than or equal to the
  published one.
- Never delete a submission or a draft you did not create without asking.
- Never change pricing, availability, age rating or the listing text other than "What's new" – those are the user's choices in
  Partner Center.
