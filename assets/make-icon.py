"""Generates the PdfDesk icons (16-256 px) and 512 px previews.

  assets/PdfDesk.ico          app icon: Fluent-style rounded tile in PDF red, a white page with a folded corner,
                              text lines, and a "PDF" label (same visual family as PrintDesk's blue tile).
  assets/PdfDocument.ico      file-type icon for .pdf in Explorer: a white page with a red "PDF" band.

  packaging/Assets/*.png     MSIX images (tiles, Start/taskbar icons per target size, store logo, splash screen, file-type
                              logo), named with MRT qualifiers (scale-N, targetsize-N, altform-unplated) that makepri
                              indexes into resources.pri (packaging/pack-msix.ps1).

Every size is drawn at 8x and downsampled, so edges stay clean at each size; tiny sizes drop detail.
Run:  python -I assets/make-icon.py   (needs Pillow)
"""
import os
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
FONT = "C:/Windows/Fonts/segoeuib.ttf"

RED_TOP, RED_BOTTOM = (240, 72, 64, 255), (196, 30, 36, 255)
WHITE = (255, 255, 255, 255)
PAPER_EDGE = (238, 214, 214, 255)
FOLD = (252, 200, 196, 255)
INK = (214, 120, 118, 255)


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(len(a)))


def gradient(n, top, bottom):
    g = Image.new("RGBA", (n, n))
    d = ImageDraw.Draw(g)
    for y in range(n):
        d.line([(0, y), (n, y)], fill=lerp(top, bottom, y / n))
    return g


def font(px):
    try:
        return ImageFont.truetype(FONT, max(1, px))
    except OSError:
        return ImageFont.load_default()


def page(img, u, x0, y0, x1, y1, fold, tiny, edge=PAPER_EDGE):
    """White page with a folded top-right corner."""
    d = ImageDraw.Draw(img)
    f = fold * u
    X0, Y0, X1, Y1 = x0 * u, y0 * u, x1 * u, y1 * u
    r = 3.5 * u
    # Body as a polygon with the corner cut, plus rounded other corners approximated by a rounded rect underlay.
    d.rounded_rectangle([X0, Y0 + f, X1, Y1], radius=r, fill=WHITE)
    d.rounded_rectangle([X0, Y0, X1 - f, Y1], radius=r, fill=WHITE)
    d.polygon([(X1 - f, Y0), (X1, Y0 + f), (X1 - f, Y0 + f)], fill=WHITE)
    if not tiny:
        w = max(1, round(1.2 * u))
        d.line([(X0 + r, Y0), (X1 - f, Y0)], fill=edge, width=w)
        d.line([(X1, Y0 + f), (X1, Y1 - r)], fill=edge, width=w)
        d.line([(X1 - f, Y0), (X1, Y0 + f)], fill=edge, width=w)
    # The fold itself.
    d.polygon([(X1 - f, Y0), (X1 - f, Y0 + f), (X1, Y0 + f)], fill=FOLD)
    return d


def draw_app(size):
    ss = 8
    n = size * ss
    u = n / 100.0
    tiny, small = size <= 24, size <= 48
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))

    margin = 0 if tiny else 2
    mask = Image.new("L", (n, n), 0)
    ImageDraw.Draw(mask).rounded_rectangle([margin * u, margin * u, (100 - margin) * u, (100 - margin) * u], radius=round(22 * u), fill=255)
    img.paste(gradient(n, RED_TOP, RED_BOTTOM), (0, 0), mask)

    if not small:
        hl = Image.new("RGBA", (n, n), (0, 0, 0, 0))
        ImageDraw.Draw(hl).rounded_rectangle([margin * u, margin * u, (100 - margin) * u, 52 * u], radius=round(22 * u), fill=(255, 255, 255, 24))
        img = Image.alpha_composite(img, Image.composite(hl, Image.new("RGBA", (n, n)), mask))

    # Soft shadow under the page.
    sh = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    ImageDraw.Draw(sh).rounded_rectangle([25 * u, 17 * u, 75 * u, 87 * u], radius=round(4 * u), fill=(90, 0, 0, 70))
    img = Image.alpha_composite(img, sh.transform((n, n), Image.AFFINE, (1, 0, 0, 0, 1, -round(2 * u))))

    if tiny:
        d = page(img, u, 22, 12, 78, 88, 16, True)
    else:
        d = page(img, u, 25, 14, 75, 84, 14, False)
        for i, w in enumerate([34, 28, 34]):
            y = 28 + i * 7
            d.rounded_rectangle([32 * u, y * u, (32 + w * 0.92) * u, (y + 2.6) * u], radius=round(1.3 * u), fill=INK)

    # "PDF" label band across the lower page, overhanging both sides like a ribbon.
    band = (14, 56, 86, 78) if not tiny else (10, 52, 90, 82)
    d.rounded_rectangle([band[0] * u, band[1] * u, band[2] * u, band[3] * u], radius=round(5 * u), fill=(176, 20, 28, 255))
    if size >= 20:
        f = font(round((band[3] - band[1]) * 0.78 * u))
        d.text(((band[0] + band[2]) / 2 * u, (band[1] + band[3]) / 2 * u + 0.5 * u), "PDF", font=f, fill=WHITE, anchor="mm")
    return img.resize((size, size), Image.LANCZOS)


def draw_doc(size):
    """File-type icon: a page (no tile), red band with PDF."""
    ss = 8
    n = size * ss
    u = n / 100.0
    tiny = size <= 24
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    sh = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    ImageDraw.Draw(sh).rounded_rectangle([15 * u, 5 * u, 85 * u, 97 * u], radius=round(5 * u), fill=(0, 0, 0, 60))
    img = Image.alpha_composite(img, sh.transform((n, n), Image.AFFINE, (1, 0, 0, 0, 1, -round(1.5 * u))))
    d = page(img, u, 15, 3, 85, 95, 20, tiny, edge=(200, 200, 205, 255))
    if not tiny:
        for i, w in enumerate([46, 38, 46, 30]):
            y = 20 + i * 7
            d.rounded_rectangle([23 * u, y * u, (23 + w * 0.95) * u, (y + 2.6) * u], radius=round(1.3 * u), fill=(196, 198, 204, 255))
    band = (8, 58, 74, 84)
    d.rounded_rectangle([band[0] * u, band[1] * u, band[2] * u, band[3] * u], radius=round(4 * u), fill=(208, 36, 40, 255))
    if size >= 20:
        f = font(round((band[3] - band[1]) * 0.74 * u))
        d.text(((band[0] + band[2]) / 2 * u, (band[1] + band[3]) / 2 * u + 0.5 * u), "PDF", font=f, fill=WHITE, anchor="mm")
    return img.resize((size, size), Image.LANCZOS)


def save(draw, name):
    images = [draw(s) for s in SIZES]
    images[-1].save(os.path.join(HERE, f"{name}.ico"), sizes=[(s, s) for s in SIZES], append_images=images[:-1])
    draw(512).save(os.path.join(HERE, f"{name}-512.png"))


# MSIX images. Base sizes are the scale-100 sizes; each is written at these scales.
SCALES = [100, 125, 150, 200, 400]
TARGET_SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
# name: (width, height, icon size as a fraction of the shorter side)
TILES = {
    "Square44x44Logo": (44, 44, 1.0),
    "Square71x71Logo": (71, 71, 0.7),
    "Square150x150Logo": (150, 150, 0.6),
    "Wide310x150Logo": (310, 150, 0.6),
    "Square310x310Logo": (310, 310, 0.5),
    "StoreLogo": (50, 50, 1.0),
    "SplashScreen": (620, 300, 0.5),
}


def centered(draw, w, h, frac):
    """The icon drawn at frac of the shorter side, centered on a transparent w x h canvas."""
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    icon = draw(max(16, round(min(w, h) * frac)))
    img.paste(icon, ((w - icon.width) // 2, (h - icon.height) // 2), icon)
    return img


def save_msix(folder):
    os.makedirs(folder, exist_ok=True)
    for old in os.listdir(folder):
        if old.lower().endswith(".png"):
            os.remove(os.path.join(folder, old))
    count = 0
    for name, (w, h, frac) in TILES.items():
        for scale in SCALES:
            centered(draw_app, round(w * scale / 100), round(h * scale / 100), frac).save(os.path.join(folder, f"{name}.scale-{scale}.png"))
            count += 1
    # Taskbar, Start list and title bar pick an exact pixel size; the app tile has its own background, so plated and
    # unplated (light and dark taskbar) look the same. File-type logo: the document icon.
    for size in TARGET_SIZES:
        app, doc = draw_app(size), draw_doc(size)
        for suffix in ("", "_altform-unplated", "_altform-lightunplated"):
            app.save(os.path.join(folder, f"Square44x44Logo.targetsize-{size}{suffix}.png"))
            count += 1
        for suffix in ("", "_altform-unplated"):
            doc.save(os.path.join(folder, f"FileLogo.targetsize-{size}{suffix}.png"))
            count += 1
    return count


if __name__ == "__main__":
    save(draw_app, "PdfDesk")
    save(draw_doc, "PdfDocument")
    n = save_msix(os.path.join(HERE, "..", "packaging", "Assets"))
    print(f"wrote PdfDesk.ico, PdfDocument.ico, previews and {n} MSIX images in packaging/Assets")
