#!/usr/bin/env python3
"""Draws Lightcone's Steam library art (capsules, hero, logo, icon) into data/steam-art/.
Clean shapes only: a projector lens throwing a cone of warm light, on a dark gradient, and the wordmark.
Run: python3 tools/art/make_steam_art.py"""
import math
import os
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "data", "steam-art")
FONT = os.path.join(ROOT, "src", "Lightcone.App", "Assets", "Fonts", "ChakraPetch-SemiBold.ttf")
BG_TOP, BG_BOTTOM = (20, 17, 28), (8, 9, 13)
ACCENT = (255, 179, 71)
TEXT = (240, 236, 228)


def gradient(w, h):
    img = Image.new("RGB", (w, h))
    px = img.load()
    for y in range(h):
        t = y / max(1, h - 1)
        c = tuple(int(BG_TOP[i] * (1 - t) + BG_BOTTOM[i] * t) for i in range(3))
        for x in range(w):
            px[x, y] = c
    return img


def glow(img, cx, cy, r, alpha=110):
    layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=ACCENT + (alpha,))
    layer = layer.filter(ImageFilter.GaussianBlur(r * 0.6))
    img.paste(layer, (0, 0), layer)


def beam(img, x, y, length, spread, lens, angle=0.0, alpha=150, dust=True):
    """A projector lens at (x, y) throwing a cone of light `length` long, `spread` wide at the end."""
    w, h = img.size
    layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
    steps = 48
    for i in range(steps):
        # Overlapping cones, narrower and brighter towards the centre line: a soft-edged beam.
        t = i / (steps - 1)
        half = spread / 2 * (1 - t * 0.85)
        a = int(alpha * (0.12 + 0.88 * t) / steps * 2.2)
        cone = Image.new("RGBA", img.size, (0, 0, 0, 0))
        cd = ImageDraw.Draw(cone)
        ca, sa = math.cos(angle), math.sin(angle)
        ex, ey = x + length * ca, y + length * sa
        px, py = -sa, ca
        cd.polygon([(x + px * lens * 0.35, y + py * lens * 0.35), (ex + px * half, ey + py * half),
                    (ex - px * half, ey - py * half), (x - px * lens * 0.35, y - py * lens * 0.35)], fill=ACCENT + (a,))
        layer = Image.alpha_composite(layer, cone)
    # Fade with distance from the lens.
    fade = Image.new("L", img.size, 0)
    fd = ImageDraw.Draw(fade)
    for r in range(int(length), 0, -max(1, int(length / 60))):
        fd.ellipse((x - r, y - r, x + r, y + r), fill=int(255 * (1 - r / length) ** 0.7))
    layer.putalpha(Image.composite(layer.getchannel("A"), Image.new("L", img.size, 0), fade))
    layer = layer.filter(ImageFilter.GaussianBlur(max(1, lens * 0.08)))
    img.alpha_composite(layer)
    d = ImageDraw.Draw(img)
    if dust:
        rnd = __import__("random").Random(7)
        for _ in range(int(length / 6)):
            t = rnd.random() ** 0.8
            off = (rnd.random() - 0.5) * spread * t * 0.9
            dx = x + length * t * math.cos(angle) - off * math.sin(angle)
            dy = y + length * t * math.sin(angle) + off * math.cos(angle)
            r = max(1, lens * rnd.uniform(0.015, 0.05))
            d.ellipse((dx - r, dy - r, dx + r, dy + r), fill=TEXT + (rnd.randint(40, 150),))
    # The lens: a bright core in a ring.
    glow(img, x, y, lens * 1.4, 120)
    d.ellipse((x - lens, y - lens, x + lens, y + lens), fill=(26, 24, 34, 255), outline=ACCENT + (255,), width=max(2, int(lens * 0.16)))
    core = lens * 0.48
    d.ellipse((x - core, y - core, x + core, y + core), fill=(255, 236, 200, 255))


def supersample(w, h, draw, bg=True, ss=3):
    big = gradient(w * ss, h * ss).convert("RGBA") if bg else Image.new("RGBA", (w * ss, h * ss), (0, 0, 0, 0))
    draw(big, ss)
    return big.resize((w, h), Image.LANCZOS)


def wordmark(d, cx, cy, size, ss):
    f = ImageFont.truetype(FONT, int(size * ss))
    text = "LIGHTCONE"
    # wide letter spacing
    spacing = size * ss * 0.18
    widths = [d.textlength(ch, font=f) for ch in text]
    total = sum(widths) + spacing * (len(text) - 1)
    x = cx * ss - total / 2
    for ch, wch in zip(text, widths):
        d.text((x, cy * ss), ch, font=f, fill=TEXT, anchor="lm")
        x += wch + spacing


def tagline(d, cx, cy, size, ss, text="MEDIA HUB"):
    f = ImageFont.truetype(FONT, int(size * ss))
    d.text((cx * ss, cy * ss), text, font=f, fill=ACCENT + (255,), anchor="mm")


def portrait(img, ss):
    w, h = img.size
    beam(img, w * 0.5, h * 0.12, h * 0.55, w * 1.1, w * 0.07, angle=math.pi / 2)
    d = ImageDraw.Draw(img)
    wordmark(d, 300, 700, 84, ss)
    tagline(d, 300, 775, 30, ss)


def wide(img, ss):
    w, h = img.size
    beam(img, w * 0.06, h * 0.5, w * 0.95, h * 1.3, h * 0.08)
    d = ImageDraw.Draw(img)
    wordmark(d, 560, 195, 92, ss)
    tagline(d, 560, 280, 28, ss)


def hero(img, ss):
    w, h = img.size
    beam(img, w * 0.04, h * 0.42, w * 1.0, h * 1.6, h * 0.06, angle=0.05, alpha=130)


def logo(img, ss):
    w, h = img.size
    d = ImageDraw.Draw(img)
    wordmark(d, 640, 330, 170, ss)
    tagline(d, 640, 460, 52, ss)


def icon(img, ss):
    w, h = img.size
    mask = Image.new("L", img.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, w - 1, h - 1), radius=int(w * 0.22), fill=255)
    bg = gradient(w, h).convert("RGBA")
    beam(bg, w * 0.22, h * 0.3, w * 1.0, w * 1.0, w * 0.13, angle=0.55, alpha=200, dust=False)
    img.paste(bg, (0, 0), mask)


def main():
    os.makedirs(OUT, exist_ok=True)
    supersample(600, 900, portrait).convert("RGB").save(os.path.join(OUT, "capsule.png"), optimize=True)
    supersample(920, 430, wide).convert("RGB").save(os.path.join(OUT, "wide.png"), optimize=True)
    supersample(1920, 620, hero, ss=2).convert("RGB").save(os.path.join(OUT, "hero.png"), optimize=True)
    supersample(1280, 720, logo, bg=False).save(os.path.join(OUT, "logo.png"), optimize=True)
    ic = supersample(256, 256, icon, bg=False)
    ic.save(os.path.join(OUT, "icon.png"), optimize=True)
    # The app's own window / exe icon.
    app = os.path.join(ROOT, "src", "Lightcone.App")
    ic.save(os.path.join(app, "icon.ico"), sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])


if __name__ == "__main__":
    main()
