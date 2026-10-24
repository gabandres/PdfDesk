using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace InkvoltPdf.Core;

/// <summary>
/// Keeps a tagged PDF's structure tree (/StructTreeRoot) consistent when pages go away (deleted, or not extracted) or
/// lose their content (redacted): marked-content references (MCIDs, MCRs) and object references (OBJR) to those pages
/// are removed, structure elements left without content go too, and the /ParentTree and /IDTree entries that pointed
/// at them are dropped. Nothing in the tree then leads to the removed pages, so saving no longer carries their content
/// streams, fonts and images along.
/// </summary>
internal static class StructTree
{
    private const int MaxDepth = 256;

    /// <summary>The /ParentTree keys a page uses: its /StructParents and its annotations' /StructParent.</summary>
    public static IEnumerable<int> ParentKeys(PdfDictionary page)
    {
        if (PdfObj.Int(page.Elements["/StructParents"]) is { } k) yield return k;
        if (PdfObj.Array(page.Elements["/Annots"]) is { } annots)
            foreach (var a in annots.Elements.Select(PdfObj.Dict).OfType<PdfDictionary>())
                if (PdfObj.Int(a.Elements["/StructParent"]) is { } ak) yield return ak;
    }

    /// <param name="gonePages">Pages whose content no longer exists (still in the document or already removed).</param>
    /// <param name="goneKeys">The <see cref="ParentKeys"/> of those pages, collected before they were changed.</param>
    /// <param name="objectGone">Objects (annotations) that were removed; OBJRs to them are dropped.</param>
    public static void Prune(PdfDocument doc, IEnumerable<PdfDictionary> gonePages, IEnumerable<int> goneKeys, Func<PdfDictionary, bool>? objectGone = null)
    {
        if (PdfObj.Dict(doc.Internals.Catalog.Elements["/StructTreeRoot"]) is not { } root) return;

        var live = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var liveIds = new HashSet<int>();
        foreach (var p in doc.Pages)
        {
            live.Add(p);
            if (p.Reference is { } r) liveIds.Add(r.ObjectNumber);
        }
        var gone = new HashSet<PdfDictionary>(gonePages, ReferenceEqualityComparer.Instance);
        bool PageGone(PdfItem? pg)
        {
            if (PdfObj.Dict(pg) is not { } d) return false;
            if (gone.Contains(d)) return true;
            if (live.Contains(d)) return false;
            return !(pg is PdfReference r && liveIds.Contains(r.ObjectNumber));
        }

        var removed = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);

        // Prunes the kids of a node; true when the node had content and has none left.
        bool PruneKids(PdfDictionary node, PdfItem? page, int depth)
        {
            var k = node.Elements["/K"];
            if (k is null) return false;
            if (PdfObj.Resolve(k) is PdfArray arr)
            {
                if (arr.Elements.Count == 0) return false;
                for (var i = arr.Elements.Count - 1; i >= 0; i--)
                    if (!Keep(arr.Elements[i], page, depth + 1)) arr.Elements.RemoveAt(i);
                if (arr.Elements.Count > 0) return false;
            }
            else if (Keep(k, page, depth + 1)) return false;
            node.Elements.Remove("/K");
            return true;
        }

        bool Keep(PdfItem? item, PdfItem? page, int depth)
        {
            if (depth > MaxDepth) return true;
            switch (PdfObj.Resolve(item))
            {
                case PdfInteger:
                    return !PageGone(page);
                case PdfDictionary d:
                    var type = PdfObj.NameOf(d, "/Type");
                    var pg = d.Elements.ContainsKey("/Pg") ? d.Elements["/Pg"] : page;
                    if (type == "/MCR") return !PageGone(pg);
                    if (type == "/OBJR")
                    {
                        if (PageGone(pg)) return false;
                        if (PdfObj.Dict(d.Elements["/Obj"]) is not { } obj) return false;
                        return !(objectGone?.Invoke(obj) == true || (obj.Elements.ContainsKey("/P") && PageGone(obj.Elements["/P"])));
                    }
                    if (!seen.Add(d)) return !removed.Contains(d);
                    var empty = PruneKids(d, pg, depth);
                    var ownGone = d.Elements.ContainsKey("/Pg") && PageGone(d.Elements["/Pg"]);
                    if (empty || (ownGone && !d.Elements.ContainsKey("/K")))
                    {
                        removed.Add(d);
                        // Still referenced elsewhere (an outline's /SE, a stale parent tree)? Then at least lead nowhere.
                        d.Elements.Remove("/Pg");
                        d.Elements.Remove("/K");
                        return false;
                    }
                    if (ownGone) d.Elements.Remove("/Pg"); // its remaining content names its own pages
                    return true;
                default:
                    return true;
            }
        }

        PruneKids(root, null, 0);

        var keys = goneKeys.ToHashSet();
        EditTree(PdfObj.Dict(root.Elements["/ParentTree"]), "/Nums", (key, value) =>
        {
            if (PdfObj.Int(key) is { } n && keys.Contains(n)) return false;
            switch (PdfObj.Resolve(value))
            {
                case PdfArray arr:
                    var any = false;
                    for (var i = 0; i < arr.Elements.Count; i++)
                    {
                        if (PdfObj.Dict(arr.Elements[i]) is { } e && removed.Contains(e)) arr.Elements[i] = PdfNull.Value;
                        else if (PdfObj.Resolve(arr.Elements[i]) is not PdfNull) any = true;
                    }
                    return any;
                case PdfDictionary d:
                    return !removed.Contains(d);
                default:
                    return true;
            }
        });
        EditTree(PdfObj.Dict(root.Elements["/IDTree"]), "/Names", (_, value) => PdfObj.Dict(value) is not { } d || !removed.Contains(d));
    }

    /// <summary>Removes the key/value pairs of a name or number tree that <paramref name="keep"/> rejects.</summary>
    private static void EditTree(PdfDictionary? node, string pairsKey, Func<PdfItem, PdfItem?, bool> keep, int depth = 0)
    {
        if (node is null || depth > 64) return;
        if (PdfObj.Array(node.Elements[pairsKey]) is { } pairs)
            for (var i = (pairs.Elements.Count & ~1) - 2; i >= 0; i -= 2)
                if (!keep(pairs.Elements[i], pairs.Elements[i + 1]))
                {
                    pairs.Elements.RemoveAt(i + 1);
                    pairs.Elements.RemoveAt(i);
                }
        if (PdfObj.Array(node.Elements["/Kids"]) is { } kids)
            foreach (var kid in kids.Elements.Select(PdfObj.Dict).OfType<PdfDictionary>()) EditTree(kid, pairsKey, keep, depth + 1);
    }
}
