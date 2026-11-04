# Dessine l'icône d'Averse (même dessin que ui/icon.svg) en PNG / ICO.
# Usage : python scripts/gen-icons.py
import os
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
S = 1024  # dessin en 64 unités, x16
K = S / 64


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def icon(rounded=True):
    c1, c2 = (0x3A, 0x5B, 0xFF), (0x12, 0xC2, 0xDF)
    grad = Image.new("RGB", (S, S))
    px = grad.load()
    for y in range(S):
        for x in range(S):
            px[x, y] = lerp(c1, c2, (x + y) / (2 * (S - 1)))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, S - 1, S - 1), radius=16 * K if rounded else 0, fill=255)
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    img.paste(grad, (0, 0), mask)

    over = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(over)

    def line(p, q, w, alpha=255):
        col = (255, 255, 255, alpha)
        p = (p[0] * K, p[1] * K)
        q = (q[0] * K, q[1] * K)
        d.line((p, q), fill=col, width=round(w * K))
        r = w * K / 2
        for x, y in (p, q):
            d.ellipse((x - r, y - r, x + r, y + r), fill=col)

    rain = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(rain)
    for a, b in (((20, 10.5), (16.5, 18.5)), ((31, 9), (27.5, 17)), ((42, 10.5), (38.5, 18.5)), ((51, 12), (48.5, 18))):
        line(a, b, 3.5)
    rain.putalpha(rain.getchannel("A").point(lambda v: v * 140 // 255))
    img = Image.alpha_composite(img, rain)

    d = ImageDraw.Draw(over)
    line((32, 26), (32, 45), 5)
    line((23.5, 37), (32, 45.5), 5)
    line((40.5, 37), (32, 45.5), 5)
    line((19, 53), (45, 53), 5)
    return Image.alpha_composite(img, over)


def save(img, path, size):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    img.resize((size, size), Image.LANCZOS).save(path)
    print("ok ", os.path.relpath(path, ROOT))


img = icon()
square = icon(rounded=False)
ico = os.path.join(ROOT, "windows", "Averse", "averse.ico")
img.resize((256, 256), Image.LANCZOS).save(ico, sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])
print("ok ", os.path.relpath(ico, ROOT))
docs = os.path.join(ROOT, "docs")
save(img, os.path.join(docs, "icon-192.png"), 192)
save(img, os.path.join(docs, "icon-512.png"), 512)
save(square, os.path.join(docs, "apple-touch-icon.png"), 180)
img.resize((48, 48), Image.LANCZOS).save(os.path.join(docs, "favicon.ico"), sizes=[(16, 16), (32, 32), (48, 48)])

# Image de partage (og.png) : dégradé, icône, nom et accroche.
from PIL import ImageFont
W, H = 1200, 630
og = Image.new("RGB", (W, H), (14, 14, 18))
d = ImageDraw.Draw(og)
for y in range(0, H, 22):
    for x in range(0, W, 22):
        d.ellipse((x - 1, y - 1, x + 1, y + 1), fill=(32, 32, 40))
ic = img.resize((220, 220), Image.LANCZOS)
og.paste(ic, (110, 205), ic)
fonts = "C:/Windows/Fonts/"
try:
    big = ImageFont.truetype(fonts + "segoeuib.ttf", 120)
    small = ImageFont.truetype(fonts + "segoeui.ttf", 40)
    mono = ImageFont.truetype(fonts + "consola.ttf", 28)
except OSError:
    big = small = mono = ImageFont.load_default()
d.text((380, 190), "Averse", font=big, fill=(241, 240, 244))
d.text((384, 340), "Le client torrent sans pub.", font=small, fill=(163, 161, 173))
d.text((384, 400), "Windows · Android · libre et gratuit", font=mono, fill=(125, 147, 255))
og.save(os.path.join(docs, "og.png"), optimize=True)
print("ok  docs/og.png")
