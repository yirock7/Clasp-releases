# Releasing an update

The source code and the releases live in one public repository, `yirock7/Clasp-releases`. Clasp
checks it for new versions using GitHub's unauthenticated release list. Every release is published
there: the tag `clasp-vX.Y.Z`, the installer, its `.sha256` and its `.sig`.

The app installs an update only when all of these hold:

- the tag is `clasp-vX.Y.Z` and newer than the running version;
- the assets are named `Clasp-X.Y.Z-win-x64-setup.exe` and `Clasp-X.Y.Z-win-x64-setup.exe.sig`;
- the asset URLs are `https://github.com/yirock7/Clasp-releases/releases/download/...`;
- the download has exactly the advertised size;
- the signature verifies with the public key built into the app (`UpdateSignature.PublicKey`).

The signature covers the version, the file name and the SHA-256, so an older genuine installer
cannot be passed off as a newer release.

## Docs to update for every release

A release is not done until the documents describe the version that ships. Before tagging, go
through this list against everything that changed since the last release (`git log clasp-vPREVIOUS..main`):

- **`docs/release-notes/vX.Y.Z.md`** and a **`CHANGELOG.md`** entry, in plain words for users.
- **`README.md`**: the feature list, what Clasp can and can't do, the supported-devices table
  (what has been tested on real hardware), privacy (anything new over Bluetooth or the internet),
  install requirements and the FAQ.
- **The signature checker tag** in `README.md` and `docs/INSTALLING-AND-UPDATING.md`: point it at
  the new `clasp-vX.Y.Z` tag, and update the "currently" tag below.
- **`windows/hub/README.md`** and **`docs/ARCHITECTURE.md`**, when how the app works has changed
  (new components, new Windows APIs, new data flows).
- **`docs/BUILDING.md`**: new or changed command-line arguments and Demo-mode options
  (compare with the argument comment at the top of `App.xaml.cs`).
- **`docs/INSTALLING-AND-UPDATING.md`** and this file, when installing, updating or releasing changed.
- **`docs/PAIRING-MODES.md`**, when pairing steps or models changed.
- **Screenshots in `assets/screenshots/`**: retake any whose window looks different now, in Demo
  mode (`--transport=demo` with `--show=...`), so they never show an older version.

If something on the list doesn't need a change, that's fine; check it anyway.

## The signing key

The private key is `%APPDATA%\Clasp-release\update-signing-key.pem` on the maintainer's
release machine. It is **never** committed. Back it up somewhere safe and offline (for example, an encrypted USB drive
or a password manager's file attachment).

- **If the key is lost,** installed copies can no longer update themselves. Make a new key with
  `UpdateSigner new-key`, put the new public key in `UpdateSignature.PublicKey`, and tell users
  to install that release by hand once.
- **If the key leaks,** do the same and remove the old releases' `.sig` files.

## Steps

Build from the merged `main` commit, then sign, then publish. Replace `X.Y.Z` with the version.

```powershell
# 1. Build (from windows/hub)
.\installer\Build-Installer.ps1 -Version X.Y.Z -CompilerPath "$env:TEMP\inno-6.7.3\ISCC.exe"

# 2. Sign; this also re-verifies against the key built into the app
dotnet run --project tools\UpdateSigner -- sign "$env:APPDATA\Clasp-release\update-signing-key.pem" `
    installer\artifacts\Clasp-X.Y.Z-win-x64-setup.exe X.Y.Z

# 3. Publish (tag on the merged main commit; the notes live in docs\release-notes\)
gh release create clasp-vX.Y.Z --repo yirock7/Clasp-releases --prerelease --verify-tag --title "Clasp X.Y.Z" `
    --notes-file ..\..\docs\release-notes\vX.Y.Z.md `
    installer\artifacts\Clasp-X.Y.Z-win-x64-setup.exe installer\artifacts\Clasp-X.Y.Z-win-x64-setup.exe.sha256 `
    installer\artifacts\Clasp-X.Y.Z-win-x64-setup.exe.sig
```

Write the release text first as `docs/release-notes/vX.Y.Z.md` (plain words, what changed for users,
no alarm) and add the version to `CHANGELOG.md`. `--verify-tag` expects the tag to exist already,
so tag the merged `main` commit before publishing. Because the one-line install command fetches the
checker script from that tag, the tag must contain `windows/hub/tools/Verify-ClaspInstaller.ps1`.

**Release checklist item, before publishing:** run `Verify-ClaspInstaller.ps1` against the final
artifacts, on Windows PowerShell 5.1, the way a user would. It must print `Signature OK: Clasp X.Y.Z`.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\Verify-ClaspInstaller.ps1 `
    -Installer installer\artifacts\Clasp-X.Y.Z-win-x64-setup.exe
```

The README's one-line command downloads that script from the source repo at a fixed tag
(currently `clasp-v0.6.0`), so when the script changes, update the tag in `README.md`,
and `docs/INSTALLING-AND-UPDATING.md`. If the public key ever
changes, update the fingerprint printed in those files too.

The signature check is the best a manual install can offer today. The long-term fix is
Authenticode code-signing of the installer itself (free for open-source projects through
SignPath), which would also quiet the Windows SmartScreen warning.

## What the app does

- **Settings → Updates:** choose how often to check: only when you click, daily, weekly or
  monthly. The flyout asks once whether to check automatically.
- **Checking:** an automatic check runs a minute after launch, and then hourly the app asks the
  schedule whether one is due. It only reads the release list; nothing about the user or their
  devices is sent.
- **Installing:** clicking **Update** downloads the installer into
  `%LOCALAPPDATA%\Clasp\updates`, verifies it, starts it with
  `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH`, and quits. Setup
  upgrades in place and reopens Clasp to the tray (`Clasp.iss`, `[Run]` with `RelaunchRequested`).
- **Development builds** (version `0.0.0` or no version) never check.
