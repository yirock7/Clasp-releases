# Toolchain

What you need installed, and which job needs which part. Most people only need the first row.

| Job | You need |
| --- | --- |
| Run the tests, or `dotnet build` the app | .NET 10 SDK |
| Build the installer (Native AOT publish) | .NET 10 SDK, Visual Studio C++ build tools, Inno Setup 6.7.3 |
| Regenerate icons and the wordmark | Python 3 with Pillow (maintainer only) |
| Build the optional Rust engine | Rust (see `windows/daemon`) |

All of it is for Windows 11, 64-bit.

## .NET SDK

`windows/hub/global.json` pins SDK `10.0.100` and lets it roll forward to a newer feature band (`latestFeature`), without prereleases. Install it with:

```powershell
winget install --id Microsoft.DotNet.SDK.10 --exact
```

The app targets `net10.0-windows10.0.19041.0`, x64 only, as an unpackaged, self-contained WinUI 3 app (Windows App SDK). NuGet restores everything else.

## Native AOT: Visual Studio C++ build tools

The installer build runs `dotnet publish` with Native AOT (`PublishAot` in `AppleDeviceHub.App.csproj`). That compiles the app and the parts of .NET it uses into one native `Clasp.exe`, and it links with the Visual Studio C++ tools.

Install Visual Studio or the Build Tools with the **Desktop development with C++** workload. You do not need the Visual Studio editor itself.

The AOT link step finds Visual Studio with `vswhere.exe`, which the Visual Studio Installer puts in `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer`. If that folder is not on your PATH, the publish fails with "'vswhere.exe' is not recognized". `windows/hub/installer/Build-Installer.ps1` adds the folder to PATH for its own run. If you run `dotnet publish` by hand, add it yourself.

A plain `dotnet build` and the tests do not use AOT and do not need the C++ tools.

## Inno Setup

The installer is built with Inno Setup 6.7.3. `Build-Installer.ps1` looks for `ISCC.exe` in the usual `Inno Setup 6` folders under Program Files. If yours is somewhere else, pass `-CompilerPath` with the full path. See [BUILDING.md](BUILDING.md) for the command.

## Python (icons only)

Python is not needed to build or run Clasp. Two maintainer scripts in `windows/hub/tools` redraw the brand artwork:

- `make-icons.py` writes the app and tray icons, the tray glyph masks and `brand/clasp-icon-1024.png`.
- `make-wordmark.py` draws the wordmark.

Both need Pillow (`pip install pillow`). They read Segoe UI fonts from the Windows fonts folder, so they are meant to be run on Windows. Run them from `windows/hub`, for example `python tools/make-icons.py`. The generated files are committed, so you only need this if you change the artwork.
