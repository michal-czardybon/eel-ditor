"""Flat app icon for Eel-ditor: a dark grey eel bent in a U on a light rounded square.

The head is at the bottom, facing left; the body runs right along a horizontal line, turns up in a half circle
and comes back left along a horizontal line at the top, where it narrows into the tail fin. Like a real eel it has
a long dorsal fin (along the inside of the bend), an anal fin (outside) that join into the pointed tail fin,
a pectoral fin behind the gill cover, an eye and a mouth.
The shapes are drawn along a centre line as overlapping discs, outline first, then the fill.
Usage: py tools/make_icon.py <output dir>   (writes app.ico, app.png, app-256.png)
"""
import math
import sys
from PIL import Image, ImageDraw

out_dir = sys.argv[1]
S = 1024
SS = 2  # draw at 2x, then downsample: smooth edges
img = Image.new("RGBA", (S * SS, S * SS), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

def P(x, y):
    return (x * SS, y * SS)

def disc(x, y, r, fill):
    d.ellipse((SS * (x - r), SS * (y - r), SS * (x + r), SS * (y + r)), fill=fill)

def poly(points, fill):
    d.polygon([P(x, y) for x, y in points], fill=fill)

OUTLINE = (24, 27, 31, 255)
BODY = (78, 85, 93, 255)       # dark grey
BELLY = (122, 130, 139, 255)   # lighter grey
FIN = (104, 112, 121, 255)
WHITE = (250, 251, 252, 255)
OUT = 11

# Background: light rounded square, slightly darker lower half (flat).
d.rounded_rectangle((SS * 32, SS * 32, SS * (S - 32), SS * (S - 32)), radius=SS * 200, fill=(232, 238, 242, 255))
d.rounded_rectangle((SS * 32, SS * (S // 2 + 80), SS * (S - 32), SS * (S - 32)), radius=SS * 200, fill=(216, 226, 233, 255))
d.rectangle((SS * 32, SS * (S // 2 + 80), SS * (S - 32), SS * (S // 2 + 280)), fill=(216, 226, 233, 255))

# ---------------------------------------------------------------- the centre line
Y_BOTTOM, Y_TOP = 690, 330          # the two horizontal parts
X_HEAD, X_TURN, X_TAIL = 330, 650, 230
R = (Y_BOTTOM - Y_TOP) / 2
CY = (Y_BOTTOM + Y_TOP) / 2
segments = [
    (X_TURN - X_HEAD, lambda u: (X_HEAD + u * (X_TURN - X_HEAD), Y_BOTTOM)),                          # exactly horizontal
    (math.pi * R, lambda u: (X_TURN + R * math.sin(u * math.pi), CY + R * math.cos(u * math.pi))),   # the half circle
    (X_TURN - X_TAIL, lambda u: (X_TURN - u * (X_TURN - X_TAIL), Y_TOP)),                            # exactly horizontal
]
TOTAL = sum(l for l, _ in segments)

def at(dist):
    """Point and unit direction at a distance along the centre line (from the head)."""
    dist = max(0.0, min(TOTAL, dist))
    s = dist
    for length, f in segments:
        if s <= length:
            u = s / length
            x0, y0 = f(max(0.0, u - 1e-4))
            x1, y1 = f(min(1.0, u + 1e-4))
            ln = math.hypot(x1 - x0, y1 - y0) or 1
            return f(u), ((x1 - x0) / ln, (y1 - y0) / ln)
        s -= length
    return segments[-1][1](1.0), (-1.0, 0.0)

W = 62                      # half the body's thickness, the same along the body
TAPER = 250                 # the last part narrows towards the tail fin
W_REAR = 46                 # the rear part (from the bend on) is a little slimmer
REAR_START = segments[0][0] + segments[1][0] * 0.35
def half_width(dist):
    rest = TOTAL - dist
    if dist <= REAR_START:
        base = W
    else:
        k = min(1.0, (dist - REAR_START) / (TOTAL - TAPER - REAR_START))
        base = W - (W - W_REAR) * (0.5 - 0.5 * math.cos(k * math.pi))
    if rest >= TAPER:
        return base
    k = rest / TAPER
    return 7 + (W_REAR - 7) * (1 - (1 - k) ** 2.2)

STEPS = 1400
samples = [(i * TOTAL / STEPS) for i in range(STEPS + 1)]

def inner(direction):
    # The inside of the bend: up along the bottom part, down along the top part.
    dx, dy = direction
    return dy, -dx

# The eel lies flat and turns over in the bend: along both horizontal parts the back is up and the light belly down.
# In the bend it rolls away from the viewer: the belly slips off the outside edge, the bend shows the dark back,
# and the belly comes back from the inside edge on the top part.
TWIST_FROM = segments[0][0] + segments[1][0] * 0.05
TWIST_TO = segments[0][0] + segments[1][0] * 0.95
def roll(dist):
    """0 before the bend, 1 after it (smooth)."""
    k = min(1.0, max(0.0, (dist - TWIST_FROM) / (TWIST_TO - TWIST_FROM)))
    return 0.5 - 0.5 * math.cos(k * math.pi)
def twist(dist):
    # +1 where the back is on the inside of the bend, -1 where it is on the outside.
    return math.cos(roll(dist) * math.pi)

# ---------------------------------------------------------------- fins (under the body)
def fin_band(start, side, height, fill, grow=0):
    for dist in samples:
        if dist < start:
            continue
        (x, y), dirn = at(dist)
        nx, ny = inner(dirn)
        t = twist(dist)
        w = half_width(dist)
        # The fin rises from nothing at its start and stays as high to the tail, and gets lower with the body
        # towards the tail. Where the body turns over, the fin slides under the body to the other side.
        h = height * min(1.0, (dist - start) / 120) * (0.35 + 0.65 * w / W)
        r = h / 2 + grow
        c = (w + h / 2 - 2) * t * side
        disc(x + nx * c, y + ny * c, r, fill)

DORSAL_START, ANAL_START = 230, 520
# Tail fin: a pointed leaf past the end of the body, joining the two fins.
(tx, ty), _ = at(TOTAL)
def tail_leaf(grow):
    pts = []
    L, H = 100, 30
    for i in range(41):                      # upper edge: from the body to the tip
        u = i / 40
        pts.append((tx + 10 - u * L, ty - H * (1 - u) ** 1.4 * (0.6 + 0.4 * (1 - u)) - grow * (1 - u)))
    for i in range(40, -1, -1):              # lower edge back
        u = i / 40
        pts.append((tx + 10 - u * L, ty + H * (1 - u) ** 1.4 * (0.6 + 0.4 * (1 - u)) + grow * (1 - u)))
    pts[0] = (pts[0][0] + grow, pts[0][1])
    return pts

poly(tail_leaf(OUT), OUTLINE)
fin_band(DORSAL_START, +1, 34, OUTLINE, grow=OUT)
fin_band(ANAL_START, -1, 24, OUTLINE, grow=OUT)
poly(tail_leaf(0), FIN)
fin_band(DORSAL_START, +1, 34, FIN)
fin_band(ANAL_START, -1, 24, FIN)

# ---------------------------------------------------------------- body
for dist in samples:
    (x, y), _ = at(dist)
    disc(x, y, half_width(dist) + OUT, OUTLINE)

# Head: a fish head facing left, with a tapered snout (built from discs along a short axis).
HEAD_LEN = 150
def head_radius(k):          # k = 0 at the snout tip, 1 where it meets the body
    return 14 + (W + 4 - 14) * math.sin(min(1.0, k) * math.pi / 2) ** 0.8
def head_point(k):           # the snout is a little lower than the body's centre line
    return X_HEAD - HEAD_LEN * (1 - k), Y_BOTTOM + 8 * (1 - k)
for i in range(121):
    k = i / 120
    x, y = head_point(k)
    disc(x, y, head_radius(k) + OUT, OUTLINE)

for dist in samples:
    (x, y), _ = at(dist)
    disc(x, y, half_width(dist), BODY)
for i in range(121):
    k = i / 120
    x, y = head_point(k)
    disc(x, y, head_radius(k), BODY)

# Belly: a lighter band along the lower side of both horizontal parts. The body's cross-section is a circle; the
# belly sits at an angle psi on it (0 = facing the viewer) and turns with the roll, out of sight in the bend.
for dist in samples:
    if dist < 40 or dist > TOTAL - 40:
        continue
    (x, y), dirn = at(dist)
    nx, ny = inner(dirn)
    w = half_width(dist)
    psi = math.radians(-30 - 300 * roll(dist))
    vis = math.cos(psi)
    if vis <= 0.05:
        continue
    r = w * 0.36 * vis ** 0.6
    off = max(-(w - r - 4), min(w - r - 4, w * math.sin(psi)))
    disc(x + nx * off, y + ny * off, r, BELLY)
for i in range(60, 121):
    k = i / 120
    x, y = head_point(k)
    r = head_radius(k)
    disc(x, y + r * 0.5, r * 0.36, BELLY)

# Pectoral fin: a rounded teardrop behind the gill cover, pointing back and a little down.
def pectoral(grow):
    cx, cy, rx, ry, rot = X_HEAD + 36, Y_BOTTOM + 14, 40 + grow, 15 + grow, math.radians(22)
    pts = []
    for i in range(48):
        a = 2 * math.pi * i / 48
        # A teardrop: wider at the front (near the gill), narrower at the back.
        k = 1 - 0.35 * (1 + math.cos(a)) / 2
        x, y = rx * math.cos(a), ry * math.sin(a) * k
        pts.append((cx + x * math.cos(rot) - y * math.sin(rot), cy + x * math.sin(rot) + y * math.cos(rot)))
    return pts
poly(pectoral(OUT * 0.8), OUTLINE)
poly(pectoral(0), FIN)

# Gill cover: an arc behind the eye.
gx = X_HEAD - 20
d.arc((SS * (gx - 40), SS * (Y_BOTTOM - 46), SS * (gx + 20), SS * (Y_BOTTOM + 46)), start=-70, end=70, fill=OUTLINE, width=SS * 10)

# Mouth: from the snout tip back and slightly down.
sx, sy = head_point(0)
d.line((P(sx - 12, sy + 2), P(sx + 46, sy + 10)), fill=OUTLINE, width=SS * 9)

# Eye: large enough to be seen at small sizes.
ex, ey = X_HEAD - 92, Y_BOTTOM - 20
disc(ex, ey, 26, OUTLINE)
disc(ex, ey, 20, WHITE)
disc(ex - 4, ey + 1, 11, OUTLINE)
disc(ex - 8, ey - 4, 4, WHITE)

img = img.resize((S, S), Image.LANCZOS)
img.save(f"{out_dir}/app.png")
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
img.save(f"{out_dir}/app.ico", sizes=[(s, s) for s in sizes])
img.resize((256, 256), Image.LANCZOS).save(f"{out_dir}/app-256.png")
print("ok")
