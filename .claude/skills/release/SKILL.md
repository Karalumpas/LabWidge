---
name: release
description: Publish a new version of LabWidge so installed apps (including friends') are offered the update. Raises the version in both .csproj files, writes CHANGELOG.md, creates a branch and PR, merges after approval and follows the workflow until the release is in Karalumpas/labwidge-releases. Use it when the user types /release or /udgiv, or asks to release, publish, ship, "udgive", make a new version, raise the version or send the update out.
---

# Publish a new version

The app checks the public repository `Karalumpas/labwidge-releases` for updates; the source is in `Karalumpas/LabWidge`.
The workflow `.github/workflows/release.yml` builds Setup and publishes it there when the version in a `.csproj` changes on `main`.
Your job is to make the version bump correct and see it all the way through.

Talk to the user in their own language. Everything that ends up on GitHub – changelog, commit messages, PR texts – is in English.

## 1. Starting point

```bash
git checkout main && git pull
git status --short                                  # must be empty – otherwise ask the user
git describe --tags --abbrev=0 --match "v*"         # the last released version
git log <last-tag>..HEAD --oneline --no-merges
git diff <last-tag>..HEAD --stat
```

If there are no commits since the last tag, say so and stop.

## 2. Propose a version and changelog – and get them approved

- **Version** (x.y.z): new feature → raise y and set z to 0. Fixes only → raise z. Big overhauls → ask the user.
- **Changelog**: 1–5 bullet points in English, written for an ordinary user (a friend, not a developer).
  Describe what they notice, not which files changed. The text is shown in the update dialog and in the
  "What's new" notification after the update. The notification shows only the first point directly,
  so put the most important one first.

Show the proposal to the user and wait for a yes before changing anything. The user's corrections take precedence.

## 3. Edit the files

The version appears in **four places in two files**, and the workflow stops if they do not match:

| File | Field | Format |
|---|---|---|
| `LabWidge/LabWidge.csproj` | `<Version>` | `1.6.0` |
| `LabWidge/LabWidge.csproj` | `<AssemblyVersion>` | `1.6.0.0` |
| `LabWidge/LabWidge.csproj` | `<FileVersion>` | `1.6.0.0` |
| `Setup/LabWidge.Setup.csproj` | `<Version>` | `1.6.0` |

`AssemblyVersion`/`FileVersion` is the app's known version, which the update check compares against. If they are forgotten,
the app thinks it is still old and offers the same update again and again.

In `CHANGELOG.md` a new section goes **at the top** (right below the intro text, above the previous version).
The heading must be exactly `## <version>`, without "v" and without a date. Otherwise the workflow cannot find the text.

Check afterwards:

```bash
git grep -nE "<(Version|AssemblyVersion|FileVersion)>" -- "*.csproj"
```

## 4. Build

The running widget usually locks `bin\Debug`, so build to a temporary folder:

```bash
dotnet build LabWidge/LabWidge.csproj -c Debug -nologo -v q -o <scratchpad>/build
```

On errors, stop and fix them. A version is never published if it does not build.

## 5. Branch, commit and PR

```bash
git checkout -b release/v<version>
git add -A
git commit -m "Release v<version>"     # with the attribution the session specifies
git push -u origin release/v<version>
gh pr create --base main --title "Release v<version>" --body "<the changelog points + what was tested>"
```

Wait for the build check (`.github/workflows/build.yml` builds the app and Setup, runs the tests and checks that the versions match):

```bash
gh pr checks <nr> --watch --fail-fast
```

If it fails, fix it on the branch before anything else. Then give the user the link to the PR and ask whether you may merge.
A merge publishes the version to every installation.

## 6. Merge and follow the workflow

```bash
gh pr merge <nr> --merge --delete-branch         # deletes the release branch locally and on GitHub
git checkout main && git pull --prune
gh run list --workflow release.yml -L 1          # find the run for the merge commit
gh run watch <run-id> --exit-status
```

The release branch is not kept: the code is in `main`, and the workflow sets the tag `v<version>`.
Check that only `main` is left (e.g. if `--delete-branch` could not delete a branch):

```bash
git branch -a                                    # only main and origin/main
git branch -d release/v<version>                 # if it is still there locally
git push origin --delete release/v<version>      # if it is still on GitHub
```

Finally check what the app itself sees (without signing in):

```bash
curl -s https://api.github.com/repos/Karalumpas/labwidge-releases/releases/latest | grep -E '"tag_name"|browser_download_url'
```

`tag_name` must be `v<version>`, and both `LabWidge-Setup.exe` and `IpTrayWidget-Setup.exe` must be attached
(installations from before the rename to LabWidge look for the old name).

## When the workflow fails

Read the error with `gh run view <run-id> --log-failed`.

- **`RELEASES_TOKEN is missing` or 401/403 at "Publish"**: the token is missing or expired. Ask the user to
  create a new fine-grained token (only `Karalumpas/labwidge-releases`, **Contents: Read and write**) and save it as the
  secret `RELEASES_TOKEN` in `Karalumpas/LabWidge`. Never handle or type the token yourself.
  Once it is saved, run again with `gh workflow run release.yml` (the version is skipped if it is already released).
- **"The versions do not match"**: fix the `.csproj` files in a new PR.
- **Build errors**: fix them in a new PR. The version was not released, so the same version number can be reused.

If the user needs the release right away and the token cannot be sorted out, you can publish manually from `main`:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -BuildOnly
Copy-Item dist\LabWidge-Setup.exe dist\IpTrayWidget-Setup.exe
gh release create v<version> dist\LabWidge-Setup.exe dist\IpTrayWidget-Setup.exe -R Karalumpas/labwidge-releases --title "LabWidge <version>" --notes-file <notes.md>
git tag v<version>; git push origin v<version>
```

The notes must have the same shape as the workflow's: `## What's new`, the changelog points and finally the line
``Run `LabWidge-Setup.exe`. SmartScreen: "More info" → "Run anyway".``

## Rules

- Never reuse a version number that has been released, and never delete releases. Fix mistakes with a new version.
- Only release from `main`.
- Installations older than 1.5.1 check a repository they can no longer reach and never see updates. Mention this if the user
  wonders why someone is not offered the version: they need to install manually once from
  https://github.com/Karalumpas/labwidge-releases/releases/latest
- Windows Smart App Control can block new unsigned builds until code signing through SignPath is in place.
  If a release is blocked on the user's PC, a new build (new version) is the workaround.
