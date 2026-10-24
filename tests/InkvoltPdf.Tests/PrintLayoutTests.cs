using InkvoltPdf.Core;

namespace InkvoltPdf.Tests;

public class PrintLayoutTests
{
    // Letter paper in device-independent pixels, quarter-inch margins.
    private static readonly RectD Letter = new(24, 24, 768, 1008);
    private static (double W, double H) Portrait(int _) => (816, 1056);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(8, 2)]
    [InlineData(9, 3)]
    [InlineData(17, 5)]
    public void BookletsUseSheetsOfFourPages(int pages, int sheets) => Assert.Equal(sheets, PrintLayout.BookletSheets(pages));

    [Fact]
    public void BookletOfEightPages()
    {
        // Sheet 1: 8|1 on the front, 2|7 on the back; sheet 2: 6|3, then 4|5 (0-based below).
        Assert.Equal([(7, 0), (1, 6), (5, 2), (3, 4)], PrintLayout.BookletSides(8));
        // Bound on the right: each side mirrored.
        Assert.Equal([(0, 7), (6, 1), (2, 5), (4, 3)], PrintLayout.BookletSides(8, rightBinding: true));
        // Padded with blank pages at the end: 5 pages make 8, the back cover side is blank.
        Assert.Equal([(-1, 0), (1, -1), (-1, 2), (3, 4)], PrintLayout.BookletSides(5));
        Assert.Equal([(-1, 0), (-1, -1)], PrintLayout.BookletSides(1));
    }

    [Theory]
    [MemberData(nameof(OneToSeventeen))]
    public void FoldedBookletsReadInOrder(int pages)
    {
        var sides = PrintLayout.BookletSides(pages);
        var sheets = sides.Count / 2;
        Assert.Equal(PrintLayout.BookletSheets(pages) * 2, sides.Count);
        // Nest the folded sheets (sheet 1 outside) and read the right halves of the fronts, then turn the pages: the
        // reading order of a saddle-stitched booklet is front-right, back-left, back-right, … of the nested sheets.
        var read = new List<int>();
        for (var s = 0; s < sheets; s++) read.AddRange([sides[2 * s].Right, sides[2 * s + 1].Left]);
        for (var s = sheets - 1; s >= 0; s--) read.AddRange([sides[2 * s + 1].Right, sides[2 * s].Left]);
        Assert.Equal([.. Enumerable.Range(0, sheets * 4).Select(i => i < pages ? i : -1)], read);
        // Every page exactly once.
        Assert.Equal(Enumerable.Range(0, pages), sides.SelectMany(s => new[] { s.Left, s.Right }).Where(p => p >= 0).Order());
    }

    public static TheoryData<int> OneToSeventeen() => [.. Enumerable.Range(1, 17)];

    [Fact]
    public void BookletPagesSitAgainstTheFold()
    {
        var side = PrintLayout.BookletSide(7, 0, Portrait, Letter);
        // Portrait paper: the sheet is used turned, so the "left" page is on the top half, turned a quarter.
        var left = side.Pages.Single(p => p.Page == 7);
        var right = side.Pages.Single(p => p.Page == 0);
        Assert.All(side.Pages, p => Assert.Equal(1, p.Turns));
        Assert.Equal(Letter.Y + Letter.Height / 2, left.Box.Bottom, 3);
        Assert.Equal(Letter.Y + Letter.Height / 2, right.Box.Y, 3);
        // Half the sheet each, proportions kept (turned: box width is the page's height).
        Assert.Equal(816.0 / 1056, left.Box.Height / left.Box.Width, 3);
        Assert.True(left.Box.Height <= Letter.Height / 2 + 0.001 && left.Box.Width <= Letter.Width + 0.001);
        // A blank half has nothing on it.
        Assert.Single(PrintLayout.BookletSide(-1, 0, Portrait, Letter).Pages);
    }

    [Theory]
    [InlineData(2, 2, 1, true)]   // two portrait pages side by side on the turned sheet
    [InlineData(4, 2, 2, false)]
    [InlineData(6, 3, 2, true)]
    [InlineData(9, 3, 3, false)]
    [InlineData(16, 4, 4, false)]
    public void PagesPerSheetGrids(int n, int cols, int rows, bool turned) =>
        Assert.Equal((cols, rows, turned), PrintLayout.Grid(n, 816, 1056, Letter.Width, Letter.Height));

    [Fact]
    public void LandscapePagesTwoUpStackOnPortraitPaper() =>
        Assert.Equal((1, 2, false), PrintLayout.Grid(2, 1056, 816, Letter.Width, Letter.Height));

    [Fact]
    public void PagesFillTheirCellsInOrder()
    {
        int[] nine = [0, 1, 2, 3, 4, 5, 6, 7, 8];
        var across = PrintLayout.PagesPerSheet(nine, Portrait, 9, PagesOrder.Across, true, Letter);
        Assert.True(across.Borders);
        Assert.Equal(9, across.Pages.Count);
        Assert.All(across.Pages, p => Assert.Equal(0, p.Turns));
        // Across: 0 1 2 / 3 4 5 / 6 7 8; down: 0 3 6 / 1 4 7 / 2 5 8.
        Assert.True(across.Pages[1].Box.X > across.Pages[0].Box.X && Math.Abs(across.Pages[1].Box.Y - across.Pages[0].Box.Y) < 0.01);
        Assert.True(across.Pages[3].Box.Y > across.Pages[0].Box.Y && Math.Abs(across.Pages[3].Box.X - across.Pages[0].Box.X) < 0.01);
        var down = PrintLayout.PagesPerSheet(nine, Portrait, 9, PagesOrder.Down, false, Letter);
        Assert.Equal(across.Pages[3].Box, down.Pages[1].Box);
        Assert.Equal(across.Pages[1].Box, down.Pages[3].Box);
        // A sheet with fewer pages than cells fills the first ones.
        Assert.Equal(across.Pages[1].Box, PrintLayout.PagesPerSheet([0, 1], Portrait, 9, PagesOrder.Across, false, Letter).Pages[1].Box);
        // Inside the printable area, proportions kept, none overlapping.
        foreach (var p in across.Pages)
        {
            Assert.True(p.Box.X >= Letter.X && p.Box.Right <= Letter.Right && p.Box.Y >= Letter.Y && p.Box.Bottom <= Letter.Bottom);
            Assert.Equal(816.0 / 1056, p.Box.Width / p.Box.Height, 3);
        }
        Assert.All(across.Pages.SelectMany((a, i) => across.Pages.Skip(i + 1), (a, b) => a.Box.Intersect(b.Box)), r => Assert.True(r.IsEmpty));
    }

    [Fact]
    public void TwoUpTurnsThePages()
    {
        var sheet = PrintLayout.PagesPerSheet([0, 1], Portrait, 2, PagesOrder.Across, false, Letter);
        Assert.All(sheet.Pages, p => Assert.Equal(1, p.Turns));
        // The turned sheet's left page lands on the top half of the paper.
        Assert.True(sheet.Pages[0].Box.Bottom <= sheet.Pages[1].Box.Y + 0.01);
        Assert.Equal(1056.0 / 816, sheet.Pages[0].Box.Width / sheet.Pages[0].Box.Height, 3);
    }

    [Fact]
    public void PlanCountsSheets()
    {
        int[] pages = [.. Enumerable.Range(0, 11)];
        Assert.Equal(11, new PrintPlan(pages, Portrait, Letter, new PrintSetup()).SheetCount);
        Assert.Equal(3, new PrintPlan(pages, Portrait, Letter, new PrintSetup { Handling = PrintHandling.PagesPerSheet, PagesPerSheet = 4 }).SheetCount);
        var booklet = new PrintPlan(pages, Portrait, Letter, new PrintSetup { Handling = PrintHandling.Booklet });
        Assert.Equal((6, 3), (booklet.SheetCount, booklet.PaperCount));
        // The last pages-per-sheet sheet holds what's left.
        Assert.Equal([8, 9, 10], new PrintPlan(pages, Portrait, Letter, new PrintSetup { Handling = PrintHandling.PagesPerSheet, PagesPerSheet = 4 }).Sheet(2).Pages.Select(p => p.Page));
        // Page numbers come from the list printed, not its positions.
        var picked = new PrintPlan([4, 9], Portrait, Letter, new PrintSetup { Handling = PrintHandling.Booklet });
        Assert.Equal([4], picked.Sheet(0).Pages.Select(p => p.Page));
        Assert.Equal([9], picked.Sheet(1).Pages.Select(p => p.Page));
    }

    [Fact]
    public void FitShrinksOrEnlargesOnRequest()
    {
        // A 4 x 6 in page (384 x 576 DIP): actual size stays, fit to paper fills the height.
        var actual = PrintLayout.Single(0, 384, 576, Letter, PrintSizing.ActualSize);
        Assert.Equal((384.0, 576.0), (actual.Box.Width, actual.Box.Height));
        Assert.Equal(Letter.CenterX, actual.Box.CenterX, 3);
        var fit = PrintLayout.Single(0, 384, 576, Letter, PrintSizing.FitToPaper);
        Assert.Equal(Letter.Height, fit.Box.Height, 3);
        // Oversized pages shrink either way; landscape pages turn onto portrait paper.
        Assert.Equal(Letter.Width, PrintLayout.Single(0, 1632, 2112, Letter, PrintSizing.ActualSize).Box.Width, 3);
        Assert.Equal(1, PrintLayout.Single(0, 1056, 816, Letter, PrintSizing.ActualSize).Turns);
    }

    [Theory]
    [InlineData(2.0, 0.0, 2, 3, true)]   // 200% of Letter: 6 turned sheets rather than 9 upright (the margins)
    [InlineData(1.0, 0.0, 1, 2, true)]   // the margins make even 100% a little too big for one sheet
    [InlineData(0.9, 0.0, 1, 1, false)]
    [InlineData(3.0, 48.0, 3, 5, true)]
    [InlineData(2.5, 36.0, 3, 3, false)]
    public void PosterTiling(double scale, double overlap, int cols, int rows, bool turned) =>
        Assert.Equal((cols, rows, turned), PrintLayout.PosterTiles(816, 1056, scale, Letter.Width, Letter.Height, overlap));

    [Fact]
    public void PosterTilesCoverThePageOnce()
    {
        var setup = new PrintSetup { Handling = PrintHandling.Poster, PosterScale = 2.5, Overlap = 36, CutMarks = true };
        var plan = new PrintPlan([0], Portrait, Letter, setup);
        var (cols, rows, _) = PrintLayout.PosterTiles(816, 1056, 2.5, Letter.Width, Letter.Height, 36);
        Assert.Equal(cols * rows, plan.SheetCount);
        // The sources tile the page, overlapping only by the overlap (in page units: 36 / 2.5).
        var sources = Enumerable.Range(0, plan.SheetCount).Select(i => plan.Sheet(i).Pages.Single().Source!.Value).ToList();
        Assert.Equal(0, sources.Min(s => s.X), 3);
        Assert.Equal(0, sources.Min(s => s.Y), 3);
        Assert.Equal(816, sources.Max(s => s.Right), 3);
        Assert.Equal(1056, sources.Max(s => s.Bottom), 3);
        var first = sources[0];
        var next = sources[1];
        Assert.Equal(36 / 2.5, first.Right - next.X, 3);
        // Placed at 250%; inner edges get cut marks, outer ones don't.
        var sheet = plan.Sheet(0);
        Assert.Equal(first.Width * 2.5, sheet.Pages[0].Box.Width, 3);
        Assert.Equal(4, sheet.Marks.Count); // right and bottom edges of the top-left sheet, two ticks each
        Assert.All(sheet.Marks, m => Assert.True(m.X1 >= Letter.X - 0.01 && m.X2 <= Letter.Right + 0.01 && m.Y2 <= Letter.Bottom + 0.01));
        Assert.Empty(new PrintPlan([0], Portrait, Letter, setup with { CutMarks = false }).Sheet(0).Marks);
    }

    [Fact]
    public void PosterTurnsSheetsWhenThatSavesPaper()
    {
        // A tall banner page (1:3) at 100%: 4 turned sheets rather than 6 upright.
        Assert.Equal((1, 4, true), PrintLayout.PosterTiles(800, 2400, 1, Letter.Width, Letter.Height, 0));
        var tile = PrintLayout.PosterTile(0, 800, 2400, 0, new PrintSetup { Handling = PrintHandling.Poster, PosterScale = 1 }, Letter);
        Assert.Equal(1, tile.Pages.Single().Turns);
    }
}
