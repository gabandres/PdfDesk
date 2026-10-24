using MuPDFCore;
using InkvoltPdf.Core;

namespace InkvoltPdf.Tests;

/// <summary>Hit-testing and caret placement on synthetic text layers; the view rotation maths; MuPDF page caching.</summary>
public class PageTextTests
{
    private const double W = 6, H = 10; // character box

    /// <summary>A left-to-right line of fixed-width characters with its top-left at (x, y).</summary>
    private static List<(string, RectD)> Line(string text, double x, double y) =>
        [.. text.Select((c, i) => (c.ToString(), new RectD(x + i * W, y, W, H)))];

    /// <summary>A right-to-left line in reading order: the first character is the rightmost.</summary>
    private static List<(string, RectD)> RtlLine(string text, double right, double y) =>
        [.. text.Select((c, i) => (c.ToString(), new RectD(right - (i + 1) * W, y, W, H)))];

    /// <summary>Text turned clockwise: reads top to bottom, characters stacked at x.</summary>
    private static List<(string, RectD)> DownLine(string text, double x, double y) =>
        [.. text.Select((c, i) => (c.ToString(), new RectD(x, y + i * W, H, W)))];

    [Fact]
    public void BetweenTwoLinesOfTheLeftColumnTheCaretStaysInThatColumn()
    {
        // Two columns whose lines are offset: right-column lines sit level with the gaps of the left column.
        var lines = new List<List<(string, RectD)>>();
        for (var i = 0; i < 5; i++) lines.Add(Line("left column text here", 50, 100 + i * 14));
        for (var i = 0; i < 5; i++) lines.Add(Line("right column text", 250, 107 + i * 14));
        var text = new PageText(lines);
        var rightStart = text.Text.IndexOf("right", StringComparison.Ordinal);

        var caret = text.HitTest(80, 112); // in the gap between left lines 1 and 2, level with right line 1
        Assert.True(caret < rightStart, $"caret {caret} jumped to the right column");
        caret = text.HitTest(280, 119); // the gap between right lines 1 and 2, level with a left line
        Assert.True(caret >= rightStart, $"caret {caret} jumped to the left column");
    }

    [Fact]
    public void BesideAShortLastLineOrAHeadingTheCaretGoesToItsEnd()
    {
        var text = new PageText(
        [
            Line("Title", 50, 60),
            Line("a paragraph line that is long", 50, 100),
            Line("another long paragraph line..", 50, 114),
            Line("end.", 50, 128),
            Line("next paragraph starts here...", 50, 150),
        ]);
        Assert.Equal(5, text.HitTest(200, 65)); // right of the heading
        var end = text.Text.IndexOf("end.", StringComparison.Ordinal) + 4;
        Assert.Equal(end, text.HitTest(200, 133)); // right of the paragraph's short last line
        Assert.Equal(text.Text.IndexOf("another", StringComparison.Ordinal) + 2, text.HitTest(50 + 2 * W + 1, 118));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SidewaysLinesPlaceTheCaretAlongTheirDirection(bool withDirection)
    {
        // Text on a page shown turned: the line runs down the page, x says nothing about the position in it.
        var text = new PageText([DownLine("abcd", 100, 100)], withDirection ? [(0, 1)] : null);
        Assert.Equal(1, text.HitTest(140, 100 + W * 1.2)); // upper half of 'b'
        Assert.Equal(2, text.HitTest(60, 100 + W * 1.8));  // lower half of 'b'
        Assert.Equal(4, text.HitTest(105, 300));           // below the end
        Assert.Equal(0, text.HitTest(105, 20));            // above the start
    }

    [Fact]
    public void RightToLeftCharactersCountFromTheirRightSide()
    {
        var text = new PageText([RtlLine("אבג", 130, 100)]); // א 124-130, ב 118-124, ג 112-118
        Assert.Equal(0, text.HitTest(129, 105)); // right half of א: before it
        Assert.Equal(1, text.HitTest(125, 105)); // left half of א: after it
        Assert.Equal(3, text.HitTest(100, 105)); // left of everything: the end of the line
        Assert.Equal(0, text.HitTest(150, 105)); // right of everything: the start
    }

    [Fact]
    public void MixedLinesPlaceTheCaretPerRun()
    {
        // "ab אב" in reading order; the Hebrew run is laid out right to left after the space.
        List<(string, RectD)> line =
        [
            ("a", new RectD(50, 100, W, H)), ("b", new RectD(56, 100, W, H)), (" ", new RectD(62, 100, W, H)),
            ("א", new RectD(74, 100, W, H)), ("ב", new RectD(68, 100, W, H)),
        ];
        var text = new PageText([line]);
        Assert.Equal(0, text.HitTest(51, 105)); // left half of 'a'
        Assert.Equal(2, text.HitTest(61, 105)); // right half of 'b'
        Assert.Equal(3, text.HitTest(79, 105)); // right half of א: before it
        Assert.Equal(4, text.HitTest(76, 105)); // left half of א: after it
        Assert.Equal(5, text.HitTest(69, 105)); // left half of ב: after it, the end
    }

    [Fact]
    public void ASurrogatePairIsNeverSplit()
    {
        List<(string, RectD)> line = [("a", new RectD(50, 100, W, H)), ("😀", new RectD(56, 100, W, H)), ("b", new RectD(62, 100, W, H))];
        var text = new PageText([line]);
        Assert.Equal(1, text.HitTest(57, 105));
        Assert.Equal(3, text.HitTest(61, 105));
    }

    [Fact]
    public void MovingBetweenLinesKeepsThePositionAlongThem()
    {
        var text = new PageText([Line("first line", 50, 100), RtlLine("שורה שנייה", 110, 114)]);
        var second = text.Text.IndexOf('\n') + 1;
        // From x = 52 (inside 'f') down to the right-to-left line: its leftmost character is its last one.
        Assert.Equal(text.Text.Length, text.NearestCaretInLine(second, 50.5, 119));
        Assert.Equal(second, text.NearestCaretInLine(second, 109.5, 119));
        Assert.Equal(1, text.NearestCaretInLine(0, 56 + 1, 105));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void RotatedViewMapsPointsBothWays(int rotation)
    {
        const double pw = 200, ph = 300;
        var (vw, vh) = RotatedView.Size(pw, ph, rotation);
        foreach (var (x, y) in new[] { (0.0, 0.0), (pw, 0.0), (pw, ph), (0.0, ph), (17.5, 42.25) })
        {
            var (vx, vy) = RotatedView.ToView(x, y, pw, ph, rotation);
            Assert.InRange(vx, 0, vw);
            Assert.InRange(vy, 0, vh);
            var (bx, by) = RotatedView.FromView(vx, vy, pw, ph, rotation);
            Assert.Equal(x, bx, 9);
            Assert.Equal(y, by, 9);
        }
        var r = new RectD(10, 20, 30, 40);
        Assert.Equal(r, RotatedView.FromView(RotatedView.ToView(r, pw, ph, rotation), pw, ph, rotation));
    }

    [Fact]
    public void ClockwiseMeansTheTopLeftCornerGoesTopRight()
    {
        Assert.Equal((300.0, 0.0), RotatedView.ToView(0, 0, 200, 300, 90));
        Assert.Equal((0.0, 200.0), RotatedView.ToView(0, 0, 200, 300, 270));
        Assert.Equal(270, RotatedView.Normalize(-90));
        Assert.Equal(0, RotatedView.Normalize(360));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void RenderedImagesTurnLikeTheView(int rotation)
    {
        // 3 x 2 image; each pixel's blue byte is its index.
        var px = new byte[3 * 2 * 3];
        for (var i = 0; i < 6; i++) px[i * 3] = (byte)i;
        var img = new RenderedImage(3, 2, px);
        var turned = img.Rotated(rotation);
        var (w, h) = RotatedView.Size(3, 2, rotation);
        Assert.Equal((int)w, turned.Width);
        Assert.Equal((int)h, turned.Height);
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 3; x++)
            {
                // The pixel's center goes where the view puts that point.
                var (vx, vy) = RotatedView.ToView(x + 0.5, y + 0.5, 3, 2, rotation);
                Assert.Equal((byte)(y * 3 + x), turned.Pixels[((int)vy * turned.Width + (int)vx) * 3]);
            }
    }

    [Fact]
    public void ScrollingALongDocumentKeepsFewPagesLoaded()
    {
        using var engine = PdfEngine.Open(TestPdf.Make(90), InputFileTypes.PDF, null);
        Assert.InRange(engine.LoadedCounts().Render.Pages, 0, PdfEngine.LoadedPagesKept);
        var first = engine.Render(0, 0.25);
        for (var p = 0; p < engine.PageCount; p++)
        {
            engine.Render(p, 0.25);
            engine.GetText(p, cache: false);
            engine.GetLinks(p);
        }
        var (render, text) = engine.LoadedCounts();
        Assert.InRange(render.Pages, 1, PdfEngine.LoadedPagesKept);
        Assert.InRange(render.Lists, 0, PdfEngine.LoadedPagesKept);
        Assert.InRange(text.Pages, 1, PdfEngine.LoadedPagesKept);
        Assert.InRange(text.Lists, 0, PdfEngine.LoadedPagesKept);
        // A freed page loads again.
        Assert.Equal(first.Pixels, engine.Render(0, 0.25).Pixels);
        Assert.Contains("Page 1", engine.GetText(0, cache: false).Text);
    }

    [Fact]
    public void CancelledTextAndLinkRequestsDoNotRun()
    {
        using var engine = PdfEngine.Open(TestPdf.Make(2), InputFileTypes.PDF, null);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => engine.GetText(0, cancel: cts.Token));
        Assert.Throws<OperationCanceledException>(() => engine.GetLinks(0, cts.Token));
        Assert.Contains("Page 1", engine.GetText(0).Text);
    }
}
