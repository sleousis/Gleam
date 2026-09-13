"""
The icons of Gleam, Dawntrail Ready and Soundswap, drawn by one set of rules so they match to the pixel.

    python tools/make-icons.py            (from the Gleam repository; the others are its siblings under one folder)

Writes icon.png (512), icon-192.png and icon-96.png into each plugin's src/<Name>/images. icon.png and icon-192.png
are the full cut; icon-96.png is the small cut: thicker strokes, no glow or highlight, two larger sparkles.

The rules (canvas 2048 px, then halved step by step and resized):
  tile      rounded square, radius 22% of the size, vertical gradient #1E1A3E to #0D0B1C
  ring      centre (1024, 1080), outer radius 610, stroke 190 (small cut 250), violet #654FF0, opening 200..250 deg
            full cut: a blurred violet glow around it and a faint #A99CFF highlight along its arcs
  ends      a round cap, or the shared arrowhead: tip on the ring's centre line, 1.2 strokes long, growing outward
            only (0.75 strokes past the ring), corners rounded by 0.1 strokes
  symbol    pearl #F1EEFF, strokes as wide as the ring's, round caps and joins; scaled so the smallest circle
            enclosing its ink is centred on the ring's centre and ends GAP px inside the ring
  sparkles  four-point stars at (470, 330), (690, 190), (320, 620); small cut two, larger
"""
import math, os, random, sys
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

S = 2048
VIOLET, LAV, PEARL = (0x65, 0x4F, 0xF0, 255), (0xDD, 0xD6, 0xFF, 255), (0xF1, 0xEE, 0xFF, 255)
CX, CY, R = 1024, 1080, 610
GAP = 48                                   # symbol ink to ring: 12 px at 512
CUTS = ((512, False, "icon.png"), (192, False, "icon-192.png"), (96, True, "icon-96.png"))


def width(small): return 250 if small else 190


# ---------- geometry ----------

def mec(points):
    """Smallest circle enclosing the points (incremental, expected linear time)."""
    pts = points[:]
    random.Random(1).shuffle(pts)
    def inside(c, p): return c is not None and math.hypot(p[0] - c[0], p[1] - c[1]) <= c[2] + 1e-7
    def two(a, b): return ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2, math.hypot(a[0] - b[0], a[1] - b[1]) / 2)
    def three(a, b, c):
        d = 2 * (a[0] * (b[1] - c[1]) + b[0] * (c[1] - a[1]) + c[0] * (a[1] - b[1]))
        if abs(d) < 1e-12: return max((two(a, b), two(a, c), two(b, c)), key=lambda k: k[2])
        ux = ((a[0]**2 + a[1]**2) * (b[1] - c[1]) + (b[0]**2 + b[1]**2) * (c[1] - a[1]) + (c[0]**2 + c[1]**2) * (a[1] - b[1])) / d
        uy = ((a[0]**2 + a[1]**2) * (c[0] - b[0]) + (b[0]**2 + b[1]**2) * (a[0] - c[0]) + (c[0]**2 + c[1]**2) * (b[0] - a[0])) / d
        return (ux, uy, math.hypot(a[0] - ux, a[1] - uy))
    c = None
    for i, p in enumerate(pts):
        if inside(c, p): continue
        c = (p[0], p[1], 0.0)
        for j in range(i):
            q = pts[j]
            if inside(c, q): continue
            c = two(p, q)
            for k in range(j):
                if not inside(c, pts[k]): c = three(p, q, pts[k])
    return c


def hull_points(mask):
    """The convex hull of every boundary ink pixel's corners."""
    m = mask
    edge = m & ~(np.roll(m, 1, 0) & np.roll(m, -1, 0) & np.roll(m, 1, 1) & np.roll(m, -1, 1))
    ys, xs = np.nonzero(edge)
    pts = set()
    for x, y in zip(xs.tolist(), ys.tolist()):
        pts.update(((x, y), (x + 1, y), (x, y + 1), (x + 1, y + 1)))
    pts = sorted(pts)
    def cross(o, a, b): return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])
    lower, upper = [], []
    for p in pts:
        while len(lower) >= 2 and cross(lower[-2], lower[-1], p) <= 0: lower.pop()
        lower.append(p)
    for p in reversed(pts):
        while len(upper) >= 2 and cross(upper[-2], upper[-1], p) <= 0: upper.pop()
        upper.append(p)
    return lower[:-1] + upper[:-1]


# ---------- symbols: primitives in design units ----------

def draw_symbol(d, prims, w, s, ox, oy, fill):
    T = lambda p: (ox + s * p[0], oy + s * p[1])
    for kind, *a in prims:
        if kind == "line":                          # polyline, stroke w, round joins and caps
            pts = [T(p) for p in a[0]]
            if len(pts) > 1: d.line(pts, fill=fill, width=int(w), joint="curve")
            for x, y in (pts[0], pts[-1]):
                d.ellipse((x - w / 2, y - w / 2, x + w / 2, y + w / 2), fill=fill)
        elif kind == "curve":                       # a smooth path: PIL's joints break on many short segments,
            pts = [T(p) for p in a[0]]              # so each piece is drawn straight with a round joint
            for p, q in zip(pts, pts[1:]): d.line([p, q], fill=fill, width=int(w))
            for x, y in pts: d.ellipse((x - w / 2, y - w / 2, x + w / 2, y + w / 2), fill=fill)
        elif kind == "poly":                        # a filled outline
            d.polygon([T(p) for p in a[0]], fill=fill)


def ellipse_pts(cx, cy, a, b, deg, n=240):
    t = math.radians(deg)
    return [(cx + a * math.cos(u) * math.cos(t) - b * math.sin(u) * math.sin(t),
             cy + a * math.cos(u) * math.sin(t) + b * math.sin(u) * math.cos(t))
            for u in (2 * math.pi * i / n for i in range(n))]


def half_disc_pts(cx, cy, r, n=180):
    return [(cx + r * math.cos(math.pi + math.pi * i / n), cy + r * math.sin(math.pi + math.pi * i / n)) for i in range(n + 1)]


def bezier(p0, p1, p2, n=48):
    return [((1 - t)**2 * p0[0] + 2 * (1 - t) * t * p1[0] + t * t * p2[0], (1 - t)**2 * p0[1] + 2 * (1 - t) * t * p1[1] + t * t * p2[1])
            for t in (i / n for i in range(n + 1))]


def wedge(cx, cy, deg, r0, r1, half):
    a = math.radians(deg); n = (-math.sin(a), math.cos(a))
    b = (cx + r0 * math.cos(a), cy + r0 * math.sin(a))
    return [(b[0] + n[0] * half, b[1] + n[1] * half), (cx + r1 * math.cos(a), cy + r1 * math.sin(a)), (b[0] - n[0] * half, b[1] - n[1] * half)]


# Each symbol is a function of the stroke width w. Details that would close up or vanish when the small cut
# thickens its strokes (rays, the flag's opening) grow with w, the way Gleam's small cut grows its sparkles.

def CHECK(w):
    """Gleam: a check mark (Gleam's original strokes, before fitting)."""
    return [("line", [(760, 1085), (955, 1280), (1330, 860)])]


def SUN(w):
    """Dawntrail Ready: a sun rising over the horizon, rays pointed like the sparkles."""
    gap = 0.29 * w                               # between sun and horizon, and sun and rays: stays open in the small cut
    horizon = 75 + gap + w / 2
    return [
        ("line", [(-300, horizon), (300, horizon)]),
        ("poly", half_disc_pts(0, 75, 190)),
        *[("poly", wedge(0, 75, a, 190 + gap, 190 + gap + 0.72 * w, 0.28 * w)) for a in (198, 234, 270, 306, 342)],
    ]


def NOTE(w):
    """Soundswap: an eighth note, its flag kept clear of the stem at every stroke width."""
    return [
        ("poly", ellipse_pts(-110, 230, 165, 122, -22)),
        ("line", [(0, 215), (0, -300)]),
        ("curve", bezier((0, -300), (1.3 * w, -250), (1.6 * w, -20))),
    ]


def fit(prims, w):
    """Scale and offset that put the symbol's ink in a circle GAP px inside the ring, centred on the ring."""
    reach = R - w - GAP
    allpts = [p for _, *a in prims for p in a[0]]
    dx = (min(p[0] for p in allpts) + max(p[0] for p in allpts)) / 2
    dy = (min(p[1] for p in allpts) + max(p[1] for p in allpts)) / 2
    def measure(s):
        m = Image.new("L", (S, S), 0)
        draw_symbol(ImageDraw.Draw(m), prims, w, s, S / 2 - s * dx, S / 2 - s * dy, 255)
        ink = np.array(m) > 127
        if not ink.any(): raise SystemExit("a symbol drew nothing while being fitted")
        return mec(hull_points(ink))
    lo, hi = 0.2, 2.0
    for _ in range(30):
        mid = (lo + hi) / 2
        if measure(mid)[2] > reach: hi = mid
        else: lo = mid
    c = measure(lo)
    return lo, S / 2 - lo * dx + (CX - c[0]), S / 2 - lo * dy + (CY - c[1])


# ---------- the ring ----------

def arrowhead(d, end_deg, w, fill):
    """
    A triangle pointing clockwise, its tip on the ring's centre line, corners rounded like the ring's caps. It
    grows outward only: its inner side is flush with the ring's inner edge, so every symbol keeps the same gap
    to its ring whether that ring ends in caps or arrows.
    """
    rm, out, L, rho = R - w / 2, 0.75 * w, 1.2 * w, 0.1 * w
    a0 = math.radians(end_deg); a1 = a0 + L / rm
    v = [(CX + (R - w) * math.cos(a0), CY + (R - w) * math.sin(a0)),
         (CX + (R + out) * math.cos(a0), CY + (R + out) * math.sin(a0)),
         (CX + rm * math.cos(a1), CY + rm * math.sin(a1))]
    la, lb, lc = (math.dist(v[1], v[2]), math.dist(v[0], v[2]), math.dist(v[0], v[1]))
    per = la + lb + lc
    ix, iy = ((la * v[0][0] + lb * v[1][0] + lc * v[2][0]) / per, (la * v[0][1] + lb * v[1][1] + lc * v[2][1]) / per)
    area = abs((v[1][0] - v[0][0]) * (v[2][1] - v[0][1]) - (v[2][0] - v[0][0]) * (v[1][1] - v[0][1])) / 2
    k = 1 - rho / (2 * area / per)
    u = [(ix + (x - ix) * k, iy + (y - iy) * k) for x, y in v]
    d.polygon(u, fill=fill)
    for i in range(3):
        d.line([u[i], u[(i + 1) % 3]], fill=fill, width=int(2 * rho))
        d.ellipse((u[i][0] - rho, u[i][1] - rho, u[i][0] + rho, u[i][1] + rho), fill=fill)


RINGS = {
    # (start, end, end style) per arc, degrees clockwise from east; every ring keeps the 200..250 opening
    "progress": [(250, 560, "cap")],
    "update": [(250, 560, "arrow")],
    "swap": [(250, 380, "arrow"), (70, 200, "arrow")],
}


# ---------- the icon ----------

def star(d, x, y, size, color):
    pts = []
    for i in range(8):
        ang = math.radians(i * 45 - 90)
        rad = size if i % 2 == 0 else size * 0.34
        pts.append((x + rad * math.cos(ang), y + rad * math.sin(ang)))
    d.polygon(pts, fill=color)


def draw(out_size, small, ring, symbol):
    w = width(small)
    prims = symbol(w)
    s, ox, oy = fit(prims, w)

    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, S - 1, S - 1), radius=int(S * 0.22), fill=255)
    grad = Image.new("RGBA", (S, S))
    top, bottom = (0x1E, 0x1A, 0x3E), (0x0D, 0x0B, 0x1C)
    gp = grad.load()
    for y in range(S):
        t = y / (S - 1)
        c = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,)
        for x in range(S): gp[x, y] = c
    img.paste(grad, (0, 0), mask)

    if not small:
        glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        ImageDraw.Draw(glow).ellipse((CX - R - 40, CY - R - 40, CX + R + 40, CY + R + 40), outline=(0x65, 0x4F, 0xF0, 150), width=260)
        img.alpha_composite(glow.filter(ImageFilter.GaussianBlur(90)))

    arc = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ad = ImageDraw.Draw(arc)
    for start, end, style in RINGS[ring]:
        ad.arc((CX - R, CY - R, CX + R, CY + R), start=start, end=end, fill=VIOLET, width=w)
        for ang in ((start, end) if style == "cap" else (start,)):
            a = math.radians(ang)
            ex, ey = CX + (R - w / 2) * math.cos(a), CY + (R - w / 2) * math.sin(a)
            ad.ellipse((ex - w / 2, ey - w / 2, ex + w / 2, ey + w / 2), fill=VIOLET)
        if style == "arrow": arrowhead(ad, end, w, VIOLET)
    if not small:
        for start, end, _ in RINGS[ring]:
            ad.arc((CX - R + 30, CY - R + 30, CX + R - 30, CY + R - 30), start=start, end=end, fill=(0xA9, 0x9C, 0xFF, 70), width=60)
    img.alpha_composite(arc)

    cd = ImageDraw.Draw(img)
    draw_symbol(cd, prims, w, s, ox, oy, PEARL)

    if small:
        star(cd, 470, 330, 210, PEARL); star(cd, 700, 170, 120, LAV)
    else:
        star(cd, 470, 330, 160, PEARL); star(cd, 690, 190, 95, LAV); star(cd, 320, 620, 75, LAV)

    size = S
    while size // 2 >= out_size * 2:
        size //= 2
        img = img.resize((size, size), Image.LANCZOS)
    return img.resize((out_size, out_size), Image.LANCZOS)


ICONS = [("Gleam", "progress", CHECK), ("DawntrailReady", "update", SUN), ("Soundswap", "swap", NOTE)]

if __name__ == "__main__":
    root = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "..", ".."))
    for name, ring, symbol in ICONS:
        folder = os.path.join(root, name, "src", name, "images")
        if not os.path.isdir(folder): raise SystemExit(f"no {folder}")
        for size, small, file in CUTS:
            draw(size, small, ring, symbol).save(os.path.join(folder, file), optimize=True)
        print("wrote", folder)
