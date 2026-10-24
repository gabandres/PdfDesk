using System.Globalization;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace InkvoltPdf.Core;

/// <summary>Bates numbering: a prefix, a running number padded with zeros, a suffix ("ABC-000123").</summary>
public sealed record BatesFormat(string Prefix = "", long Start = 1, int Digits = 6, string Suffix = "")
{
    /// <summary>The number of the <paramref name="index"/>th page numbered (0 = the first).</summary>
    public string Format(int index) =>
        Prefix + (Start + index).ToString("D" + Math.Clamp(Digits, 1, 18), CultureInfo.InvariantCulture) + Suffix;
}

/// <summary>The six places a header or footer text can go.</summary>
public enum HeaderFooterSlot { HeaderLeft, HeaderCenter, HeaderRight, FooterLeft, FooterCenter, FooterRight }

/// <summary>What <see cref="HeaderFooter.Apply"/> writes. Texts may hold tokens (see <see cref="HeaderFooter.Expand"/>).</summary>
public sealed record HeaderFooterOptions
{
    public string HeaderLeft { get; init; } = "";
    public string HeaderCenter { get; init; } = "";
    public string HeaderRight { get; init; } = "";
    public string FooterLeft { get; init; } = "";
    public string FooterCenter { get; init; } = "";
    public string FooterRight { get; init; } = "";

    public double FontSize { get; init; } = 10;
    public AnnotColor Color { get; init; } = new(0, 0, 0);

    /// <summary>Points from the edges of the page as shown: the header's top, the footer's bottom, the side texts' outer edges.</summary>
    public double MarginTop { get; init; } = 36;
    public double MarginBottom { get; init; } = 36;
    public double MarginLeft { get; init; } = 72;
    public double MarginRight { get; init; } = 72;

    /// <summary>What &lt;&lt;bates&gt;&gt; becomes; null leaves it empty.</summary>
    public BatesFormat? Bates { get; init; }

    /// <summary>&lt;&lt;file&gt;&gt;: the document's file name.</summary>
    public string FileName { get; init; } = "";

    /// <summary>&lt;&lt;date&gt;&gt; and &lt;&lt;time&gt;&gt;.</summary>
    public DateTime Date { get; init; } = DateTime.Now;

    /// <summary>How dates and times are written (null: the current culture).</summary>
    public CultureInfo? Culture { get; init; }

    // For a preview made on a page extracted from the document: the numbers it would get there.

    /// <summary>Added to each page's number (&lt;&lt;page&gt;&gt;).</summary>
    public int PageNumberOffset { get; init; }

    /// <summary>&lt;&lt;pages&gt;&gt; instead of the document's page count.</summary>
    public int? PageCount { get; init; }

    /// <summary>Added to each page's Bates index.</summary>
    public int BatesOffset { get; init; }

    public string Text(HeaderFooterSlot slot) => slot switch
    {
        HeaderFooterSlot.HeaderLeft => HeaderLeft,
        HeaderFooterSlot.HeaderCenter => HeaderCenter,
        HeaderFooterSlot.HeaderRight => HeaderRight,
        HeaderFooterSlot.FooterLeft => FooterLeft,
        HeaderFooterSlot.FooterCenter => FooterCenter,
        _ => FooterRight,
    };

    public bool IsEmpty => Enum.GetValues<HeaderFooterSlot>().All(s => string.IsNullOrWhiteSpace(Text(s)));
}

/// <summary>
/// Headers and footers, page numbers and Bates numbers, written into the pages as content (they print everywhere and
/// can't be moved by accident), in Helvetica (an embedded subset font for other scripts). Each page's text is a form
/// XObject laid out on the page as shown (rotation and crop applied); <see cref="Remove"/> finds and removes it again
/// (see <see cref="PageMarks"/>). Applying again replaces the earlier header and footer of those pages.
/// </summary>
public static class HeaderFooter
{
    internal const string Kind = "HeaderFooter";

    /// <summary>The tokens texts may use (date and time also take a .NET format: &lt;&lt;date:yyyy-MM-dd&gt;&gt;).</summary>
    public static readonly IReadOnlyList<string> Tokens = ["<<page>>", "<<pages>>", "<<date>>", "<<time>>", "<<file>>", "<<bates>>"];

    private static readonly Regex TokenPattern = new(@"<<\s*(page|pages|date|time|file|bates)\s*(?::([^<>]*))?>>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Replaces the tokens in <paramref name="template"/>: &lt;&lt;page&gt;&gt; (1-based), &lt;&lt;pages&gt;&gt;,
    /// &lt;&lt;date&gt;&gt; and &lt;&lt;time&gt;&gt; (short forms of <paramref name="culture"/>, or a format after a colon),
    /// &lt;&lt;file&gt;&gt; and &lt;&lt;bates&gt;&gt;. Tokens are matched ignoring case; anything else stays as typed.
    /// </summary>
    public static string Expand(string template, int page, int pages, string? bates, string fileName, DateTime date, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        return TokenPattern.Replace(template, m =>
        {
            var format = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null;
            return m.Groups[1].Value.ToLowerInvariant() switch
            {
                "page" => page.ToString(culture),
                "pages" => pages.ToString(culture),
                "date" => Format(date, format ?? "d", culture),
                "time" => Format(date, format ?? "t", culture),
                "file" => fileName,
                _ => bates ?? "",
            };
        });
    }

    private static string Format(DateTime date, string format, CultureInfo culture)
    {
        try
        {
            return date.ToString(format.Length == 0 ? "d" : format, culture);
        }
        catch (FormatException)
        {
            return date.ToString("d", culture);
        }
    }

    /// <summary>
    /// Where a slot's text starts (its baseline's left end), in displayed-page coordinates (origin at the bottom-left
    /// of the page as shown, y up), for text <paramref name="textWidth"/> wide whose font rises <paramref name="ascent"/>
    /// above and drops <paramref name="descent"/> (negative) below the baseline.
    /// </summary>
    public static (double X, double Y) Position(HeaderFooterSlot slot, double textWidth, double pageWidth, double pageHeight,
        double ascent, double descent, HeaderFooterOptions o)
    {
        var header = slot is HeaderFooterSlot.HeaderLeft or HeaderFooterSlot.HeaderCenter or HeaderFooterSlot.HeaderRight;
        var y = header ? pageHeight - o.MarginTop - ascent : o.MarginBottom - descent;
        var x = slot switch
        {
            HeaderFooterSlot.HeaderLeft or HeaderFooterSlot.FooterLeft => o.MarginLeft,
            HeaderFooterSlot.HeaderCenter or HeaderFooterSlot.FooterCenter => (pageWidth - textWidth) / 2,
            _ => pageWidth - o.MarginRight - textWidth,
        };
        return (x, y);
    }

    /// <summary>Writes the header and footer on <paramref name="pages"/> (0-based; Bates numbers follow page order).</summary>
    public static byte[] Apply(byte[] data, string? password, IReadOnlyCollection<int> pages, HeaderFooterOptions options)
    {
        if (options.IsEmpty) throw new ArgumentException("Type the text of a header or a footer.");
        if (!double.IsFinite(options.FontSize) || options.FontSize is < 1 or > 200) throw new ArgumentException("The font size must be from 1 to 200 points.");
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Modify, "This PDF can't be edited");
        var count = options.PageCount ?? doc.PageCount;
        var color = new PdfColorValue([options.Color.R, options.Color.G, options.Color.B]);
        var size = options.FontSize;
        var k = 0;
        var todo = new List<(int Page, Dictionary<HeaderFooterSlot, string> Texts)>();
        foreach (var p in pages.Where(p => p >= 0 && p < doc.PageCount).Distinct().Order())
        {
            var bates = options.Bates?.Format(options.BatesOffset + k++);
            todo.Add((p, Enum.GetValues<HeaderFooterSlot>().ToDictionary(s => s, s =>
                Expand(options.Text(s), p + 1 + options.PageNumberOffset, count, bates, options.FileName, options.Date, options.Culture)
                    .Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim())));
        }

        // One painter and one resource dictionary for every page: text in other scripts embeds its font once, subset to
        // the glyphs of all pages (filled in after the last page).
        var painter = new TextPainter(StdFont.Helvetica, todo.SelectMany(t => t.Texts.Values));
        var resources = new PdfDictionary(doc);
        doc.Internals.AddObject(resources);
        foreach (var (p, texts) in todo)
        {
            var page = doc.Pages[p];
            PageMarks.Remove(doc, page, Kind);
            if (texts.Values.All(t => t.Length == 0)) continue;

            var (w, h, matrix) = PageMarks.Frame(doc, page);
            foreach (var (slot, text) in texts)
            {
                if (text.Length == 0) continue;
                var (x, y) = Position(slot, painter.Measure(text) * size, w, h, painter.Ascent * size, painter.Descent * size, options);
                painter.Draw(text, x, y, size);
            }
            var form = PdfObj.FormXObject(doc, painter.Ops("/Helv", color), 0, 0, w, h, null, matrix);
            form.Elements["/Resources"] = resources.Reference!;
            PageMarks.Add(doc, page, form, Kind, behind: false, "<</Type /Pagination>>");
        }
        foreach (var (key, value) in painter.Resources(doc, "/Helv").Elements) resources.Elements[key] = value;
        doc.Info.ModificationDate = DateTime.Now;
        return PdfObj.Save(doc);
    }

    /// <summary>Removes every header and footer Inkvolt PDF added. Returns <paramref name="data"/> itself when there are none.</summary>
    public static byte[] Remove(byte[] data, string? password)
    {
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Modify, "This PDF can't be edited");
        var any = false;
        foreach (var page in doc.Pages) any |= PageMarks.Remove(doc, page, Kind);
        if (!any) return data;
        doc.Info.ModificationDate = DateTime.Now;
        return PdfObj.Save(doc);
    }

    /// <summary>True when the document has a header or footer Inkvolt PDF added.</summary>
    public static bool Has(byte[] data, string? password)
    {
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Import);
        return PageMarks.Has(doc, Kind);
    }
}
