---
name: store-submit
description: Upload a released LabWidge version to the Microsoft Store and submit it for certification through Partner Center in the user's Chrome (Claude in Chrome). Downloads the labwidge-store-package artifact from the Release workflow, uploads the MSIX into a new submission, writes "What's new" in Danish and English, and sends it to certification. Use it after /release has published a version, or when the user asks to upload, submit or send the update to the Microsoft Store / Partner Center ("upload til Store", "send til Microsoft Store").
---

# Submit a version to the Microsoft Store

LabWidge is live in the Store as product **9PN5S2Z8PTQM** (repository variable `STORE_PRODUCT_ID`). The Release workflow builds the
Store package in the job `store-package` and attaches the artifact `labwidge-store-package` (`LabWidge-<version>.msix`, about 70 MB).
This skill puts that package into a new Partner Center submission in the user's own Chrome, where they are signed in, and sends
it to certification. Microsoft certifies it (usually hours, up to three working days); then Store users get the update.

Why Chrome and not the `msstore` CLI: the CLI signs in through a Microsoft Entra app, which the user's individual developer account
cannot add. Do not suggest that route again unless the account becomes a company account.

Talk to the user in their own language. When the user has approved the release (the merge question in the release skill) or
asked for the Store upload, run the whole flow, including "Send til certificering", without further questions.

Load the `chrome-browser` skill and the Claude in Chrome tools first (`tabs_context_mcp`, `tabs_create_mcp`, `navigate`, `find`,
`get_page_text`, `form_input`, `computer`, `javascript_tool`, `tabs_close_mcp`). Partner Center shows in Danish; screenshots often
time out on it, so read it with `get_page_text`, `find` and `javascript_tool`.

## 1. Get and check the package

```powershell
$version = ([xml](Get-Content LabWidge/LabWidge.csproj)).Project.PropertyGroup.Version | ? { $_ } | Select -First 1   # on an up-to-date main
gh run list --repo Karalumpas/LabWidge --workflow release.yml --branch main -L 5 --json databaseId,headSha,conclusion
$dir = "E:\LabWidge\dist\store-$version"            # dist/ is git-ignored; use a new, empty folder
gh run download <run-id> --repo Karalumpas/LabWidge --name labwidge-store-package --dir $dir
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead("$dir\LabWidge-$version.msix")
$m = [xml](New-Object IO.StreamReader(($zip.Entries | ? FullName -eq 'AppxManifest.xml').Open())).ReadToEnd(); $zip.Dispose()
"$($m.Package.Identity.Name) $($m.Package.Identity.Version)"   # Mede.LabWidge <version>.0
```

The identity must be `Mede.LabWidge` with publisher `CN=891FF466-ADE8-4F4E-AA0D-8CFE55BC3DAE` (the `STORE_*` repository variables).
If the artifact is missing, stop and say why. Never build the MSIX locally for upload.

## 2. Open the product and check what is live

Open `https://partner.microsoft.com/dashboard/products/9PN5S2Z8PTQM/overview` in a new tab (it redirects to `/da-dk/…`; wait a few
seconds before reading). Read the "Produktudgivelse" section:

- **"Start opdateringen"**: nothing is in progress. Continue.
- **"I kladde"** (a draft exists): open its *Pakker* page and see what it holds. A draft you did not create may hold the user's
  unsaved work – ask before changing it.
- **"Under certificering" / "Opdatering i certificering"**: stop. A new submission can only start when that one is done.

**The Store only accepts a higher version than the one live.** Open the draft's (or the live submission's) *Pakker* page and read the
package version. If it is not lower than `<version>.0`, stop: the release needs a new version number through the release skill
(this happened with 1.23.1, whose number the first Store submission had already used – it went out as 1.23.2).

Click **Start opdateringen**. After ~10 seconds the overview shows "Produktopdatering: I kladde (Submission N)". Get the
submission's links from the page (`a[href*="submissions/"]`; strip the query string before printing it – the extension blocks
URLs with query data): `…/submissions/<id>/packages`, `…/managelanguages`.

## 3. Upload the package

The Chrome file-upload tool is limited to 10 MB, so the page fetches the package from a small server on the user's PC:

```bash
python -I .claude/skills/store-submit/serve.py "E:/LabWidge/dist/store-<version>" 8765   # run_in_background
curl -sI http://127.0.0.1:8765/LabWidge-<version>.msix | head -1                        # 200 OK
```

The server sends CORS and Private Network Access headers for `https://partner.microsoft.com` only, and serves only that folder.
Chrome must allow the page to reach the local network: the user allows it once per site (the site's settings → *Local network
access* → Allow). If a `fetch` to 127.0.0.1 hangs and no request reaches the server, that permission is missing or pending: ask the
user to allow it, then **reload the page** – an already-open page does not pick up the permission.

On the submission's *Pakker* page, test with a HEAD request (abort after 15 s), then hand the file to the upload field without
awaiting the 70 MB transfer in the tool call (it times out after 45 s):

```js
window.__lwUpload = { state: 'fetching' };
(async () => {
  try {
    const r = await fetch('http://127.0.0.1:8765/LabWidge-<version>.msix');
    const file = new File([await r.blob()], 'LabWidge-<version>.msix', { type: 'application/octet-stream' });
    const input = document.querySelector('input[type=file].fileuploader');
    const dt = new DataTransfer(); dt.items.add(file); input.files = dt.files;
    input.dispatchEvent(new Event('change', { bubbles: true }));
    window.__lwUpload = { state: 'handed', size: file.size };
  } catch (e) { window.__lwUpload = { state: 'error', error: String(e) }; }
})(); 'started'
```

Poll `window.__lwUpload` and the page text until the new package shows its size and version without "Uploading"/"Validating".
The warning *"restricted capabilities require approval … runFullTrust"* is expected for this desktop app; the reason is already in
the submission options. Any other validation error: stop and show it. The older package is marked *"will be removed after you save
this page"* – that is right. Click **Save**; the overview then lists `LabWidge-<version>.msix – Validated – Opdateret`.

Stop the server as soon as the upload is saved:

```powershell
Get-CimInstance Win32_Process -Filter "Name like 'python%'" | ? CommandLine -like '*serve.py*' | % { Stop-Process -Id $_.ProcessId -Force }
```

## 4. "What's new"

From `…/managelanguages`, open **Dansk (Danmark)** and **Engelsk (USA)** in turn. The field is the textarea labelled
*"Nyheder i denne version"* (max 1500 characters). Read the current text first to keep its style, then write the release's points
from `CHANGELOG.md` for an ordinary user: a line "Version <version>:" and up to four "• " bullets – English for en-us, a faithful
Danish version for da-dk. Set it with `form_input` (it fires the events the page needs), then click **Gem** at the bottom; the page
returns to the overview.

The rest of the listing – description, short description, features, keywords and screenshot captions – comes from
`docs/STORE_LISTING.md`. If that file changed since the last submission (`git log -1 -- docs/STORE_LISTING.md` against the date
of the live submission), copy every field from it into both languages in the same submission. Otherwise leave the listing alone.
Never change screenshots or logos without being asked.

## 5. Submit

On the overview, check that Pakker says *Validated* and nothing reports an error, then click **Send til certificering**. After
~10 seconds the overview shows "Opdatering i certificering" with the steps Indsendelse → Forbehandling → Certificering → Publicering.
Close the tab you opened and report: version, submission number, and that certification usually takes hours up to three working
days. When asked later, open the overview again and read the status; on a certification failure show Microsoft's report and propose
the fix (a code fix is a new version through the release skill).

## Rules

- Never upload a package that was not built by the Release workflow, or one whose version is not higher than the live one.
- Never change pricing, availability, age ratings, properties or listing text other than "What's new" – those are the user's choices.
- Never delete a submission or cancel a certification without asking.
- Keep the local server running only while uploading, and only for the package's folder.
