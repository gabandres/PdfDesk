using System.Text;
using MuPDFCore;
using InkvoltPdf.Core;

namespace InkvoltPdf.Tests;

/// <summary>Ink, shapes, lines, polygons and FreeText: created, read back, drawn by MuPDF, edited, deleted.</summary>
public class DrawingAnnotationTests
{
    private static readonly AnnotColor Blue = new(0.1, 0.2, 0.9);

    /// <summary>
    /// One empty page whose MediaBox doesn't start at 0,0 and whose CropBox trims it, optionally rotated (page space:
    /// 560 x 720, or 720 x 560 when turned a quarter).
    /// </summary>
    private static byte[] OddPage(int rotate) => Core2Pdf.Build(
    [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        $"<< /Type /Page /Parent 2 0 R /MediaBox [100 200 712 992] /CropBox [120 230 680 950] /Rotate {rotate} /Contents 4 0 R /Resources << >> >>",
        Core2Pdf.Stream(""),
    ]);

    /// <summary>Fraction of pixels in a page-space region (rendered at 2x) that aren't near-white.</summary>
    private static double Painted(byte[] data, RectD region, int page = 0)
    {
        using var engine = PdfEngine.Open(data, InputFileTypes.PDF, null);
        var img = engine.Render(page, 2, region);
        var n = 0;
        for (var i = 0; i + 2 < img.Pixels.Length; i += 3)
            if (img.Pixels[i] < 200 || img.Pixels[i + 1] < 200 || img.Pixels[i + 2] < 200) n++;
        return n / (double)(img.Width * img.Height);
    }

    private static AnnotationInfo Only(byte[] data) => Assert.Single(Annotations.Read(data, null));

    private static void Near(PointD expected, PointD actual)
    {
        Assert.Equal(expected.X, actual.X, 0.05);
        Assert.Equal(expected.Y, actual.Y, 0.05);
    }

    private static void Near(RectD expected, RectD actual)
    {
        Assert.Equal(expected.X, actual.X, 0.05);
        Assert.Equal(expected.Y, actual.Y, 0.05);
        Assert.Equal(expected.Width, actual.Width, 0.05);
        Assert.Equal(expected.Height, actual.Height, 0.05);
    }

    [Fact]
    public void InkRoundTripsAndIsDrawn()
    {
        IReadOnlyList<PointD>[] strokes = [[new(100, 300), new(150, 320), new(200, 300), new(250, 340)], [new(120, 360), new(220, 360)]];
        var id = Annotations.NewId();
        var data = Annotations.AddInk(TestPdf.Make(1), null, 0, strokes, AnnotStyle.Pen(Blue, 3) with { Opacity = 0.8 }, "Ana", id);

        var a = Only(data);
        Assert.Equal(id, a.Id);
        Assert.Equal(AnnotationKind.Ink, a.Kind);
        Assert.Equal("Ana", a.Author);
        Assert.NotNull(a.Geometry);
        Assert.Equal(2, a.Geometry.Paths.Count);
        for (var s = 0; s < strokes.Length; s++)
            for (var i = 0; i < strokes[s].Count; i++) Near(strokes[s][i], a.Geometry.Paths[s][i]);
        Near(new RectD(100, 300, 150, 60), a.Geometry.Box);
        var st = a.Style!;
        Assert.Equal(Blue, st.Color);
        Assert.Equal(3, st.Width, 0.01);
        Assert.Equal(0.8, st.Opacity, 0.01);
        Assert.Equal(0.8, a.Opacity, 0.01);
        Assert.False(st.Highlighter);
        Assert.True(a.Rect.Contains(100, 300) && a.Rect.Contains(250, 340));

        Assert.True(Painted(data, new RectD(115, 355, 90, 10)) > 0.2, "the second stroke is drawn");
        Assert.True(Painted(data, new RectD(300, 400, 50, 50)) == 0, "nothing elsewhere");
        var raw = Encoding.Latin1.GetString(data);
        Assert.Contains("/InkList", raw);
    }

    [Fact]
    public void HighlighterMultipliesOverText()
    {
        var page = TestPdf.Make(1);
        // "The quick brown fox" baseline is at y = 140 (page space), 20 pt Arial.
        var data = Annotations.AddInk(page, null, 0, [[new(72, 133), new(260, 133)]], AnnotStyle.Marker(), "Ana");
        var a = Only(data);
        Assert.True(a.Style!.Highlighter);
        Assert.Equal(14, a.Style.Width, 0.01);
        Assert.Contains("/Multiply", Encoding.Latin1.GetString(data));

        using var engine = PdfEngine.Open(data, InputFileTypes.PDF, null);
        var img = engine.Render(0, 1, new RectD(72, 127, 180, 12));
        int yellow = 0, dark = 0;
        for (var i = 0; i + 2 < img.Pixels.Length; i += 3)
        {
            if (img.Pixels[i] < 120 && img.Pixels[i + 1] > 150 && img.Pixels[i + 2] > 200) yellow++;
            if (img.Pixels[i] + img.Pixels[i + 1] + img.Pixels[i + 2] < 150) dark++;
        }
        Assert.True(yellow > img.Width * img.Height / 4, "tinted yellow");
        Assert.True(dark > 20, "the text under it stays dark");
    }

    [Theory]
    [InlineData(ShapeKind.Rectangle, AnnotationKind.Square)]
    [InlineData(ShapeKind.Ellipse, AnnotationKind.Circle)]
    public void ShapesRoundTripAndAreDrawn(ShapeKind shape, AnnotationKind kind)
    {
        var box = new RectD(200, 400, 150, 100);
        var style = new AnnotStyle { Color = AnnotColor.Red, Fill = Blue, Width = 4, Dash = [6, 3], Opacity = 0.5 };
        var data = Annotations.AddShape(TestPdf.Make(1), null, 0, shape, box, style, "Ana");

        var a = Only(data);
        Assert.Equal(kind, a.Kind);
        Near(box, a.Geometry!.Box);
        Near(box, a.Rect);
        Assert.Equal(AnnotColor.Red, a.Style!.Color);
        Assert.Equal(Blue, a.Style.Fill);
        Assert.Equal(4, a.Style.Width, 0.01);
        Assert.Equal([6.0, 3.0], a.Style.Dash!);
        Assert.Equal(0.5, a.Style.Opacity, 0.01);

        // Filled (bluish) in the middle.
        using (var engine = PdfEngine.Open(data, InputFileTypes.PDF, null))
        {
            var img = engine.Render(0, 1, new RectD(box.CenterX - 5, box.CenterY - 5, 10, 10));
            Assert.True(img.Pixels[0] > img.Pixels[2] + 40, "blue fill at half opacity");
        }
        Assert.Equal(0, Painted(data, new RectD(box.Right + 3, box.Y, 20, 20)));

        // No fill: the middle stays white, the border is drawn.
        var outline = Annotations.AddShape(TestPdf.Make(1), null, 0, shape, box, AnnotStyle.Pen(AnnotColor.Red, 2), "Ana");
        Assert.Null(Only(outline).Style!.Fill);
        Assert.Equal(0, Painted(outline, new RectD(box.CenterX - 5, box.CenterY - 5, 10, 10)));
        Assert.True(Painted(outline, new RectD(box.X, box.CenterY - 3, 3, 6)) > 0.3, "left edge stroked");
    }

    [Fact]
    public void LinesAndArrowsRoundTrip()
    {
        var style = AnnotStyle.Arrow(AnnotColor.Red, 2) with { StartEnding = LineEnding.Circle };
        var data = Annotations.AddLine(TestPdf.Make(1), null, 0, new PointD(100, 500), new PointD(300, 600), style, "Ana");

        var a = Only(data);
        Assert.Equal(AnnotationKind.Line, a.Kind);
        var path = Assert.Single(a.Geometry!.Paths);
        Near(new PointD(100, 500), path[0]);
        Near(new PointD(300, 600), path[1]);
        Assert.Equal(LineEnding.Circle, a.Style!.StartEnding);
        Assert.Equal(LineEnding.OpenArrow, a.Style.EndEnding);
        Assert.Contains("/OpenArrow", Encoding.Latin1.GetString(data));
        Assert.True(a.Rect.Contains(300, 600) && a.Rect.Contains(95, 495), "the rect holds the endings");

        Assert.True(Painted(data, new RectD(195, 545, 10, 10)) > 0.1, "the line's middle");
        Assert.True(Painted(data, new RectD(288, 585, 8, 8)) > 0.05, "the arrow head");

        foreach (var ending in Enum.GetValues<LineEnding>())
        {
            var d = Annotations.AddLine(TestPdf.Make(1), null, 0, new PointD(100, 500), new PointD(300, 500), AnnotStyle.Pen(Blue) with { EndEnding = ending, Fill = AnnotColor.Yellow }, "Ana");
            Assert.Equal(ending, Only(d).Style!.EndEnding);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PolygonsAndPolylinesRoundTrip(bool closed)
    {
        PointD[] v = [new(100, 100), new(300, 120), new(250, 300), new(120, 250)];
        var data = Annotations.AddPolygon(OddPage(0), null, 0, v, closed, new AnnotStyle { Color = Blue, Width = 3, Fill = closed ? AnnotColor.Yellow : null }, "Ana");
        var a = Only(data);
        Assert.Equal(closed ? AnnotationKind.Polygon : AnnotationKind.PolyLine, a.Kind);
        var path = Assert.Single(a.Geometry!.Paths);
        Assert.Equal(4, path.Count);
        for (var i = 0; i < 4; i++) Near(v[i], path[i]);
        Assert.True(Painted(data, new RectD(195, 105, 10, 10)) > 0.1, "first edge drawn");
        // The closing edge only on a polygon.
        Assert.Equal(closed, Painted(data, new RectD(106, 170, 6, 10)) > 0.05);
    }

    [Fact]
    public void FreeTextRoundTripsWrapsAndIsExtracted()
    {
        var box = new RectD(100, 450, 160, 80);
        var text = "Please review this paragraph again before Friday";
        var data = Annotations.AddFreeText(TestPdf.Make(1), null, 0, box, text, AnnotStyle.TextBox(12, Blue, new AnnotColor(1, 1, 0.8)), "Ana");

        var a = Only(data);
        Assert.Equal(AnnotationKind.FreeText, a.Kind);
        Assert.False(a.IsInkvoltPdfStamp);
        Assert.Equal(text, a.Contents);
        Near(box, a.Geometry!.Box);
        Assert.Empty(a.Geometry.Callout);
        Assert.Equal(Blue, a.Style!.Color);
        Assert.Equal(12, a.Style.FontSize, 0.01);
        Assert.Equal(AnnotStyle.Black, a.Style.TextColor);
        Assert.Equal(new AnnotColor(1, 1, 0.8), a.Style.Fill);
        var raw = Encoding.Latin1.GetString(data);
        Assert.Contains("/DA", raw);
        Assert.Contains("/DS", raw);
        Assert.Contains("/RC", raw);

        // Wrapped onto several lines, all inside the box.
        var lines = Core2Pdf.Lines(data).Where(l => l.Box.Y > 400).ToList();
        Assert.True(lines.Count >= 3, string.Join(" | ", lines.Select(l => l.Text)));
        Assert.Contains("Friday", string.Join(" ", lines.Select(l => l.Text)));
        Assert.All(lines, l => Assert.True(l.Box.X >= box.X && l.Box.Right <= box.Right + 1 && l.Box.Bottom <= box.Bottom + 1, $"{l.Text} {l.Box}"));

        // Editing the text redraws it.
        var edited = Annotations.SetContents(data, null, a.Id, "Done");
        Assert.Contains("Done", Core2Pdf.AllText(edited));
        Assert.DoesNotContain("Friday", Core2Pdf.AllText(edited));
        Assert.Equal(a.Id, Only(edited).Id);

        // Typewriter: no border, no fill.
        var tw = Annotations.AddFreeText(TestPdf.Make(1), null, 0, box, "Typed", AnnotStyle.Typewriter(14, AnnotColor.Red), "Ana");
        var t = Only(tw);
        Assert.Null(t.Style!.Color);
        Assert.Equal(AnnotColor.Red, t.Style.TextColor);
        Assert.Contains("/FreeTextTypewriter", Encoding.Latin1.GetString(tw));
        Assert.Contains("Typed", Core2Pdf.AllText(tw));
        Assert.True(Annotations.FreeTextHeight("Typed", 160, AnnotStyle.Typewriter(14)) is > 14 and < 30);
    }

    [Fact]
    public void FreeTextInOtherScriptsEmbedsAFont()
    {
        var data = Annotations.AddFreeText(TestPdf.Make(1), null, 0, new RectD(100, 450, 250, 40), "Привет, мир", AnnotStyle.Typewriter(), "Ana");
        Assert.Contains("Привет, мир", Core2Pdf.AllText(data));
        Assert.Equal("Привет, мир", Only(data).Contents);
    }

    [Fact]
    public void CalloutRoundTripsAndDrawsItsLeader()
    {
        var box = new RectD(300, 300, 150, 50);
        PointD tip = new(150, 450), knee = new(250, 325);
        var data = Annotations.AddFreeText(TestPdf.Make(1), null, 0, box, "Look here", AnnotStyle.TextBox(), "Ana", [tip, knee]);

        var a = Only(data);
        Near(box, a.Geometry!.Box);
        Assert.Equal(2, a.Geometry.Callout.Count);
        Near(tip, a.Geometry.Callout[0]);
        Near(knee, a.Geometry.Callout[1]);
        Assert.Equal(LineEnding.OpenArrow, a.Style!.EndEnding);
        Assert.True(a.Rect.Contains(tip.X, tip.Y));
        var raw = Encoding.Latin1.GetString(data);
        Assert.Contains("/FreeTextCallout", raw);
        Assert.Contains("/CL", raw);
        Assert.Contains("/RD", raw);
        Assert.Contains("Look here", Core2Pdf.AllText(data));
        Assert.True(Painted(data, new RectD(198, 385, 6, 6)) > 0.05, "the leader line, tip to knee");
        Assert.True(Painted(data, new RectD(270, 322, 20, 6)) > 0.05, "the leader line, knee to box");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void PlacedCorrectlyOnRotatedAndCroppedPages(int rotation)
    {
        var data = OddPage(rotation);
        using (var e = PdfEngine.Open(data, InputFileTypes.PDF, null))
        {
            var g = e.GetPage(0);
            Assert.Equal(rotation is 90 or 270 ? 720 : 560, g.Width, 0.5);
        }
        var shapeBox = new RectD(60, 80, 120, 70);
        var textBox = new RectD(250, 300, 220, 30);
        data = Annotations.AddShape(data, null, 0, ShapeKind.Rectangle, shapeBox, new AnnotStyle { Color = AnnotColor.Red, Fill = AnnotColor.Red, Width = 1 }, "Ana");
        data = Annotations.AddInk(data, null, 0, [[new(60, 200), new(160, 230), new(260, 200)]], AnnotStyle.Pen(Blue, 3), "Ana");
        data = Annotations.AddLine(data, null, 0, new PointD(60, 260), new PointD(200, 260), AnnotStyle.Arrow(Blue, 2), "Ana");
        data = Annotations.AddFreeText(data, null, 0, textBox, "UPRIGHT CALLOUT", AnnotStyle.TextBox(14), "Ana", [new PointD(220, 400)]);

        var list = Annotations.Read(data, null);
        Near(shapeBox, list.Single(a => a.Kind == AnnotationKind.Square).Geometry!.Box);
        Near(shapeBox, list.Single(a => a.Kind == AnnotationKind.Square).Rect);
        Near(new PointD(160, 230), list.Single(a => a.Kind == AnnotationKind.Ink).Geometry!.Paths[0][1]);
        Near(new PointD(200, 260), list.Single(a => a.Kind == AnnotationKind.Line).Geometry!.Paths[0][1]);
        var ft = list.Single(a => a.Kind == AnnotationKind.FreeText);
        Near(textBox, ft.Geometry!.Box);
        Near(new PointD(220, 400), Assert.Single(ft.Geometry.Callout));

        Assert.True(Painted(data, new RectD(shapeBox.X + 5, shapeBox.Y + 5, 20, 20)) > 0.9, "the filled rectangle where it was put");
        Assert.Equal(0, Painted(data, new RectD(shapeBox.Right + 5, shapeBox.Y, 20, 20)));
        Assert.True(Painted(data, new RectD(150, 222, 20, 14)) > 0.05, "the ink stroke");
        Assert.True(Painted(data, new RectD(120, 255, 20, 10)) > 0.1, "the line");
        Assert.True(Painted(data, new RectD(217, 397, 6, 6)) > 0.05, "the callout tip");

        // The text reads left to right in the viewer, inside its box.
        using var engine = PdfEngine.Open(data, InputFileTypes.PDF, null);
        var origin = engine.GetPage(0);
        var line = Core2Pdf.Lines(data).Single(l => l.Text.Contains("UPRIGHT"));
        var box = line.Box with { X = line.Box.X - origin.OriginX, Y = line.Box.Y - origin.OriginY };
        Assert.True(box.Width > box.Height * 3, $"rotation {rotation}: text runs sideways ({box})");
        Assert.InRange(box.X, textBox.X, textBox.X + 10);
        Assert.InRange(box.Y, textBox.Y, textBox.Bottom);
    }

    [Fact]
    public void GeometryAndStyleEditsKeepTheIdAndRedraw()
    {
        var page = TestPdf.Make(2);
        var id = Annotations.NewId();
        var data = Annotations.AddShape(page, null, 1, ShapeKind.Ellipse, new RectD(100, 300, 100, 100), new AnnotStyle { Color = AnnotColor.Red, Fill = AnnotColor.Red }, "Ana", id);
        Assert.True(Painted(data, new RectD(140, 340, 20, 20), 1) > 0.9);

        var moved = Annotations.UpdateGeometry(data, null, id, new AnnotGeometry { Box = new RectD(300, 500, 80, 60) });
        var a = Only(moved);
        Assert.Equal(id, a.Id);
        Assert.Equal(1, a.PageIndex);
        Near(new RectD(300, 500, 80, 60), a.Geometry!.Box);
        Assert.Equal(0, Painted(moved, new RectD(140, 340, 20, 20), 1));
        Assert.True(Painted(moved, new RectD(330, 520, 20, 20), 1) > 0.9);

        var restyled = Annotations.UpdateStyle(moved, null, id, a.Style! with { Fill = null, Color = Blue, Width = 5 });
        var r = Only(restyled);
        Assert.Equal(id, r.Id);
        Assert.Equal(Blue, r.Style!.Color);
        Assert.Null(r.Style.Fill);
        Assert.Equal(0, Painted(restyled, new RectD(330, 520, 20, 20), 1));

        // Ink scales with its bounds.
        var inkId = Annotations.NewId();
        var ink = Annotations.AddInk(page, null, 0, [[new(100, 100), new(200, 200)]], AnnotStyle.Pen(Blue), "Ana", inkId);
        var scaled = Annotations.SetBounds(ink, null, inkId, new RectD(300, 400, 50, 200));
        var path = Only(scaled).Geometry!.Paths[0];
        Near(new PointD(300, 400), path[0]);
        Near(new PointD(350, 600), path[1]);
        Assert.Equal(inkId, Only(scaled).Id);

        // A callout's tip moves on its own.
        var ftId = Annotations.NewId();
        var ft = Annotations.AddFreeText(page, null, 0, new RectD(300, 300, 100, 40), "Hi", AnnotStyle.TextBox(), "Ana", [new PointD(200, 200)], ftId);
        var g = Only(ft).Geometry!;
        var retargeted = Annotations.UpdateGeometry(ft, null, ftId, g with { Callout = [new PointD(450, 450)] });
        Near(new PointD(450, 450), Assert.Single(Only(retargeted).Geometry!.Callout));
        Assert.Equal("Hi", Only(retargeted).Contents);

        Assert.Throws<ArgumentException>(() => Annotations.UpdateGeometry(ft, null, ftId, new AnnotGeometry()));
        Assert.Throws<InvalidOperationException>(() => Annotations.UpdateStyle(ft, null, "nope", AnnotStyle.Typewriter()));
    }

    [Fact]
    public void StampsAndTextMarkupAreNotReshaped()
    {
        var stamp = TextStamps.Put(TestPdf.Make(1), null, 0, new RectD(100, 100, 100, 20), StampKind.Text, "Hi", 12);
        var s = Only(stamp);
        Assert.Null(s.Geometry);
        Assert.Throws<InvalidOperationException>(() => Annotations.SetBounds(stamp, null, s.Id, new RectD(0, 0, 50, 50)));
    }

    [Fact]
    public void DeleteRemovesEachKind()
    {
        var data = TestPdf.Make(1);
        data = Annotations.AddInk(data, null, 0, [[new(100, 100), new(150, 150)]], AnnotStyle.Pen(Blue), "Ana");
        data = Annotations.AddShape(data, null, 0, ShapeKind.Rectangle, new RectD(100, 200, 50, 50), AnnotStyle.Pen(Blue), "Ana");
        data = Annotations.AddShape(data, null, 0, ShapeKind.Ellipse, new RectD(200, 200, 50, 50), AnnotStyle.Pen(Blue), "Ana");
        data = Annotations.AddLine(data, null, 0, new(100, 300), new(200, 300), AnnotStyle.Arrow(Blue), "Ana");
        data = Annotations.AddPolygon(data, null, 0, [new(300, 300), new(350, 300), new(320, 350)], true, AnnotStyle.Pen(Blue), "Ana");
        data = Annotations.AddFreeText(data, null, 0, new RectD(100, 400, 100, 30), "Bye", AnnotStyle.Typewriter(), "Ana");
        var all = Annotations.Read(data, null);
        Assert.Equal(6, all.Count);
        Assert.All(all, a => Assert.NotNull(a.Geometry));
        foreach (var a in all) data = Annotations.Delete(data, null, a.Id);
        Assert.Empty(Annotations.Read(data, null));
        Assert.Equal(0, Painted(data, new RectD(90, 180, 300, 260)));
    }

    [Fact]
    public void RejectsUnusableInput()
    {
        var page = TestPdf.Make(1);
        Assert.Throws<ArgumentException>(() => Annotations.AddInk(page, null, 0, [], AnnotStyle.Pen(Blue), "Ana"));
        Assert.Throws<ArgumentException>(() => Annotations.AddInk(page, null, 0, [[new(double.NaN, 1)]], AnnotStyle.Pen(Blue), "Ana"));
        Assert.Throws<ArgumentException>(() => Annotations.AddShape(page, null, 0, ShapeKind.Rectangle, default, AnnotStyle.Pen(Blue), "Ana"));
        Assert.Throws<ArgumentException>(() => Annotations.AddLine(page, null, 0, new(1, 1), new(2, 2), new AnnotStyle { Color = null }, "Ana"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Annotations.AddShape(page, null, 3, ShapeKind.Rectangle, new RectD(1, 1, 5, 5), AnnotStyle.Pen(Blue), "Ana"));
        // A single tap draws a dot.
        var dot = Annotations.AddInk(page, null, 0, [[new(400, 400)]], AnnotStyle.Pen(Blue, 6), "Ana");
        Assert.True(Painted(dot, new RectD(398, 398, 4, 4)) > 0.5);
    }

    [Fact]
    public void SignedFileKeepsItsSignatureThroughDrawingEdits()
    {
        var pdf = IncrSignatureTests.SignedForm();
        var original = Signatures.Verify(pdf);
        var inkId = Annotations.NewId();
        var data = Annotations.AddInk(pdf, null, 0, [[new(100, 400), new(200, 450)]], AnnotStyle.Marker(), "Ana", inkId);
        AssertPermitted(pdf, data);
        var ftId = Annotations.NewId();
        data = Annotations.AddFreeText(data, null, 0, new RectD(300, 400, 150, 40), "Approved ✓ Ωmega", AnnotStyle.TextBox(), "Ana", [new PointD(250, 350)], ftId);
        AssertPermitted(pdf, data);
        data = Annotations.AddShape(data, null, 0, ShapeKind.Rectangle, new RectD(100, 600, 50, 50), AnnotStyle.Pen(Blue), "Ana");
        data = Annotations.AddLine(data, null, 0, new(100, 700), new(200, 700), AnnotStyle.Arrow(Blue), "Ana");
        data = Annotations.UpdateGeometry(data, null, inkId, new AnnotGeometry { Paths = [[new(120, 420), new(220, 470)]] });
        data = Annotations.UpdateStyle(data, null, ftId, AnnotStyle.TextBox(16, Blue));
        data = Annotations.SetBounds(data, null, ftId, new RectD(150, 150, 200, 200));
        data = Annotations.SetContents(data, null, ftId, "Changed");
        AssertPermitted(pdf, data);
        Assert.Contains("Changed", Core2Pdf.AllText(data));
        data = Annotations.Delete(data, null, inkId);
        AssertPermitted(pdf, data);
        Assert.Empty(Signatures.BrokenByEdits(original, data));

        static void AssertPermitted(byte[] signed, byte[] edited)
        {
            Assert.True(edited.AsSpan(0, signed.Length).SequenceEqual(signed), "appended as an update");
            var s = Signatures.Verify(edited).Single(x => x.FieldName == "Approval");
            Assert.Equal(SignatureStatus.ModifiedAfterSigning, s.Status);
        }
    }
}
