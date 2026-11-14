# ArtworkGallery

A small review harness for the model artwork (see `docs/MODEL-ARTWORK-PLAN.md`). It shows every `ArtworkGroup` in the real `ModelArtwork` and `CaseArtwork` controls. The App sources are linked, not copied, so the gallery cannot drift from what ships.

It is not in `AppleDeviceHub.sln`, is never published or installed, and has no tray, single-instance lock, settings file or Bluetooth. It is unpackaged, self-contained and x64, with the same package versions as the App, but with `PublishAot` off.

## Build and run

```
cd windows/hub
dotnet build tools/ArtworkGallery -c Debug
tools/ArtworkGallery/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/ArtworkGallery.exe --theme=dark --state=closed
```

(If the output folder differs, look under `tools/ArtworkGallery/bin`.)

To save an eight-model review sheet with simulated status cues:

```
tools/ArtworkGallery/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/ArtworkGallery.exe --page=models --state=cues --capture=C:\Users\you\Pictures\beats-artwork.png --result=C:\Users\you\Pictures\beats-artwork-result.txt
```

## Arguments

| Argument | Meaning |
|---|---|
| `--theme=light` or `--theme=dark` | Theme set on the window root (default light) |
| `--state=open`, `closed`, `play`, `loop`, or `cues` | State applied to each drawing. `cues` shows connected, charging, and worn cues as a simulated review state only; it does not represent live device data. |
| `--cycle` | One artwork steps through every group twice, 200 ms apart, rotating `PlayOpen`, `ShowClosed`, `ShowOpen` |
| `--page=models` | Labeled 4-by-2 gallery of Powerbeats 3, Powerbeats 4, BeatsX, Beats Flex, Powerbeats Pro, Beats Fit Pro, Beats Solo Buds, and Beats Studio 3 using their per-model renderer. |
| `--model=0x2720 [--colorway=id]` | Show one large drawing for a model id, optionally using a colorway declared by that drawing. |
| `--page=controls` | Show the real glass toggle and slider (linked `GlassStyles.xaml`) in every state instead of the artwork: toggle off, on, disabled off and on, a long wrapping header, row toggles with no header, sliders at 0, 40 and 100, a disabled slider and the volume slider with its end icons. Light or dark from `--theme` (default page stays the artwork) |
| `--capture=<absolute PNG path>` | Save the visible gallery root to a PNG about 180 ms after layout and the requested state are applied, then exit 0. A play-state capture shows the opening transition. The path must be absolute and end in .png. |
| `--reduce-motion` | Calls `EffectsPolicy.SetOverrides` with reduced motion: no knob or thumb growth, and the toggle jumps instead of sliding |
| `--reduce-transparency` | Calls `EffectsPolicy.SetOverrides` with reduced transparency: the soft knob and thumb shadows are hidden |
| `--result=<path>` | Write the success message or exception details here before capture/cycle exit. |

The window title is `ArtworkGallery`.

## Linked from the App

`Views/ModelArtwork.xaml(.cs)`, `Views/CaseArtwork.xaml(.cs)`, `Design/Motion.cs`, `Design/EffectsPolicy.cs`, `Design/GlassTokens.xaml`, and for the controls page `Design/GlassStyles.xaml`, `Design/GlassAccentOverrides.xaml`, `Design/ControlFeel.cs`, `Design/GlassSurface.cs`, `Design/GlassButton.cs`, `Design/GlassStatusChip.cs`, `Design/GlassBind.cs`. No stubs are needed.

High Contrast cannot be forced from an argument; turn it on in Windows settings and start the gallery.
