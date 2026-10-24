using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace InkvoltPdf.Core;

/// <summary>
/// An editable bookmark (outline item). Build new ones with the constructor; edit read ones with <c>with</c>
/// expressions, which keep the link to the bookmark in the file (so an unchanged destination keeps its exact view and
/// action, e.g. a web link).
/// </summary>
/// <param name="PageIndex">The page it leads to, or -1: no page (a heading, or an action other than going to a page).</param>
/// <param name="Top">Where on the page (page space: points from the displayed page's top); null = the page's top as the viewer likes.</param>
public sealed record Bookmark(string Title, int PageIndex = -1, double? Top = null)
{
    public IReadOnlyList<Bookmark> Children { get; init; } = [];

    /// <summary>Children shown expanded.</summary>
    public bool Open { get; init; }

    public bool Bold { get; init; }
    public bool Italic { get; init; }

    /// <summary>Title color; null = the viewer's default (black).</summary>
    public AnnotColor? Color { get; init; }

    /// <summary>
    /// Read only: the action when it isn't a jump to a page in this document ("URI", "Launch", "GoToR", "Named",
    /// "JavaScript", ...); kept when <see cref="PageIndex"/> and <see cref="Top"/> stay as read.
    /// </summary>
    public string? ActionKind { get; init; }

    /// <summary>Read only: the web address of a URI action.</summary>
    public string? Uri { get; init; }

    /// <summary>The bookmark this was read from (1-based, in reading order; 0 = new). Valid for the bytes it was read from.</summary>
    internal int SourceId { get; init; }
}

/// <summary>
/// Reads and rewrites the document's bookmarks: add, rename, delete, reorder, nest, restyle, retarget. A signed
/// document gets the change as an incremental update (bookmarks are outside what signatures lock).
/// </summary>
public static class Bookmarks
{
    private const int FlagItalic = 1, FlagBold = 2;

    /// <summary>The bookmarks as a tree (empty when there are none).</summary>
    public static IReadOnlyList<Bookmark> Read(byte[] data, string? password)
    {
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Import);
        var id = 0;
        return Convert(PdfStructure.ReadOutline(doc));

        List<Bookmark> Convert(List<PdfStructure.OutlineItem> items)
        {
            var list = new List<Bookmark>();
            foreach (var item in items)
            {
                var sourceId = ++id;
                var d = item.Dict;
                var (page, top) = Target(doc, d);
                var action = PdfObj.Dict(d.Elements["/A"]);
                var kind = action is null || page >= 0 ? null : PdfObj.Name(action.Elements["/S"]);
                var flags = PdfObj.Int(d.Elements["/F"]) ?? 0;
                list.Add(new Bookmark(PdfObj.Str(d.Elements["/Title"]) ?? "", page, top)
                {
                    SourceId = sourceId,
                    Children = Convert(item.Children),
                    Open = item.Open,
                    Bold = (flags & FlagBold) != 0,
                    Italic = (flags & FlagItalic) != 0,
                    Color = ColorOf(d),
                    ActionKind = kind,
                    Uri = kind == "URI" ? PdfObj.Str(action!.Elements["/URI"]) : null,
                });
            }
            return list;
        }
    }

    /// <summary>
    /// Makes <paramref name="outline"/> the document's bookmarks (none: the outline is removed). Bookmarks read by
    /// <see cref="Read"/> from these bytes keep their dictionary (and whatever else it holds) when they come back;
    /// the destination is rewritten only when <see cref="Bookmark.PageIndex"/> or <see cref="Bookmark.Top"/> changed.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A bookmark leads to a page the document doesn't have.</exception>
    public static byte[] SetOutline(byte[] data, string? password, IReadOnlyList<Bookmark> outline)
    {
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Modify, "This PDF can't be edited");
        var existing = new List<PdfDictionary>();
        Flatten(PdfStructure.ReadOutline(doc));
        var used = new HashSet<int>();
        var items = Build(outline, 0);
        if (used.Count < existing.Count) SignedEdits.NoteRemoval();
        PdfStructure.WriteOutline(doc, items);
        return PdfObj.Save(doc);

        void Flatten(List<PdfStructure.OutlineItem> items)
        {
            foreach (var item in items)
            {
                existing.Add(item.Dict);
                Flatten(item.Children);
            }
        }

        List<PdfStructure.OutlineItem> Build(IReadOnlyList<Bookmark> nodes, int depth)
        {
            if (depth > 64) throw new ArgumentException("Bookmarks are nested too deeply.", nameof(outline));
            var list = new List<PdfStructure.OutlineItem>();
            foreach (var node in nodes)
            {
                if (node.PageIndex >= doc.PageCount || node.PageIndex < -1)
                    throw new ArgumentOutOfRangeException(nameof(outline), $"Bookmark \"{node.Title}\" leads to page {node.PageIndex + 1}, which the document doesn't have.");
                var reuse = node.SourceId > 0 && node.SourceId <= existing.Count && used.Add(node.SourceId);
                var d = reuse ? existing[node.SourceId - 1] : new PdfDictionary(doc);
                if (!reuse) doc.Internals.AddObject(d);
                var title = PdfObj.MakeString(node.Title);
                if (PdfObj.Str(d.Elements["/Title"]) != node.Title) d.Elements["/Title"] = title;

                var (page, top) = reuse ? Target(doc, d) : (-1, null);
                if (!reuse || page != node.PageIndex || !Near(top, node.Top))
                {
                    d.Elements.Remove("/A");
                    d.Elements.Remove("/SE");
                    if (node.PageIndex >= 0) d.Elements["/Dest"] = Destination(doc, node.PageIndex, node.Top);
                    else d.Elements.Remove("/Dest");
                }

                var flags = (PdfObj.Int(d.Elements["/F"]) ?? 0) & ~(FlagBold | FlagItalic);
                flags |= (node.Bold ? FlagBold : 0) | (node.Italic ? FlagItalic : 0);
                if (flags != (PdfObj.Int(d.Elements["/F"]) ?? 0)) d.Elements["/F"] = new PdfInteger(flags);
                if (flags == 0) d.Elements.Remove("/F");

                if (ColorOf(d) != node.Color)
                {
                    if (node.Color is { } c && c != new AnnotColor(0, 0, 0)) d.Elements["/C"] = PdfObj.Reals(doc, c.R, c.G, c.B);
                    else d.Elements.Remove("/C");
                }

                var item = new PdfStructure.OutlineItem(d, node.Open);
                item.Children.AddRange(Build(node.Children, depth + 1));
                list.Add(item);
            }
            return list;
        }
    }

    private static bool Near(double? a, double? b) => a is null ? b is null : b is not null && Math.Abs(a.Value - b.Value) < 0.5;

    /// <summary>The page (and top, page space) an outline item leads to: its /Dest, or a GoTo action's; -1 when neither.</summary>
    private static (int Page, double? Top) Target(PdfDocument doc, PdfDictionary item)
    {
        var dest = item.Elements["/Dest"];
        if (dest is null && PdfObj.Dict(item.Elements["/A"]) is { } a && PdfObj.Name(a.Elements["/S"]) == "GoTo") dest = a.Elements["/D"];
        if (dest is null || DocumentFeatures.ResolveDestination(doc, dest) is not { } d) return (-1, null);
        return (d.PageIndex, d.Y);
    }

    private static AnnotColor? ColorOf(PdfDictionary item) =>
        PdfObj.Array(item.Elements["/C"]) is { Elements.Count: 3 } c ? new AnnotColor(PdfObj.Num(c, 0), PdfObj.Num(c, 1), PdfObj.Num(c, 2)) : null;

    /// <summary>[page /XYZ left top null]: a position on the page (top, in page space), keeping the zoom.</summary>
    private static PdfArray Destination(PdfDocument doc, int pageIndex, double? top)
    {
        var page = doc.Pages[pageIndex];
        var a = new PdfArray(doc);
        a.Elements.Add(page.ReferenceNotNull);
        a.Elements.Add(new PdfName("/XYZ"));
        PdfItem left = PdfNull.Value, y = PdfNull.Value;
        if (top is { } t)
        {
            // Page space runs down the displayed page; on a page shown sideways its "down" is the PDF's x axis.
            var (ux, uy) = PdfObj.ToPdf(page, 0, t);
            if (((page.Rotate % 360) + 360) % 360 is 90 or 270) left = new PdfReal(Math.Round(ux, 2));
            else y = new PdfReal(Math.Round(uy, 2));
        }
        a.Elements.Add(left);
        a.Elements.Add(y);
        a.Elements.Add(PdfNull.Value);
        return a;
    }
}
