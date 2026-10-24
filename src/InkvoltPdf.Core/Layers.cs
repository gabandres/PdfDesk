using System.Runtime.CompilerServices;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace InkvoltPdf.Core;

/// <summary>An optional-content group (layer).</summary>
/// <param name="Id">The group's object number: the key for <see cref="Layers.WithVisibility"/>.</param>
/// <param name="DefaultVisible">Visible in the default configuration (/OCProperties /D: /BaseState, /ON, /OFF).</param>
/// <param name="Locked">Listed in /D /Locked: the author doesn't want users toggling it.</param>
public sealed record LayerInfo(int Id, string Name, bool DefaultVisible, bool Locked);

/// <summary>A node of the layer panel tree (/D /Order): a layer (with optional sub-layers) or a labeled group.</summary>
public sealed record LayerOrderNode(string? Label, int? LayerId, IReadOnlyList<LayerOrderNode> Children);

/// <summary>Optional content (layers): list them and produce copies with chosen layers shown or hidden.</summary>
public static class Layers
{
    public static IReadOnlyList<LayerInfo> List(byte[] data, string? password)
    {
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Import, "This PDF's layers can't be read");
        return ListIn(doc);
    }

    private static List<LayerInfo> ListIn(PdfDocument doc)
    {
        var props = PdfObj.Dict(doc.Internals.Catalog.Elements["/OCProperties"]);
        if (PdfObj.Array(props?.Elements["/OCGs"]) is not { } ocgs) return [];
        var d = PdfObj.Dict(props!.Elements["/D"]);
        var on = Ids(d?.Elements["/ON"]);
        var off = Ids(d?.Elements["/OFF"]);
        var locked = Ids(d?.Elements["/Locked"]);
        var baseOff = PdfObj.Name(d?.Elements["/BaseState"]) == "OFF";
        var list = new List<LayerInfo>();
        foreach (var item in ocgs.Elements)
        {
            if (item is not PdfReference r || PdfObj.Dict(r) is not { } ocg) continue;
            var id = r.ObjectNumber;
            if (list.Any(l => l.Id == id)) continue;
            var visible = off.Contains(id) ? false : on.Contains(id) || !baseOff;
            list.Add(new LayerInfo(id, PdfObj.Str(ocg.Elements["/Name"]) ?? $"Layer {id}", visible, locked.Contains(id)));
        }
        return list;
    }

    /// <summary>The layer panel tree from /D /Order (layers in the order the author arranged them); flat when absent.</summary>
    public static IReadOnlyList<LayerOrderNode> ReadOrder(byte[] data, string? password)
    {
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Import, "This PDF's layers can't be read");
        var props = PdfObj.Dict(doc.Internals.Catalog.Elements["/OCProperties"]);
        var known = ListIn(doc).Select(l => l.Id).ToHashSet();
        if (PdfObj.Array(PdfObj.Dict(props?.Elements["/D"])?.Elements["/Order"]) is not { } order)
            return [.. known.Select(id => new LayerOrderNode(null, id, []))];
        return Parse(order, known, 0, new StrongBox<int>(10_000));
    }

    /// <param name="budget">Elements left to visit: an /Order whose arrays repeat or contain themselves would otherwise
    /// take exponential time.</param>
    private static List<LayerOrderNode> Parse(PdfArray arr, HashSet<int> known, int depth, StrongBox<int> budget)
    {
        var nodes = new List<LayerOrderNode>();
        if (depth > 32) return nodes;
        foreach (var e in arr.Elements)
        {
            if (--budget.Value < 0) break;
            if (e is PdfReference r && known.Contains(r.ObjectNumber))
                nodes.Add(new LayerOrderNode(null, r.ObjectNumber, []));
            else if (PdfObj.Array(e) is { } sub)
            {
                var label = sub.Elements.Count > 0 ? PdfObj.Str(sub.Elements[0]) : null;
                if (label is not null)
                {
                    var rest = new PdfArray();
                    foreach (var x in sub.Elements.Skip(1)) rest.Elements.Add(x);
                    nodes.Add(new LayerOrderNode(label, null, Parse(rest, known, depth + 1, budget)));
                }
                else
                {
                    // An array right after a layer holds that layer's children.
                    var children = Parse(sub, known, depth + 1, budget);
                    if (nodes.Count > 0 && nodes[^1].LayerId is not null && nodes[^1].Children.Count == 0)
                        nodes[^1] = nodes[^1] with { Children = children };
                    else nodes.Add(new LayerOrderNode(null, null, children));
                }
            }
        }
        return nodes;
    }

    /// <summary>
    /// A copy of the document whose default layer configuration shows/hides the given layers (others keep their default),
    /// for viewing and printing. Usage-based automatic states (/AS) are dropped so the choice is what renders.
    /// </summary>
    public static byte[] WithVisibility(byte[] data, string? password, IReadOnlyDictionary<int, bool> visibility) =>
        Apply(data, password, visibility, forViewing: true);

    /// <summary>Saves the given layer states as the document's default (what other viewers show when opening it).</summary>
    public static byte[] SetDefaultVisibility(byte[] data, string? password, IReadOnlyDictionary<int, bool> visibility) =>
        Apply(data, password, visibility, forViewing: false);

    private static byte[] Apply(byte[] data, string? password, IReadOnlyDictionary<int, bool> visibility, bool forViewing)
    {
        // Layer ids are object numbers as read (List opens for reading); PDFsharp renumbers objects when it opens for
        // editing, so translate them by position in /OCGs.
        List<int> fileIds;
        using (var view = PdfObj.Open(data, password, PdfDocumentOpenMode.Import, "This PDF can't be edited")) fileIds = OcgNumbers(view);
        using var doc = PdfObj.Open(data, password, PdfDocumentOpenMode.Modify, "This PDF can't be edited");
        var props = PdfObj.Dict(doc.Internals.Catalog.Elements["/OCProperties"]);
        if (PdfObj.Array(props?.Elements["/OCGs"]) is not { } ocgs) return data;
        var editIds = OcgNumbers(doc);
        if (editIds.Count == fileIds.Count)
        {
            var translated = new Dictionary<int, bool>();
            for (var i = 0; i < fileIds.Count; i++)
                if (visibility.TryGetValue(fileIds[i], out var v)) translated[editIds[i]] = v;
            visibility = translated;
        }
        var current = ListIn(doc).ToDictionary(l => l.Id, l => l.DefaultVisible);
        var d = PdfObj.Dict(props!.Elements["/D"]);
        if (d is null)
        {
            d = new PdfDictionary(doc);
            props.Elements["/D"] = d;
        }
        var on = new PdfArray(doc);
        var off = new PdfArray(doc);
        foreach (var item in ocgs.Elements)
        {
            if (item is not PdfReference r || !current.TryGetValue(r.ObjectNumber, out var visible)) continue;
            if (visibility.TryGetValue(r.ObjectNumber, out var wanted)) visible = wanted;
            if (on.Elements.Concat(off.Elements).OfType<PdfReference>().Any(x => x.ObjectNumber == r.ObjectNumber)) continue;
            (visible ? on : off).Elements.Add(r);
        }
        d.Elements["/BaseState"] = new PdfName("/ON");
        d.Elements["/ON"] = on;
        d.Elements["/OFF"] = off;
        if (forViewing) d.Elements.Remove("/AS");
        return PdfObj.Save(doc);
    }

    private static List<int> OcgNumbers(PdfDocument doc) =>
        PdfObj.Array(PdfObj.Dict(doc.Internals.Catalog.Elements["/OCProperties"])?.Elements["/OCGs"]) is { } a
            ? [.. a.Elements.Select(e => e is PdfReference r ? r.ObjectNumber : -1)]
            : [];

    private static HashSet<int> Ids(PdfItem? item) =>
        PdfObj.Array(item) is { } a ? [.. a.Elements.OfType<PdfReference>().Select(r => r.ObjectNumber)] : [];
}
