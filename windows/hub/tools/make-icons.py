"""Generate Clasp's tray glyphs (ORIGINAL artwork, no third-party artwork).

The glyph is the soft charging-case silhouette from the AirPods Pro artwork (rounded shoulders,
lid seam, small status light) in one flat colour. Views/ClaspGlyph.xaml draws the same numbers as
vectors for the High Contrast fallback of the in-app logo (see GRID/BOX/... below). The full-colour
app icon and logo are NOT made here: tools/make-logo.py owns AppIcon.ico, ClaspLogo.png and
brand/clasp-icon-1024.png.

Writes:
  src/AppleDeviceHub.App/Assets/TrayIcon-Dark.ico    white glyph for a dark taskbar
  src/AppleDeviceHub.App/Assets/TrayIcon-Light.ico   near-black glyph for a light taskbar

Everything is drawn at 1024 px with Pillow and downsampled with Lanczos, so small sizes stay
crisp. Tray glyphs get a slightly heavier stroke because 16 px eats thin lines.

Also writes (see "Tray glyph masks" below):
  src/AppleDeviceHub.App/Shell/TrayGlyphMasks.g.cs   per-ArtworkGroup alpha masks for the tray
  brand/tray-glyphs-preview.png                      review sheet for those masks

Usage (from windows/hub):  python tools/make-icons.py [--tray-glyphs-only]

  --tray-glyphs-only   rebuild only the tray glyph masks and their preview, skipping the tray .ico files.
  (Plain `python -I` ignores per-user packages, so Pillow installed per-user is not found.)
"""
import io
import os
import struct

from PIL import Image, ImageChops, ImageDraw, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
HUB = os.path.join(HERE, "..")
ASSETS = os.path.join(HUB, "src", "AppleDeviceHub.App", "Assets")
BRAND = os.path.join(HUB, "brand")

S = 1024  # master canvas


# --- The glyph -----------------------------------------------------------------------------
# One silhouette for every surface, taken from the AirPods Pro case drawing in Assets/Artwork:
# a wide soft body (squarer shoulders, deeply rounded bottom), a lid seam near the top and a small
# status light below it. Units are a 64-wide grid; the glyph's own box is 56 x 38 at (4, 13).
# Views/ClaspGlyph.xaml draws the very same numbers as vector shapes, so keep them in step.
GRID = 64
BOX = (4.0, 13.0, 60.0, 51.0)      # outer bounds x0, y0, x1, y1
STROKE = 5.5                       # outline weight (heavier than the artwork so 16 px keeps it)
R_TOP, R_BOT = 11.5, 16.5          # outer corner radii: shoulders, base
SEAM_Y, SEAM_W = 27.5, 3.5         # lid seam: centre line and thickness
LIGHT = (32.0, 38.5, 3.2)          # status light: cx, cy, radius


def rrect_points(x0, y0, x1, y1, r_top, r_bot, steps=24):
    """Polygon for a rectangle with one radius on the top corners and another on the bottom."""
    import math
    pts = []

    def arc(cx, cy, r, a0, a1):
        for i in range(steps + 1):
            a = math.radians(a0 + (a1 - a0) * i / steps)
            pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))

    arc(x1 - r_top, y0 + r_top, r_top, -90, 0)      # top right
    arc(x1 - r_bot, y1 - r_bot, r_bot, 0, 90)       # bottom right
    arc(x0 + r_bot, y1 - r_bot, r_bot, 90, 180)     # bottom left
    arc(x0 + r_top, y0 + r_top, r_top, 180, 270)    # top left
    return pts


def glyph_mask(scale, offset):
    """L-mode mask of the glyph. scale maps grid units to px; offset is the grid origin in px.

    Outline + seam + light (the tray / High Contrast glyph).
    """
    m = Image.new("L", (S, S), 0)

    def P(pts):
        return [(offset[0] + x * scale, offset[1] + y * scale) for x, y in pts]

    x0, y0, x1, y1 = BOX
    d = ImageDraw.Draw(m)
    d.polygon(P(rrect_points(x0, y0, x1, y1, R_TOP, R_BOT)), fill=255)
    d.polygon(P(rrect_points(x0 + STROKE, y0 + STROKE, x1 - STROKE, y1 - STROKE,
                             R_TOP - STROKE, R_BOT - STROKE)), fill=0)
    sy = SEAM_Y
    d.rectangle([offset[0] + x0 * scale, offset[1] + (sy - SEAM_W / 2) * scale,
                 offset[0] + x1 * scale, offset[1] + (sy + SEAM_W / 2) * scale], fill=255)
    cx, cy, r = LIGHT
    d.ellipse([offset[0] + (cx - r) * scale, offset[1] + (cy - r) * scale,
               offset[0] + (cx + r) * scale, offset[1] + (cy + r) * scale], fill=255)
    return m


def master_glyph(color):
    """Monochrome tray glyph, scaled so the glyph's box fills ~94% of the canvas width."""
    scale = S * 0.96 / (BOX[2] - BOX[0])
    gw, gh = (BOX[2] - BOX[0]) * scale, (BOX[3] - BOX[1]) * scale
    off = ((S - gw) / 2 - BOX[0] * scale, (S - gh) / 2 - BOX[1] * scale)
    m = glyph_mask(scale, off)
    img = Image.new("RGBA", (S, S), (*color, 0))
    img.putalpha(m)
    return img


def png_bytes(img):
    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return buf.getvalue()


def dib_bytes(img):
    w, h = img.size
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    px = img.load()
    xor = b"".join(bytes((px[x, y][2], px[x, y][1], px[x, y][0], px[x, y][3]))
                   for y in reversed(range(h)) for x in range(w))
    mask_row = ((w + 31) // 32) * 4
    return header + xor + b"\x00" * (mask_row * h)


def write_ico(path, master, sizes):
    images = []
    for s in sizes:
        img = master.resize((s, s), Image.LANCZOS)
        images.append((s, png_bytes(img) if s >= 256 else dib_bytes(img)))
    offset = 6 + 16 * len(images)
    out = struct.pack("<HHH", 0, 1, len(images))
    for s, data in images:
        dim = 0 if s >= 256 else s
        out += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    out += b"".join(d for _, d in images)
    with open(path, "wb") as f:
        f.write(out)
    print(f"wrote {os.path.relpath(path, HUB)} ({len(out)} bytes, sizes {sizes})")


# --- Tray glyph masks (one silhouette per ArtworkGroup) -------------------------------------
# Same 64-unit grid and rounded flat style as the case glyph above. Strokes are 6.2 units so they
# land at >= 1.5 px at 16 px. Each shape is drawn at 1024 px (16 px per unit) with Pillow and
# downsampled to 16/20/24/32 px; the result is the 8-bit alpha mask the tray renderer tints.
import math

TG_STROKE = 6.2
TG_SIZES = [16, 20, 24, 32]
TG_GROUPS = ["Generic", "StemEarbuds", "ProEarbuds", "MaxHeadphones", "OverEarHeadphones",
             "OnEarHeadphones", "BudsWithCase", "EarHookWithCase", "EarHookCorded", "Neckband"]
K = S / GRID


class _Canvas:
    def __init__(self):
        self.m = Image.new("L", (S, S), 0)
        self.d = ImageDraw.Draw(self.m)

    def _p(self, pts):
        return [(x * K, y * K) for x, y in pts]

    def poly(self, pts, v=255):
        self.d.polygon(self._p(pts), fill=v)

    def disc(self, cx, cy, r, v=255):
        self.d.ellipse([(cx - r) * K, (cy - r) * K, (cx + r) * K, (cy + r) * K], fill=v)

    def rrect(self, x0, y0, x1, y1, r, v=255):
        self.d.rounded_rectangle([x0 * K, y0 * K, x1 * K, y1 * K], radius=r * K, fill=v)

    def line(self, pts, w=TG_STROKE, v=255):
        P = self._p(pts)
        self.d.line(P, fill=v, width=int(round(w * K)), joint="curve")
        for x, y in (P[0], P[-1]):
            self.d.ellipse([x - w * K / 2, y - w * K / 2, x + w * K / 2, y + w * K / 2], fill=v)

    def arc(self, cx, cy, rx, ry, a0, a1, w=TG_STROKE, steps=64):
        pts = [(cx + rx * math.cos(math.radians(a0 + (a1 - a0) * i / steps)),
                cy + ry * math.sin(math.radians(a0 + (a1 - a0) * i / steps))) for i in range(steps + 1)]
        self.line(pts, w)

    def case(self, x0, y0, x1, y1, r_top, r_bot, seam_y=None, light=None, w=TG_STROKE):
        self.poly(rrect_points(x0, y0, x1, y1, r_top, r_bot))
        self.poly(rrect_points(x0 + w, y0 + w, x1 - w, y1 - w, max(r_top - w, 1), max(r_bot - w, 1)), 0)
        if seam_y is not None:
            self.d.rectangle([x0 * K, (seam_y - 1.9) * K, x1 * K, (seam_y + 1.9) * K], fill=255)
        if light:
            self.disc(*light)


def _tg_generic(c):      # the Clasp case glyph, same numbers as BOX/R_*/SEAM_Y/LIGHT above
    c.case(*BOX, R_TOP, R_BOT, SEAM_Y, LIGHT)


def _tg_stem(c):         # short, wide, squarer case: lid seam + light
    c.case(9, 13, 55, 51, 12, 17, 27, (32, 39, 3.4))


def _tg_max(c):          # tall rounded cups, heavy canopy band
    # flat-topped frame (outer band) + inner canopy strip, squarer tall cups
    c.line([(11, 32), (11, 18), (17, 11), (47, 11), (53, 18), (53, 32)], 6.4)
    c.line([(22, 24), (42, 24)], 4.4)
    c.rrect(3, 28, 19, 60, 5)
    c.rrect(45, 28, 61, 60, 5)


def _tg_overear(c):      # oval cups, plain band
    c.arc(32, 30, 21, 20, 180, 360, TG_STROKE)
    c.rrect(4, 28, 18, 54, 7)
    c.rrect(46, 28, 60, 54, 7)


def _tg_onear(c):        # smaller round cups, tighter band
    c.arc(32, 30, 18, 17, 180, 360, TG_STROKE)
    c.disc(14, 40, 9)
    c.disc(50, 40, 9)


def _tg_buds_case(c):    # horizontal pill case, no seam, light
    c.rrect(6, 19, 58, 47, 14)
    c.rrect(6 + TG_STROKE, 19 + TG_STROKE, 58 - TG_STROKE, 47 - TG_STROKE, 14 - TG_STROKE, 0)
    c.disc(32, 33, 3.6)


def _tg_hook_case(c):    # tall case on the left, ear hook curling off its top right
    c.case(4, 8, 40, 60, 13, 14, 25, (22, 46, 3.4))
    c.arc(43, 22, 13, 13, -100, 75, 8.0)
    c.disc(46.5, 36, 6.0)


def _tg_hook_corded(c):  # two hooks (arch + bud) joined by a sagging cord
    for sx in (1, -1):
        def X(x):
            return x if sx == 1 else 64 - x
        # "?" shape: stem up from the bud, then a hook arc curling over and back down
        hook = [(X(8), 42), (X(5.5), 24)]
        hook += [(X(16.5 + 11 * math.cos(math.radians(a))), 24 + 11 * math.sin(math.radians(a)))
                 for a in range(180, 411, 10)]
        c.line(hook, 6.2)
        c.disc(X(8), 46, 6.4)                                   # round bud
    pts = [(8 + 48 * i / 40, 50 + 5 * math.sin(math.pi * i / 40)) for i in range(41)]
    c.line(pts, 5.0)                                            # short sagging cord along the bottom


def _tg_neckband(c):     # U-shaped band with a bud at each tip
    c.arc(32, 22, 23, 26, 0, 180, TG_STROKE)
    for x in (9, 55):
        c.line([(x, 22), (x, 14)], TG_STROKE)
        c.disc(x, 11, 5.4)


TG_DRAW = {
    "Generic": _tg_generic, "StemEarbuds": _tg_stem, "ProEarbuds": _tg_generic,
    "MaxHeadphones": _tg_max, "OverEarHeadphones": _tg_overear, "OnEarHeadphones": _tg_onear,
    "BudsWithCase": _tg_buds_case, "EarHookWithCase": _tg_hook_case,
    "EarHookCorded": _tg_hook_corded, "Neckband": _tg_neckband,
}


def tray_glyph_master(group):
    c = _Canvas()
    TG_DRAW[group](c)
    m = c.m
    # centre the drawn ink in the 64-unit grid so every glyph sits in the same place
    bb = m.getbbox()
    cx, cy = (bb[0] + bb[2]) / 2, (bb[1] + bb[3]) / 2
    shifted = Image.new("L", (S, S), 0)
    shifted.paste(m, (int(round(S / 2 - cx)), int(round(S / 2 - cy))))
    return shifted


def tray_glyph_mask(master, size, fill=None, pad=1):
    """Fit the ink so its larger dimension fills `fill` px (default: cell minus `pad` px each side),
    keeping aspect ratio, centred in a size x size cell."""
    ink = master.crop(master.getbbox())
    fill = fill if fill is not None else size - 2 * pad
    k = fill / max(ink.size)
    w, h = max(1, round(ink.width * k)), max(1, round(ink.height * k))
    out = Image.new("L", (size, size), 0)
    out.paste(ink.resize((w, h), Image.LANCZOS), ((size - w) // 2, (size - h) // 2))
    return out


def tray_glyph_with_number(master, size, text="80"):
    """Preview of the T3 layout: glyph at 75 % top-left, bold number bottom-right with a knock-out
    halo. Drawn at 8x and downsampled, so it approximates (not reproduces) the GDI rendering."""
    from PIL import ImageFont
    q = 8
    big = size * q
    g = tray_glyph_mask(master, size, fill=round((size - 2) * 0.75), pad=0)
    glyph = Image.new("L", (size, size), 0)
    glyph.paste(g, (1, 1))
    glyph = glyph.resize((big, big), Image.LANCZOS)
    try:
        font = ImageFont.truetype("segoeuib.ttf", int(size * 0.6 * q))
    except OSError:
        font = ImageFont.load_default()
    txt = Image.new("L", (big, big), 0)
    ImageDraw.Draw(txt).text((big - 0.5 * q, big - 0.5 * q), text, font=font, fill=255, anchor="rb")
    halo = txt.filter(ImageFilter.MaxFilter(2 * q + 1))
    glyph = ImageChops.multiply(glyph, ImageChops.invert(halo))
    return ImageChops.lighter(glyph, txt).resize((size, size), Image.LANCZOS)


CS_HEADER = """// <auto-generated>
// Generated by tools/make-icons.py - do not edit. Re-run `python tools/make-icons.py` from windows/hub.
// One 8-bit alpha mask per ArtworkGroup at 16/20/24/32 px (row-major, size*size bytes). The tray
// renderer tints them with the taskbar foreground colour.
// </auto-generated>

using AppleDeviceHub.Core.Ble;

namespace AppleDeviceHub.App.Shell;

internal static class TrayGlyphMasks
{
    /// <summary>The alpha mask for a group at a size snapped like <see cref="TrayIconRenderer.SnapSize"/>.</summary>
    public static ReadOnlySpan<byte> Get(ArtworkGroup group, int size) => (group, TrayIconRenderer.SnapSize(size)) switch
    {
"""


def write_tray_glyphs():
    masks = {g: tray_glyph_master(g) for g in TG_GROUPS}
    out = [CS_HEADER]
    for g in TG_GROUPS:
        for s in TG_SIZES:
            out.append(f"        (ArtworkGroup.{g}, {s}) => {g}{s},\n")
    out.append("        (_, 16) => Generic16,\n        (_, 20) => Generic20,\n        (_, 24) => Generic24,\n        _ => Generic32,\n    };\n")
    for g in TG_GROUPS:
        for s in TG_SIZES:
            data = list(tray_glyph_mask(masks[g], s).tobytes())
            out.append(f"\n    private static ReadOnlySpan<byte> {g}{s} => new byte[]\n    {{\n")
            for r in range(s):
                out.append("        " + ", ".join(str(v) for v in data[r * s:(r + 1) * s]) + ",\n")
            out.append("    };\n")
    out.append("}\n")
    path = os.path.join(HUB, "src", "AppleDeviceHub.App", "Shell", "TrayGlyphMasks.g.cs")
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("".join(out))
    print(f"wrote {os.path.relpath(path, HUB)}")
    write_tray_preview(masks)


def write_tray_preview(masks):
    """Sheet: one row per group; per background (dark, light) the four real sizes, then 4x enlarged."""
    from PIL import ImageFont
    try:
        font = ImageFont.truetype("segoeui.ttf", 13)
        small = ImageFont.truetype("segoeui.ttf", 11)
    except OSError:
        font = small = ImageFont.load_default()
    pad, rowh, labw, cell = 12, 4 * 32 + 16, 150, 40
    big_sizes = [16, 24]
    panel_w = 8 * cell + 2 * sum(s * 4 + 8 for s in big_sizes) + 3 * pad
    W = labw + 2 * panel_w + pad
    H = 40 + rowh * len(TG_GROUPS) + pad
    img = Image.new("RGB", (W, H), (128, 128, 128))
    d = ImageDraw.Draw(img)
    for pi, (bg, fg) in enumerate([((32, 32, 32), (255, 255, 255)), ((243, 243, 243), (24, 24, 28))]):
        x0 = labw + pi * panel_w
        d.rectangle([x0, 0, x0 + panel_w - 2, H], fill=bg)
        d.text((x0 + 6, 6), "dark taskbar" if pi == 0 else "light taskbar", font=font, fill=fg)
        d.text((x0 + 6, 22), "actual 16/20/24/32 | 75% + \"80\" actual | 4x 16,24 | 75% + \"80\" 4x 16,24",
               font=small, fill=fg)
    for gi, g in enumerate(TG_GROUPS):
        y = 40 + gi * rowh
        d.text((8, y + rowh // 2 - 16), g, font=font, fill=(255, 255, 255))
        for pi, fg in enumerate([(255, 255, 255), (24, 24, 28)]):
            x = labw + pi * panel_w + 6
            for s in TG_SIZES:
                m = tray_glyph_mask(masks[g], s)
                img.paste(Image.new("RGB", (s, s), fg), (x + (cell - s) // 2, y + (rowh - 16 - s) // 2), m)
                x += cell
            x += pad
            for s in TG_SIZES:
                m = tray_glyph_with_number(masks[g], s)
                img.paste(Image.new("RGB", (s, s), fg), (x + (cell - s) // 2, y + (rowh - 16 - s) // 2), m)
                x += cell
            x += pad
            for fn in (tray_glyph_mask, tray_glyph_with_number):
                for s in big_sizes:
                    m = fn(masks[g], s).resize((s * 4, s * 4), Image.NEAREST)
                    img.paste(Image.new("RGB", (s * 4, s * 4), fg), (x, y + (rowh - 16 - s * 4) // 2), m)
                    x += s * 4 + 8
                x += pad
    path = os.path.join(BRAND, "tray-glyphs-preview.png")
    img.save(path)
    print(f"wrote {os.path.relpath(path, HUB)}")


if __name__ == "__main__":
    import sys
    os.makedirs(ASSETS, exist_ok=True)
    os.makedirs(BRAND, exist_ok=True)
    if "--tray-glyphs-only" in sys.argv:
        write_tray_glyphs()
        raise SystemExit(0)
    write_ico(os.path.join(ASSETS, "TrayIcon-Dark.ico"), master_glyph((255, 255, 255)), [16, 20, 24, 32, 40, 48, 64])
    write_ico(os.path.join(ASSETS, "TrayIcon-Light.ico"), master_glyph((24, 24, 28)), [16, 20, 24, 32, 40, 48, 64])
    write_tray_glyphs()
