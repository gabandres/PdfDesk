namespace InkvoltPdf.Core;

/// <summary>How pages go onto the paper when printing (Acrobat's "Page Sizing &amp; Handling").</summary>
public enum PrintHandling
{
    /// <summary>One page per sheet, turned to the paper's orientation, sized by <see cref="PrintSetup.Sizing"/>.</summary>
    Fit,

    /// <summary>Several pages per sheet (2, 4, 6, 9 or 16), each scaled to its cell.</summary>
    PagesPerSheet,

    /// <summary>Saddle-stitched booklet: two pages on each side of a sheet, ordered so the folded stack reads in order.</summary>
    Booklet,

    /// <summary>One page enlarged across several sheets, to be trimmed and put together.</summary>
    Poster,
}

/// <summary>Size in Fit handling.</summary>
public enum PrintSizing
{
    /// <summary>The page's own size; only pages larger than the paper shrink to fit.</summary>
    ActualSize,

    /// <summary>Every page shrinks or grows to fill the paper.</summary>
    FitToPaper,
}

/// <summary>The order pages fill a sheet's cells: rows left to right (across), or columns top to bottom (down).</summary>
public enum PagesOrder { Across, Down }

/// <summary>Print handling choices. Lengths (<see cref="Overlap"/>) are in the paper's units.</summary>
public sealed record PrintSetup
{
    public PrintHandling Handling { get; init; } = PrintHandling.Fit;
    public PrintSizing Sizing { get; init; } = PrintSizing.ActualSize;

    /// <summary>Pages per sheet: 2, 4, 6, 9 or 16.</summary>
    public int PagesPerSheet { get; init; } = 2;

    public PagesOrder Order { get; init; } = PagesOrder.Across;

    /// <summary>A thin line around each page (pages per sheet).</summary>
    public bool Borders { get; init; }

    /// <summary>Booklet bound on the right (right-to-left languages): page 1 is on the right of the cover's back.</summary>
    public bool RightBinding { get; init; }

    /// <summary>Poster: the page's size on paper as a multiple of its own (2 = 200%).</summary>
    public double PosterScale { get; init; } = 2;

    /// <summary>Poster: how much neighboring sheets repeat of each other, for gluing.</summary>
    public double Overlap { get; init; }

    /// <summary>Poster: marks where to trim each sheet (the middle of each overlap).</summary>
    public bool CutMarks { get; init; } = true;

    public static readonly IReadOnlyList<int> PagesPerSheetChoices = [2, 4, 6, 9, 16];
}

/// <summary>
/// A page (or, for a poster, part of one) on a sheet. <paramref name="Box"/>: where, on the paper (origin at its
/// top-left, y down); <paramref name="Turns"/>: quarter turns clockwise it is drawn with (0 or 1); <paramref name="Source"/>:
/// the part of the page shown (page units, as shown, null for all of it). Page and box sizes keep the page's
/// proportions: with a turn, the box's width is the source's height.
/// </summary>
public sealed record PlacedPage(int Page, RectD Box, int Turns, RectD? Source = null);

/// <summary>A cut mark: a short line on the sheet (paper coordinates).</summary>
public readonly record struct CutMark(double X1, double Y1, double X2, double Y2);

/// <summary>What one side of one sheet of paper carries.</summary>
public sealed record PrintSheet(IReadOnlyList<PlacedPage> Pages, IReadOnlyList<CutMark> Marks, bool Borders)
{
    public static readonly PrintSheet Blank = new([], [], false);
}

/// <summary>
/// Where printed pages go on the paper: one per sheet (fit), several per sheet, booklets and posters. Pure geometry,
/// in any units as long as pages and paper use the same ones (the print code uses device-independent pixels).
/// <see cref="PrintPlan"/> lays out sheets one at a time, so printing renders each sheet only when it is sent.
/// </summary>
public static class PrintLayout
{
    /// <summary>
    /// The factor that sizes a page (already turned to the paper's orientation) on paper <paramref name="w"/> x
    /// <paramref name="h"/>, keeping its proportions. Actual size shrinks only what doesn't fit; fit to paper also
    /// enlarges (pages within 3% of the paper print at their own size).
    /// </summary>
    public static double Scale(double pageW, double pageH, double w, double h, PrintSizing sizing)
    {
        if (pageW <= 0 || pageH <= 0) return 1;
        var fit = Math.Min(w / pageW, h / pageH);
        if (sizing == PrintSizing.ActualSize || fit is > 1 and < 1.03) return Math.Min(1, fit);
        return fit;
    }

    /// <summary>One page per sheet: centered in <paramref name="area"/>, turned when that fits better (landscape on portrait paper).</summary>
    public static PlacedPage Single(int page, double w, double h, RectD area, PrintSizing sizing)
    {
        var turn = (w > h) != (area.Width > area.Height) && Math.Abs(w - h) > 1;
        var (fw, fh) = turn ? (h, w) : (w, h);
        var s = Scale(fw, fh, area.Width, area.Height, sizing);
        double dw = fw * s, dh = fh * s;
        return new PlacedPage(page, new RectD(area.X + (area.Width - dw) / 2, area.Y + (area.Height - dh) / 2, dw, dh), turn ? 1 : 0);
    }

    // ---- Pages per sheet ----

    /// <summary>
    /// The grid for <paramref name="n"/> pages per sheet: columns and rows, and whether the sheet is used turned (its
    /// long side across, the pages turned a quarter clockwise onto the paper): whichever shows a
    /// <paramref name="pageW"/> x <paramref name="pageH"/> page largest (2 portrait pages on portrait paper: side by
    /// side on the turned sheet).
    /// </summary>
    public static (int Cols, int Rows, bool Turned) Grid(int n, double pageW, double pageH, double areaW, double areaH)
    {
        if (!PrintSetup.PagesPerSheetChoices.Contains(n)) throw new ArgumentOutOfRangeException(nameof(n), "Pages per sheet: 2, 4, 6, 9 or 16.");
        var best = (Cols: 1, Rows: n, Turned: false);
        var bestScale = -1.0;
        foreach (var turned in new[] { false, true })
        {
            var (w, h) = turned ? (areaH, areaW) : (areaW, areaH);
            for (var cols = 1; cols <= n; cols++)
            {
                if (n % cols != 0) continue;
                var rows = n / cols;
                // Grids as people expect them: 2 = 2x1 or 1x2, 6 = 3x2 or 2x3, squares otherwise.
                if (Math.Max(cols, rows) > 2 * Math.Min(cols, rows) && n != 2) continue;
                var s = Math.Min(w / cols / pageW, h / rows / pageH);
                if (s > bestScale * 1.0001)
                {
                    bestScale = s;
                    best = (cols, rows, turned);
                }
            }
        }
        return best;
    }

    /// <summary>The cell of the <paramref name="k"/>th page on a cols x rows grid of a w x h sheet (sheet coordinates).</summary>
    public static RectD Cell(int k, int cols, int rows, PagesOrder order, double w, double h)
    {
        var (col, row) = order == PagesOrder.Across ? (k % cols, k / cols) : (k / rows, k % rows);
        double cw = w / cols, ch = h / rows;
        return new RectD(col * cw, row * ch, cw, ch);
    }

    /// <summary>
    /// A sheet of <paramref name="pages"/> (at most <paramref name="n"/>; their sizes from <paramref name="size"/>),
    /// laid out on the grid <see cref="Grid"/> picks for the first page. Each page is scaled to fit its cell, centered.
    /// </summary>
    public static PrintSheet PagesPerSheet(IReadOnlyList<int> pages, Func<int, (double W, double H)> size, int n, PagesOrder order, bool borders, RectD area)
    {
        if (pages.Count == 0) return PrintSheet.Blank;
        var (pw, ph) = size(pages[0]);
        var (cols, rows, turned) = Grid(n, pw, ph, area.Width, area.Height);
        var (w, h) = turned ? (area.Height, area.Width) : (area.Width, area.Height);
        // A small gap between neighbors, so pages (and their borders) don't touch.
        var gap = Math.Min(w / cols, h / rows) * 0.02;
        var list = new List<PlacedPage>();
        for (var k = 0; k < Math.Min(n, pages.Count); k++)
        {
            var cell = Cell(k, cols, rows, order, w, h).Inflate(-gap / 2);
            var (sw, sh) = size(pages[k]);
            var s = Math.Min(cell.Width / sw, cell.Height / sh);
            var box = new RectD(cell.X + (cell.Width - sw * s) / 2, cell.Y + (cell.Height - sh * s) / 2, sw * s, sh * s);
            list.Add(new PlacedPage(pages[k], ToPaper(box, turned, area), turned ? 1 : 0));
        }
        return new PrintSheet(list, [], borders);
    }

    /// <summary>
    /// A box on a sheet used turned (its width along the paper's height) to the paper: the sheet is turned a quarter
    /// clockwise onto <paramref name="area"/>, so its top-left corner lands at the area's top-right.
    /// </summary>
    internal static RectD ToPaper(RectD box, bool turned, RectD area) =>
        turned ? new RectD(area.Right - box.Bottom, area.Y + box.X, box.Height, box.Width) : new RectD(area.X + box.X, area.Y + box.Y, box.Width, box.Height);

    private static (double X, double Y) ToPaper(double x, double y, bool turned, RectD area) =>
        turned ? (area.Right - y, area.Y + x) : (area.X + x, area.Y + y);

    // ---- Booklet ----

    /// <summary>Sheets of paper a booklet of <paramref name="pageCount"/> pages needs (4 pages each, both sides).</summary>
    public static int BookletSheets(int pageCount) => Math.Max(1, (pageCount + 3) / 4);

    /// <summary>
    /// The pages on each side of a saddle-stitched booklet's sheets, in printing order (sheet 1 front, sheet 1 back,
    /// sheet 2 front, …): indexes into the <paramref name="pageCount"/> pages printed, -1 for a blank (the count is
    /// padded to a multiple of 4 with blank pages at the end). Folded and stacked, the sheets read 1, 2, 3, … With
    /// <paramref name="rightBinding"/> each side is mirrored (the booklet opens to the left).
    /// </summary>
    public static IReadOnlyList<(int Left, int Right)> BookletSides(int pageCount, bool rightBinding = false)
    {
        var sheets = BookletSheets(pageCount);
        var n = sheets * 4;
        var sides = new List<(int, int)>(sheets * 2);
        int P(int i) => i < pageCount ? i : -1;
        for (var s = 0; s < sheets; s++)
        {
            (int L, int R) front = (P(n - 1 - 2 * s), P(2 * s));
            (int L, int R) back = (P(2 * s + 1), P(n - 2 - 2 * s));
            sides.Add(rightBinding ? (front.R, front.L) : front);
            sides.Add(rightBinding ? (back.R, back.L) : back);
        }
        return sides;
    }

    /// <summary>
    /// One side of a booklet sheet: the sheet used with its long side across (turned onto portrait paper), the two pages
    /// side by side, each scaled to its half and set against the fold.
    /// </summary>
    public static PrintSheet BookletSide(int left, int right, Func<int, (double W, double H)> size, RectD area)
    {
        var turned = area.Width < area.Height;
        var (w, h) = turned ? (area.Height, area.Width) : (area.Width, area.Height);
        var half = w / 2;
        var list = new List<PlacedPage>();
        foreach (var (page, isLeft) in new[] { (left, true), (right, false) })
        {
            if (page < 0) continue;
            var (pw, ph) = size(page);
            var s = Math.Min(half / pw, h / ph);
            double bw = pw * s, bh = ph * s;
            var box = new RectD(isLeft ? half - bw : half, (h - bh) / 2, bw, bh);
            list.Add(new PlacedPage(page, ToPaper(box, turned, area), turned ? 1 : 0));
        }
        return new PrintSheet(list, [], false);
    }

    // ---- Poster ----

    /// <summary>
    /// How a page <paramref name="pageW"/> x <paramref name="pageH"/> enlarged <paramref name="scale"/> times is tiled:
    /// columns and rows of sheets of <paramref name="areaW"/> x <paramref name="areaH"/>, overlapping by
    /// <paramref name="overlap"/>, and whether the sheets are used turned (fewer sheets wins; upright on a tie).
    /// </summary>
    public static (int Cols, int Rows, bool Turned) PosterTiles(double pageW, double pageH, double scale, double areaW, double areaH, double overlap)
    {
        (int, int) Count(double w, double h)
        {
            var ov = ClampOverlap(overlap, w, h);
            return (Tiles(pageW * scale, w, ov), Tiles(pageH * scale, h, ov));
        }
        var (c1, r1) = Count(areaW, areaH);
        var (c2, r2) = Count(areaH, areaW);
        return c2 * r2 < c1 * r1 ? (c2, r2, true) : (c1, r1, false);
    }

    private static double ClampOverlap(double overlap, double w, double h) => Math.Clamp(overlap, 0, Math.Min(w, h) / 4);

    /// <summary>Tiles of <paramref name="tile"/> overlapping by <paramref name="ov"/> needed to cover <paramref name="length"/>.</summary>
    private static int Tiles(double length, double tile, double ov) =>
        Math.Max(1, (int)Math.Ceiling((length - ov) / (tile - ov) - 1e-9));

    /// <summary>
    /// Sheet <paramref name="tile"/> (row by row from the top-left) of the poster of page <paramref name="page"/>
    /// (<paramref name="pageW"/> x <paramref name="pageH"/>): the part of the page it shows, centered on the tiles as a
    /// whole, and cut marks at the middle of each overlap with a neighbor.
    /// </summary>
    public static PrintSheet PosterTile(int page, double pageW, double pageH, int tile, PrintSetup setup, RectD area)
    {
        var scale = setup.PosterScale;
        var (cols, rows, turned) = PosterTiles(pageW, pageH, scale, area.Width, area.Height, setup.Overlap);
        var (w, h) = turned ? (area.Height, area.Width) : (area.Width, area.Height);
        var ov = ClampOverlap(setup.Overlap, w, h);
        double stepX = w - ov, stepY = h - ov;
        double pw = pageW * scale, ph = pageH * scale;
        // The poster centered on what the tiles cover together.
        var ox = (cols * stepX + ov - pw) / 2;
        var oy = (rows * stepY + ov - ph) / 2;
        var (col, row) = (tile % cols, tile / cols);
        // This sheet's window onto the enlarged page, and the part of the page in it.
        var window = new RectD(col * stepX - ox, row * stepY - oy, w, h);
        var shown = window.Intersect(new RectD(0, 0, pw, ph));
        var pages = new List<PlacedPage>();
        if (!shown.IsEmpty)
        {
            var box = new RectD(shown.X - window.X, shown.Y - window.Y, shown.Width, shown.Height);
            var source = new RectD(shown.X / scale, shown.Y / scale, shown.Width / scale, shown.Height / scale);
            pages.Add(new PlacedPage(page, ToPaper(box, turned, area), turned ? 1 : 0, source));
        }

        var marks = new List<CutMark>();
        if (setup.CutMarks)
        {
            var len = Math.Min(Math.Min(w, h) * 0.04, 18);
            void Vertical(double x)
            {
                marks.Add(Mark(x, 0, x, len));
                marks.Add(Mark(x, h - len, x, h));
            }
            void Horizontal(double y)
            {
                marks.Add(Mark(0, y, len, y));
                marks.Add(Mark(w - len, y, w, y));
            }
            CutMark Mark(double x1, double y1, double x2, double y2)
            {
                var (a, b) = ToPaper(x1, y1, turned, area);
                var (c, d) = ToPaper(x2, y2, turned, area);
                return new CutMark(a, b, c, d);
            }
            if (col > 0) Vertical(ov / 2);
            if (col < cols - 1) Vertical(w - ov / 2);
            if (row > 0) Horizontal(ov / 2);
            if (row < rows - 1) Horizontal(h - ov / 2);
        }
        return new PrintSheet(pages, marks, false);
    }
}

/// <summary>
/// A print job's sheets: how many, and each one laid out when asked for (so a long job is never laid out, or rendered,
/// all at once). Pages are the 0-based page numbers printed, in order; sizes and paper in the same units.
/// </summary>
public sealed class PrintPlan
{
    private readonly IReadOnlyList<int> _pages;
    private readonly Func<int, (double W, double H)> _size;
    private readonly RectD _area;
    private readonly PrintSetup _setup;
    private readonly IReadOnlyList<(int Left, int Right)>? _sides;
    private readonly int[]? _posterStart; // poster: the first sheet of each page, and the total at the end

    /// <param name="size">A page's size as shown (rotation applied).</param>
    /// <param name="area">The paper's printable area (paper coordinates: origin at its top-left, y down).</param>
    public PrintPlan(IReadOnlyList<int> pages, Func<int, (double W, double H)> size, RectD area, PrintSetup setup)
    {
        _pages = pages;
        _size = size;
        _area = area;
        _setup = setup;
        switch (setup.Handling)
        {
            case PrintHandling.Booklet:
                _sides = PrintLayout.BookletSides(pages.Count, setup.RightBinding);
                SheetCount = _sides.Count;
                break;
            case PrintHandling.PagesPerSheet:
                SheetCount = (pages.Count + setup.PagesPerSheet - 1) / setup.PagesPerSheet;
                break;
            case PrintHandling.Poster:
                _posterStart = new int[pages.Count + 1];
                for (var i = 0; i < pages.Count; i++)
                {
                    var (w, h) = size(pages[i]);
                    var (c, r, _) = PrintLayout.PosterTiles(w, h, setup.PosterScale, area.Width, area.Height, setup.Overlap);
                    _posterStart[i + 1] = _posterStart[i] + c * r;
                }
                SheetCount = _posterStart[^1];
                break;
            default:
                SheetCount = pages.Count;
                break;
        }
    }

    /// <summary>Sides of paper printed (a booklet: two per sheet of paper).</summary>
    public int SheetCount { get; }

    /// <summary>Sheets of paper used (a booklet prints on both sides).</summary>
    public int PaperCount => _setup.Handling == PrintHandling.Booklet ? SheetCount / 2 : SheetCount;

    public PrintSheet Sheet(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, SheetCount);
        switch (_setup.Handling)
        {
            case PrintHandling.Booklet:
            {
                var (l, r) = _sides![index];
                return PrintLayout.BookletSide(l < 0 ? -1 : _pages[l], r < 0 ? -1 : _pages[r], _size, _area);
            }
            case PrintHandling.PagesPerSheet:
            {
                var n = _setup.PagesPerSheet;
                var slice = _pages.Skip(index * n).Take(n).ToList();
                return PrintLayout.PagesPerSheet(slice, _size, n, _setup.Order, _setup.Borders, _area);
            }
            case PrintHandling.Poster:
            {
                var i = Array.FindLastIndex(_posterStart!, s => s <= index);
                var (w, h) = _size(_pages[i]);
                return PrintLayout.PosterTile(_pages[i], w, h, index - _posterStart![i], _setup, _area);
            }
            default:
            {
                var (w, h) = _size(_pages[index]);
                return new PrintSheet([PrintLayout.Single(_pages[index], w, h, _area, _setup.Sizing)], [], false);
            }
        }
    }
}
