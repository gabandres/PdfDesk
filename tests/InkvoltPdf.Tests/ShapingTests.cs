using InkvoltPdf.Core;

namespace InkvoltPdf.Tests;

/// <summary>Complex-script shaping (HarfBuzz), bidi and the embedded CID fonts of Fill &amp; Sign and form appearances.</summary>
public class ShapingTests
{
    private static TrueTypeFont Arial => SystemFonts.Get("arial.ttf") ?? throw new InvalidOperationException("Arial is missing");

    [Fact]
    public void ArabicIsJoined()
    {
        var joined = TextShaper.Shape("مرحبا", Arial).Glyphs.Select(g => g.Glyph).ToList();
        var isolated = "مرحبا".Select(c => TextShaper.Shape(c.ToString(), Arial).Glyphs.Single().Glyph).ToList();
        Assert.Equal(5, joined.Count);
        // Written in logical order: the same letters, but in their initial/medial/final forms.
        Assert.NotEqual(isolated, joined);
        Assert.True(joined.Zip(isolated).Count(p => p.First != p.Second) >= 3, "most letters take a joined form");
    }

    [Fact]
    public void HebrewRunsRightToLeft()
    {
        var line = TextShaper.Shape("שלום", Arial);
        Assert.True(line.Rtl);
        var xs = line.Glyphs.Select(g => g.X).ToList(); // logical order
        for (var i = 1; i < xs.Count; i++) Assert.True(xs[i] < xs[i - 1], "each next letter sits left of the previous one");
        Assert.Equal("שלום", string.Concat(line.Glyphs.Select(g => g.Text)));
    }

    [Fact]
    public void NumbersInRightToLeftTextStayLeftToRight()
    {
        var line = TextShaper.Shape("مرحبا 123 بكم", Arial);
        double X(string t) => line.Glyphs.Single(g => g.Text == t).X;
        Assert.True(X("1") < X("2") && X("2") < X("3"), "digits read left to right");
        Assert.True(line.Glyphs[0].X > X("3"), "the first word is rightmost");
        Assert.True(line.Glyphs[^1].X < X("1"), "the last word is leftmost");
    }

    [Fact]
    public void LatinRunInsideRightToLeftParagraph()
    {
        var (levels, para) = Bidi.Levels("שלום abc!");
        Assert.Equal(1, para);
        Assert.Equal([1, 1, 1, 1, 1, 2, 2, 2, 1], levels.Select(l => (int)l));
        Assert.Equal(0, Bidi.Levels("abc שלום def").Paragraph);
        Assert.Equal([0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0, 0], Bidi.Levels("abc שלום def").Levels.Select(l => (int)l));
    }

    [Fact]
    public void DevanagariFormsConjuncts()
    {
        var nirmala = SystemFonts.For('क', null)!;
        var line = TextShaper.Shape("क्ष", nirmala);
        Assert.True(line.Glyphs.Count < 3, "ka + virama + ssa become one conjunct glyph");
        Assert.Equal("क्ष", string.Concat(line.Glyphs.Select(g => g.Text)));
        // The i-matra is drawn before (left of) the consonant it follows in the text; extraction still reads "कि".
        var ki = TextShaper.Shape("कि", nirmala).Glyphs;
        Assert.Equal(2, ki.Count);
        Assert.True(ki[0].X < ki[1].X);
        Assert.Equal("कि", string.Concat(ki.Select(g => g.Text)));
    }

    [Theory]
    [InlineData("مرحبا")]
    [InlineData("שלום עולם")]
    [InlineData("क्षत्रिय किताब")]
    [InlineData("Ελληνικά and Русский")]
    public void StampTextExtractsAsTyped(string text)
    {
        var data = TextStamps.Put(TestPdf.Make(1), null, 0, new RectD(100, 300, 300, 30), StampKind.Text, text, 14);
        Assert.Contains(text, Core2Pdf.AllText(data));
        // The font is a subset: far smaller than the system fonts it came from.
        Assert.True(data.Length < 120_000, $"{data.Length} bytes");
    }

    [Fact]
    public void FormFieldAppearanceIsShaped()
    {
        var form = Core2Pdf.Form(["<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /Rect [72 600 372 630] /DA (/Helv 12 Tf 0 g) /P 3 0 R >>"]);
        var filled = Core2Pdf.WithoutNeedAppearances(FormEditor.SetValues(form, null, new Dictionary<string, string> { ["name"] = "مرحبا שלום" }));
        var text = Core2Pdf.AllText(filled);
        Assert.Contains("مرحبا", text);
        Assert.Contains("שלום", text);
        Assert.True(Core2Pdf.Ink(filled, 0, new RectD(72, 162, 300, 30)) > 0.01, "the value is drawn");
    }
}
