# Building Clasp from source

You need Windows 11 (64-bit) and the .NET 10 SDK:

```powershell
winget install --id Microsoft.DotNet.SDK.10 --exact
```

Open a new terminal afterwards so `dotnet` is on your PATH. The exact SDK version Clasp expects is pinned in `windows/hub/global.json`.

The tests and the normal `dotnet build` below need only the SDK. Building the installer is different: it compiles the app to native code (Native AOT), which needs the Visual Studio C++ build tools. [TOOLCHAIN.md](TOOLCHAIN.md) lists everything, including Inno Setup and Python.

## Run the tests

From the repository root:

```powershell
dotnet test windows/hub/tests/AppleDeviceHub.Core.Tests -c Release
```

These cover the logic in `AppleDeviceHub.Core`: battery parsing, model data, settings, update checking and signature verification, and so on. They need no Bluetooth and no hardware.

## Build and run the app

```powershell
dotnet build windows/hub/src/AppleDeviceHub.App/AppleDeviceHub.App.csproj -c Release -p:Platform=x64
.\Start-Clasp.ps1
```

`Start-Clasp.ps1` starts the Release build in the tray. If Clasp is already running it leaves that copy alone. Add `-Settings` to open the Settings window.

## Demo mode

Demo mode uses made-up AirPods data and no Bluetooth, which makes it good for working on the interface. Anything synthetic is labelled "Demo" on screen. Demo mode is opt-in: running the executable with no arguments uses real nearby detection, and only `--transport=demo` switches to made-up data. Start the built executable with arguments:

```powershell
$exe = 'windows\hub\src\AppleDeviceHub.App\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\Clasp.exe'
& $exe --transport=demo --show=flyout
```

Some useful arguments (the `--demo-...` ones only apply together with `--transport=demo`):

| Argument | Effect |
| --- | --- |
| `--show=flyout`, `--show=card`, `--show=settings`, `--show=welcome` | Open that window straight away |
| `--welcome-page=1..4` | With `--show=welcome`, jump to a page |
| `--settings-section=general`, `airpods`, `history`, `sound`, `shortcuts`, `advanced`, `about` | With `--show=settings`, jump to a section |
| `--demo-history` | Fill the battery history chart with made-up data (never saved) |
| `--demo-history=sparse` | Made-up history with long gaps, as when AirPods are only used with this PC now and then |
| `--demo-battery=low`, `unknown`, `stale` | Start with that battery situation |
| `--demo-model=0x1720` | Show a different model's artwork and battery layout, by its model id |
| `--demo-ears=in`, `out`, `left`, `case` | Report where the earbuds are (in ears, out of the case, only the left out, both in the case), so you can see the buds pull out over their rings |

Only one copy of Clasp runs at a time, and a second launch exits quietly. If you also use Clasp day to day, close it before testing a build, because your running copy will win.

## Build the installer

You need Inno Setup 6.7.3 (its `ISCC.exe` is the compiler) and the Visual Studio C++ build tools, as described in [TOOLCHAIN.md](TOOLCHAIN.md). From the repository root:

```powershell
.\windows\hub\installer\Build-Installer.ps1 -Version 0.0.1 -OutputDirectory .\artifacts\installer -CompilerPath '<path to ISCC.exe>'
```

This publishes a self-contained x64 build, checks the payload, starts it once in Demo mode as a smoke test, and writes `Clasp-<version>-win-x64-setup.exe` plus a `.sha256` file. It does not run the installer. See `windows/hub/installer/README.md` for what the installer does. Release signing is covered in [RELEASING-UPDATES.md](RELEASING-UPDATES.md) and only matters to the maintainer.

## The optional Rust engine

`windows/daemon` holds `clasp-engine`, a small Rust program the app can talk to for hardware control. The shipped app does not start it. To build it, run `cargo build --release --bin clasp-engine` and `cargo test` inside `windows/daemon`.
