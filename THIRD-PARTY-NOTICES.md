# Third-party notices

Clasp stands on other people's work. This page lists what it uses, where it came from, and under which licence. Clasp itself is under the [GNU General Public License v3.0](LICENSE).

If I have got something wrong here, please [open an issue](https://github.com/yirock7/Clasp-releases/issues/new/choose) and I will fix it.

## Projects Clasp is built on

### LibrePods

Clasp is built on the research of [LibrePods](https://github.com/kavishdevar/librepods) by Kavish Devar and contributors, who documented how AirPods talk over Bluetooth. The protocol notes in [`docs/`](docs/README.md) (`AAP Definitions.md`, `opcodes.md`, `control_commands.md`, `device-info.md`) come from that project.
Licence: GPL-3.0. Copyright (C) 2025 LibrePods contributors. `docs/AAP Definitions.md` was copied from LibrePods when it carried AGPL-3.0 text, and it keeps that licence text. The AGPL-3.0 permits combining with GPL-3.0 work, and Clasp as a whole is distributed under GPL-3.0.

### CAPod and MagicPodsCore

I used two other open-source projects as references for facts about devices: [CAPod](https://github.com/d4rken-org/capod) and [MagicPodsCore](https://github.com/steam3d/MagicPodsCore). From them I took model identifiers and battery layouts (which Bluetooth model number is which device, and where the left, right and case values sit in a broadcast), written into Clasp's own model catalog. Both are GPL-3.0 projects: [CAPod](https://github.com/d4rken-org/capod) is GPL-3.0 and [MagicPodsCore](https://github.com/steam3d/MagicPodsCore) is GPL-3.0. Facts such as a model number are not copyrightable, and I have not copied their source code, but they deserve the credit.

## Code from NTPods

[NTPods](https://github.com/arctumn/ntpods): copyright arctumn and NTPods contributors. Code from it is in `windows/hub/src/AppleDeviceHub.Core/Interop/Upstream/NtpodsMessages.cs`, `windows/daemon/src/aap.rs` and `windows/drivers/aap`.
Licence: GPL-3.0.

## Tools used to build Clasp

### Inno Setup

The installer is built with [Inno Setup](https://jrsoftware.org/isinfo.php) by Jordan Russell and Martijn Laan, and the setup program contains parts of its runtime. Inno Setup is free to use and distribute under its own permissive licence ([Inno Setup License](https://jrsoftware.org/files/is/license.txt)), which asks that the copyright notice is kept.

### Python (icon generator)

`windows/hub/tools/make-logo.py` draws the app icon and logo, and `windows/hub/tools/make-icons.py` draws the tray glyph shapes. It is a developer tool and is not shipped.

## Libraries and runtimes inside the app

| Component | Used for | Licence |
|---|---|---|
| .NET runtime (Microsoft) | Compiled into the self-contained app | MIT |
| [Microsoft.WindowsAppSDK.WinUI](https://www.nuget.org/packages/Microsoft.WindowsAppSDK.WinUI) 2.3.9 | The user interface (WinUI 3), shipped beside the app | Microsoft Software License Terms for the package; see the package. Its dependencies (other `Microsoft.WindowsAppSDK.*` packages) are listed with their own terms on NuGet |
| [Microsoft.Windows.SDK.BuildTools](https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools) 10.0.26100.9169 | Build time only | Microsoft Software License Terms; see the package |
| Microsoft WebView2 loader | Comes with the Windows App SDK runtime | BSD-style licence from Microsoft; see the package |

The in-app page **Settings > About > Open-source licenses** shows a short version of this list.

## Libraries used only for testing and development

These are not part of the installer.

| Package | Licence |
|---|---|
| [xunit](https://www.nuget.org/packages/xunit) 2.9.2 | Apache-2.0 |
| [xunit.runner.visualstudio](https://www.nuget.org/packages/xunit.runner.visualstudio) 2.8.2 | Apache-2.0 |
| [Microsoft.NET.Test.Sdk](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk) 17.12.0 | MIT |
| [Microsoft.Extensions.TimeProvider.Testing](https://www.nuget.org/packages/Microsoft.Extensions.TimeProvider.Testing) 9.0.0 | MIT |

## The optional Rust engine (not shipped)

`windows/daemon` uses the Rust crates `serde`, `serde_json` and `windows-sys`, each under MIT OR Apache-2.0, plus their own dependencies (see `windows/daemon/Cargo.lock`). The app does not include or start the engine.

## Trademarks and artwork

AirPods, Beats, Apple and related names are trademarks of Apple Inc. Clasp is not affiliated with or endorsed by Apple or Beats; the names are used only to say which devices Clasp works with. Clasp's artwork, including the device pictures, is original and does not use Apple's product images.
