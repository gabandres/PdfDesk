using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfDesk.Core;

/// <summary>Margins as the user sees the page (after its /Rotate), in points.</summary>
public readonly record struct Margins(double Left, double Top, double Right, double Bottom);

public sealed record PdfMetadata(
    string Title, string Author, string Subject, string Keywords,
    string Creator, string Producer, DateTime? Created, DateTime? Modified, string Version);

/// <summary>A source document for merge/insert.</summary>
public sealed record PdfSource(byte[] Data, string? Password, string Name);

/// <summary>
/// Page-level edits. Every operation takes the current PDF bytes and returns new bytes, which keeps undo trivial
/// (snapshots) and leaves the original document untouched until the user saves. Edits happen in place (Modify mode)
/// so bookmarks, forms and metadata survive wherever the pages they belong to survive.
/// </summary>
public static class PageEditor
{
    public static byte[] Delete(byte[] data, string? password, IReadOnlyCollection<int> pages)
    {
        return Edit(data, password, doc =>
        {
            if (pages.Distinct().Count() >= doc.PageCount)
                throw new InvalidOperationException("A document must keep at least one page.");
            foreach (var i in pages.Distinct().OrderDescending()) doc.Pages.RemoveAt(i);
        });
    }

    /// <summary>Puts the pages in <paramref name="order"/> (a permutation of 0..n-1).</summary>
    public static byte[] Reorder(byte[] data, string? password, IReadOnlyList<int> order)
    {
        return Edit(data, password, doc =>
        {
            if (order.Count != doc.PageCount || order.Distinct().Count() != order.Count || order.Any(i => i < 0 || i >= doc.PageCount))
                throw new ArgumentException("Order must be a permutation of the document's pages.", nameof(order));

            var current = Enumerable.Range(0, doc.PageCount).ToList();
            for (var target = 0; target < order.Count; target++)
            {
                var from = current.IndexOf(order[target]);
                if (from == target) continue;
                doc.Pages.MovePage(from, target);
                current.RemoveAt(from);
                current.Insert(target, order[target]);
            }
        });
    }

    /// <summary>The page order after moving <paramref name="pages"/> so they sit before original page <paramref name="before"/> (count = end).</summary>
    public static int[] MoveOrder(int pageCount, IReadOnlyCollection<int> pages, int before)
    {
        var moving = pages.Distinct().Order().ToArray();
        var rest = Enumerable.Range(0, pageCount).Where(i => !moving.Contains(i)).ToList();
        var at = rest.Count(i => i < before);
        rest.InsertRange(at, moving);
        return [.. rest];
    }

    public static byte[] Move(byte[] data, string? password, int pageCount, IReadOnlyCollection<int> pages, int before) =>
        Reorder(data, password, MoveOrder(pageCount, pages, before));

    public static byte[] Reverse(byte[] data, string? password, int pageCount) =>
        Reorder(data, password, [.. Enumerable.Range(0, pageCount).Reverse()]);

    /// <summary>Rotates pages by a multiple of 90° (positive = clockwise).</summary>
    public static byte[] Rotate(byte[] data, string? password, IReadOnlyCollection<int> pages, int degrees)
    {
        if (degrees % 90 != 0) throw new ArgumentException("Rotation must be a multiple of 90°.", nameof(degrees));
        return Edit(data, password, doc =>
        {
            foreach (var i in pages.Distinct())
            {
                var page = doc.Pages[i];
                page.Rotate = (((page.Rotate + degrees) % 360) + 360) % 360;
            }
        });
    }

    /// <summary>Inserts pages of <paramref name="source"/> before page <paramref name="at"/> (count = append).</summary>
    public static byte[] Insert(byte[] data, string? password, PdfSource source, IReadOnlyList<int>? sourcePages, int at)
    {
        return Edit(data, password, doc =>
        {
            using var src = OpenImport(source.Data, source.Password);
            var take = sourcePages ?? [.. Enumerable.Range(0, src.PageCount)];
            at = Math.Clamp(at, 0, doc.PageCount);
            for (var k = 0; k < take.Count; k++) doc.Pages.Insert(at + k, src.Pages[take[k]]);
        });
    }

    public static byte[] InsertBlank(byte[] data, string? password, int at, int count, double widthPt, double heightPt)
    {
        return Edit(data, password, doc =>
        {
            at = Math.Clamp(at, 0, doc.PageCount);
            for (var k = 0; k < count; k++)
            {
                var page = doc.InsertPage(at + k);
                page.MediaBox = new PdfRectangle(new PdfSharp.Drawing.XRect(0, 0, widthPt, heightPt));
            }
        });
    }

    /// <summary>Inserts a copy of each page right after it.</summary>
    public static byte[] Duplicate(byte[] data, string? password, IReadOnlyCollection<int> pages)
    {
        return Edit(data, password, doc =>
        {
            using var copy = OpenImport(data, password);
            foreach (var i in pages.Distinct().OrderDescending()) doc.Pages.Insert(i + 1, copy.Pages[i]);
        });
    }

    /// <summary>A new document with just these pages (in document order).</summary>
    public static byte[] Extract(byte[] data, string? password, IReadOnlyCollection<int> pages)
    {
        var keep = pages.ToHashSet();
        return Edit(data, password, doc =>
        {
            if (keep.Count == 0) throw new InvalidOperationException("No pages selected.");
            for (var i = doc.PageCount - 1; i >= 0; i--)
                if (!keep.Contains(i)) doc.Pages.RemoveAt(i);
        });
    }

    /// <summary>Splits into consecutive chunks of <paramref name="pagesPerFile"/> pages.</summary>
    public static List<int[]> ChunksOf(int pageCount, int pagesPerFile) =>
        [.. Enumerable.Range(0, pageCount).Chunk(Math.Max(1, pagesPerFile))];

    /// <summary>Combines documents into a new one, optionally with a bookmark per source file.</summary>
    public static byte[] Merge(IReadOnlyList<PdfSource> sources, bool bookmarkPerFile)
    {
        if (sources.Count == 0) throw new ArgumentException("Nothing to combine.", nameof(sources));
        using var doc = new PdfDocument();
        foreach (var source in sources)
        {
            using var src = OpenImport(source.Data, source.Password);
            var first = doc.PageCount;
            for (var i = 0; i < src.PageCount; i++) doc.AddPage(src.Pages[i]);
            if (bookmarkPerFile && doc.PageCount > first)
                doc.Outlines.Add(Path.GetFileNameWithoutExtension(source.Name), doc.Pages[first], true);
        }
        return Save(doc);
    }

    /// <summary>Shrinks the visible area (CropBox) of pages by the given margins.</summary>
    public static byte[] Crop(byte[] data, string? password, IReadOnlyCollection<int> pages, Margins m)
    {
        return Edit(data, password, doc =>
        {
            foreach (var i in pages.Distinct())
            {
                var page = doc.Pages[i];
                var box = page.Elements.ContainsKey("/CropBox") ? page.CropBox : page.MediaBox;
                var (l, b, r, t) = ToPdfSpace(m, page.Rotate);
                var x1 = Math.Min(box.X1, box.X2) + l;
                var y1 = Math.Min(box.Y1, box.Y2) + b;
                var x2 = Math.Max(box.X1, box.X2) - r;
                var y2 = Math.Max(box.Y1, box.Y2) - t;
                if (x2 - x1 < 1 || y2 - y1 < 1)
                    throw new InvalidOperationException($"The margins are larger than page {i + 1}.");
                page.CropBox = new PdfRectangle(new PdfSharp.Drawing.XPoint(x1, y1), new PdfSharp.Drawing.XPoint(x2, y2));
            }
        });
    }

    /// <summary>Removes any crop, showing each page's full media box again.</summary>
    public static byte[] Uncrop(byte[] data, string? password, IReadOnlyCollection<int> pages)
    {
        return Edit(data, password, doc =>
        {
            foreach (var i in pages.Distinct())
            {
                doc.Pages[i].Elements.Remove("/CropBox");
                doc.Pages[i].Elements.Remove("/TrimBox");
                doc.Pages[i].Elements.Remove("/BleedBox");
                doc.Pages[i].Elements.Remove("/ArtBox");
            }
        });
    }

    /// <summary>Maps user-facing margins (left, top, right, bottom) to unrotated PDF edges (x1, y1, x2, y2 insets).</summary>
    internal static (double Left, double Bottom, double Right, double Top) ToPdfSpace(Margins m, int rotate) =>
        ((rotate % 360 + 360) % 360) switch
        {
            90 => (m.Top, m.Left, m.Bottom, m.Right),
            180 => (m.Right, m.Top, m.Left, m.Bottom),
            270 => (m.Bottom, m.Right, m.Top, m.Left),
            _ => (m.Left, m.Bottom, m.Right, m.Top),
        };

    public static PdfMetadata ReadMetadata(byte[] data, string? password)
    {
        using var doc = Open(data, password, PdfDocumentOpenMode.Import);
        var info = doc.Info;
        return new PdfMetadata(info.Title, info.Author, info.Subject, info.Keywords, info.Creator, info.Producer,
            TryDate(() => info.CreationDate), TryDate(() => info.ModificationDate), $"{doc.Version / 10}.{doc.Version % 10}");

        static DateTime? TryDate(Func<DateTime> get)
        {
            try
            {
                var d = get();
                return d == DateTime.MinValue ? null : d;
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or ArgumentException)
            {
                return null;
            }
        }
    }

    public static byte[] WriteMetadata(byte[] data, string? password, string title, string author, string subject, string keywords)
    {
        return Edit(data, password, doc =>
        {
            doc.Info.Title = title;
            doc.Info.Author = author;
            doc.Info.Subject = subject;
            doc.Info.Keywords = keywords;
        });
    }

    private static byte[] Edit(byte[] data, string? password, Action<PdfDocument> edit)
    {
        using var doc = Open(data, password, PdfDocumentOpenMode.Modify);
        edit(doc);
        doc.Info.ModificationDate = DateTime.Now;
        return Save(doc);
    }

    private static PdfDocument OpenImport(byte[] data, string? password) => Open(data, password, PdfDocumentOpenMode.Import);

    private static PdfDocument Open(byte[] data, string? password, PdfDocumentOpenMode mode)
    {
        try
        {
            var stream = new MemoryStream(data, writable: false);
            return string.IsNullOrEmpty(password) ? PdfReader.Open(stream, mode) : PdfReader.Open(stream, password, mode);
        }
        catch (PdfReaderException ex)
        {
            throw new InvalidOperationException($"This PDF can't be edited: {ex.Message}", ex);
        }
    }

    private static byte[] Save(PdfDocument doc)
    {
        doc.Options.CompressContentStreams = true;
        doc.Options.NoCompression = false;
        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }
}
