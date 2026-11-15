"""Render the Clasp wordmark (mark + name + tagline) for README, store and social use.

Usage (from windows/hub, after make-logo.py):  python -I tools/make-wordmark.py
Needs Segoe UI Variable (ships with Windows 11).
"""
import os
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
BRAND = os.path.join(HERE, "..", "brand")
FONT = r"C:\Windows\Fonts\SegUIVar.ttf"


def font(size, weight):
    f = ImageFont.truetype(FONT, size)
    try:
        f.set_variation_by_axes([weight, 36])  # wght, opsz (Display)
    except (OSError, ValueError):
        pass
    return f


def render(theme):
    ink = (245, 247, 250) if theme == "dark" else (17, 19, 24)
    sub = (170, 178, 194) if theme == "dark" else (92, 99, 112)
    W, H = 1600, 520
    img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    mark = Image.open(os.path.join(BRAND, "clasp-icon-1024.png")).resize((360, 360), Image.LANCZOS)
    img.paste(mark, (80, (H - 360) // 2), mark)
    d = ImageDraw.Draw(img)
    name_font, tag_font = font(210, 650), font(52, 400)
    tagline = "Connect your AirPods with one click."  # was the lid claim; see packet hardware-readiness
    nb = d.textbbox((0, 0), "Clasp", font=name_font)   # includes the p descender
    tb = d.textbbox((0, 0), tagline, font=tag_font)
    gap = 26
    block = (nb[3] - nb[1]) + gap + (tb[3] - tb[1])
    top = (H - block) // 2
    d.text((500 - nb[0], top - nb[1]), "Clasp", font=name_font, fill=ink)
    d.text((506 - tb[0], top + (nb[3] - nb[1]) + gap - tb[1]), tagline, font=tag_font, fill=sub)
    img = img.crop(img.getbbox())
    pad = 40
    out = Image.new("RGBA", (img.width + pad * 2, img.height + pad * 2), (0, 0, 0, 0))
    out.paste(img, (pad, pad), img)
    path = os.path.join(BRAND, f"clasp-wordmark-{theme}.png")
    out.save(path)
    print("wrote", os.path.relpath(path, os.path.join(HERE, "..")))


if __name__ == "__main__":
    render("dark")
    render("light")
