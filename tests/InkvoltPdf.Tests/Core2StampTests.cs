using System.Text;
using InkvoltPdf.Core;

namespace InkvoltPdf.Tests;

public class Core2StampTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void NonLatinStampTextRendersWithAnEmbeddedFont(int rotation)
    {
        var page = rotation == 0 ? TestPdf.Make(1) : PageEditor.Rotate(TestPdf.Make(1), null, [0], rotation);
        var data = TextStamps.Put(page, null, 0, new RectD(100, 300, 300, 40), StampKind.Text, "Привет мир\n日本語テキスト", 14);
        var text = Core2Pdf.AllText(data);
        Assert.Contains("Привет мир", text);
        Assert.Contains("日本語テキスト", text);
        Assert.Equal("Привет мир\n日本語テキスト", TextStamps.Read(data, null).Single().Text);
        var line = Core2Pdf.Lines(data).Single(l => l.Text.Contains("Привет"));
        Assert.True(line.Box.Width > line.Box.Height * 2, $"stamp text should be upright: {line.Box}");
        Assert.InRange(line.Box.X, 95, 120);
    }

    [Fact]
    public void StampsShareTheirFontObjects()
    {
        var data = TestPdf.Make(1);
        for (var i = 0; i < 3; i++) data = TextStamps.Put(data, null, 0, new RectD(100, 100 + 40 * i, 200, 20), StampKind.Text, $"Line {i}", 12);
        data = TextStamps.Put(data, null, 0, new RectD(100, 300, 14, 14), StampKind.Check, "", 12);
        data = TextStamps.Put(data, null, 0, new RectD(130, 300, 14, 14), StampKind.Cross, "", 12);
        var raw = Encoding.Latin1.GetString(data);
        Assert.Equal(1, Count(raw, "/BaseFont /Helvetica"));
        Assert.Equal(1, Count(raw, "/BaseFont /ZapfDingbats"));
        Assert.Equal(5, TextStamps.Read(data, null).Count);
        Assert.Contains("Line 2", Core2Pdf.AllText(data));
    }

    private static int Count(string s, string what)
    {
        var parts = what.Split(' ');
        return System.Text.RegularExpressions.Regex.Matches(s, System.Text.RegularExpressions.Regex.Escape(parts[0]) + @"\s*" + System.Text.RegularExpressions.Regex.Escape(parts[1]) + @"(?![\w-])").Count;
    }
}
