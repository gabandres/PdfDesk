using InkvoltPdf.Core;

namespace InkvoltPdf.Tests;

/// <summary>Mixed-direction text (Fill &amp; Sign, form fields) extracts as one line in logical order.</summary>
public class BidiLineTests
{
    public static TheoryData<string> Mixed => new()
    {
        "Hello مرحبا world",
        "abc שלום def",
        "שלום abc!",
        "مرحبا 123 بكم",
        "Pay שלום עולם to Ada (now)",
    };

    [Theory]
    [MemberData(nameof(Mixed))]
    public void StampIsOneLogicalLine(string text)
    {
        var data = TextStamps.Put(TestPdf.Make(1), null, 0, new RectD(100, 300, 400, 30), StampKind.Text, text, 14);
        var line = Assert.Single(Core2Pdf.Lines(data), l => l.Box.Y > 290 && l.Box.Y < 330);
        Assert.Equal(text, line.Text);
        Assert.DoesNotContain(line.Text, c => c is '‎' or '‏');
    }

    [Theory]
    [MemberData(nameof(Mixed))]
    public void FormValueIsOneLogicalLine(string text)
    {
        var form = Core2Pdf.Form(["<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /Rect [72 600 472 630] /DA (/Helv 12 Tf 0 g) /P 3 0 R >>"]);
        var filled = Core2Pdf.WithoutNeedAppearances(FormEditor.SetValues(form, null, new Dictionary<string, string> { ["name"] = text }));
        Assert.Equal(text, Assert.Single(Core2Pdf.Lines(filled)).Text);
    }

    [Fact]
    public void SingleDirectionLinesHaveNoMarks()
    {
        var arial = SystemFonts.Get("arial.ttf")!;
        Assert.False(TextShaper.Shape("Hello world", arial).Mixed);
        Assert.False(TextShaper.Shape("שלום עולם", arial).Mixed);
        Assert.True(TextShaper.Shape("Hello שלום", arial).Mixed);
        Assert.False(TextShaper.Shape("Hello שלום", arial, visualOrder: true).Mixed);
    }
}
