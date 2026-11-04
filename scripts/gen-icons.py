# Image de partage d'Averse (docs/og.png), à partir de docs/icon-512.png.
# Les icônes elles-mêmes (SVG, PNG, ICO, Android) sont générées par le playground :
# bored-dev-playground/brand/icons/averse.svg, puis « node build.js averse » dans bored-dev-playground/brand.
# Usage : python scripts/gen-icons.py
import os
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
docs = os.path.join(ROOT, "docs")
img = Image.open(os.path.join(docs, "icon-512.png")).convert("RGBA")

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
