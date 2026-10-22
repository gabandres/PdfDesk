using PdfDesk.Core;
using PdfSharp.Pdf;

namespace PdfDesk.Tests;

public class Core2FormTests
{
    private const string W = "/Type /Annot /Subtype /Widget /F 4 /P 3 0 R";

    private static byte[] TextForm(string extra = "", string rect = "72 700 300 720", string da = "/Helv 12 Tf 0 g") =>
        Core2Pdf.Form([$"<< {W} /FT /Tx /T (name) /Rect [{rect}] /DA ({da}) {extra} >>"]);

    [Fact]
    public void HostileFieldSettingsStayCheapAndWriteAValidStream()
    {
        // A comb field claiming a billion cells, a NaN font size and a broken format script.
        var data = TextForm("/Ff 16777216 /MaxLen 1000000000 /MK << /BC [0 0 0] >> /AA << /F << /S /JavaScript /JS (AFNumber_Format\\(1.2.3, 0\\);) >> >>",
            da: "/Helv NaN Tf 0 g");
        var filled = FormEditor.SetValues(data, null, new Dictionary<string, string> { ["name"] = "12345" });
        Assert.Contains("12345", Core2Pdf.AllText(Core2Pdf.WithoutNeedAppearances(filled)));

        // One unbroken multiline value much wider than the box: wrapped in linear time.
        var multi = TextForm("/Ff 4096", rect: "72 500 200 720");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        FormEditor.SetValues(multi, null, new Dictionary<string, string> { ["name"] = new string('W', 20_000) });
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), sw.Elapsed.ToString());
    }

    [Fact]
    public void FillingWritesAnAppearanceStreamThatViewersUseWithoutNeedAppearances()
    {
        var filled = FormEditor.SetValues(TextForm(), null, new Dictionary<string, string> { ["name"] = "Bob Builder" });
        using (var doc = Core2Pdf.Open(filled))
        {
            var field = Core2Pdf.Field(doc, "name")!;
            Assert.NotNull(field.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N"));
            Assert.True(doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")!.Elements.GetBoolean("/NeedAppearances"));
        }
        var strict = Core2Pdf.WithoutNeedAppearances(filled);
        Assert.Contains("Bob Builder", Core2Pdf.AllText(strict));
        Assert.True(Core2Pdf.Ink(strict, 0, new RectD(72, 72, 228, 20)) > 0.02);
    }

    [Fact]
    public void NonLatinTextIsDrawnWithAnEmbeddedUnicodeFont()
    {
        const string value = "Ωμέγα Привет 你好";
        var filled = Core2Pdf.WithoutNeedAppearances(FormEditor.SetValues(TextForm(), null, new Dictionary<string, string> { ["name"] = value }));
        var text = Core2Pdf.AllText(filled);
        Assert.Contains("Ωμέγα", text);
        Assert.Contains("Привет", text);
        Assert.Contains("你好", text);
        Assert.Equal(value, FormEditor.Read(filled, null).Single().Value);
    }

    [Fact]
    public void CombFieldsPutOneCharacterPerCell()
    {
        // 100 pt wide, MaxLen 5 → 20 pt cells starting at x = 72.
        var form = TextForm("/Ff 16777216 /MaxLen 5", "72 700 172 720", "/Helv 0 Tf 0 g");
        var filled = Core2Pdf.WithoutNeedAppearances(FormEditor.SetValues(form, null, new Dictionary<string, string> { ["name"] = "12345" }));
        var chars = Core2Pdf.Lines(filled).SelectMany(l => l.Chars).Where(c => char.IsDigit(c.Ch[0])).ToList();
        Assert.Equal(5, chars.Count);
        for (var i = 0; i < 5; i++) Assert.Equal(72 + 20 * i + 10, chars[i].Box.CenterX, 2);
    }

    [Fact]
    public void CenteredAndMultilineTextIsLaidOut()
    {
        var centered = Core2Pdf.WithoutNeedAppearances(FormEditor.SetValues(TextForm("/Q 1"), null, new Dictionary<string, string> { ["name"] = "Mid" }));
        var line = Core2Pdf.Lines(centered).Single(l => l.Text.Contains("Mid"));
        Assert.Equal(72 + 228 / 2.0, line.Box.CenterX, 1);

        var multi = TextForm("/Ff 4096", "72 600 200 720", "/Helv 0 Tf 0 g");
        var long_ = "The quick brown fox jumps over the lazy dog and keeps running far away";
        var wrapped = Core2Pdf.WithoutNeedAppearances(FormEditor.SetValues(multi, null, new Dictionary<string, string> { ["name"] = long_ }));
        var lines = Core2Pdf.Lines(wrapped).Where(l => l.Box.Y >= 72 && l.Box.Bottom <= 192).ToList();
        Assert.True(lines.Count >= 3, $"expected wrapped lines, got {lines.Count}");
        Assert.All(lines, l => Assert.True(l.Box.Right <= 200 + 1, $"line overflows: {l.Text}"));
    }

    [Fact]
    public void WidgetRotationTurnsTheText()
    {
        var form = TextForm("/MK << /R 90 /BC [0 0 1] /BG [1 1 0.8] >>", "72 500 92 700");
        var filled = Core2Pdf.WithoutNeedAppearances(FormEditor.SetValues(form, null, new Dictionary<string, string> { ["name"] = "VERTICAL" }));
        var line = Core2Pdf.Lines(filled).Single(l => l.Text.Contains("VERTICAL"));
        Assert.True(line.Box.Height > line.Box.Width * 2, $"text should run vertically: {line.Box}");
    }

    [Fact]
    public void FillingRemovesXfaAndRichValues()
    {
        var form = Core2Pdf.Form([$"<< {W} /FT /Tx /T (name) /Rect [72 700 300 720] /DA (/Helv 12 Tf 0 g) /RV (<body>old</body>) >>"], "/XFA 6 0 R",
            more: [Core2Pdf.Stream("<xdp:xdp/>")]);
        Assert.Equal((true, false), FormEditor.HasXfa(form, null));
        var filled = FormEditor.SetValues(form, null, new Dictionary<string, string> { ["name"] = "New" });
        Assert.Equal((false, false), FormEditor.HasXfa(filled, null));
        using var doc = Core2Pdf.Open(filled);
        Assert.False(Core2Pdf.Field(doc, "name")!.Elements.ContainsKey("/RV"));

        var xfaOnly = Core2Pdf.Build([
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] /XFA 4 0 R >> /NeedsRendering true >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            Core2Pdf.Stream("<xdp:xdp/>"),
        ]);
        Assert.Equal((true, true), FormEditor.HasXfa(xfaOnly, null));
        Assert.Equal((false, false), FormEditor.HasXfa(TestPdf.Make(1), null));
    }

    [Fact]
    public void ChoiceFieldsWithLabelsAndMultiSelect()
    {
        var form = Core2Pdf.Form([
            $"<< {W} /FT /Ch /Ff 2097152 /T (sizes) /Opt [[(s) (Small)] [(m) (Medium)] [(l) (Large)]] /Rect [72 600 200 660] /DA (/Helv 10 Tf 0 g) >>",
            $"<< {W} /FT /Ch /Ff 131072 /T (combo) /Opt [[(us) (United States)] [(pr) (Puerto Rico)]] /Rect [72 500 250 520] /DA (/Helv 10 Tf 0 g) >>",
        ]);
        var sizes = FormEditor.Read(form, null).Single(w => w.FieldName == "sizes");
        Assert.True(sizes.MultiSelect);
        Assert.Equal(["s", "m", "l"], sizes.Options);
        Assert.Equal(["Small", "Medium", "Large"], sizes.OptionLabels!);

        var filled = FormEditor.SetValues(form, null, new Dictionary<string, string> { ["sizes"] = "l\ns", ["combo"] = "Puerto Rico" });
        var read = FormEditor.Read(filled, null);
        Assert.Equal("s\nl", read.Single(w => w.FieldName == "sizes").Value); // stored in option order
        Assert.Equal("pr", read.Single(w => w.FieldName == "combo").Value);
        using (var doc = Core2Pdf.Open(filled))
        {
            var f = Core2Pdf.Field(doc, "sizes")!;
            Assert.Equal(2, f.Elements.GetArray("/V")!.Elements.Count);
            var idx = f.Elements.GetArray("/I")!;
            Assert.Equal([0, 2], idx.Elements.Select(e => ((PdfInteger)e).Value));
        }
        var text = Core2Pdf.AllText(Core2Pdf.WithoutNeedAppearances(filled));
        Assert.Contains("Puerto Rico", text); // combo shows the display text
        Assert.Contains("Small", text);       // list box shows its items
    }

    [Fact]
    public void CalculationsAndFormatsRunAfterFilling()
    {
        var form = Core2Pdf.Form([
            $"<< {W} /FT /Tx /T (a) /Rect [72 700 200 720] /DA (/Helv 10 Tf 0 g) >>",
            $"<< {W} /FT /Tx /T (b) /Rect [72 670 200 690] /DA (/Helv 10 Tf 0 g) >>",
            $"<< {W} /FT /Tx /Ff 1 /T (total) /Rect [72 640 200 660] /DA (/Helv 10 Tf 0 g) /AA << /C << /S /JavaScript /JS (AFSimple_Calculate\\(\"SUM\", new Array\\(\"a\", \"b\"\\)\\);) >> /F << /S /JavaScript /JS (AFNumber_Format\\(2, 0, 0, 0, \"$\", true\\);) >> >> >>",
            $"<< {W} /FT /Tx /Ff 1 /T (double) /Rect [72 610 200 630] /DA (/Helv 10 Tf 0 g) /AA << /C << /S /JavaScript /JS 9 0 R >> >> >>",
        ], "/CO [7 0 R 8 0 R]", more: [
            Core2Pdf.Stream("var t = this.getField(\"total\").value;\nevent.value = t * 2 - AFMakeNumber(getField(\"a\").value);"),
        ]);
        var filled = FormEditor.SetValues(form, null, new Dictionary<string, string> { ["a"] = "1200", ["b"] = "34.5" });
        var widgets = FormEditor.Read(filled, null);
        var total = widgets.Single(w => w.FieldName == "total");
        Assert.Equal("1234.5", total.Value);
        Assert.Equal("$1,234.50", total.DisplayValue);
        Assert.True(total.Calculated);
        Assert.Equal("1269", widgets.Single(w => w.FieldName == "double").Value);
        Assert.Contains("$1,234.50", Core2Pdf.AllText(Core2Pdf.WithoutNeedAppearances(filled)));
    }

    [Fact]
    public void WidgetFlagsAndRadioRules()
    {
        var form = Core2Pdf.Form([
            $"<< /Type /Annot /Subtype /Widget /F 36 /P 3 0 R /FT /Tx /T (noview) /Rect [72 700 200 720] >>",
            $"<< /Type /Annot /Subtype /Widget /F 68 /P 3 0 R /FT /Tx /T (locked) /Rect [72 670 200 690] >>",
            "<< /FT /Btn /Ff 49152 /T (pick) /Kids [8 0 R 9 0 R] /V /A >>",
        ], more: [
            $"<< {W} /Parent 7 0 R /Rect [72 600 90 618] /AS /A /AP << /N << /A 10 0 R /Off 10 0 R >> >> >>",
            $"<< {W} /Parent 7 0 R /Rect [120 600 138 618] /AS /Off /AP << /N << /B 10 0 R /Off 10 0 R >> >> >>",
            Core2Pdf.Stream("", "/Type /XObject /Subtype /Form /BBox [0 0 18 18]"),
        ], extraAnnots: "8 0 R 9 0 R");
        var widgets = FormEditor.Read(form, null);
        Assert.DoesNotContain(widgets, w => w.FieldName == "noview");
        Assert.True(widgets.Single(w => w.FieldName == "locked").ReadOnly);
        Assert.All(widgets.Where(w => w.FieldName == "pick"), w => Assert.True(w.NoToggleToOff));

        // NoToggleToOff: "Off" is ignored, the group keeps its choice.
        var off = FormEditor.SetValues(form, null, new Dictionary<string, string> { ["pick"] = "Off" });
        Assert.Equal("A", FormEditor.Read(off, null).First(w => w.FieldName == "pick").Value);
        var b = FormEditor.SetValues(form, null, new Dictionary<string, string> { ["pick"] = "B" });
        Assert.Equal("B", FormEditor.Read(b, null).First(w => w.FieldName == "pick").Value);
    }

    [Fact]
    public void StructureTabOrderBeatsGeometry()
    {
        // Geometrically "top" comes first, but the structure tree lists "bottom" first and the page asks for /Tabs /S.
        var form = Core2Pdf.Form([
            $"<< {W} /FT /Tx /T (top) /Rect [72 700 200 720] >>",
            $"<< {W} /FT /Tx /T (bottom) /Rect [72 600 200 620] >>",
        ], more: [
            "<< /Type /StructTreeRoot /K [8 0 R] >>",
            "<< /S /Form /P 7 0 R /K [<< /Type /OBJR /Obj 6 0 R >> << /Type /OBJR /Obj 5 0 R >>] >>",
        ], pageExtra: "/Tabs /S", catalogExtra: "/StructTreeRoot 7 0 R");
        var widgets = FormEditor.Read(form, null);
        Assert.Equal(["bottom", "top"], widgets.Select(w => w.FieldName));
        Assert.Equal([0, 1], widgets.Select(w => w.TabOrder));

        var geometric = Core2Pdf.Form([$"<< {W} /FT /Tx /T (top) /Rect [72 700 200 720] >>", $"<< {W} /FT /Tx /T (bottom) /Rect [72 600 200 620] >>"]);
        Assert.Equal(["top", "bottom"], FormEditor.Read(geometric, null).Select(w => w.FieldName));
    }

    [Fact]
    public void ToleratesFontsWithDictionaryEncodingsAndOddEntries()
    {
        // Real forms: /DR fonts with an /Encoding dictionary (Differences), an indirect /BaseFont, a widget /Subtype stored oddly.
        var form = Core2Pdf.Build([
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 4 0 R /TiRo 6 0 R >> /Encoding << /PDFDocEncoding 7 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [5 0 R] /Resources << >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /Type /Encoding /Differences [32 /space] >> >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /Rect [72 700 300 720] /DA (/TiRo 11 Tf 0 0 1 rg) /F 4 /P 3 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont 8 0 R /Encoding 7 0 R >>",
            "<< /Type /Encoding /Differences [24 /breve /caron] >>",
            "/Times-Roman",
        ]);
        var filled = FormEditor.SetValues(form, null, new Dictionary<string, string> { ["name"] = "Tolerant" });
        Assert.Contains("Tolerant", Core2Pdf.AllText(Core2Pdf.WithoutNeedAppearances(filled)));
    }

    [Fact]
    public void ExistingFormStillFillsAndRendersThroughMuPdf()
    {
        // The original fixture: checks and radios unchanged, text gets an appearance.
        var filled = FormEditor.SetValues(FormTests.MakeForm(), null, new Dictionary<string, string> { ["name"] = "Zoë", ["size"] = "L" });
        var strict = Core2Pdf.WithoutNeedAppearances(filled);
        var text = Core2Pdf.AllText(strict);
        Assert.Contains("Zoë", text);
        Assert.Contains("L", text);
    }
}
