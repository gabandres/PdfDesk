using System.Text;
using System.Xml;
using System.Xml.Linq;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
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
/// so bookmarks, forms and metadata survive wherever the pages they belong to survive. Removing pages drops the
/// bookmarks, links, actions, named destinations, article beads and form widgets that pointed at them; imported pages
/// bring their bookmarks, links and form fields along (never hidden copies of pages they lead to). Page labels follow
/// their pages.
/// </summary>
public static class PageEditor
{
    public static byte[] Delete(byte[] data, string? password, IReadOnlyCollection<int> pages)
    {
        return Edit(data, password, doc =>
        {
            if (pages.Distinct().Count() >= doc.PageCount)
                throw new InvalidOperationException("A document must keep at least one page.");
            PdfStructure.RemovePages(doc, pages.Distinct());
            SignedEdits.NoteRemoval();
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
            var imported = new List<(int, PdfPage)>();
            for (var k = 0; k < take.Count; k++) imported.Add((take[k], doc.Pages.Insert(at + k, src.Pages[take[k]])));

            var import = new PdfStructure.Import(doc, src, imported);
            import.FixLinks();
            var outline = PdfStructure.ReadOutline(doc);
            outline.AddRange(import.Outline());
            PdfStructure.WriteOutline(doc, outline);
            import.RegisterFields();
            PdfStructure.DropStrayReferences(doc);
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
            var originals = Enumerable.Range(0, doc.PageCount).Select(i => doc.Pages[i]).ToArray();
            var copies = new List<(int, PdfPage)>();
            foreach (var i in pages.Distinct().OrderDescending()) copies.Add((i, doc.Pages.Insert(i + 1, copy.Pages[i])));

            // Links on a copy lead where the original's do; its form fields become separate fields.
            var import = new PdfStructure.Import(doc, copy, copies, i => originals[i]);
            import.FixLinks();
            import.RegisterFields();
            PdfStructure.DropStrayReferences(doc);
        });
    }

    /// <summary>A new document with just these pages (in document order).</summary>
    public static byte[] Extract(byte[] data, string? password, IReadOnlyCollection<int> pages)
    {
        var keep = pages.ToHashSet();
        return Edit(data, password, doc =>
        {
            if (!keep.Any(i => i >= 0 && i < doc.PageCount)) throw new InvalidOperationException("No pages selected.");
            PdfStructure.RemovePages(doc, Enumerable.Range(0, doc.PageCount).Where(i => !keep.Contains(i)));
        }, rewrite: true);
    }

    /// <summary>Splits into consecutive chunks of <paramref name="pagesPerFile"/> pages.</summary>
    public static List<int[]> ChunksOf(int pageCount, int pagesPerFile) =>
        [.. Enumerable.Range(0, pageCount).Chunk(Math.Max(1, pagesPerFile))];

    /// <summary>
    /// Combines documents into a new one, keeping each file's bookmarks (nested under a bookmark per source file when
    /// <paramref name="bookmarkPerFile"/>), links and form fields (renamed on a name clash, e.g. "name_2").
    /// </summary>
    public static byte[] Merge(IReadOnlyList<PdfSource> sources, bool bookmarkPerFile)
    {
        if (sources.Count == 0) throw new ArgumentException("Nothing to combine.", nameof(sources));
        using var doc = new PdfDocument();
        var outline = new List<PdfStructure.OutlineItem>();
        foreach (var source in sources)
        {
            using var src = OpenImport(source.Data, source.Password);
            var imported = new List<(int, PdfPage)>();
            for (var i = 0; i < src.PageCount; i++) imported.Add((i, doc.AddPage(src.Pages[i])));
            if (imported.Count == 0) continue;

            var import = new PdfStructure.Import(doc, src, imported);
            import.FixLinks();
            var items = import.Outline();
            if (bookmarkPerFile)
            {
                var file = PdfStructure.NewOutlineItem(doc, Path.GetFileNameWithoutExtension(source.Name), imported[0].Item2);
                file.Children.AddRange(items);
                outline.Add(file);
            }
            else
            {
                outline.AddRange(items);
            }
            import.RegisterFields();
        }
        PdfStructure.WriteOutline(doc, outline);
        PdfStructure.DropStrayReferences(doc);
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
                var (bx1, by1, bx2, by2) = PdfObj.VisibleBox(page);
                var (l, b, r, t) = ToPdfSpace(m, page.Rotate);
                var x1 = bx1 + l;
                var y1 = by1 + b;
                var x2 = bx2 - r;
                var y2 = by2 - t;
                if (x2 - x1 < 1 || y2 - y1 < 1)
                    throw new InvalidOperationException($"The margins are larger than page {i + 1}.");
                page.CropBox = new PdfRectangle(new PdfSharp.Drawing.XPoint(x1, y1), new PdfSharp.Drawing.XPoint(x2, y2));
            }
        });
    }

    /// <summary>
    /// Removes any crop (the CropBox, which is all <see cref="Crop"/> sets), showing each page's full media box again.
    /// The print production boxes (TrimBox, BleedBox, ArtBox) stay, clipped to the media box.
    /// </summary>
    public static byte[] Uncrop(byte[] data, string? password, IReadOnlyCollection<int> pages)
    {
        return Edit(data, password, doc =>
        {
            foreach (var i in pages.Distinct())
            {
                var page = doc.Pages[i];
                page.Elements.Remove("/CropBox");
                var (mx1, my1, mx2, my2) = PdfObj.VisibleBox(page);
                foreach (var key in new[] { "/TrimBox", "/BleedBox", "/ArtBox" })
                {
                    if (PdfObj.Array(page.Elements[key]) is not { Elements.Count: 4 } box) continue;
                    double ax = PdfObj.Num(box, 0), ay = PdfObj.Num(box, 1), bx = PdfObj.Num(box, 2), by = PdfObj.Num(box, 3);
                    var (x1, y1, x2, y2) = (Math.Max(mx1, Math.Min(ax, bx)), Math.Max(my1, Math.Min(ay, by)), Math.Min(mx2, Math.Max(ax, bx)), Math.Min(my2, Math.Max(ay, by)));
                    if (x2 <= x1 || y2 <= y1) page.Elements.Remove(key);
                    else if (x1 != Math.Min(ax, bx) || y1 != Math.Min(ay, by) || x2 != Math.Max(ax, bx) || y2 != Math.Max(ay, by))
                        page.Elements[key] = PdfObj.Reals(doc, x1, y1, x2, y2);
                }
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
                return d == DateTime.MinValue ? null : d.Kind == DateTimeKind.Utc ? d.ToLocalTime() : d;
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

    /// <param name="rewrite">Always write a new file, never an incremental update of a signed one (a new document,
    /// e.g. extracted pages, mustn't carry the whole original along).</param>
    private static byte[] Edit(byte[] data, string? password, Action<PdfDocument> edit, bool rewrite = false)
    {
        using var doc = Open(data, password, PdfDocumentOpenMode.Modify);
        var xmp = Xmp.Read(doc);
        var labels = PdfStructure.ReadLabels(doc);
        edit(doc);
        if (labels is not null) PdfStructure.WriteLabels(doc, labels);
        doc.Info.ModificationDate = DateTime.Now;
        return Save(doc, xmp, rewrite);
    }

    private static PdfDocument OpenImport(byte[] data, string? password) => Open(data, password, PdfDocumentOpenMode.Import);

    private static PdfDocument Open(byte[] data, string? password, PdfDocumentOpenMode mode) =>
        PdfObj.Open(data, password, mode, "This PDF can't be edited");

    /// <param name="xmp">The XMP packet the document had before the edit, if any. PDFsharp always replaces it with one of
    /// its own (unescaped, ModifyDate = CreationDate), so the original is written back updated from the Info values.</param>
    /// <param name="rewrite">Skip the incremental update a signed document otherwise gets (see <see cref="Incremental"/>).</param>
    /// <exception cref="SignaturesWouldBreakException">A signed document whose edit can't be appended (see <see cref="SignedEdits"/>).</exception>
    private static byte[] Save(PdfDocument doc, XDocument? xmp = null, bool rewrite = false)
    {
        if (!rewrite && Incremental.Save(doc, xmp) is { } appended) return appended;
        doc.Options.CompressContentStreams = true;
        doc.Options.NoCompression = false;
        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return Xmp.Patch(ms, doc.Info, xmp);
    }
}

/// <summary>
/// Bookmarks, destinations and form fields at the PDF object level. PDFsharp's outline model rewrites destinations it
/// doesn't understand and knows nothing of named destinations or field registration, so this works on the dictionaries.
/// Objects are identified by object id (or by instance when direct), never by PDFsharp wrapper instance.
/// </summary>
internal static class PdfStructure
{
    private const int MaxDepth = 64;

    /// <summary>A bookmark and its children, before /First, /Next, /Count and the rest are written.</summary>
    internal sealed class OutlineItem(PdfDictionary dict, bool open)
    {
        public PdfDictionary Dict { get; } = dict;
        public bool Open { get; } = open;
        public List<OutlineItem> Children { get; } = [];
    }

    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;
    private static PdfDictionary? Dict(PdfItem? item) => Resolve(item) as PdfDictionary;
    private static PdfArray? Arr(PdfItem? item) => Resolve(item) as PdfArray;
    private static bool IsWidget(PdfDictionary d) => d.Elements.GetName("/Subtype") == "/Widget";
    private static bool IsLink(PdfDictionary d) => d.Elements.GetName("/Subtype") == "/Link";
    private static IEnumerable<PdfItem> Annots(PdfDictionary page) => Arr(page.Elements["/Annots"])?.Elements ?? Enumerable.Empty<PdfItem>();

    private static object Id(PdfItem item) => item switch
    {
        PdfReference r => r.ObjectID,
        PdfObject { Reference: { } r } => r.ObjectID,
        _ => item,
    };

    /// <summary>The page object an explicit destination array leads to.</summary>
    private static object? TargetOf(PdfArray? dest) =>
        dest is { Elements.Count: > 0 } && dest.Elements[0] is PdfReference r ? r.ObjectID : null;

    private static PdfString Text(string s) =>
        new(s, s.All(c => c < 128) ? PdfStringEncoding.PDFDocEncoding : PdfStringEncoding.Unicode);

    /// <summary>A page's label as its /PageLabels range makes it: style (D, R, r, A, a; null = prefix only), prefix, number.</summary>
    internal readonly record struct PageLabel(string? Style, string Prefix, int Number);

    /// <summary>Each page's label, in page order, or null when the document has no /PageLabels.</summary>
    public static List<(PdfPage Page, PageLabel Label)>? ReadLabels(PdfDocument doc)
    {
        if (Dict(doc.Internals.Catalog.Elements["/PageLabels"]) is not { } tree) return null;
        var ranges = PdfObj.NumberTree(tree).Where(e => e.Key >= 0).OrderBy(e => e.Key).ToList();
        var list = new List<(PdfPage, PageLabel)>(doc.PageCount);
        var r = -1;
        for (var i = 0; i < doc.PageCount; i++)
        {
            while (r + 1 < ranges.Count && ranges[r + 1].Key <= i) r++;
            if (r < 0)
            {
                list.Add((doc.Pages[i], new PageLabel("D", "", i + 1))); // before the first range: shown as plain numbers
                continue;
            }
            var d = Dict(ranges[r].Value);
            var first = Math.Clamp(PdfObj.Int(d?.Elements["/St"]) ?? 1, 1, 1_000_000_000);
            list.Add((doc.Pages[i], new PageLabel(PdfObj.Name(d?.Elements["/S"]), PdfObj.Str(d?.Elements["/P"]) ?? "",
                (int)Math.Min(int.MaxValue, (long)first + i - ranges[r].Key))));
        }
        return list;
    }

    /// <summary>
    /// Rewrites /PageLabels after pages were removed, added or reordered: every page keeps the label it had (it usually
    /// matches the number printed on it), a new page continues the numbering of the page before it, and the labels are
    /// written as the fewest ranges (/S, /P, /St). Nothing changes when the pages are as they were.
    /// </summary>
    public static void WriteLabels(PdfDocument doc, List<(PdfPage Page, PageLabel Label)> before)
    {
        if (doc.PageCount == before.Count && Enumerable.Range(0, doc.PageCount).All(i => ReferenceEquals(doc.Pages[i], before[i].Page))) return;
        var known = new Dictionary<PdfPage, PageLabel>(ReferenceEqualityComparer.Instance);
        foreach (var (page, label) in before) known.TryAdd(page, label);

        var nums = new PdfArray(doc);
        PageLabel? previous = null;
        for (var i = 0; i < doc.PageCount; i++)
        {
            var label = known.TryGetValue(doc.Pages[i], out var l) ? l
                : previous is { } p ? p with { Number = p.Number == int.MaxValue ? p.Number : p.Number + 1 }
                : new PageLabel("D", "", 1);
            if (previous is not { } prev || prev.Style != label.Style || prev.Prefix != label.Prefix || (label.Style is not null && label.Number != prev.Number + 1))
            {
                var range = new PdfDictionary(doc);
                if (label.Style is { } style) range.Elements.SetName("/S", "/" + style);
                if (label.Prefix.Length > 0) range.Elements["/P"] = Text(label.Prefix);
                if (label.Style is not null && label.Number != 1) range.Elements.SetInteger("/St", label.Number);
                nums.Elements.Add(new PdfInteger(i));
                nums.Elements.Add(range);
            }
            previous = label;
        }
        var tree = new PdfDictionary(doc);
        tree.Elements["/Nums"] = nums;
        doc.Internals.Catalog.Elements["/PageLabels"] = tree;
    }

    /// <summary>
    /// Removes pages along with the bookmarks, links, named destinations, form widgets, article beads and actions
    /// (any annotation's /A and /AA, the page and document /AA, /OpenAction) that lead to them. Whatever else still
    /// refers to them is cut too (<see cref="DropStrayReferences"/>), so a file saved from the result can't carry a
    /// removed page's content along (Extract and Split rely on this).
    /// </summary>
    public static void RemovePages(PdfDocument doc, IEnumerable<int> pages)
    {
        var removedPages = new HashSet<object>();
        var removedAnnots = new HashSet<object>();
        var gonePages = new List<PdfDictionary>();
        var goneKeys = new List<int>();
        foreach (var i in pages.Distinct().OrderDescending())
        {
            var page = doc.Pages[i];
            removedPages.Add(Id(page));
            gonePages.Add(page);
            goneKeys.AddRange(StructTree.ParentKeys(page));
            foreach (var a in Annots(page)) removedAnnots.Add(Id(a));
            doc.Pages.RemoveAt(i);
        }
        if (removedPages.Count == 0) return;

        var names = new NamedDests(doc);
        bool Dead(PdfArray? dest) => TargetOf(dest) is { } id && removedPages.Contains(id);

        WriteOutline(doc, Prune(ReadOutline(doc), item => Dead(names.Of(item.Dict))));

        // A jump (or a chain of actions with one) to a removed page: the action goes.
        bool DeadAction(PdfItem? item)
        {
            for (var depth = 0; depth < MaxDepth && Dict(item) is { } action; depth++)
            {
                if (action.Elements.GetName("/S") == "/GoTo" && Dead(names.Explicit(action.Elements["/D"]))) return true;
                item = Resolve(action.Elements["/Next"]) is PdfArray next ? next.Elements.FirstOrDefault(DeadAction) : action.Elements["/Next"];
            }
            return false;
        }
        void DropDeadTriggers(PdfDictionary owner)
        {
            if (Dict(owner.Elements["/AA"]) is not { } aa) return;
            foreach (var key in aa.Elements.Keys.ToList())
                if (DeadAction(aa.Elements[key])) aa.Elements.Remove(key);
        }

        for (var i = 0; i < doc.PageCount; i++)
        {
            var page = doc.Pages[i];
            DropDeadTriggers(page);
            if (Arr(page.Elements["/Annots"]) is not { } annots) continue;
            for (var k = annots.Elements.Count - 1; k >= 0; k--)
            {
                if (Dict(annots.Elements[k]) is not { } a) continue;
                if (IsLink(a) && Dead(names.Of(a)))
                {
                    annots.Elements.RemoveAt(k);
                    continue;
                }
                // Form buttons, widgets and other annotations jump with actions (a /Dest is only for links).
                if (DeadAction(a.Elements["/A"])) a.Elements.Remove("/A");
                if (!IsLink(a) && Dead(names.Explicit(a.Elements["/Dest"]))) a.Elements.Remove("/Dest");
                DropDeadTriggers(a);
            }
        }

        var catalog = doc.Internals.Catalog;
        var open = Resolve(catalog.Elements["/OpenAction"]);
        if ((open is PdfArray dest && Dead(dest)) || (open is PdfDictionary && DeadAction(open)))
            catalog.Elements.Remove("/OpenAction");
        DropDeadTriggers(catalog);

        names.RemoveWhere(Dead);
        DropWidgets(doc, removedAnnots.Contains);
        StructTree.Prune(doc, gonePages, goneKeys, a => removedAnnots.Contains(Id(a)));
        PruneThreads(doc);
        DropStrayReferences(doc, id => removedPages.Contains(id) || removedAnnots.Contains(id));
    }

    /// <summary>
    /// Article threads (/Threads): beads on pages no longer in the document are unlinked from their thread's ring, and a
    /// thread left without beads is dropped.
    /// </summary>
    private static void PruneThreads(PdfDocument doc)
    {
        var catalog = doc.Internals.Catalog;
        if (Arr(catalog.Elements["/Threads"]) is not { } threads) return;
        var live = new HashSet<object>();
        for (var i = 0; i < doc.PageCount; i++) live.Add(Id(doc.Pages[i]));
        for (var t = threads.Elements.Count - 1; t >= 0; t--)
        {
            if (Dict(threads.Elements[t]) is not { } thread)
            {
                threads.Elements.RemoveAt(t);
                continue;
            }
            var beads = new List<PdfDictionary>();
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            for (var bead = Dict(thread.Elements["/F"]); bead is not null && beads.Count < 100_000 && seen.Add(bead); bead = Dict(bead.Elements["/N"]))
                if (bead.Elements["/P"] is { } p && live.Contains(Id(p))) beads.Add(bead);
            if (beads.Count == 0)
            {
                threads.Elements.RemoveAt(t);
                continue;
            }
            if (beads.Count == seen.Count) continue;
            foreach (var b in beads)
                if (b.Reference is null) doc.Internals.AddObject(b);
            for (var k = 0; k < beads.Count; k++)
            {
                beads[k].Elements.SetReference("/N", beads[(k + 1) % beads.Count]);
                beads[k].Elements.SetReference("/V", beads[(k + beads.Count - 1) % beads.Count]);
                if (k > 0) beads[k].Elements.Remove("/T");
            }
            if (thread.IsIndirect) beads[0].Elements.SetReference("/T", thread);
            thread.Elements.SetReference("/F", beads[0]);
        }
        if (threads.Elements.Count == 0) catalog.Elements.Remove("/Threads");
    }

    /// <summary>
    /// Cuts every remaining reference to a page that isn't in the document (a removed page, or a stray copy PDFsharp made
    /// of a source page an imported annotation pointed at) and to the objects <paramref name="gone"/> picks (by id), so the
    /// saved file can't carry their content along. A destination array or GoTo action leading to such a page is dropped
    /// from whatever holds it (an /A or /AA entry, an action's /Next, a name or number tree pair, an array), and so is an
    /// annotation or bead whose /P is such a page. References elsewhere (a structure element's /Pg, a reply's /IRT) are
    /// removed.
    /// </summary>
    internal static void DropStrayReferences(PdfDocument doc, Func<object, bool>? gone = null)
    {
        var pages = new HashSet<object>();
        for (var i = 0; i < doc.PageCount; i++) pages.Add(Id(doc.Pages[i]));
        // Indirect objects → whether they are gone (or lead only to a gone page); false while being visited (cycles).
        var memo = new Dictionary<PdfObjectID, bool>();
        var catalog = doc.Internals.Catalog;
        Container(catalog, 0);
        if (doc.Info is { } info) Container(info, 0);

        bool Dead(PdfItem? item, int depth)
        {
            if (depth > 512) return false;
            switch (item)
            {
                case PdfReference r:
                    if (gone?.Invoke(r.ObjectID) == true) return true;
                    if (memo.TryGetValue(r.ObjectID, out var known)) return known;
                    if (r.Value is PdfDictionary d && d.Elements.GetName("/Type") == "/Page" && !pages.Contains(r.ObjectID)) return memo[r.ObjectID] = true;
                    memo[r.ObjectID] = false;
                    return memo[r.ObjectID] = Container(r.Value, depth + 1);
                case PdfDictionary or PdfArray:
                    return Container((PdfObject)item, depth + 1);
                default:
                    return false;
            }
        }

        // Prunes what a dictionary or array holds; true when the container itself must go.
        bool Container(PdfObject? obj, int depth)
        {
            switch (obj)
            {
                case PdfDictionary d:
                    foreach (var key in d.Elements.Keys.ToList())
                    {
                        var value = d.Elements[key];
                        if (key is "/Names" or "/Nums" && Resolve(value) is PdfArray pairs)
                        {
                            for (var i = (pairs.Elements.Count & ~1) - 2; i >= 0; i -= 2)
                                if (Dead(pairs.Elements[i + 1], depth))
                                {
                                    pairs.Elements.RemoveAt(i + 1);
                                    pairs.Elements.RemoveAt(i);
                                }
                            continue;
                        }
                        if (!Dead(value, depth)) continue;
                        if (key == "/P" && value is PdfReference { Value: PdfDictionary p } && p.Elements.GetName("/Type") == "/Page") return true; // an annotation or bead of a gone page
                        d.Elements.Remove(key);
                        if (key == "/D" && d.Elements.GetName("/S") == "/GoTo") return true; // a jump to a gone page
                    }
                    return false;
                case PdfArray a:
                    // A destination: [page /XYZ left top zoom], [page /Fit]…
                    if (a.Elements.Count > 1 && Resolve(a.Elements[1]) is PdfName && a.Elements[0] is PdfReference first && Dead(first, depth)) return true;
                    for (var i = a.Elements.Count - 1; i >= 0; i--)
                        if (Dead(a.Elements[i], depth)) a.Elements.RemoveAt(i);
                    return false;
                default:
                    return false;
            }
        }
    }

    /// <summary>The document's bookmarks as a tree (empty if none).</summary>
    public static List<OutlineItem> ReadOutline(PdfDocument doc)
    {
        var seen = new HashSet<object>();
        return Dict(doc.Internals.Catalog.Elements["/Outlines"]) is { } root ? Children(root, 0) : [];

        List<OutlineItem> Children(PdfDictionary parent, int depth)
        {
            var list = new List<OutlineItem>();
            var item = parent.Elements["/First"];
            while (depth < MaxDepth && item is not null && Dict(item) is { } d && seen.Add(Id(item)))
            {
                var node = new OutlineItem(d, d.Elements.GetInteger("/Count") > 0);
                node.Children.AddRange(Children(d, depth + 1));
                list.Add(node);
                item = d.Elements["/Next"];
            }
            return list;
        }
    }

    /// <summary>Makes <paramref name="items"/> the document's bookmarks (removing the outline when there are none).</summary>
    public static void WriteOutline(PdfDocument doc, List<OutlineItem> items)
    {
        var catalog = doc.Internals.Catalog;
        if (items.Count == 0)
        {
            if (!catalog.Elements.ContainsKey("/Outlines")) return;
            catalog.Elements.Remove("/Outlines");
            if (catalog.Elements.GetName("/PageMode") == "/UseOutlines") catalog.Elements.Remove("/PageMode");
            return;
        }

        var root = Dict(catalog.Elements["/Outlines"]);
        if (root is null)
        {
            root = new PdfDictionary(doc);
            root.Elements.SetName("/Type", "/Outlines");
            if (!catalog.Elements.ContainsKey("/PageMode")) catalog.Elements.SetName("/PageMode", "/UseOutlines");
        }
        if (root.Reference is null) doc.Internals.AddObject(root);
        catalog.Elements.SetReference("/Outlines", root);
        root.Elements.SetInteger("/Count", Link(doc, root, items));
    }

    /// <summary>Links <paramref name="items"/> under <paramref name="parent"/>; returns how many show when it is open.</summary>
    private static int Link(PdfDocument doc, PdfDictionary parent, List<OutlineItem> items)
    {
        if (items.Count == 0)
        {
            parent.Elements.Remove("/First");
            parent.Elements.Remove("/Last");
            return 0;
        }

        foreach (var item in items)
            if (item.Dict.Reference is null) doc.Internals.AddObject(item.Dict);
        parent.Elements.SetReference("/First", items[0].Dict);
        parent.Elements.SetReference("/Last", items[^1].Dict);

        var visible = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var d = items[i].Dict;
            d.Elements.SetReference("/Parent", parent);
            if (i > 0) d.Elements.SetReference("/Prev", items[i - 1].Dict);
            else d.Elements.Remove("/Prev");
            if (i < items.Count - 1) d.Elements.SetReference("/Next", items[i + 1].Dict);
            else d.Elements.Remove("/Next");

            var below = Link(doc, d, items[i].Children);
            if (items[i].Children.Count > 0) d.Elements.SetInteger("/Count", items[i].Open ? below : -below);
            else d.Elements.Remove("/Count");
            visible += 1 + (items[i].Open ? below : 0);
        }
        return visible;
    }

    /// <summary>Drops the items <paramref name="dead"/> picks, moving their children up in their place.</summary>
    private static List<OutlineItem> Prune(List<OutlineItem> items, Func<OutlineItem, bool> dead)
    {
        var kept = new List<OutlineItem>();
        foreach (var item in items)
        {
            var children = Prune(item.Children, dead);
            item.Children.Clear();
            item.Children.AddRange(children);
            if (dead(item)) kept.AddRange(children);
            else kept.Add(item);
        }
        return kept;
    }

    /// <summary>A new bookmark; without a page it is a plain heading.</summary>
    public static OutlineItem NewOutlineItem(PdfDocument doc, string title, PdfPage? page, PdfArray? viewLike = null, bool open = true)
    {
        var d = new PdfDictionary(doc);
        doc.Internals.AddObject(d);
        d.Elements["/Title"] = Text(title);
        if (page is not null) d.Elements["/Dest"] = Dest(doc, page, viewLike);
        return new OutlineItem(d, open);
    }

    /// <summary>A destination on <paramref name="page"/> with the view (/XYZ left top zoom, /Fit…) of <paramref name="like"/>.</summary>
    private static PdfArray Dest(PdfDocument doc, PdfPage page, PdfArray? like)
    {
        var a = new PdfArray(doc);
        a.Elements.Add(page.ReferenceNotNull);
        if (like is { Elements.Count: > 1 } && Resolve(like.Elements[1]) is PdfName)
        {
            for (var i = 1; i < like.Elements.Count; i++)
                a.Elements.Add(Resolve(like.Elements[i]) is { } v and not (PdfObject or PdfString or PdfReference) ? v : PdfNull.Value);
        }
        else
        {
            a.Elements.Add(new PdfName("/XYZ"));
            a.Elements.Add(PdfNull.Value);
            a.Elements.Add(PdfNull.Value);
            a.Elements.Add(PdfNull.Value);
        }
        return a;
    }

    /// <summary>Unregisters form widgets <paramref name="dead"/> picks, and fields left without widgets.</summary>
    private static void DropWidgets(PdfDocument doc, Func<object, bool> dead)
    {
        var acro = Dict(doc.Internals.Catalog.Elements["/AcroForm"]);
        if (acro is null || Arr(acro.Elements["/Fields"]) is not { } fields) return;
        var dropped = new HashSet<object>();
        PruneFields(fields, dead, dropped, 0);
        if (dropped.Count > 0 && Arr(acro.Elements["/CO"]) is { } order)
            for (var k = order.Elements.Count - 1; k >= 0; k--)
                if (dropped.Contains(Id(order.Elements[k])))
                    order.Elements.RemoveAt(k);
    }

    private static void PruneFields(PdfArray kids, Func<object, bool> dead, HashSet<object> dropped, int depth)
    {
        for (var k = kids.Elements.Count - 1; k >= 0; k--)
        {
            var item = kids.Elements[k];
            if (Dict(item) is not { } field) continue;
            var id = Id(item);
            var drop = false;
            if (IsWidget(field) && dead(id))
            {
                drop = true;
            }
            else if (depth < MaxDepth && Arr(field.Elements["/Kids"]) is { Elements.Count: > 0 } sub)
            {
                PruneFields(sub, dead, dropped, depth + 1);
                drop = sub.Elements.Count == 0;
            }
            if (!drop) continue;
            kids.Elements.RemoveAt(k);
            dropped.Add(id);
        }
    }

    /// <summary>A document's named destinations: the /Names /Dests tree and the PDF 1.1 /Dests dictionary.</summary>
    private sealed class NamedDests
    {
        private readonly Dictionary<string, PdfItem> _strings = new(StringComparer.Ordinal);
        private readonly List<PdfArray> _leaves = [];
        private readonly PdfDictionary? _old;

        public NamedDests(PdfDocument doc)
        {
            var catalog = doc.Internals.Catalog;
            if (Dict(Dict(catalog.Elements["/Names"])?.Elements["/Dests"]) is { } tree) Walk(tree, 0, []);
            _old = Dict(catalog.Elements["/Dests"]);
        }

        private void Walk(PdfDictionary node, int depth, HashSet<PdfDictionary> seen)
        {
            if (depth > MaxDepth || !seen.Add(node)) return;
            if (Arr(node.Elements["/Names"]) is { } pairs)
            {
                _leaves.Add(pairs);
                for (var i = 0; i + 1 < pairs.Elements.Count; i += 2)
                    if (Resolve(pairs.Elements[i]) is PdfString key)
                        _strings.TryAdd(key.Value, pairs.Elements[i + 1]);
            }
            if (Arr(node.Elements["/Kids"]) is { } kids)
                foreach (var kid in kids.Elements)
                    if (Dict(kid) is { } d)
                        Walk(d, depth + 1, seen);
        }

        /// <summary>The explicit destination array behind a /Dest or GoTo /D value (array, name or string).</summary>
        public PdfArray? Explicit(PdfItem? dest)
        {
            var d = Resolve(Resolve(dest) switch
            {
                PdfString s => _strings.GetValueOrDefault(s.Value),
                PdfName n => _old?.Elements[n.Value],
                var other => other,
            });
            return d as PdfArray ?? Arr(Dict(d)?.Elements["/D"]);
        }

        /// <summary>The explicit destination of a bookmark or link: its /Dest, or the /D of its GoTo action.</summary>
        public PdfArray? Of(PdfDictionary item)
        {
            if (item.Elements["/Dest"] is { } dest) return Explicit(dest);
            var action = Dict(item.Elements["/A"]);
            return action?.Elements.GetName("/S") == "/GoTo" ? Explicit(action.Elements["/D"]) : null;
        }

        public void RemoveWhere(Func<PdfArray, bool> dead)
        {
            foreach (var pairs in _leaves)
                for (var i = (pairs.Elements.Count & ~1) - 2; i >= 0; i -= 2)
                    if (Explicit(pairs.Elements[i + 1]) is { } a && dead(a))
                    {
                        pairs.Elements.RemoveAt(i + 1);
                        pairs.Elements.RemoveAt(i);
                    }
            if (_old is not null)
                foreach (var key in _old.Elements.Keys.ToArray())
                    if (Explicit(_old.Elements[key]) is { } a && dead(a))
                        _old.Elements.Remove(key);
        }
    }

    /// <summary>
    /// Pages just imported from another document. PDFsharp copies their annotations but leaves link destinations
    /// pointing at stray copies of the source pages (MuPDF resolves those to page 1), drops named destinations,
    /// bookmarks and the form-field registration; this puts them back.
    /// </summary>
    internal sealed class Import
    {
        private readonly PdfDocument _doc;
        private readonly PdfDocument _src;
        private readonly IReadOnlyList<(int Source, PdfPage Page)> _pages;
        private readonly Func<int, PdfPage?> _target;
        private readonly Dictionary<object, int> _srcIndex = [];
        private readonly NamedDests _srcNames;
        private readonly Dictionary<object, PdfItem> _clones = [];

        /// <param name="pages">(source page index, imported page) pairs.</param>
        /// <param name="target">Where a jump to source page i should land; default: its imported copy, if any.</param>
        public Import(PdfDocument doc, PdfDocument src, IReadOnlyList<(int, PdfPage)> pages, Func<int, PdfPage?>? target = null)
        {
            _doc = doc;
            _src = src;
            _pages = pages;
            var first = new Dictionary<int, PdfPage>();
            foreach (var (s, p) in pages) first.TryAdd(s, p);
            _target = target ?? (i => first.GetValueOrDefault(i));
            for (var i = 0; i < src.PageCount; i++) _srcIndex.TryAdd(Id(src.Pages[i]), i);
            _srcNames = new NamedDests(src);
        }

        /// <summary>Where a source destination lands in the target: (false, _) when it isn't a page of the source, (true, null) when that page wasn't imported.</summary>
        private (bool IsPage, PdfPage? Page) Map(PdfArray? dest) =>
            TargetOf(dest) is { } id && _srcIndex.TryGetValue(id, out var index) ? (true, _target(index)) : (false, null);

        /// <summary>
        /// Points internal links on the imported pages at the imported pages, and drops links to pages left behind; the
        /// same for the GoTo actions of other annotations (form buttons' /A, the /AA triggers). PDFsharp would otherwise
        /// bring a hidden copy of every page they lead to along (see <see cref="DropStrayReferences"/>, which callers run
        /// afterwards for whatever else still leads to such a copy).
        /// </summary>
        public void FixLinks()
        {
            foreach (var (source, page) in _pages)
            {
                var from = Arr(_src.Pages[source].Elements["/Annots"]);
                var to = Arr(page.Elements["/Annots"]);
                if (from is null || to is null || from.Elements.Count != to.Elements.Count) continue;
                for (var k = to.Elements.Count - 1; k >= 0; k--)
                {
                    if (Dict(from.Elements[k]) is not { } annot || Dict(to.Elements[k]) is not { } copy) continue;
                    if (!IsLink(annot))
                    {
                        Retarget(annot, copy, "/A");
                        if (Dict(annot.Elements["/AA"]) is { } triggers && Dict(copy.Elements["/AA"]) is { } copied)
                            foreach (var key in triggers.Elements.Keys) Retarget(triggers, copied, key);
                        continue;
                    }
                    var dest = _srcNames.Of(annot);
                    var (isPage, target) = Map(dest);
                    if (!isPage) continue;
                    if (target is null)
                    {
                        to.Elements.RemoveAt(k);
                        continue;
                    }
                    copy.Elements.Remove("/A");
                    copy.Elements["/Dest"] = Dest(_doc, target, dest);
                }
            }
        }

        /// <summary>A copied GoTo action (<paramref name="key"/> of <paramref name="to"/>) aimed at the imported page, or dropped when its page wasn't imported.</summary>
        private void Retarget(PdfDictionary from, PdfDictionary to, string key)
        {
            if (Dict(from.Elements[key]) is not { } action || action.Elements.GetName("/S") != "/GoTo") return;
            var dest = _srcNames.Explicit(action.Elements["/D"]);
            var (isPage, target) = Map(dest);
            if (!isPage) return;
            if (target is null)
            {
                to.Elements.Remove(key);
                return;
            }
            var jump = new PdfDictionary(_doc);
            jump.Elements.SetName("/S", "/GoTo");
            jump.Elements["/D"] = Dest(_doc, target, dest);
            to.Elements[key] = jump;
        }

        /// <summary>The source's bookmarks, recreated for the imported pages (bookmarks to pages left behind are dropped, their children kept).</summary>
        public List<OutlineItem> Outline() => Copy(ReadOutline(_src));

        private List<OutlineItem> Copy(List<OutlineItem> items)
        {
            var list = new List<OutlineItem>();
            foreach (var item in items)
            {
                var children = Copy(item.Children);
                var src = item.Dict;
                var dest = _srcNames.Of(src);
                var (isPage, target) = Map(dest);
                if (isPage && target is null)
                {
                    list.AddRange(children);
                    continue;
                }

                var copy = NewOutlineItem(_doc, src.Elements.GetString("/Title"), target, dest, item.Open);
                if (!isPage && Dict(src.Elements["/A"]) is { } action && action.Elements.GetName("/S") == "/URI" &&
                    Resolve(action.Elements["/URI"]) is PdfString uri)
                {
                    var a = new PdfDictionary(_doc);
                    a.Elements.SetName("/S", "/URI");
                    a.Elements["/URI"] = new PdfString(uri.Value);
                    copy.Dict.Elements["/A"] = a;
                }
                if (Arr(src.Elements["/C"]) is { Elements.Count: 3 } color && color.Elements.All(c => c is not (PdfObject or PdfReference or PdfString)))
                    copy.Dict.Elements["/C"] = new PdfArray(_doc, [.. color.Elements]);
                if (src.Elements.ContainsKey("/F")) copy.Dict.Elements.SetInteger("/F", src.Elements.GetInteger("/F"));
                copy.Children.AddRange(children);
                list.Add(copy);
            }
            return list;
        }

        /// <summary>
        /// Registers the form fields of the imported widgets in the target's /AcroForm (renaming a field whose name is
        /// taken, e.g. "name" to "name_2"), keeping only the widgets that came along, plus the form's default
        /// appearance resources.
        /// </summary>
        public void RegisterFields()
        {
            var roots = new List<PdfItem>();
            var seenRoots = new HashSet<object>();
            foreach (var (_, page) in _pages)
                foreach (var annot in Annots(page))
                {
                    if (Dict(annot) is not { } widget || !IsWidget(widget)) continue;
                    var root = annot;
                    var d = widget;
                    for (var depth = 0; depth < MaxDepth && d.Elements["/Parent"] is { } parent && Dict(parent) is { } pd; depth++)
                    {
                        root = parent;
                        d = pd;
                    }
                    if (root is PdfReference && seenRoots.Add(Id(root))) roots.Add(root);
                }
            if (roots.Count == 0) return;

            var live = new HashSet<object>();
            for (var i = 0; i < _doc.PageCount; i++)
                foreach (var annot in Annots(_doc.Pages[i]))
                    live.Add(Id(annot));

            var catalog = _doc.Internals.Catalog;
            var acro = Dict(catalog.Elements["/AcroForm"]);
            if (acro is null)
            {
                acro = new PdfDictionary(_doc);
                _doc.Internals.AddObject(acro);
                catalog.Elements.SetReference("/AcroForm", acro);
            }
            var fields = Arr(acro.Elements["/Fields"]);
            if (fields is null)
            {
                fields = new PdfArray(_doc);
                acro.Elements["/Fields"] = fields;
            }
            var registered = new HashSet<object>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in fields.Elements)
            {
                registered.Add(Id(f));
                if (Dict(f)?.Elements.GetString("/T") is { Length: > 0 } t) names.Add(t);
            }

            var added = false;
            foreach (var root in roots)
            {
                if (registered.Contains(Id(root)) || Dict(root) is not { } field) continue;
                if (IsWidget(field) && !live.Contains(Id(root))) continue;
                if (Arr(field.Elements["/Kids"]) is { Elements.Count: > 0 } kids)
                {
                    PruneFields(kids, id => !live.Contains(id), [], 0);
                    if (kids.Elements.Count == 0) continue;
                }
                if (field.Elements.GetString("/T") is { Length: > 0 } name)
                {
                    var unique = name;
                    for (var n = 2; names.Contains(unique); n++) unique = $"{name}_{n}";
                    if (unique != name) field.Elements["/T"] = Text(unique);
                    names.Add(unique);
                }
                fields.Elements.Add(root);
                added = true;
            }
            if (added) CopyFormDefaults(acro);
        }

        /// <summary>Brings the source form's /DA, /NeedAppearances and the /DR resources the target lacks.</summary>
        private void CopyFormDefaults(PdfDictionary acro)
        {
            if (Dict(_src.Internals.Catalog.Elements["/AcroForm"]) is not { } from) return;
            if (!acro.Elements.ContainsKey("/DA") && from.Elements.GetString("/DA") is { Length: > 0 } da)
                acro.Elements.SetString("/DA", da);
            if (from.Elements.GetBoolean("/NeedAppearances")) acro.Elements.SetBoolean("/NeedAppearances", true);
            if (Dict(from.Elements["/DR"]) is not { } fromDr) return;

            var dr = Dict(acro.Elements["/DR"]);
            if (dr is null)
            {
                dr = new PdfDictionary(_doc);
                acro.Elements["/DR"] = dr;
            }
            foreach (var key in fromDr.Elements.Keys)
            {
                if (Dict(fromDr.Elements[key]) is not { } fromSub)
                {
                    if (!dr.Elements.ContainsKey(key)) dr.Elements[key] = Clone(fromDr.Elements[key], 0);
                    continue;
                }
                var sub = Dict(dr.Elements[key]);
                if (sub is null)
                {
                    sub = new PdfDictionary(_doc);
                    dr.Elements[key] = sub;
                }
                foreach (var name in fromSub.Elements.Keys)
                    if (!sub.Elements.ContainsKey(name))
                        sub.Elements[name] = Clone(fromSub.Elements[name], 0);
            }
        }

        /// <summary>Deep-copies a source object into the target (indirect objects once each).</summary>
        private PdfItem Clone(PdfItem? item, int depth)
        {
            if (depth > MaxDepth) return PdfNull.Value;
            switch (item)
            {
                case PdfReference r when r.Value is PdfDictionary or PdfArray:
                    if (_clones.TryGetValue(r.ObjectID, out var done)) return done;
                    PdfObject obj = r.Value is PdfArray ? new PdfArray(_doc) : new PdfDictionary(_doc);
                    _doc.Internals.AddObject(obj);
                    _clones[r.ObjectID] = obj.ReferenceNotNull;
                    Fill(obj, r.Value, depth);
                    return obj.ReferenceNotNull;
                case PdfReference r:
                    return Clone(r.Value, depth + 1);
                case PdfDictionary or PdfArray:
                    PdfObject direct = item is PdfArray ? new PdfArray(_doc) : new PdfDictionary(_doc);
                    Fill(direct, (PdfObject)item, depth);
                    return direct;
                case null:
                    return PdfNull.Value;
                default:
                    return item; // names, numbers, strings, booleans
            }
        }

        private void Fill(PdfObject target, PdfObject source, int depth)
        {
            if (source is PdfArray a)
            {
                foreach (var e in a.Elements) ((PdfArray)target).Elements.Add(Clone(e, depth + 1));
            }
            else if (source is PdfDictionary d)
            {
                var t = (PdfDictionary)target;
                foreach (var key in d.Elements.Keys)
                    if (key != "/Length")
                        t.Elements[key] = Clone(d.Elements[key], depth + 1);
                if (d.Stream is { } stream) t.CreateStream(stream.Value);
            }
        }
    }
}

/// <summary>
/// Keeps the XMP metadata packet in step with the Info dictionary. PDFsharp regenerates XMP on every save from a
/// template (values not XML-escaped, ModifyDate = CreationDate) and discards the document's own packet, so the saved
/// bytes get a corrected packet: the original one with the Info-backed properties updated, else a fresh one.
/// </summary>
internal static class Xmp
{
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Pdf = "http://ns.adobe.com/pdf/1.3/";
    private static readonly XNamespace XmpNs = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace XmpMM = "http://ns.adobe.com/xap/1.0/mm/";
    private static readonly XNamespace Meta = "adobe:ns:meta/";

    /// <summary>The document's XMP packet, or null if it has none or it isn't usable XML.</summary>
    public static XDocument? Read(PdfDocument doc)
    {
        try
        {
            var metadata = doc.Internals.Catalog.Elements["/Metadata"];
            if ((metadata is PdfReference r ? r.Value : metadata) is not PdfDictionary { Stream: { } stream }) return null;
            using var reader = XmlReader.Create(new MemoryStream(stream.UnfilteredValue),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var xmp = XDocument.Load(reader);
            return xmp.Descendants(Rdf + "RDF").Any() ? xmp : null;
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or NotImplementedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The file PDFsharp just saved into <paramref name="saved"/>, with its XMP packet replaced; as saved if it can't be.
    /// Read straight from the stream's buffer, so the file is copied once.
    /// </summary>
    public static byte[] Patch(MemoryStream saved, PdfDocumentInformation info, XDocument? original)
    {
        byte[] packet;
        try
        {
            packet = Build(info, original);
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException or InvalidOperationException)
        {
            return saved.ToArray();
        }
        return Replace(saved.GetBuffer().AsSpan(0, (int)saved.Length), packet) ?? saved.ToArray();
    }

    internal static byte[] Build(PdfDocumentInformation info, XDocument? original)
    {
        var xmp = original is null
            ? new XDocument(new XElement(Meta + "xmpmeta", new XAttribute(XNamespace.Xmlns + "x", Meta),
                new XElement(Rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", Rdf))))
            : new XDocument(original);
        var rdf = xmp.Descendants(Rdf + "RDF").First();

        var created = TryDate(() => info.CreationDate);
        var modified = TryDate(() => info.ModificationDate) ?? created ?? DateTime.Now;
        Set(rdf, Dc + "title", info.Title, v => Alt(v));
        Set(rdf, Dc + "creator", info.Author, v => new XElement(Rdf + "Seq", new XElement(Rdf + "li", v)));
        Set(rdf, Dc + "description", info.Subject, v => Alt(v));
        Set(rdf, Pdf + "Keywords", info.Keywords);
        Set(rdf, Pdf + "Producer", info.Producer);
        Set(rdf, XmpNs + "CreatorTool", info.Creator);
        if (created is { } c && !Has(rdf, XmpNs + "CreateDate")) Set(rdf, XmpNs + "CreateDate", Format(c));
        Set(rdf, XmpNs + "ModifyDate", Format(modified));
        Set(rdf, XmpNs + "MetadataDate", Format(modified));
        if (!Has(rdf, XmpMM + "DocumentID"))
            Set(rdf, XmpMM + "DocumentID", $"uuid:{Guid.NewGuid()}");
        Set(rdf, XmpMM + "InstanceID", $"uuid:{Guid.NewGuid()}");

        var text = "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n" +
                   xmp.Root!.ToString(SaveOptions.DisableFormatting) + "\n<?xpacket end=\"w\"?>";
        return new UTF8Encoding(false).GetBytes(text);

        static XElement Alt(string v) =>
            new(Rdf + "Alt", new XElement(Rdf + "li", new XAttribute(XNamespace.Xml + "lang", "x-default"), v));
        static string Format(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool Has(XElement rdf, XName name) =>
        rdf.Elements(Rdf + "Description").Any(d => d.Element(name) is not null || d.Attribute(name) is not null);

    /// <summary>Sets a property where the packet keeps it (element or attribute of an rdf:Description); an empty value removes it.</summary>
    private static void Set(XElement rdf, XName name, string? value, Func<string, XElement>? structured = null)
    {
        value = Clean(value);
        var descriptions = rdf.Elements(Rdf + "Description").ToList();
        if (structured is null && !string.IsNullOrEmpty(value) && descriptions.Select(d => d.Attribute(name)).FirstOrDefault(a => a is not null) is { } attribute)
        {
            attribute.Value = value;
            return;
        }
        foreach (var d in descriptions)
        {
            d.Attribute(name)?.Remove();
            d.Elements(name).Remove();
        }
        if (string.IsNullOrEmpty(value)) return;

        var host = descriptions.FirstOrDefault(d =>
                       d.Elements().Any(e => e.Name.Namespace == name.Namespace) || d.Attributes().Any(a => a.Name.Namespace == name.Namespace))
                   ?? descriptions.FirstOrDefault();
        if (host is null)
        {
            host = new XElement(Rdf + "Description", new XAttribute(Rdf + "about", ""));
            rdf.Add(host);
        }
        var prefix = name.Namespace == Dc ? "dc" : name.Namespace == Pdf ? "pdf" : name.Namespace == XmpMM ? "xmpMM" : "xmp";
        if (host.GetPrefixOfNamespace(name.Namespace) is null && host.Attribute(XNamespace.Xmlns + prefix) is null)
            host.Add(new XAttribute(XNamespace.Xmlns + prefix, name.Namespace));
        host.Add(new XElement(name, structured is null ? value : structured(value)));
    }

    /// <summary>Drops characters XML can't carry (PDF strings may hold control characters).</summary>
    private static string Clean(string? s) =>
        string.IsNullOrEmpty(s) ? "" : string.Concat(s.Where((c, i) => XmlConvert.IsXmlChar(c) ||
            (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) ||
            (char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(s[i - 1])))).Trim();

    private static DateTime? TryDate(Func<DateTime> get)
    {
        try
        {
            var d = get();
            return d == DateTime.MinValue ? null : d.Kind == DateTimeKind.Utc ? d.ToLocalTime() : d;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// PDFsharp writes its metadata stream as the last object before the xref table, uncompressed and unencrypted:
    /// swap it for <paramref name="packet"/> and move the xref offset. Nothing before it moves, so the table stays valid.
    /// </summary>
    private static byte[]? Replace(ReadOnlySpan<byte> pdf, byte[] packet)
    {
        var latin1 = Encoding.Latin1;
        var tailStart = Math.Max(0, pdf.Length - 4096);
        var tail = latin1.GetString(pdf[tailStart..]);
        var sx = tail.LastIndexOf("startxref", StringComparison.Ordinal);
        var tr = tail.LastIndexOf("trailer", StringComparison.Ordinal);
        if (sx < 0 || tr < 0 || tail.IndexOf("/Encrypt", tr, StringComparison.Ordinal) >= 0) return null;
        var numberStart = sx + "startxref".Length;
        while (numberStart < tail.Length && char.IsWhiteSpace(tail[numberStart])) numberStart++;
        var numberEnd = numberStart;
        while (numberEnd < tail.Length && char.IsAsciiDigit(tail[numberEnd])) numberEnd++;
        if (!int.TryParse(tail.AsSpan(numberStart, numberEnd - numberStart), out var xref) || xref <= 0 || xref >= pdf.Length) return null;
        if (latin1.GetString(pdf.Slice(xref, Math.Min(4, pdf.Length - xref))) != "xref") return null;

        var windowStart = Math.Max(0, xref - (1 << 20));
        var window = latin1.GetString(pdf[windowStart..xref]);
        var header = System.Text.RegularExpressions.Regex.Match(window,
            @"(?<=\n)(\d+) 0 obj\s*<</Type/Metadata/Subtype/XML/Length \d+>>\s*stream\r?\n", System.Text.RegularExpressions.RegexOptions.RightToLeft);
        if (!header.Success || !window.TrimEnd().EndsWith("endstream\nendobj", StringComparison.Ordinal)) return null;
        if (window.IndexOf("endobj", header.Index, StringComparison.Ordinal) < window.TrimEnd().Length - "endobj".Length) return null;

        var objStart = windowStart + header.Index;
        var head = latin1.GetBytes($"{header.Groups[1].Value} 0 obj\n<</Type/Metadata/Subtype/XML/Length {packet.Length}>>\nstream\n");
        var foot = latin1.GetBytes("\nendstream\nendobj\n");
        var newXref = objStart + head.Length + packet.Length + foot.Length;

        var digits = latin1.GetBytes(newXref.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var tableAndTrailer = pdf[xref..(tailStart + numberStart)];
        var rest = pdf[(tailStart + numberEnd)..];
        var result = new byte[newXref + tableAndTrailer.Length + digits.Length + rest.Length];
        pdf[..objStart].CopyTo(result);
        head.CopyTo(result, objStart);
        packet.CopyTo(result, objStart + head.Length);
        foot.CopyTo(result, objStart + head.Length + packet.Length);
        tableAndTrailer.CopyTo(result.AsSpan(newXref));
        digits.CopyTo(result, newXref + tableAndTrailer.Length);
        rest.CopyTo(result.AsSpan(newXref + tableAndTrailer.Length + digits.Length));
        return result;
    }
}
