"""Build Clasp's app icon and every logo asset derived from it (maintainer tool, Windows only).

The mark is the charging case from Clasp's own vector artwork (Assets/Artwork/airpods-pro.svg),
lid open with the earbuds peeking out, standing on a dark frosted "liquid glass" tile with a thin
aurora-coloured edge. At 16-24 px the case is shown closed (seam + green light) because the open
lid and buds turn to mush there. The artwork is original vector art drawn in code, not a photo.

Pipeline: compose two SVGs on a 1024 tile -> rasterize with headless Microsoft Edge (or Chrome)
-> downscale with Pillow (LANCZOS) -> write the files below.

Writes (all paths relative to windows/hub, except assets/ which is at the repo root):
  brand/clasp-icon.svg, brand/clasp-icon-small.svg    the two source drawings (open / closed case)
  brand/clasp-icon-1024.png                           master raster, large art
  src/AppleDeviceHub.App/Assets/AppIcon.ico           16/20/24 from the small art, 32..256 from the large
  src/AppleDeviceHub.App/Assets/ClaspLogo.png         256 px in-app logo
  ../../assets/clasp-icon.png                         512 px README mark
  ../../assets/social-preview.png                     1280x640 social card

Usage (from windows/hub):  python tools/make-logo.py
Needs Pillow and Microsoft Edge or Google Chrome (headless), plus Segoe UI Variable for the card.
The tray glyphs are NOT made here; see make-icons.py.
"""
import io
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import time

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
HUB = os.path.normpath(os.path.join(HERE, ".."))
REPO = os.path.normpath(os.path.join(HUB, "..", ".."))
ASSETS = os.path.join(HUB, "src", "AppleDeviceHub.App", "Assets")
BRAND = os.path.join(HUB, "brand")
ARTWORK = os.path.join(ASSETS, "Artwork", "airpods-pro.svg")
FONT = r"C:\Windows\Fonts\SegUIVar.ttf"

BROWSERS = [
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
]

# ---------------------------------------------------------------------------------------------
# SVG composition
# ---------------------------------------------------------------------------------------------

TILE = 'x="56" y="56" width="912" height="912" rx="214"'   # the glass pane on the 1024 canvas


def read_artwork():
    """Pull the style, defs and drawing groups out of the app's case artwork."""
    with open(ARTWORK, encoding="utf-8") as f:
        src = f.read().replace("\r\n", "\n")
    style = re.search(r"<style>.*?</style>", src, re.S).group(0)
    defs = re.search(r"<defs>(.*?)</defs>", src, re.S).group(1)

    def part(name):
        return re.search(r'(<g data-part="%s">.*?\n  </g>)' % name, src, re.S).group(1)

    lip = re.search(r'(<g id="front-lip">.*?\n  </g>)', src, re.S).group(1)
    return style, defs, {n: part(n) for n in ("shadow", "body", "buds", "light")}, lip


# The artwork is a closed-lid case drawing with the buds shown through the opening; the lid itself
# is not part of it, so a static open lid (two stacked rounded rects and two soft shadows) is added.
LID = """
  <g>
    <rect x="71" y="66" width="158" height="44" rx="26" ry="15" fill="url(#lidSide)" stroke="var(--edge)"/>
    <rect x="79" y="70" width="142" height="31" rx="20" ry="11" fill="url(#lidIn)"/>
    <ellipse cx="114" cy="83" rx="21" ry="9" fill="#000" opacity=".08"/>
    <ellipse cx="186" cy="83" rx="21" ry="9" fill="#000" opacity=".08"/>
  </g>"""

# Closed case for 16-24 px: body, lid cap with a highlight, seam, and the green status light.
CLOSED = """
  <g>
    <path d="M71,120 L71,160 C71,188 92,202 120,202 L180,202 C208,202 229,188 229,160 L229,120 Z" fill="url(#wBody)" stroke="var(--edge)"/>
    <path d="M71,120 L71,160 C71,188 92,202 120,202 L180,202 C208,202 229,188 229,160 L229,120 Z" fill="url(#bodyEdge)"/>
    <path d="M71,126 L71,112 C71,100 82,92 97,92 L203,92 C218,92 229,100 229,112 L229,126 Z" fill="url(#wBody)" stroke="var(--edge)"/>
    <path d="M78,104 C84,97 92,96 100,96 L200,96 C208,96 216,97 222,104" stroke="#fff" stroke-width="2.4" fill="none" stroke-linecap="round"/>
    <rect x="71" y="124" width="158" height="4.5" fill="#9AA6C4"/>
    <ellipse cx="150" cy="160" rx="16" ry="16" fill="url(#lightGlow)"/>
    <circle cx="150" cy="160" r="6" fill="var(--light-core)"/>
  </g>"""

# Tile: dark frosted pane, aurora border (blurred colour blobs visible only in a hairline edge band),
# a darker cast shadow under the case, and two crisp strokes on the edge (a prism gradient and a
# faint white line) inside the tile clip.
TILE_DEFS = """
    <filter id="blurJ" x="-50%" y="-50%" width="200%" height="200%"><feGaussianBlur stdDeviation="55"/></filter>
    <filter id="blurJm" x="-50%" y="-50%" width="200%" height="200%"><feGaussianBlur stdDeviation="7"/></filter>
    <filter id="blurJ2" x="-50%" y="-50%" width="200%" height="200%"><feGaussianBlur stdDeviation="40"/></filter>
    <linearGradient id="paneJ" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#3A4170"/><stop offset=".55" stop-color="#262B4D"/><stop offset="1" stop-color="#171A31"/></linearGradient>
    <mask id="edgeJ" maskUnits="userSpaceOnUse" x="0" y="0" width="1024" height="1024">
      <rect x="0" y="0" width="1024" height="1024" fill="#fff"/>
      <rect x="78" y="78" width="868" height="868" rx="194" fill="#000" filter="url(#blurJm)"/>
    </mask>
    <linearGradient id="prismJ" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#7FF3F8"/><stop offset=".35" stop-color="#B6A2FF"/><stop offset=".7" stop-color="#FF9FCF"/><stop offset="1" stop-color="#8FA6FF"/></linearGradient>
    <clipPath id="tcJ"><rect """ + TILE + """/></clipPath>
"""

TILE_LAYER = f"""<g clip-path="url(#tcJ)"><rect {TILE} fill="url(#paneJ)"/>
      <g mask="url(#edgeJ)"><g filter="url(#blurJ)">
        <circle cx="140" cy="160" r="330" fill="#1FB9C8"/>
        <circle cx="910" cy="200" r="330" fill="#6E4CF0"/>
        <circle cx="900" cy="860" r="300" fill="#D2488C"/>
        <circle cx="400" cy="1000" r="260" fill="#C7579E"/>
        <circle cx="110" cy="880" r="280" fill="#3456DA"/>
      </g></g>
      <ellipse cx="512" cy="700" rx="290" ry="110" fill="#05060F" opacity=".45" filter="url(#blurJ2)"/><rect x="60" y="60" width="904" height="904" rx="210" fill="none" stroke="url(#prismJ)" stroke-width="5" opacity=".95"/>
      <rect x="64" y="64" width="896" height="896" rx="206" fill="none" stroke="#FFFFFF" stroke-width="1.6" opacity=".55"/></g>"""

# Glass finish: rim, top specular highlight, prism stroke.
GLASS_DEFS = """
    <linearGradient id="rim" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#FFFFFF" stop-opacity=".95"/><stop offset=".45" stop-color="#FFFFFF" stop-opacity=".25"/>
      <stop offset="1" stop-color="#2A3050" stop-opacity=".55"/></linearGradient>
    <linearGradient id="spec" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#FFFFFF" stop-opacity=".55"/><stop offset="1" stop-color="#FFFFFF" stop-opacity="0"/></linearGradient>
    <linearGradient id="prism" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#5FE3E0" stop-opacity=".9"/><stop offset=".5" stop-color="#8B6CFF" stop-opacity=".7"/>
      <stop offset="1" stop-color="#FF7FB8" stop-opacity=".9"/></linearGradient>
"""
GLASS_LAYERS = f"""
  <path d="M 120,270 C 140,120 260,80 512,80 C 764,80 884,120 904,270 C 760,220 640,206 512,206 C 384,206 264,220 120,270 Z" fill="url(#spec)"/>
  <rect {TILE} fill="none" stroke="url(#prism)" stroke-width="10" opacity=".55"/>
  <rect x="62" y="62" width="900" height="900" rx="208" fill="none" stroke="url(#rim)" stroke-width="7"/>"""


def build_svgs():
    """Return (large_svg, small_svg): open case with buds, and closed case."""
    style, defs, p, lip = read_artwork()
    case_open = (f'<g transform="translate(-43,22) scale(3.7)">'
                 f'{p["shadow"]}{LID}{p["body"]}{p["buds"]}{lip}{p["light"]}</g>')
    case_closed = f'<g transform="translate(-130,-170) scale(4.28)">{p["shadow"]}{CLOSED}</g>'

    def svg(case):
        return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024" width="1024" height="1024">'
                f'{style}\n  <defs>{defs}{TILE_DEFS}{GLASS_DEFS}</defs>\n  {TILE_LAYER}{GLASS_LAYERS}\n  {case}\n</svg>')

    return svg(case_open), svg(case_closed)


# ---------------------------------------------------------------------------------------------
# Rasterizing
# ---------------------------------------------------------------------------------------------

def find_browser():
    for b in BROWSERS:
        if os.path.isfile(b):
            return b
    sys.exit("make-logo: neither Microsoft Edge nor Google Chrome was found in Program Files.")


def rasterize(svg_text, size, browser, work):
    """Render svg_text at size x size on a transparent background with headless Edge/Chrome."""
    svg_path = os.path.join(work, "art.svg")
    html_path = os.path.join(work, "wrap.html")
    out = os.path.join(work, f"out-{size}-{abs(hash(svg_text)) % 10**8}.png")
    with open(svg_path, "w", encoding="utf-8") as f:
        f.write(svg_text)
    with open(html_path, "w", encoding="utf-8") as f:
        f.write('<!doctype html><html><body style="margin:0;background:transparent">'
                f'<img src="art.svg" width="{size}" height="{size}" style="display:block"></body></html>')
    profile = tempfile.mkdtemp(prefix="edge-profile-", dir=work)
    cmd = [browser, "--headless=new", "--disable-gpu", "--default-background-color=00000000",
           "--hide-scrollbars", f"--user-data-dir={profile}", f"--window-size={size},{size}",
           f"--screenshot={out}", "--virtual-time-budget=2000", "file:///" + html_path.replace("\\", "/")]
    subprocess.run(cmd, check=True, timeout=120, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    # Edge's launcher can return before the screenshot is flushed, so wait for a stable file.
    last, stable = -1, 0
    for _ in range(120):
        size_now = os.path.getsize(out) if os.path.isfile(out) else -1
        stable = stable + 1 if size_now > 0 and size_now == last else 0
        if stable >= 3:
            break
        last = size_now
        time.sleep(0.25)
    if stable < 3:
        sys.exit("make-logo: browser produced no screenshot.")
    img = Image.open(out)
    img.load()
    if img.size != (size, size):
        sys.exit(f"make-logo: expected {size}x{size}, browser produced {img.size}.")
    img = img.convert("RGBA")
    lo, hi = img.getchannel("A").getextrema()
    if lo != 0 or hi != 255:
        sys.exit(f"make-logo: no transparency in the render (alpha range {lo}..{hi}); "
                 "the browser ignored the transparent background.")
    return img


def down(img, size):
    return img.resize((size, size), Image.LANCZOS)


# ---------------------------------------------------------------------------------------------
# Outputs
# ---------------------------------------------------------------------------------------------

def save(img, path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    img.save(path)
    print("wrote", os.path.relpath(path, REPO), img.size)


def write_ico(path, frames):
    """frames: {size: RGBA image}. PNG-compressed 32-bit frames."""
    sizes = sorted(frames)
    blobs = []
    for s in sizes:
        buf = io.BytesIO()
        frames[s].save(buf, format="PNG")
        blobs.append(buf.getvalue())
    offset = 6 + 16 * len(sizes)
    out = struct.pack("<HHH", 0, 1, len(sizes))
    for s, data in zip(sizes, blobs):
        dim = 0 if s >= 256 else s
        out += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    out += b"".join(blobs)
    with open(path, "wb") as f:
        f.write(out)
    print("wrote", os.path.relpath(path, REPO), f"({len(out)} bytes, sizes {sizes})")


def seg_font(size, weight):
    f = ImageFont.truetype(FONT, size)
    try:
        f.set_variation_by_axes([weight, 36])  # wght, opsz (Display)
    except (OSError, ValueError):
        pass
    return f


def social_card(icon):
    """1280x640 card: pane-coloured gradient, icon left, wordmark and tagline right."""
    W, H = 1280, 640
    bg = Image.new("RGBA", (W, H))
    d = ImageDraw.Draw(bg)
    top, bot = (0x26, 0x2B, 0x4D), (0x17, 0x1A, 0x31)   # #262B4D -> #171A31
    for y in range(H):
        t = y / (H - 1)
        d.line([(0, y), (W, y)], fill=tuple(round(top[i] + (bot[i] - top[i]) * t) for i in range(3)) + (255,))
    size = 400
    mark = icon.resize((size, size), Image.LANCZOS)
    bg.alpha_composite(mark, (90, (H - size) // 2))
    name_font, tag_font = seg_font(140, 600), seg_font(32, 400)
    tag = "Your AirPods on Windows, one click away"
    x = 90 + size + 60
    nb = d.textbbox((0, 0), "Clasp", font=name_font)
    tb = d.textbbox((0, 0), tag, font=tag_font)
    if x + 4 + (tb[2] - tb[0]) > W - 90:
        sys.exit("make-logo: tagline does not fit the social card.")
    gap = 30
    block = (nb[3] - nb[1]) + gap + (tb[3] - tb[1])
    y0 = (H - block) // 2
    d.text((x - nb[0], y0 - nb[1]), "Clasp", font=name_font, fill=(245, 247, 250))
    d.text((x + 4 - tb[0], y0 + (nb[3] - nb[1]) + gap - tb[1]), tag, font=tag_font, fill=(170, 178, 194))
    return bg.convert("RGB")


def main():
    large_svg, small_svg = build_svgs()
    for name, text in (("clasp-icon.svg", large_svg), ("clasp-icon-small.svg", small_svg)):
        path = os.path.join(BRAND, name)
        os.makedirs(BRAND, exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(text + "\n")
        print("wrote", os.path.relpath(path, REPO))

    browser = find_browser()
    work = tempfile.mkdtemp(prefix="clasp-logo-")
    try:
        big = rasterize(large_svg, 1024, browser, work)      # >= 4x of every large frame up to 256
        small = rasterize(small_svg, 256, browser, work)     # 16x the smallest, 4x of 64
    finally:
        shutil.rmtree(work, ignore_errors=True)

    save(big, os.path.join(BRAND, "clasp-icon-1024.png"))
    frames = {s: down(small, s) for s in (16, 20, 24)}
    frames.update({s: down(big, s) for s in (32, 40, 48, 64, 96, 128, 256)})
    write_ico(os.path.join(ASSETS, "AppIcon.ico"), frames)
    save(down(big, 256), os.path.join(ASSETS, "ClaspLogo.png"))
    save(down(big, 512), os.path.join(REPO, "assets", "clasp-icon.png"))
    save(social_card(big), os.path.join(REPO, "assets", "social-preview.png"))


if __name__ == "__main__":
    main()
