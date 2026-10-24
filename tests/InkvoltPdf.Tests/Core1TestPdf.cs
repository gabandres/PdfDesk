using System.Text;

namespace InkvoltPdf.Tests;

/// <summary>
/// Hand-written PDFs with navigation and forms: pages show "Page N" (1-based), plus an outline, named destinations,
/// internal links and AcroForm fields spread over several pages.
/// </summary>
internal static class Core1TestPdf
{
    /// <summary>
    /// Five pages (612 x 792) and:
    /// outline Ch1 (p1, XYZ top 700) > [Ch1.1 (named "sec2" = p2 top 600), Ch1.2 (GoTo action p3 top 500)],
    /// Ch2 (p4, FitH 500, closed) > [Ch2.1 (p5 top 400)];
    /// on page 1: a link to p3 (top 692), a link to named "sec4" (p4, Fit) and text field <paramref name="textField"/>;
    /// radio group <paramref name="radioField"/> with a widget on page 2 and one on page 3.
    /// </summary>
    public static byte[] Navigation(string textField = "name", string radioField = "color", string label = "Page")
    {
        const int pages = 5;
        string P(int i) => $"{5 + i} 0 R";
        var objects = new Dictionary<int, string>
        {
            [1] = $"<< /Type /Catalog /Pages 2 0 R /Outlines 3 0 R /Names << /Dests << /Names [(sec2) [{P(1)} /XYZ 0 600 0] (sec4) [{P(3)} /Fit]] >> >> " +
                  "/AcroForm << /Fields [22 0 R 23 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 4 0 R >> >> >> >>",
            [2] = $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages).Select(P))}] /Count {pages} >>",
            [3] = "<< /Type /Outlines /First 15 0 R /Last 18 0 R /Count 3 >>",
            [4] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            [15] = $"<< /Title (Ch1) /Parent 3 0 R /Next 18 0 R /First 16 0 R /Last 17 0 R /Count 2 /Dest [{P(0)} /XYZ 0 700 0] >>",
            [16] = "<< /Title (Ch1.1) /Parent 15 0 R /Next 17 0 R /Dest (sec2) >>",
            [17] = $"<< /Title (Ch1.2) /Parent 15 0 R /Prev 16 0 R /A << /S /GoTo /D [{P(2)} /XYZ 0 500 0] >> >>",
            [18] = $"<< /Title (Ch2) /Parent 3 0 R /Prev 15 0 R /First 19 0 R /Last 19 0 R /Count -1 /Dest [{P(3)} /FitH 500] >>",
            [19] = $"<< /Title (Ch2.1) /Parent 18 0 R /Dest [{P(4)} /XYZ 0 400 0] >>",
            [20] = $"<< /Type /Annot /Subtype /Link /Rect [72 600 200 620] /Border [0 0 0] /Dest [{P(2)} /XYZ 0 692 0] >>",
            [21] = "<< /Type /Annot /Subtype /Link /Rect [72 560 200 580] /Border [0 0 0] /A << /S /GoTo /D (sec4) >> >>",
            [22] = $"<< /Type /Annot /Subtype /Widget /FT /Tx /T ({textField}) /Rect [72 500 300 520] /DA (/Helv 12 Tf 0 g) /V (Alice) /F 4 /P {P(0)} >>",
            [23] = $"<< /FT /Btn /Ff 49152 /T ({radioField}) /Kids [24 0 R 25 0 R] /V /Off >>",
            [24] = $"<< /Type /Annot /Subtype /Widget /Parent 23 0 R /Rect [72 400 90 418] /AS /Off /F 4 /P {P(1)} /AP << /N << /Red 26 0 R /Off 27 0 R >> >> >>",
            [25] = $"<< /Type /Annot /Subtype /Widget /Parent 23 0 R /Rect [72 400 90 418] /AS /Off /F 4 /P {P(2)} /AP << /N << /Blue 26 0 R /Off 27 0 R >> >> >>",
            [26] = Stream("q 0 g 2 2 14 14 re f Q", "/Type /XObject /Subtype /Form /BBox [0 0 18 18]"),
            [27] = Stream("", "/Type /XObject /Subtype /Form /BBox [0 0 18 18]"),
        };
        string[] annots = ["[20 0 R 21 0 R 22 0 R]", "[24 0 R]", "[25 0 R]", "[]", "[]"];
        for (var i = 0; i < pages; i++)
        {
            objects[5 + i] = $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots {annots[i]} /Contents {10 + i} 0 R /Resources << /Font << /Helv 4 0 R >> >> >>";
            objects[10 + i] = Stream($"BT /Helv 20 Tf 72 700 Td ({label} {i + 1}) Tj ET");
        }
        return Build(objects);
    }

    /// <summary>One page with an Info dictionary and an XMP packet carrying a PDF/A id and a custom property.</summary>
    public static byte[] WithXmp(string title = "Old title")
    {
        var xmp = "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n" +
                  "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                  "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" " +
                  "xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" xmlns:my=\"urn:example:my\" xmp:CreateDate=\"2020-01-02T03:04:05Z\" pdfaid:part=\"2\">" +
                  $"<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{title}</rdf:li></rdf:Alt></dc:title>" +
                  "<xmp:ModifyDate>2020-01-02T03:04:05Z</xmp:ModifyDate><my:Custom>keep me</my:Custom>" +
                  "</rdf:Description></rdf:RDF></x:xmpmeta>\n<?xpacket end=\"w\"?>";
        var objects = new Dictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>",
            [2] = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            [3] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << /Font << /Helv 7 0 R >> >> >>",
            [4] = Stream(xmp, "/Type /Metadata /Subtype /XML"),
            [5] = $"<< /Title ({title}) /Producer (Test) /CreationDate (D:20200102030405Z) >>",
            [6] = Stream("BT /Helv 20 Tf 72 700 Td (Page 1) Tj ET"),
            [7] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        return Build(objects, "/Info 5 0 R");
    }

    /// <summary>One page that takes MuPDF a long time to render: many overlapping semi-transparent shapes.</summary>
    public static byte[] Heavy(int shapes = 600)
    {
        var sb = new StringBuilder("/G gs\n");
        var rng = new Random(42);
        for (var i = 0; i < shapes; i++)
            sb.Append($"{rng.Next(0, 10) / 10.0} {rng.Next(0, 10) / 10.0} 0.5 rg {rng.Next(0, 560)} {rng.Next(0, 740)} m {rng.Next(0, 612)} {rng.Next(0, 792)} l {rng.Next(0, 612)} {rng.Next(0, 792)} l f\n");
        var objects = new Dictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R >>",
            [2] = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            [3] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /ExtGState << /G << /ca 0.4 /BM /Multiply >> >> >> >>",
            [4] = Stream(sb.ToString()),
        };
        return Build(objects);
    }

    /// <summary>Serializes numbered objects (1..max, no gaps) with a classic xref table.</summary>
    public static byte[] Build(IReadOnlyDictionary<int, string> objects, string trailerExtra = "")
    {
        var count = objects.Keys.Max();
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[count + 1];
        for (var n = 1; n <= count; n++)
        {
            offsets[n] = Encoding.UTF8.GetByteCount(sb.ToString());
            sb.Append($"{n} 0 obj\n{(objects.TryGetValue(n, out var o) ? o : "null")}\nendobj\n");
        }
        var xref = Encoding.UTF8.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {count + 1}\n0000000000 65535 f \n");
        for (var n = 1; n <= count; n++) sb.Append($"{offsets[n]:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {count + 1} /Root 1 0 R {trailerExtra} >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>A stream object; content is ASCII (or UTF-8, as the whole file is written as UTF-8).</summary>
    public static string Stream(string content, string dict = "") => $"<< {dict} /Length {Encoding.UTF8.GetByteCount(content)} >>\nstream\n{content}\nendstream";
}
