# Contributing to Clasp

Thanks for taking an interest. Clasp is a one-person hobby project, so I may be slow to answer, but I read everything.

## Reporting a bug

Use the [bug report form](https://github.com/yirock7/Clasp-releases/issues/new?template=bug_report.yml). The most helpful things you can tell me are your Clasp version (Settings, About), your Windows version, and exactly which AirPods or Beats model you have. If you can, include the last few lines of `%LOCALAPPDATA%\AppleDeviceHub\hub.log`. Read it before you post it; it is written without device names, but it is your file.

Reports from real hardware I don't own are especially valuable. The [supported devices table](README.md#supported-devices) says which models have only been checked on paper.

For anything security-related, please don't open a public issue. See [SECURITY.md](SECURITY.md).

## Building and testing

The full steps are in [docs/BUILDING.md](docs/BUILDING.md). In short, you need the .NET 10 SDK on Windows 11 and then:

```powershell
dotnet test windows/hub/tests/AppleDeviceHub.Core.Tests -c Release
dotnet build windows/hub/src/AppleDeviceHub.App/AppleDeviceHub.App.csproj -c Release -p:Platform=x64
```

Run the app in Demo mode (`--transport=demo`, no Bluetooth needed) when you are working on the interface. BUILDING.md lists the arguments.

## How the code is laid out

- `windows/hub/src/AppleDeviceHub.Core` is plain .NET with no UI. Rules and decisions live here (battery parsing, the model catalog, settings, update checks) so they can be tested without a device.
- `windows/hub/src/AppleDeviceHub.App` is the WinUI 3 app: tray icon, flyout, Settings, welcome guide, and the Windows audio and Bluetooth glue.
- `windows/hub/tests` holds the Core tests. If you change behaviour in Core, add or adjust a test.
- `windows/hub/installer` builds the per-user installer.
- `windows/daemon` and `windows/drivers/aap` are the optional Rust engine and the Bluetooth driver source. The app does not ship or start either. [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) has more detail, and [docs/README.md](docs/README.md) indexes all the docs.

## Pull requests

Small, focused pull requests are easiest for me to review. Please:

- explain what changed and why, in plain words;
- keep the tests passing, and add one for new logic;
- say how you tried it (Demo mode, real AirPods, which model);
- keep user-facing text short and friendly, in the same voice as the rest of the app;
- not add network calls, analytics or telemetry. Clasp's only internet traffic is the update check, and that is a promise to users;
- not copy code from projects whose licence is not compatible with GPL-3.0, and say where anything borrowed came from (see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)).

By contributing you agree your work is released under the project's [GPL-3.0 licence](LICENSE).

## What fits, and what doesn't

Good fits: bug fixes, support for more models (ideally with a real broadcast you captured), accessibility, translations of the interface, clearer wording, better pairing instructions.

Probably not: features that need a kernel driver to work (listening modes, true ear detection) in the app itself, anything that changes Windows security settings, or anything that sends user data off the PC. If you are unsure, open an issue and ask before you spend time on it.

## Everyone is welcome

Please read the [Code of Conduct](CODE_OF_CONDUCT.md). Be kind, and assume the other person is trying their best.
