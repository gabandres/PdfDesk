using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace InkvoltPdf.Core;

/// <summary>
/// Saves edits to a signed PDF as an incremental update, so its signatures keep verifying: the original bytes stay
/// untouched and only the changed and new objects follow, with their own xref section (a classic table or an xref
/// stream, matching the original) and a trailer whose /Prev points at the original's. The signatures then report
/// <see cref="SignatureStatus.ModifiedAfterSigning"/> (intact, later changes appended) instead of breaking.
/// <para>
/// PDFsharp renumbers every object when it opens a document for editing, so <see cref="Track"/> (called by
/// <see cref="PdfObj.Open"/> right after a Modify-mode open, before any edit) opens the original a second time without
/// renumbering and pairs the objects structurally, starting from the catalog. A pair is kept only when both objects
/// serialize identically through the pairing (pages and page tree nodes apart from what PDFsharp's page tree flattening
/// changes; the Info dictionary always). It also hashes every object as it is before the edit. <see cref="TrySave"/>
/// then writes the objects the edit changed or added under their original numbers (new ones, and any unpaired object a
/// written one needs, under new numbers past the original /Size, along with their referrers).
/// </para>
/// Not incremental (full rewrite, which breaks the signatures): encrypted documents, redaction (it must remove data,
/// <see cref="PdfObj.Save(PdfDocument, bool)"/> with rewrite), files whose last xref can't be located, and anything
/// that fails here. A signed document is never rewritten silently: <see cref="Save"/> throws
/// <see cref="SignaturesWouldBreakException"/> instead, unless the edit runs in <see cref="SignedEdits.Run"/> with the
/// rewrite allowed (the user accepted the warning).
/// </summary>
internal static class Incremental
{
    private sealed class Base
    {
        public required byte[] Original;
        /// <summary>Edited document's object id → the original's (number, generation).</summary>
        public required Dictionary<PdfObjectID, (int Num, int Gen)> Map;
        /// <summary>Edited document's object id → SHA-256 of the object as it was before the edit.</summary>
        public required Dictionary<PdfObjectID, byte[]> Snapshot;
        /// <summary>Pages whose /Parent PDFsharp changed while flattening the page tree → their original /Parent.</summary>
        public required Dictionary<PdfObjectID, (int Num, int Gen)> Parents;
        public required int Size;
        public required long StartXref;
        public required bool XrefStream;
        public required string? Id;
    }

    private static readonly ConditionalWeakTable<PdfDocument, Base> Tracked = [];

    /// <summary>Signed documents opened for editing → why they can't be appended to (empty while nothing failed).</summary>
    private static readonly ConditionalWeakTable<PdfDocument, StrongBox<string>> Signed = [];

    /// <summary>Signed-looking files carry a /ByteRange; signature dictionaries are never compressed into object streams.</summary>
    public static bool LooksSigned(byte[] data) => data.AsSpan().IndexOf("/ByteRange"u8) >= 0;

    /// <summary>True when <see cref="TrySave"/> can write <paramref name="doc"/> as an incremental update.</summary>
    public static bool IsTracked(PdfDocument doc) => Tracked.TryGetValue(doc, out _);

    /// <summary>
    /// Remembers how <paramref name="doc"/> (just opened in Modify mode) maps onto <paramref name="data"/>, when it is
    /// signed (or always, with <paramref name="force"/>: signing appends even to an unsigned file, so later signatures
    /// never break earlier ones).
    /// </summary>
    public static void Track(PdfDocument doc, byte[] data, string? password, bool force = false)
    {
        var signed = LooksSigned(data);
        var why = new StrongBox<string>("");
        if (signed) Signed.AddOrUpdate(doc, why);
        if (!force && !signed) return;
        if (data.AsSpan().IndexOf("/Encrypt"u8) >= 0)
        {
            why.Value = "it is encrypted";
            return;
        }
        try
        {
            if (Tail(data) is not { } tail)
            {
                why.Value = "its last cross-reference section can't be found";
                return;
            }
            using var orig = string.IsNullOrEmpty(password)
                ? PdfReader.Open(new MemoryStream(data, writable: false), PdfDocumentOpenMode.Import)
                : PdfReader.Open(new MemoryStream(data, writable: false), password, PdfDocumentOpenMode.Import);
            var pairs = Pair(orig, doc, tail.HasInfo);
            if (pairs is null)
            {
                why.Value = "its objects can't be matched up";
                return;
            }

            // Keep only pairs whose objects match through the pairing (pages and page tree nodes: apart from what
            // PDFsharp's page tree flattening changes); dropping one changes how its referrers serialize, so repeat
            // until nothing changes. The Info pair is certain (both trailers name it) and PDFsharp always touches it.
            var streamHash = new Dictionary<PdfDictionary, byte[]>(ReferenceEqualityComparer.Instance);
            var map = pairs.ToDictionary(p => p.Key, p => (p.Value.ObjectNumber, p.Value.GenerationNumber));
            var info = tail.HasInfo ? doc.Info.Reference?.ObjectID : null;
            var skips = new Dictionary<PdfObjectID, HashSet<string>?>();
            var origBytes = new Dictionary<PdfObjectID, byte[]>();
            foreach (var (m, o) in pairs)
            {
                if (orig.Internals.GetObject(o) is not { } theirs || doc.Internals.GetObject(m) is not { } mine) continue;
                skips[m] = Lenient(theirs, mine);
                origBytes[o] = Serialize(theirs, r => (r.ObjectNumber, r.GenerationNumber), streamHash, skips[m]);
            }
            bool changed;
            do
            {
                changed = false;
                foreach (var (m, o) in pairs)
                {
                    if (!map.ContainsKey(m) || m == info) continue;
                    var mine = doc.Internals.GetObject(m);
                    if (mine is null || !origBytes.TryGetValue(o, out var theirs) ||
                        !Serialize(mine, r => map.TryGetValue(r, out var t) ? t : null, streamHash, skips[m]).AsSpan().SequenceEqual(theirs))
                    {
                        map.Remove(m);
                        changed = true;
                    }
                }
            } while (changed);
            if (map.Count == 0)
            {
                why.Value = "its objects can't be matched up";
                return;
            }

            // Flattened pages point at the root /Pages now; written back alone, they keep their original /Parent.
            var parents = new Dictionary<PdfObjectID, (int, int)>();
            foreach (var (m, o) in pairs)
                if (map.ContainsKey(m) && doc.Internals.GetObject(m) is PdfDictionary page && PdfObj.NameOf(page, "/Type") == "/Page" &&
                    (orig.Internals.GetObject(o) as PdfDictionary)?.Elements["/Parent"] is PdfReference op &&
                    !(page.Elements["/Parent"] is PdfReference mp && map.TryGetValue(mp.ObjectID, out var mt) && mt == (op.ObjectNumber, op.GenerationNumber)))
                    parents[m] = (op.ObjectNumber, op.GenerationNumber);

            // What the document looked like before the edit (in PDFsharp's numbering), to find what the edit changed.
            var snapshot = new Dictionary<PdfObjectID, byte[]>();
            var own = new Dictionary<PdfDictionary, byte[]>(ReferenceEqualityComparer.Instance);
            foreach (var obj in Reachable(doc, [doc.Internals.Catalog, doc.Info]))
                snapshot[obj.Reference!.ObjectID] = SHA256.HashData(Serialize(obj, r => (r.ObjectNumber, r.GenerationNumber), own));

            var maxNum = orig.Internals.GetAllObjects().Select(o => o.Reference?.ObjectNumber ?? 0).DefaultIfEmpty(0).Max();
            Tracked.AddOrUpdate(doc, new Base
            {
                Original = data,
                Map = map,
                Snapshot = snapshot,
                Parents = parents,
                Size = Math.Max(tail.Size, maxNum + 1),
                StartXref = tail.StartXref,
                XrefStream = tail.XrefStream,
                Id = tail.Id,
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not trackable: the edit can only be saved by rewriting the file.
            why.Value = $"its structure can't be extended ({ex.Message})";
        }
    }

    /// <summary>
    /// Saves an edit of a document opened through <see cref="PdfObj.Open"/>: the incremental update when it is a tracked
    /// signed document; null when the caller may rewrite the file (it isn't signed, or <see cref="SignedEdits.Run"/>
    /// allowed the rewrite).
    /// </summary>
    /// <exception cref="SignaturesWouldBreakException">A signed document whose edit can't be appended.</exception>
    public static byte[]? Save(PdfDocument doc, XDocument? xmp)
    {
        if (TrySave(doc, xmp) is { } appended)
        {
            SignedEdits.NoteAppended();
            return appended;
        }
        if (!Signed.TryGetValue(doc, out var why) || SignedEdits.RewriteAllowed) return null;
        throw new SignaturesWouldBreakException(CountSignatures(doc), why.Value is { Length: > 0 } reason ? reason : "the update can't be written");
    }

    /// <summary>Signature dictionaries (a /ByteRange and /Contents) in the document, at least 1.</summary>
    internal static int CountSignatures(PdfDocument doc)
    {
        try
        {
            return Math.Max(1, doc.Internals.GetAllObjects().Count(o => o is PdfDictionary d && d.Elements.ContainsKey("/ByteRange") && d.Elements.ContainsKey("/Contents")));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return 1;
        }
    }

    /// <summary>
    /// The original bytes followed by an incremental update holding the edits, or null when <paramref name="doc"/> isn't
    /// a tracked signed document (or the update can't be written).
    /// </summary>
    /// <param name="xmp">The XMP packet before the edit: written back updated from the Info values, as a full save does.</param>
    public static byte[]? TrySave(PdfDocument doc, XDocument? xmp)
    {
        if (!Tracked.TryGetValue(doc, out var b)) return null;
        try
        {
            if (xmp is not null && PdfObj.Dict(doc.Internals.Catalog.Elements["/Metadata"]) is { Stream: not null } meta)
            {
                meta.Stream.Value = Xmp.Build(doc.Info, xmp);
                meta.Elements.Remove("/Filter");
                meta.Elements.Remove("/DecodeParms");
                meta.Elements["/Length"] = new PdfInteger(meta.Stream.Value.Length);
            }
            return Write(doc, b);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (Signed.TryGetValue(doc, out var why)) why.Value = $"the update can't be written ({ex.Message})";
            return null;
        }
    }

    private static byte[] Write(PdfDocument doc, Base b)
    {
        // Fresh numbers for new objects start past everything the original's xref may name.
        var next = b.Size;
        var fresh = new Dictionary<PdfObjectID, int>();
        (int, int)? Id(PdfObjectID r)
        {
            if (b.Map.TryGetValue(r, out var t)) return t;
            if (doc.Internals.GetObject(r) is null) return null;
            if (!fresh.TryGetValue(r, out var n)) fresh[r] = n = next++;
            return (n, 0);
        }

        // What the edit changed or added (the Info dictionary always: its dates), compared in PDFsharp's numbering.
        var streamHash = new Dictionary<PdfDictionary, byte[]>(ReferenceEqualityComparer.Instance);
        var info = doc.Info;
        var catalog = doc.Internals.Catalog;
        var reachable = Reachable(doc, [catalog, info]);
        var objects = reachable.ToDictionary(o => o.Reference!.ObjectID);
        var dirty = new HashSet<PdfObjectID>();
        foreach (var obj in reachable)
        {
            var oid = obj.Reference!.ObjectID;
            if (ReferenceEquals(obj, info) || !b.Snapshot.TryGetValue(oid, out var h) ||
                !SHA256.HashData(Serialize(obj, r => (r.ObjectNumber, r.GenerationNumber), streamHash)).AsSpan().SequenceEqual(h))
                dirty.Add(oid);
        }

        // An object without an original counterpart gets a new number: everything referring to it is written too
        // (else the file's unchanged referrers would still show the original), and so is every such object a written
        // one refers to (else the number would lead nowhere). The trailer refers to the catalog.
        var referrers = new Dictionary<PdfObjectID, List<PdfObjectID>>();
        foreach (var obj in reachable)
            foreach (var r in Refs(obj))
                if (objects.ContainsKey(r))
                {
                    if (!referrers.TryGetValue(r, out var list)) referrers[r] = list = [];
                    list.Add(obj.Reference!.ObjectID);
                }
        var catalogId = catalog.Reference!.ObjectID;
        if (!b.Map.ContainsKey(catalogId)) dirty.Add(catalogId);
        // A rewritten (flattened) page tree node: its pages may have inherited attributes from the intermediate nodes it
        // no longer lists, so they are written too (PDFsharp copied those attributes onto them).
        foreach (var oid in dirty.ToList())
            if (objects[oid] is PdfDictionary node && PdfObj.NameOf(node, "/Type") == "/Pages" && PdfObj.Array(node.Elements["/Kids"]) is { } kids)
                foreach (var kid in kids.Elements.OfType<PdfReference>())
                    if (b.Parents.ContainsKey(kid.ObjectID) && objects.ContainsKey(kid.ObjectID)) dirty.Add(kid.ObjectID);
        var queue = new Queue<PdfObjectID>(dirty);
        while (queue.Count > 0)
        {
            var oid = queue.Dequeue();
            foreach (var r in Refs(objects[oid]))
                if (objects.ContainsKey(r) && !b.Map.ContainsKey(r) && dirty.Add(r)) queue.Enqueue(r);
            if (!b.Map.ContainsKey(oid) && referrers.TryGetValue(oid, out var from))
                foreach (var f in from)
                    if (dirty.Add(f)) queue.Enqueue(f);
        }

        var write = new List<(int Num, int Gen, PdfObject Obj, (string, string)? Replace)>();
        foreach (var oid in dirty)
        {
            if (Id(oid) is not { } id) continue;
            var obj = objects[oid];
            (string, string)? replace = null;
            // A flattened page written back alone keeps its original /Parent (its new parent isn't written).
            if (b.Parents.TryGetValue(oid, out var parent) && obj is PdfDictionary { } page &&
                page.Elements["/Parent"] is PdfReference pr && !dirty.Contains(pr.ObjectID))
                replace = ("/Parent", $"{parent.Num} {parent.Gen} R");
            write.Add((id.Item1, id.Item2, obj, replace));
        }

        // Self-check (cheaper than reading the result back): every page and the page tree root must lead somewhere.
        if (Id(catalog.Reference!.ObjectID) is null || PdfObj.Dict(catalog.Elements["/Pages"])?.Reference is not { } pagesRoot || Id(pagesRoot.ObjectID) is null ||
            Enumerable.Range(0, doc.PageCount).Any(p => doc.Pages[p].Reference is not { } pr || Id(pr.ObjectID) is null))
            throw new InvalidOperationException("The page tree doesn't line up with the original.");

        // The update is written on its own and joined to the original once, so the file is copied a single time.
        var at = b.Original.Length + (b.Original.Length > 0 && b.Original[^1] is not ((byte)'\n' or (byte)'\r') ? 1 : 0);
        using var ms = new MemoryStream();
        var offsets = new SortedDictionary<int, (long Offset, int Gen)>();
        foreach (var (num, gen, obj, replace) in write.OrderBy(w => w.Num))
        {
            offsets[num] = (at + ms.Position, gen);
            Ascii(ms, $"{num} {gen} obj\n");
            ms.Write(Serialize(obj, Id, null, null, replace));
            Ascii(ms, "\nendobj\n");
        }

        var root = Id(doc.Internals.Catalog.Reference!.ObjectID)!.Value;
        var infoRef = info.Reference is { } ir && Id(ir.ObjectID) is { } i ? $" /Info {i.Item1} {i.Item2} R" : "";
        var trailerKeys = $"/Root {root.Item1} {root.Item2} R{infoRef}{(b.Id is null ? "" : $" /ID {b.Id}")} /Prev {b.StartXref}";
        var xrefAt = at + ms.Position;
        if (b.XrefStream)
        {
            var self = next++;
            offsets[self] = (xrefAt, 0);
            var size = Math.Max(b.Size, offsets.Keys.Max() + 1);
            var w = xrefAt > uint.MaxValue ? 8 : 4;
            var rows = new MemoryStream();
            foreach (var (_, (off, gen)) in offsets)
            {
                rows.WriteByte(1);
                for (var k = w - 1; k >= 0; k--) rows.WriteByte((byte)(off >> (8 * k)));
                rows.WriteByte((byte)(gen >> 8));
                rows.WriteByte((byte)gen);
            }
            Ascii(ms, $"{self} 0 obj\n<< /Type /XRef /Size {size} /Index [{string.Join(' ', Runs(offsets.Keys).Select(r => $"{r.Start} {r.Count}"))}] /W [1 {w} 2] {trailerKeys} /Length {rows.Length} >>\nstream\n");
            ms.Write(rows.ToArray());
            Ascii(ms, "\nendstream\nendobj\n");
        }
        else
        {
            var size = Math.Max(b.Size, offsets.Count == 0 ? 0 : offsets.Keys.Max() + 1);
            var sb = new StringBuilder("xref\n");
            foreach (var (start, count) in Runs(offsets.Keys))
            {
                sb.Append(PdfObj.Inv, $"{start} {count}\n");
                for (var n = start; n < start + count; n++) sb.Append(PdfObj.Inv, $"{offsets[n].Offset:D10} {offsets[n].Gen:D5} n \n");
            }
            sb.Append(PdfObj.Inv, $"trailer\n<< /Size {size} {trailerKeys} >>\n");
            Ascii(ms, sb.ToString());
        }
        Ascii(ms, $"startxref\n{xrefAt}\n%%EOF\n");
        var result = new byte[at + ms.Length];
        b.Original.CopyTo(result, 0);
        if (at > b.Original.Length) result[b.Original.Length] = (byte)'\n';
        ms.GetBuffer().AsSpan(0, (int)ms.Length).CopyTo(result.AsSpan(at));
        return result;
    }

    private static IEnumerable<(int Start, int Count)> Runs(IEnumerable<int> sorted)
    {
        int start = -1, count = 0;
        foreach (var n in sorted)
        {
            if (count > 0 && n == start + count)
            {
                count++;
                continue;
            }
            if (count > 0) yield return (start, count);
            start = n;
            count = 1;
        }
        if (count > 0) yield return (start, count);
    }

    private static void Ascii(Stream s, string text) => s.Write(Encoding.Latin1.GetBytes(text));

    // ---- Pairing ----

    /// <summary>
    /// Pairs the edited document's objects (renumbered by PDFsharp) with the original's, walking both graphs side by side
    /// from the catalog (and the Info dictionary when the original has one), breadth first so the natural paths come first.
    /// Null when the documents don't line up at all.
    /// </summary>
    private static Dictionary<PdfObjectID, PdfObjectID>? Pair(PdfDocument orig, PdfDocument doc, bool hasInfo)
    {
        var map = new Dictionary<PdfObjectID, PdfObjectID>();
        var taken = new HashSet<PdfObjectID>();
        var queue = new Queue<(PdfItem? A, PdfItem? B)>();
        if (orig.Internals.Catalog.Reference is not { } oc || doc.Internals.Catalog.Reference is not { } dc) return null;
        queue.Enqueue((oc, dc));
        if (hasInfo && orig.Info.Reference is { } oi && doc.Info.Reference is { } di) queue.Enqueue((oi, di));
        while (queue.Count > 0)
        {
            var (a, b) = queue.Dequeue();
            switch (a, b)
            {
                case (PdfReference ra, PdfReference rb):
                    if (map.ContainsKey(rb.ObjectID) || taken.Contains(ra.ObjectID)) break;
                    if (!Compatible(ra.Value, rb.Value)) break;
                    map[rb.ObjectID] = ra.ObjectID;
                    taken.Add(ra.ObjectID);
                    queue.Enqueue((ra.Value, rb.Value));
                    break;
                case (PdfReference, _) or (_, PdfReference):
                    break;
                case (PdfDictionary da, PdfDictionary db):
                    foreach (var key in da.Elements.Keys)
                        if (db.Elements.ContainsKey(key)) queue.Enqueue((da.Elements[key], db.Elements[key]));
                    break;
                case (PdfArray aa, PdfArray ab) when aa.Elements.Count == ab.Elements.Count:
                    for (var i = 0; i < aa.Elements.Count; i++) queue.Enqueue((aa.Elements[i], ab.Elements[i]));
                    break;
            }
        }
        return map.Count == 0 ? null : map;
    }

    private static bool Compatible(PdfObject? a, PdfObject? b) => (a, b) switch
    {
        (PdfDictionary da, PdfDictionary db) => (da.Stream is null) == (db.Stream is null)
                                                && PdfObj.NameOf(da, "/Type") == PdfObj.NameOf(db, "/Type")
                                                && PdfObj.NameOf(da, "/Subtype") == PdfObj.NameOf(db, "/Subtype"),
        (PdfArray, PdfArray) => true,
        (null, _) or (_, null) => false,
        _ => a.GetType() == b.GetType(),
    };

    private static readonly string[] Inheritable = ["/MediaBox", "/CropBox", "/Resources", "/Rotate"];

    /// <summary>
    /// Keys to leave out when comparing a page or page tree node with its original: PDFsharp flattens the page tree when
    /// it opens a file for editing (new /Parent, /Kids and /Count; inherited attributes copied onto the pages).
    /// </summary>
    private static HashSet<string>? Lenient(PdfObject theirs, PdfObject mine)
    {
        if (theirs is not PdfDictionary a || mine is not PdfDictionary b) return null;
        var type = PdfObj.NameOf(a, "/Type");
        if (type is not ("/Page" or "/Pages")) return null;
        HashSet<string> skip = type == "/Pages" ? ["/Parent", "/Kids", "/Count"] : ["/Parent"];
        foreach (var k in Inheritable)
            if (a.Elements.ContainsKey(k) != b.Elements.ContainsKey(k)) skip.Add(k);
        return skip;
    }

    /// <summary>The ids an object refers to directly (not through other indirect objects).</summary>
    private static IEnumerable<PdfObjectID> Refs(PdfObject obj)
    {
        var stack = new Stack<PdfItem?>();
        stack.Push(obj);
        var first = true;
        while (stack.Count > 0)
        {
            var item = stack.Pop();
            if (!first && item is PdfObject { IsIndirect: true } o) item = o.Reference;
            first = false;
            switch (item)
            {
                case PdfReference r:
                    yield return r.ObjectID;
                    break;
                case PdfDictionary d:
                    foreach (var v in d.Elements.Values) stack.Push(v);
                    break;
                case PdfArray a:
                    foreach (var v in a.Elements) stack.Push(v);
                    break;
            }
        }
    }

    /// <summary>Indirect objects reachable from <paramref name="roots"/>, each once, in a stable order.</summary>
    private static List<PdfObject> Reachable(PdfDocument doc, IEnumerable<PdfObject> roots)
    {
        var seen = new HashSet<PdfObjectID>();
        var list = new List<PdfObject>();
        var stack = new Stack<PdfItem>();
        foreach (var r in roots.Reverse())
            if (r.IsIndirect) stack.Push(r);
        while (stack.Count > 0)
        {
            var item = stack.Pop();
            switch (item)
            {
                case PdfReference r:
                    if (r.Value is { } v && seen.Add(r.ObjectID))
                    {
                        list.Add(v);
                        Children(v);
                    }
                    break;
                case PdfObject { IsIndirect: true } o:
                    if (seen.Add(o.Reference!.ObjectID))
                    {
                        list.Add(o);
                        Children(o);
                    }
                    break;
            }
        }
        return list;

        void Children(PdfItem container)
        {
            switch (container)
            {
                case PdfDictionary d:
                    foreach (var key in d.Elements.Keys.OrderDescending(StringComparer.Ordinal)) Push(d.Elements[key]);
                    break;
                case PdfArray a:
                    for (var i = a.Elements.Count - 1; i >= 0; i--) Push(a.Elements[i]);
                    break;
            }
        }

        void Push(PdfItem? child)
        {
            switch (child)
            {
                case PdfReference or PdfObject { IsIndirect: true }:
                    stack.Push(child);
                    break;
                case PdfDictionary or PdfArray:
                    Children(child);
                    break;
            }
        }
    }

    // ---- Canonical serialization ----

    /// <summary>
    /// An indirect object's body in PDF syntax: dictionary keys sorted, strings as hex, references through
    /// <paramref name="id"/> (null → the object is gone: written as null). With <paramref name="streamHash"/> a stream's
    /// data is replaced by its SHA-256 (for comparing); without, the raw (still filtered) data is written.
    /// </summary>
    /// <param name="skip">Top-level dictionary keys left out (for comparing).</param>
    /// <param name="replace">A top-level dictionary key written with this value text instead.</param>
    internal static byte[] Serialize(PdfObject obj, Func<PdfObjectID, (int, int)?> id, Dictionary<PdfDictionary, byte[]>? streamHash,
        IReadOnlySet<string>? skip = null, (string Key, string Value)? replace = null)
    {
        using var ms = new MemoryStream();
        Item(ms, obj, id, streamHash, top: true, depth: 0, skip, replace);
        return ms.ToArray();
    }

    private static void Item(MemoryStream s, PdfItem? item, Func<PdfObjectID, (int, int)?> id, Dictionary<PdfDictionary, byte[]>? streamHash, bool top, int depth,
        IReadOnlySet<string>? skip = null, (string Key, string Value)? replace = null)
    {
        if (depth > 256) throw new InvalidOperationException("Object nesting is too deep.");
        if (!top && item is PdfObject { IsIndirect: true } indirect) item = indirect.Reference;
        switch (item)
        {
            case null or PdfNull or PdfNullObject:
                Ascii(s, "null");
                break;
            case PdfReference r:
                Ascii(s, id(r.ObjectID) is { } t ? $"{t.Item1} {t.Item2} R" : "null");
                break;
            case PdfDictionary d:
            {
                Ascii(s, "<<");
                foreach (var key in d.Elements.Keys.Order(StringComparer.Ordinal))
                {
                    if ((d.Stream is not null && key == "/Length") || skip?.Contains(key) == true) continue;
                    Name(s, key);
                    s.WriteByte((byte)' ');
                    if (replace is { } rep && rep.Key == key) Ascii(s, rep.Value);
                    else Item(s, d.Elements[key], id, streamHash, false, depth + 1);
                }
                if (d.Stream is { } stream)
                {
                    var data = stream.Value ?? [];
                    Ascii(s, $"/Length {data.Length}>>\nstream\n");
                    if (streamHash is null) s.Write(data);
                    else
                    {
                        if (!streamHash.TryGetValue(d, out var h)) streamHash[d] = h = SHA256.HashData(data);
                        s.Write(h);
                    }
                    Ascii(s, "\nendstream");
                }
                else Ascii(s, ">>");
                break;
            }
            case PdfArray a:
                s.WriteByte((byte)'[');
                for (var i = 0; i < a.Elements.Count; i++)
                {
                    if (i > 0) s.WriteByte((byte)' ');
                    Item(s, a.Elements[i], id, streamHash, false, depth + 1);
                }
                s.WriteByte((byte)']');
                break;
            case PdfName n:
                Name(s, n.Value);
                break;
            case PdfNameObject n:
                Name(s, n.Value);
                break;
            case PdfString str:
                Hex(s, str.Value, str.Encoding == PdfStringEncoding.Unicode);
                break;
            case PdfStringObject str:
                Hex(s, str.Value, str.Encoding == PdfStringEncoding.Unicode);
                break;
            case PdfDate date:
                Hex(s, PdfObj.DateString(new DateTimeOffset(date.Value)).Value, false);
                break;
            case PdfBoolean b:
                Ascii(s, b.Value ? "true" : "false");
                break;
            case PdfBooleanObject b:
                Ascii(s, b.Value ? "true" : "false");
                break;
            case PdfInteger i:
                Ascii(s, i.Value.ToString(PdfObj.Inv));
                break;
            case PdfIntegerObject i:
                Ascii(s, i.Value.ToString(PdfObj.Inv));
                break;
            case PdfLongInteger l:
                Ascii(s, l.Value.ToString(PdfObj.Inv));
                break;
            case PdfLongIntegerObject l:
                Ascii(s, l.Value.ToString(PdfObj.Inv));
                break;
            case PdfReal r:
                Ascii(s, Real(r.Value));
                break;
            case PdfRealObject r:
                Ascii(s, Real(r.Value));
                break;
            case PdfRectangle rect:
                Ascii(s, $"[{Real(rect.X1)} {Real(rect.Y1)} {Real(rect.X2)} {Real(rect.Y2)}]");
                break;
            case PdfLiteral lit:
                Ascii(s, lit.Value);
                break;
            default:
                Ascii(s, item.ToString() ?? "null");
                break;
        }
    }

    private static string Real(double v)
    {
        if (!double.IsFinite(v)) return "0";
        var text = v.ToString("0.##########", PdfObj.Inv);
        return text == "-0" ? "0" : text;
    }

    private static void Name(Stream s, string name)
    {
        s.WriteByte((byte)'/');
        foreach (var c in name.StartsWith('/') ? name[1..] : name)
        {
            if (c is > (char)0x20 and < (char)0x7F && "#()<>[]{}/%".IndexOf(c) < 0)
                s.WriteByte((byte)c);
            else if (c <= 0xFF)
                Ascii(s, $"#{(int)c:X2}");
            else
                foreach (var b in Encoding.UTF8.GetBytes(c.ToString())) Ascii(s, $"#{b:X2}");
        }
    }

    /// <summary>A string as hex: its bytes (characters 0-255) as read, or UTF-16BE with a BOM for text beyond Latin-1.</summary>
    private static void Hex(Stream s, string value, bool unicode)
    {
        byte[] bytes;
        if (unicode || value.Any(c => c > 0xFF))
            bytes = [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(value)];
        else
            bytes = Encoding.Latin1.GetBytes(value);
        Ascii(s, $"<{Convert.ToHexString(bytes)}>");
    }

    // ---- The original's last trailer ----

    internal sealed record TailInfo(long StartXref, bool XrefStream, int Size, string? Id, bool HasInfo);

    internal static TailInfo? Tail(byte[] data)
    {
        if (StartXref(data) is not { } at) return null;
        var region = Encoding.Latin1.GetString(data, (int)at, (int)Math.Min(data.Length - at, 1 << 16)).TrimStart('\0', '\t', '\n', '\f', '\r', ' ');
        bool stream;
        string dict;
        if (region.StartsWith("xref", StringComparison.Ordinal))
        {
            // The table may be long: find its trailer anywhere after it.
            var t = data.AsSpan((int)at).IndexOf("trailer"u8);
            if (t < 0) return null;
            region = Encoding.Latin1.GetString(data, (int)at + t, (int)Math.Min(data.Length - at - t, 1 << 16));
            var end = region.IndexOf("startxref", StringComparison.Ordinal);
            dict = end < 0 ? region : region[..end];
            stream = false;
        }
        else if (Regex.Match(region, @"^\d+\s+\d+\s+obj\s*<<") is { Success: true })
        {
            var end = region.IndexOf("stream", StringComparison.Ordinal);
            if (end < 0) return null;
            dict = region[..end];
            if (!Regex.IsMatch(dict, @"/Type\s*/XRef\b")) return null;
            stream = true;
        }
        else return null;
        var size = Regex.Match(dict, @"/Size\s+(\d+)");
        return new TailInfo(at, stream, size.Success && int.TryParse(size.Groups[1].Value, PdfObj.Inv, out var n) ? n : 0,
            FileId(dict), Regex.IsMatch(dict, @"/Info\s+\d+\s+\d+\s+R"));
    }

    /// <summary>
    /// The offset after the file's last "startxref". Usually in the last few hundred bytes, but producers append junk
    /// after %%EOF (padding, a second copy of a trailer, mail gateway footers), so the whole file is searched backwards.
    /// </summary>
    internal static long? StartXref(byte[] data)
    {
        var end = data.Length;
        while (end > 0 && data.AsSpan(0, end).LastIndexOf("startxref"u8) is var k and >= 0)
        {
            var p = k + "startxref".Length;
            while (p < data.Length && data[p] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0) p++;
            long at = 0;
            var digits = 0;
            for (; p < data.Length && data[p] is >= (byte)'0' and <= (byte)'9' && digits < 18; p++, digits++) at = at * 10 + (data[p] - '0');
            if (digits > 0 && at > 0 && at < data.Length) return at;
            end = k; // e.g. "startxref" inside a stream after the real one: keep looking further back
        }
        return null;
    }

    /// <summary>The trailer's /ID as two hex strings ("[&lt;…&gt; &lt;…&gt;]"), whether the file wrote them as hex or literal strings.</summary>
    internal static string? FileId(string dict)
    {
        var m = Regex.Match(dict, @"/ID\s*\[");
        if (!m.Success) return null;
        var p = m.Index + m.Length;
        var parts = new List<string>();
        while (parts.Count < 2)
        {
            while (p < dict.Length && char.IsWhiteSpace(dict[p])) p++;
            if (p >= dict.Length) return null;
            if (dict[p] == '<')
            {
                var close = dict.IndexOf('>', p);
                if (close < 0) return null;
                var hex = string.Concat(dict.AsSpan(p + 1, close - p - 1).ToString().Where(c => !char.IsWhiteSpace(c)));
                if (!hex.All(char.IsAsciiHexDigit)) return null;
                parts.Add(hex.Length % 2 == 1 ? hex + "0" : hex);
                p = close + 1;
            }
            else if (dict[p] == '(')
            {
                if (Literal(dict, ref p) is not { } bytes) return null;
                parts.Add(Convert.ToHexString(bytes));
            }
            else return null;
        }
        return $"[<{parts[0]}> <{parts[1]}>]";
    }

    /// <summary>A literal string's bytes (balanced parentheses, backslash escapes); <paramref name="p"/> at its "(", left after its ")".</summary>
    private static byte[]? Literal(string s, ref int p)
    {
        var bytes = new List<byte>();
        var depth = 0;
        for (p++; p < s.Length; p++)
        {
            var c = s[p];
            if (c == '\\')
            {
                if (++p >= s.Length) return null;
                c = s[p];
                switch (c)
                {
                    case 'n': bytes.Add((byte)'\n'); break;
                    case 'r': bytes.Add((byte)'\r'); break;
                    case 't': bytes.Add((byte)'\t'); break;
                    case 'b': bytes.Add((byte)'\b'); break;
                    case 'f': bytes.Add((byte)'\f'); break;
                    case '\r':
                        if (p + 1 < s.Length && s[p + 1] == '\n') p++; // a line continuation
                        break;
                    case '\n':
                        break;
                    case >= '0' and <= '7':
                        var v = 0;
                        for (var k = 0; k < 3 && p < s.Length && s[p] is >= '0' and <= '7'; k++, p++) v = v * 8 + (s[p] - '0');
                        p--;
                        bytes.Add((byte)v);
                        break;
                    default: bytes.Add((byte)c); break; // \( \) \\ and unknown escapes: the character itself
                }
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')' && depth-- == 0)
            {
                p++;
                return [.. bytes];
            }
            if (c == '\r')
            {
                // An end of line in a literal string reads as a single \n.
                if (p + 1 < s.Length && s[p + 1] == '\n') p++;
                c = '\n';
            }
            bytes.Add((byte)c);
        }
        return null;
    }
}
