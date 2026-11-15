# Clasp installer build

Build-Installer.ps1 publishes the unpackaged Apple Device Hub WinUI app as a self-contained x64 folder, then wraps that folder in an Inno Setup installer. The Windows App SDK runtime files remain beside the app executable, so the installer includes the complete publish output.

## Build

From the repository root in PowerShell, with the .NET 10 SDK and Inno Setup 6 available:

    .\windows\hub\installer\Build-Installer.ps1 -Version 0.3.0 -OutputDirectory .\windows\hub\installer\artifacts -CompilerPath 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'

The script builds the app itself. It does not run the installer. It creates Clasp-<version>-win-x64-setup.exe and a matching .exe.sha256 file in the output directory. The checksum file contains the lowercase SHA-256 hash, two spaces, the installer filename, and a newline.

Use the release tag clasp-vX.Y.Z as version X.Y.Z. Keep AppId in Clasp.iss unchanged for all releases so Inno Setup recognizes future installers as upgrades.

## Installer behavior

The setup installs to %LOCALAPPDATA%\Programs\Clasp for the current user and does not request administrator access. It adds Start Menu entries to launch Clasp in nearby-detection mode, open the GitHub Releases page in the default browser, and uninstall Clasp. A desktop shortcut is offered as an unchecked optional task.

The first install selects **Start Clasp when I sign in** by default. When Setup finds an existing installation, `checkedonce` defaults that task to off. If the task is selected, Setup writes the per-user `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Clasp` value with `--transport=ble --startup`; if it is cleared, Setup leaves any existing value alone, so a silent update never turns off "Start Clasp when I sign in". The app owns that value afterwards: the in-app Startup toggle reads and changes it. Windows Task Manager can disable the entry separately, and Clasp does not override that choice. Uninstall removes the Clasp Run value, and the `clasp` link registration only when its command is exactly this installation's. An interactive uninstall also asks (default No) whether to delete `%LOCALAPPDATA%\Clasp` (settings and battery history); a silent uninstall never deletes it.

The setup asks before closing a running Clasp process when files need updating. It does not force-close the process. A silent in-app update passes `/RELAUNCH`, which reopens Clasp to the tray afterwards; a manual run of Setup does not restart it. Settings and local diagnostics remain under %LOCALAPPDATA%\Clasp; no install rule deletes that folder, and an uninstall deletes it only after an explicit Yes in the interactive prompt described above. Uninstall removes the application files and shortcuts. The root GPL license is shown during setup and installed with the app.

The program used to be `AppleDeviceHub.exe`. Upgrading from such a version installs `Clasp.exe`, removes the old executable and its compiled-resource files (`[InstallDelete]`), and rewrites an existing `Run\Clasp` value that still names the old executable in this install folder so it names `Clasp.exe` (arguments kept; any other value is left alone). The app repeats that repair, and the same one for the `clasp` link command, when it starts. `AppId` is unchanged, so this is an upgrade of the same installation. The first start of the new version moves `%LOCALAPPDATA%\AppleDeviceHub` to `%LOCALAPPDATA%\Clasp` (see CHANGELOG.md); an interactive uninstall that answers Yes deletes both folders.

The update shortcut is a manual link to the releases page. Clasp itself can also check for updates and install them when you click **Update**, after verifying a signature (see docs/RELEASING-UPDATES.md). The installer is not code-signed yet, so Windows may show an unknown-publisher or SmartScreen prompt.

## Supported Windows editions

The current installer targets native x64 Windows 11 client editions, build 22000 or later. It rejects Windows Server and Windows on ARM because those configurations are not qualified. The installer is per-user and does not require administrator access.

## Runtime files and installation requirements

The installed app is built as a self-contained x64 publish. The publish includes the .NET runtime host/CoreCLR and the Windows App SDK runtime used by the app; a user does not need the .NET SDK, Visual Studio, or Inno Setup to install or run Clasp. Inno Setup 6.7.3 and the .NET 10 SDK are build-time tools only.

The build checks that every app XAML file has its compiled `.xbf` resource, `Clasp.pri` exists, the executable is x64, the .NET and Windows App SDK runtime files are present, and the publish contains no driver or signing files. It records a SHA-256 inventory next to the setup file and checks that Inno Setup's compile log lists the critical `.xbf` resources and app PRI. These checks protect the package build; they do not replace a fresh-machine installation trial.

## Demo-mode startup check

The installer build launches the published app with `--transport=demo --show=flyout`. Demo mode does not use Bluetooth. On a qualified interactive Windows 11 x64 client, the check requires a visible window, verifies the .NET and Windows App SDK modules load from the publish folder, and confirms an existing settings file's hash is unchanged. It closes only the process it started.

The hosted Actions runner may be a Windows Server machine or may lack an interactive desktop. In that case the result is `UNVERIFIED`, not a pass. CI may still upload a test installer artifact, but a tag-triggered draft release is held until the smoke passes on a qualified Windows 11 x64 client. Physical Bluetooth pairing, device behavior, and installation on a clean Windows machine remain separate acceptance checks.
