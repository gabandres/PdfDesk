"""Generates the Inkvolt PDF icons (16-256 px) and 512 px previews.

  assets/InkvoltPdf.ico       app icon ("Vivid Fold", shared with Inkvolt Print): no tile, per Windows 11 app icon
                              guidance; one bold folded sheet in amber to coral to pink with a white bolt.
  assets/Inkvolt-512.png      the suite's mark (website, README): an ink drop with a white bolt, amber to pink to
                              violet, spanning both apps' colors.
  assets/PdfDocument.ico      file-type icon for .pdf in Explorer: a white page with a "PDF" band in the same colors.

  packaging/Assets/*.png     MSIX images (tiles, Start/taskbar icons per target size, store logo, splash screen, file-type
                              logo), named with MRT qualifiers (scale-N, targetsize-N, altform-unplated) that makepri
                              indexes into resources.pri (packaging/pack-msix.ps1).

Shapes are drawn in a 48-unit grid (assets/iconkit.py, shared with Inkvolt Print) at about 1024 px and box-downsampled
to each size; 24 px and below use bolder shapes.
Run:  python -I assets/make-icon.py   (needs Pillow)
"""
import os
import sys
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)  # python -I leaves the script's folder off the path
from iconkit import Canvas, Grad, bolt_pts, drop_pts, page_pts  # noqa: E402

SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
FONT = "C:/Windows/Fonts/segoeuib.ttf"

SHEET = [(0, "#FF9248"), (0.55, "#FF4558"), (1, "#D9206F")]  # amber -> coral -> pink (Inkvolt Print: cyan -> blue)


def small(size):
    return size <= 24


def fold_shadowed(c, box, f, pg, shade):
    """The folded corner, white, casting a soft shadow onto the sheet."""
    x0, y0, x1, y1 = box
    fold = c.poly([(x1 - f, y0), (x1 - f, y0 + f), (x1, y0 + f)], [0.6, 2.0, 0.6])
    c.shadow(fold, dy=0.8, dx=-0.6, blur=1.0, color=shade, alpha=0.35, clip=pg)
    c.fill(fold, Grad((x1 - f, y0), (x1, y0 + f), [(0, (255, 255, 255, 235)), (1, (255, 255, 255, 150))]))


def draw_app(size):
    c = Canvas(size)
    s = small(size)
    box, f, bolt_h = ((6, 2, 42, 46), 13, 24) if s else ((8, 3, 40, 45), 12, 21)
    x0, y0, x1, y1 = box
    pg = c.poly(page_pts(*box, f), [2.4, 0.8, 0.8, 2.4, 2.4])
    c.shadow(pg, dy=0.9, blur=1.2, alpha=0.22)
    c.fill(pg, Grad((x0, y0), (x1, y1), SHEET))
    fold_shadowed(c, box, f, pg, (90, 0, 40))
    bolt = c.poly(bolt_pts((x0 + x1) / 2, (y0 + y1) / 2 + 2.5, bolt_h), 0.7)
    if not s:
        c.shadow(bolt, dy=0.8, blur=0.9, color=(60, 0, 40), alpha=0.25, clip=pg)
    c.fill(bolt, "#FFFFFF")
    return c.out()


def draw_doc(size):
    """File-type icon: a white page, faint text lines, and a "PDF" band in the app's colors."""
    c = Canvas(size)
    s = small(size)
    box, f = ((7, 1, 41, 47), 12) if s else ((8, 2, 40, 46), 11)
    x0, y0, x1, y1 = box
    pg = c.poly(page_pts(*box, f), [2.0, 0.8, 0.8, 2.0, 2.0])
    c.shadow(pg, dy=0.8, blur=1.0, alpha=0.28)
    c.fill(pg, Grad((x0, y0), (x1, y1), [(0, "#FFFFFF"), (1, "#EEF0F4")]))
    fold_shadowed(c, box, f, pg, (40, 40, 60))
    c.fill(c.poly([(x1 - f, y0), (x1 - f, y0 + f), (x1, y0 + f)], [0.6, 2.0, 0.6]), "#D9DDE5", 0.6)
    if not s:
        for i, w in enumerate([22, 17, 22]):
            y = 16 + i * 4.2
            c.fill(c.rrect(13, y, 13 + w, y + 1.6, 0.8), "#C3C8D2")
    band = (4, 29, 36, 41) if not s else (3, 27, 37, 42)
    b = c.rrect(*band, 2.2)
    c.shadow(b, dy=0.6, blur=0.8, color=(90, 0, 40), alpha=0.3)
    c.fill(b, Grad((band[0], band[1]), (band[2], band[3]), SHEET))
    img = c.out()
    if size >= 32:  # smaller, the letters blur into the band
        d = ImageDraw.Draw(img)
        k = size / 48
        try:
            fnt = ImageFont.truetype(FONT, max(1, round((band[3] - band[1]) * 0.72 * k)))
        except OSError:
            fnt = ImageFont.load_default()
        d.text(((band[0] + band[2]) / 2 * k, (band[1] + band[3]) / 2 * k + 0.3 * k), "PDF", font=fnt, fill="#FFFFFF", anchor="mm")
    return img


def draw_suite(size):
    """The Inkvolt mark: an ink drop with a white bolt (ink + volt)."""
    c = Canvas(size)
    s = small(size)
    dp = c.poly(drop_pts(24, 29, 16.5 if s else 15, 0 if s else 2))
    c.shadow(dp, dy=1.0, blur=1.2, alpha=0.25)
    c.fill(dp, Grad((12, 6), (36, 44), [(0, "#FF8A4C"), (0.5, "#F2416E"), (1, "#6A5CFF")]))
    bolt = c.poly(bolt_pts(24, 29.5, 19 if s else 17), 0.5)
    if not s:
        c.shadow(bolt, dy=0.8, blur=0.9, color=(60, 0, 40), alpha=0.25, clip=dp)
    c.fill(bolt, "#FFFFFF")
    return c.out()


def save(draw, name):
    images = [draw(s) for s in SIZES]
    images[-1].save(os.path.join(HERE, f"{name}.ico"), sizes=[(s, s) for s in SIZES], append_images=images[:-1])
    draw(512).save(os.path.join(HERE, f"{name}-512.png"))


# MSIX images. Base sizes are the scale-100 sizes; each is written at these scales.
SCALES = [100, 125, 150, 200, 400]
TARGET_SIZES = [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256]  # every size Windows asks for
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
    # Taskbar, Start list and title bar pick an exact pixel size. No tile, so plated and unplated are one image; its
    # colors keep their contrast on both the light and the dark taskbar. File-type logo: the document icon.
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
    save(draw_app, "InkvoltPdf")
    save(draw_doc, "PdfDocument")
    draw_suite(512).save(os.path.join(HERE, "Inkvolt-512.png"))
    n = save_msix(os.path.join(HERE, "..", "packaging", "Assets"))
    print(f"wrote InkvoltPdf.ico, PdfDocument.ico, previews and {n} MSIX images in packaging/Assets")
