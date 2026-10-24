using System.Text;
using MuPDFCore;
using InkvoltPdf.Core;

namespace InkvoltPdf.Tests;

public class FormTests
{
    /// <summary>A minimal AcroForm: text "name", check box "agree", radio group "color" (Red/Blue), combo "size" (S/M/L).</summary>
    public static byte[] MakeForm(int rotate = 0)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R 6 0 R 7 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 8 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Rotate {rotate} /Annots [4 0 R 5 0 R 9 0 R 10 0 R 7 0 R] /Contents 11 0 R /Resources << /Font << /Helv 8 0 R >> >> >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /Rect [72 700 300 720] /DA (/Helv 12 Tf 0 g) /V (Alice) /F 4 /P 3 0 R >>",
            "<< /Type /Annot /Subtype /Widget /FT /Btn /T (agree) /Rect [72 650 90 668] /V /Off /AS /Off /F 4 /P 3 0 R /AP << /N << /Yes 12 0 R /Off 13 0 R >> >> >>",
            "<< /FT /Btn /Ff 49152 /T (color) /Kids [9 0 R 10 0 R] /V /Off >>",
            "<< /Type /Annot /Subtype /Widget /FT /Ch /Ff 131072 /T (size) /Opt [(S) (M) (L)] /V (M) /Rect [72 550 200 570] /DA (/Helv 12 Tf 0 g) /F 4 /P 3 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [72 600 90 618] /AS /Off /F 4 /P 3 0 R /AP << /N << /Red 12 0 R /Off 13 0 R >> >> >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /Rect [120 600 138 618] /AS /Off /F 4 /P 3 0 R /AP << /N << /Blue 12 0 R /Off 13 0 R >> >> >>",
            Stream("BT /Helv 12 Tf 72 740 Td (Form test) Tj ET"),
            Stream("q 0 g 2 2 14 14 re f Q", "/Type /XObject /Subtype /Form /BBox [0 0 18 18]"),
            Stream("", "/Type /XObject /Subtype /Form /BBox [0 0 18 18]"),
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());

        static string Stream(string content, string dict = "") => $"<< {dict} /Length {content.Length} >>\nstream\n{content}\nendstream";
    }

    [Fact]
    public void ReadsFieldsWidgetsAndValues()
    {
        var widgets = FormEditor.Read(MakeForm(), null);
        Assert.Equal(5, widgets.Count);

        var name = widgets.Single(w => w.FieldName == "name");
        Assert.Equal(FormFieldKind.Text, name.Kind);
        Assert.Equal("Alice", name.Value);
        Assert.Equal(new RectD(72, 72, 228, 20), name.Rect); // 792 - 720 = 72 from the top

        var agree = widgets.Single(w => w.FieldName == "agree");
        Assert.Equal(FormFieldKind.CheckBox, agree.Kind);
        Assert.Equal("Yes", agree.OnState);
        Assert.Equal("Off", agree.Value);

        var radios = widgets.Where(w => w.FieldName == "color").ToList();
        Assert.Equal(["Red", "Blue"], radios.Select(r => r.OnState));
        Assert.All(radios, r => Assert.Equal(FormFieldKind.Radio, r.Kind));

        var size = widgets.Single(w => w.FieldName == "size");
        Assert.Equal(FormFieldKind.Choice, size.Kind);
        Assert.Equal(["S", "M", "L"], size.Options);
        Assert.Equal("M", size.Value);

        // Fill order: top to bottom.
        Assert.Equal(["name", "agree", "color", "color", "size"], widgets.Select(w => w.FieldName));
    }

    [Fact]
    public void FillsFieldsAndMuPdfRendersTheValues()
    {
        var filled = FormEditor.SetValues(MakeForm(), null, new Dictionary<string, string>
        {
            ["name"] = "Bob Builder",
            ["agree"] = "On",
            ["color"] = "Blue",
            ["size"] = "L",
        });

        var widgets = FormEditor.Read(filled, null);
        Assert.Equal("Bob Builder", widgets.Single(w => w.FieldName == "name").Value);
        Assert.Equal("Yes", widgets.Single(w => w.FieldName == "agree").Value);
        Assert.Equal("Blue", widgets.First(w => w.FieldName == "color").Value);
        Assert.Equal("L", widgets.Single(w => w.FieldName == "size").Value);

        using var engine = PdfEngine.Open(filled, InputFileTypes.PDF, null);
        // The checked box and the chosen radio draw their "on" appearance (a filled square).
        Assert.True(Ink(engine, new RectD(74, 126, 14, 14)) > 0.5, "check box should be drawn checked");
        Assert.True(Ink(engine, new RectD(122, 176, 14, 14)) > 0.5, "Blue radio should be drawn selected");
        Assert.True(Ink(engine, new RectD(74, 176, 14, 14)) < 0.05, "Red radio should be off");
        // The text value is drawn from the new value (appearance regenerated).
        Assert.True(Ink(engine, new RectD(72, 72, 228, 20)) > 0.02, "text field should show its value");
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void WidgetRectsFollowPageRotation(int rotate)
    {
        var data = FormEditor.SetValues(MakeForm(rotate), null, new Dictionary<string, string> { ["agree"] = "On" });
        var agree = FormEditor.Read(data, null).Single(w => w.FieldName == "agree");
        using var engine = PdfEngine.Open(data, InputFileTypes.PDF, null);
        // The rect we report must be where MuPDF actually draws the checked box.
        var inner = new RectD(agree.Rect.X + 3, agree.Rect.Y + 3, agree.Rect.Width - 6, agree.Rect.Height - 6);
        Assert.True(Ink(engine, inner) > 0.5, $"rotated {rotate}: check box not at {agree.Rect}");
    }

    [Fact]
    public void ReadOnlyFieldsAreNotChangedAndPlainPdfsHaveNoFields()
    {
        Assert.Empty(FormEditor.Read(TestPdf.Make(1), null));
        Assert.Throws<InvalidOperationException>(() => FormEditor.SetValues(TestPdf.Make(1), null, new Dictionary<string, string> { ["x"] = "y" }));
    }

    /// <summary>Fraction of dark pixels in a page region rendered at 2x.</summary>
    private static double Ink(PdfEngine engine, RectD region)
    {
        var img = engine.Render(0, 2, region);
        var dark = 0;
        for (var i = 0; i + 2 < img.Pixels.Length; i += 3)
            if (img.Pixels[i] + img.Pixels[i + 1] + img.Pixels[i + 2] < 300) dark++;
        return dark / (double)(img.Width * img.Height);
    }
}
