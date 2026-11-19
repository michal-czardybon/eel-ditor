"""Flat app icon for Carbon Editor: blue rounded square, white "C" ring, text cursor in the gap."""
import sys
from PIL import Image, ImageDraw

out_dir = sys.argv[1]
S = 1024
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
# Background: rounded square, single flat color with a slightly darker lower half (still flat, no gradient).
d.rounded_rectangle((32, 32, S - 32, S - 32), radius=200, fill=(38, 110, 200, 255))
d.rounded_rectangle((32, S // 2, S - 32, S - 32), radius=200, fill=(31, 96, 178, 255))
d.rectangle((32, S // 2, S - 32, S // 2 + 220), fill=(31, 96, 178, 255))
# "C": thick arc, open to the right.
off = 75
box = (230 - off, 230, S - 230 - off, S - 230)
d.arc(box, start=50, end=310, fill=(255, 255, 255, 255), width=120)
# Rounded ends of the arc.
import math
cx, cy, r = S / 2 - off, S / 2, (S - 460) / 2 - 60
for ang in (50, 310):
    a = math.radians(ang)
    x, y = cx + r * math.cos(a), cy + r * math.sin(a)
    d.ellipse((x - 60, y - 60, x + 60, y + 60), fill=(255, 255, 255, 255))
# Text cursor next to the C (amber accent).
d.rounded_rectangle((S / 2 + 235, S / 2 - 205, S / 2 + 300, S / 2 + 205), radius=30, fill=(255, 196, 92, 255))

img.save(f"{out_dir}/app.png")
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
img.save(f"{out_dir}/app.ico", sizes=[(s, s) for s in sizes])
img.resize((256, 256), Image.LANCZOS).save(f"{out_dir}/app-256.png")
print("ok")
