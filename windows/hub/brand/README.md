# Clasp brand kit

**Clasp** — *Connect your AirPods with one click.*

The mark is the charging case from Clasp's own artwork (`Assets/Artwork/airpods-pro.svg`), lid open with the
earbuds peeking out, on a dark frosted "liquid glass" tile with a hairline aurora-coloured border. At 16–24 px the
icon shows the closed case with its seam and green light instead, because the open lid and buds blur at that
size. The tray glyph is the case's soft silhouette in one flat colour. All artwork here is original vector art
drawn in code (not a photo) and can be regenerated at any size.

| File | Use |
|---|---|
| `clasp-icon.svg` | Source drawing of the icon: open case with buds (32 px and up) |
| `clasp-icon-small.svg` | Source drawing for 16, 20 and 24 px: closed case, seam, green light |
| `clasp-icon-1024.png` | Master raster of `clasp-icon.svg`; store listing, social artwork |
| `clasp-wordmark-dark.png` | Mark + name + tagline on dark backgrounds |
| `clasp-wordmark-light.png` | Mark + name + tagline on light backgrounds |
| `../src/AppleDeviceHub.App/Assets/AppIcon.ico` | Window, taskbar, installer (16–256 px; 16/20/24 from the small drawing) |
| `../src/AppleDeviceHub.App/Assets/ClaspLogo.png` | Full-colour in-app logo (Settings), 256 px |
| `../src/AppleDeviceHub.App/Assets/TrayIcon-*.ico` | Monochrome tray glyph (dark / light taskbar) |
| `../src/AppleDeviceHub.App/Views/ClaspGlyph.xaml` | The tray glyph shape as vectors: the High Contrast fallback of the in-app logo and the tray-icon stand-in in the welcome illustration; follows the theme |
| `../../../assets/clasp-icon.png` | 512 px mark for the repository README |
| `../../../assets/social-preview.png` | 1280 x 640 social card (icon, name, tagline) |

Regenerate (from `windows/hub`, Windows only, maintainer-only; needs Pillow and Microsoft Edge or Chrome):
`python tools/make-logo.py`, then `python tools/make-icons.py --tray-glyphs-only` (tray glyph masks; drop the flag to also
rewrite the tray `.ico` files), then `python tools/make-wordmark.py`.

## Rules

- **Name:** always "Clasp", one word, capital C. Never "Clasp for AirPods" as a product name;
  "works with AirPods" is fine as description.
- **No Apple marks.** Do not use the Apple logo, Apple product photography, SF Pro, or the
  words "Apple"/"AirPods" in the name or icon. The About page carries the non-affiliation line.
- **Colours:** dark frosted pane (`#3A4170` → `#262B4D` → `#171A31`) with a hairline aurora border blending
  teal `#1FB9C8`, violet `#6E4CF0`, magenta `#D2488C` / `#C7579E` and blue `#3456DA`, edged by a crisp prism stroke
  (`#7FF3F8` → `#B6A2FF` → `#FF9FCF` → `#8FA6FF`) and a faint white line; the case keeps
  its own soft whites and the green status light. In the UI, the halo/glow colour is the
  `GlassHaloColor` token, so it follows light, dark and high contrast.
- **Type:** Segoe UI Variable (Display, semibold for the name, regular for the tagline).
- **Clear space:** at least 25% of the icon's width on every side of the wordmark lockup.
