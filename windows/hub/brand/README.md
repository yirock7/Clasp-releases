# Clasp brand kit

**Clasp** — *Connect your AirPods with one click.*

The mark is the soft charging-case silhouette from the AirPods Pro artwork (rounded shoulders, lid seam, status light):
Its tray glyph, in-app logo and app icon all share that one silhouette. All artwork here is original
and generated from code, so it can be regenerated at any size.

| File | Use |
|---|---|
| `clasp-icon-1024.png` | Store listing, README header, social preview |
| `clasp-wordmark-dark.png` | Mark + name + tagline on dark backgrounds |
| `clasp-wordmark-light.png` | Mark + name + tagline on light backgrounds |
| `../src/AppleDeviceHub.App/Assets/AppIcon.ico` | Window, taskbar, installer (16–256 px) |
| `../src/AppleDeviceHub.App/Assets/TrayIcon-*.ico` | Monochrome tray glyph (dark / light taskbar) |
| `../src/AppleDeviceHub.App/Views/ClaspGlyph.xaml` | In-app logo (welcome guide, Settings), the tray glyph as vectors; follows the theme |

Regenerate (from `windows/hub`): `python tools/make-icons.py` (add `--tray-glyphs-only` to rebuild just the tray glyph masks), then `python tools/make-wordmark.py`.

## Rules

- **Name:** always "Clasp", one word, capital C. Never "Clasp for AirPods" as a product name;
  "works with AirPods" is fine as description.
- **No Apple marks.** Do not use the Apple logo, Apple product photography, SF Pro, or the
  words "Apple"/"AirPods" in the name or icon. The About page carries the non-affiliation line.
- **Colours:** midnight-to-indigo tile (`#4648BE` → `#11163A` → `#080A1A`), seam light
  `#E1ECFF` with an electric-blue glow (`#6096FF`), status green `#78F0AA`. In the UI, the
  halo/glow colour is the `GlassHaloColor` token, so it follows light, dark and high contrast.
- **Type:** Segoe UI Variable (Display, semibold for the name, regular for the tagline).
- **Clear space:** at least 25% of the icon's width on every side of the wordmark lockup.
