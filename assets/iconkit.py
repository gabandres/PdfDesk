"""Tiny vector-ish drawing kit on Pillow: shapes as masks in 48-unit design space, gradients via LUTs, blurred shadows.

Each icon is drawn at a supersampled canvas (~768-1024 px) and box-downsampled to the target size.
"""
import math
from functools import lru_cache
from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageMath

GRID = 48.0


def hexc(s, a=255):
    s = s.lstrip("#")
    return (int(s[0:2], 16), int(s[2:4], 16), int(s[4:6], 16), a)


@lru_cache(maxsize=8)
def _ramps(n):
    x = Image.new("F", (n, 1))
    for i in range(n):
        x.putpixel((i, 0), i + 0.5)
    X = x.resize((n, n), Image.NEAREST)
    Y = X.transpose(Image.Transpose.TRANSPOSE)
    return X, Y


class Grad:
    """Linear gradient from p0 to p1 (design units), stops = [(t, '#hex' or rgba), ...]."""

    def __init__(self, p0, p1, stops):
        self.p0, self.p1 = p0, p1
        self.stops = [(t, hexc(c) if isinstance(c, str) else c) for t, c in stops]

    def color_at(self, t):
        st = self.stops
        if t <= st[0][0]:
            return st[0][1]
        for (t0, c0), (t1, c1) in zip(st, st[1:]):
            if t <= t1:
                k = (t - t0) / (t1 - t0) if t1 > t0 else 0
                return tuple(round(c0[i] + (c1[i] - c0[i]) * k) for i in range(4))
        return st[-1][1]

    def render(self, n):
        U = n / GRID
        X, Y = _ramps(n)
        x0, y0 = self.p0[0] * U, self.p0[1] * U
        dx, dy = (self.p1[0] - self.p0[0]) * U, (self.p1[1] - self.p0[1]) * U
        L2 = dx * dx + dy * dy
        ux, uy = dx / L2 * 255.0, dy / L2 * 255.0
        t = ImageMath.lambda_eval(lambda a: (a["x"] - x0) * ux + (a["y"] - y0) * uy, x=X, y=Y)
        t = ImageMath.lambda_eval(lambda a: a["min"](a["max"](a["t"], 0.0), 255.0), t=t).convert("L")
        lut = [self.color_at(i / 255.0) for i in range(256)]
        chans = [t.point([lut[i][c] for i in range(256)]) for c in range(4)]
        return Image.merge("RGBA", chans)


class Canvas:
    def __init__(self, size):
        self.size = size
        ss = max(2, round(1024 / size)) if size < 1024 else 1
        self.n = size * ss
        self.U = self.n / GRID
        self.img = Image.new("RGBA", (self.n, self.n), (0, 0, 0, 0))

    # ---- masks -------------------------------------------------------------------------------------
    def blank(self):
        return Image.new("L", (self.n, self.n), 0)

    def poly(self, pts, r=0.0, steps=14):
        m = self.blank()
        ImageDraw.Draw(m).polygon([(x * self.U, y * self.U) for x, y in rounded(pts, r, steps)], fill=255)
        return m

    def rrect(self, x0, y0, x1, y1, r):
        m = self.blank()
        U = self.U
        ImageDraw.Draw(m).rounded_rectangle([x0 * U, y0 * U, x1 * U - 1, y1 * U - 1], radius=max(0, r * U), fill=255)
        return m

    def ellipse(self, x0, y0, x1, y1):
        m = self.blank()
        U = self.U
        ImageDraw.Draw(m).ellipse([x0 * U, y0 * U, x1 * U - 1, y1 * U - 1], fill=255)
        return m

    # ---- painting ----------------------------------------------------------------------------------
    def fill(self, mask, paint, opacity=1.0):
        if isinstance(paint, Grad):
            layer = paint.render(self.n)
        else:
            c = hexc(paint) if isinstance(paint, str) else paint
            layer = Image.new("RGBA", (self.n, self.n), c)
        a = ImageChops.multiply(layer.getchannel("A"), mask)
        if opacity < 1:
            a = a.point(lambda v: round(v * opacity))
        layer.putalpha(a)
        self.img.alpha_composite(layer)

    def shadow(self, mask, dy=1.0, blur=1.0, color=(0, 0, 0), alpha=0.3, dx=0.0, clip=None):
        m = mask.filter(ImageFilter.GaussianBlur(blur * self.U)) if blur > 0 else mask
        m = m.transform(m.size, Image.AFFINE, (1, 0, -dx * self.U, 0, 1, -dy * self.U))
        if clip is not None:
            m = ImageChops.multiply(m, clip)
        self.fill(m, (*color, 255), alpha)

    def out(self, resample=Image.BOX):
        return self.img.resize((self.size, self.size), resample)


def union(*ms):
    out = ms[0]
    for m in ms[1:]:
        out = ImageChops.lighter(out, m)
    return out


def sub(a, b):
    return ImageChops.subtract(a, b)


def inter(a, b):
    return ImageChops.multiply(a, b)


def rounded(pts, r, steps=14):
    """Polygon with each corner replaced by a quadratic-Bezier fillet; r may be a scalar or per-vertex list."""
    if not r:
        return pts
    rs = r if isinstance(r, (list, tuple)) else [r] * len(pts)
    out = []
    k = len(pts)
    for i in range(k):
        p, a, b = pts[i], pts[i - 1], pts[(i + 1) % k]
        ri = rs[i]
        if ri <= 0:
            out.append(p)
            continue
        va = (a[0] - p[0], a[1] - p[1])
        vb = (b[0] - p[0], b[1] - p[1])
        la, lb = math.hypot(*va), math.hypot(*vb)
        # tangent length for a circular fillet of radius ri at this angle
        cosang = max(-1.0, min(1.0, (va[0] * vb[0] + va[1] * vb[1]) / (la * lb)))
        ang = math.acos(cosang)
        d = ri / math.tan(ang / 2) if ang > 1e-3 else 0
        d = min(d, la / 2, lb / 2)
        s = (p[0] + va[0] / la * d, p[1] + va[1] / la * d)
        e = (p[0] + vb[0] / lb * d, p[1] + vb[1] / lb * d)
        for j in range(steps + 1):
            t = j / steps
            x = (1 - t) ** 2 * s[0] + 2 * (1 - t) * t * p[0] + t * t * e[0]
            y = (1 - t) ** 2 * s[1] + 2 * (1 - t) * t * p[1] + t * t * e[1]
            out.append((x, y))
    return out


# ---- shared geometry -------------------------------------------------------------------------------
def page_pts(x0, y0, x1, y1, f):
    """Page outline with the top-right corner cut by fold f."""
    return [(x0, y0), (x1 - f, y0), (x1, y0 + f), (x1, y1), (x0, y1)]


def page_inset(x0, y0, x1, y1, f, s):
    return page_pts(x0 + s, y0 + s, x1 - s, y1 - s, max(0.0, f - 0.586 * s))


def bolt_pts(cx, cy, h, w=None):
    """Lightning bolt centred at (cx, cy), height h."""
    w = w or h * 0.64
    n = [(0.70, 0.0), (0.04, 0.58), (0.47, 0.58), (0.30, 1.0), (0.96, 0.42), (0.53, 0.42)]
    return [(cx + (x - 0.5) * w, cy + (y - 0.5) * h) for x, y in n]


def drop_pts(cx, cy, r, top, steps=48):
    """Ink drop: circle (cx, cy, r) with tangents meeting at (cx, top)."""
    dy = cy - top
    a = math.asin(r / dy)  # angle between axis and tangent line
    # tangent points on circle
    phi = math.pi / 2 - a
    pts = [(cx, top)]
    start = -math.pi / 2 + phi  # right tangent point angle (measured from +x, y down)
    end = 3 * math.pi / 2 - phi
    for i in range(steps + 1):
        t = start + (end - start) * i / steps
        pts.append((cx + r * math.cos(t), cy + r * math.sin(t)))
    return pts
