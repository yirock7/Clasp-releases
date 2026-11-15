# Installing and updating Clasp

Download the installer from the [latest release](https://github.com/yirock7/Clasp-releases/releases/latest). Asset names look like `Clasp-X.Y.Z-win-x64-setup.exe`, with a matching `.exe.sig` (signature) file and a `.exe.sha256` (checksum) file.

## What you need

Windows 11, 64-bit (build 22000 or later), and Bluetooth. Windows Server and Windows on ARM are not supported, and the installer refuses them. You do not need the .NET SDK or any other tool to run Clasp.

Clasp is driver-free. Setup installs the app for your Windows account and does not install a Bluetooth driver, change pairing, enable Test Mode, or touch Secure Boot or any other security setting. After installing, you can pair your earbuds in Clasp's welcome guide, or in Windows Settings as before.


## Check the download first

Please do this before the "Run anyway" step. I sign every installer with a private key only I have. The check below proves the file came from me and was not swapped or edited. That matters because the installer is not code-signed yet, so Windows cannot vouch for it.

Download the installer and its `.sig` file from the Releases page into the same folder, then run this in PowerShell, with `X.Y.Z` replaced by your version:

```powershell
cd $env:USERPROFILE\Downloads
Invoke-WebRequest https://raw.githubusercontent.com/yirock7/Clasp-releases/clasp-v0.6.0/windows/hub/tools/Verify-ClaspInstaller.ps1 -OutFile Verify-ClaspInstaller.ps1
powershell -ExecutionPolicy Bypass -File .\Verify-ClaspInstaller.ps1 -Installer .\Clasp-X.Y.Z-win-x64-setup.exe
```

You should see `Signature OK: Clasp X.Y.Z`. If you see anything else, do not run the installer. The script only reads the two files. It is fetched from the source repository at a fixed release tag (not from the download page), so swapping a release's download does not also swap the checker. (Both live under the same GitHub account, so this cannot help if the account itself were compromised; code-signing through SignPath is planned for that.) It runs on the PowerShell that comes with Windows 10 and 11.

It checks against the public key in `windows/hub/src/AppleDeviceHub.Core/Updates/UpdateSignature.cs`. That key's SHA-256 fingerprint (of its DER bytes) is:

`1e656518818d696089c7d3e1688bdc34306926a8c23b87fc21b35fac53536176`

**The `.sha256` file is weaker.** It holds the lowercase hash, two spaces, and the installer's file name. A matching hash only shows the download is not corrupted and is the file published next to the checksum. Whoever could replace the installer could replace the checksum too, so it does not prove who published it. To compare it anyway:

```powershell
$installer = '.\Clasp-X.Y.Z-win-x64-setup.exe'
$expected = (Get-Content -LiteralPath ($installer + '.sha256') -Raw).Trim().Split(' ')[0]
$actual = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
if ($actual -ine $expected) { throw 'The installer does not match its checksum.' }
```

Once the signature check says OK, Windows will still show a "Windows protected your PC" box, because the installer is not Authenticode code-signed yet. Click **More info**, then **Run anyway**, only if you downloaded the file from the Releases page.

## Installing

Run the setup file. It installs under `%LOCALAPPDATA%\Programs\Clasp` and needs no administrator rights. It adds a Start Menu shortcut, offers an optional desktop shortcut, and offers **Start Clasp when I sign in** (selected on a first install, and changeable later in Settings).

## Updating

Clasp can check for updates itself. In Settings you choose how often: only when you click, daily, weekly or monthly. When a new version is found, clicking **Update** downloads the installer, verifies its signature against a key built into the app, runs it quietly, and reopens Clasp in the tray. An installer whose signature doesn't match is never run.

You can also download a newer setup file by hand and run it. It upgrades in place and keeps your settings. If Clasp is open, setup asks before closing it.

## Where things live

Settings, battery history and a small log are in `%LOCALAPPDATA%\Clasp`. Downloaded updates go to its `updates` subfolder. Earlier versions kept these in `%LOCALAPPDATA%\AppleDeviceHub`. The first start of a newer version moves them across; the old folder is deleted only after everything is safely copied. The program itself used to be `AppleDeviceHub.exe`; it is now `Clasp.exe`, so if you pinned the old one to the taskbar, pin Clasp again from the Start menu. Setup and the app repair the sign-in entry and `clasp:` link after the rename. An interactive uninstall that you answer Yes to deletes both folders.

## Uninstalling

Windows Settings, Apps, Installed apps, Clasp. Uninstalling removes the app, its shortcuts and its sign-in entry. An interactive uninstall asks whether to delete `%LOCALAPPDATA%\Clasp` too (the default is No); a silent uninstall never deletes it.

## Building the installer yourself

See [BUILDING.md](BUILDING.md).
