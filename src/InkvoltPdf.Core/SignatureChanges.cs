using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace InkvoltPdf.Core;

/// <summary>
/// What a signature's later incremental updates changed, and whether the signature permits it. The signed revision (the
/// file up to the end of the signature's byte range) is compared with the current file object by object (PDFsharp keeps
/// the file's object numbers in Import mode, and an update redefines objects under the same numbers). Each changed or
/// added object is judged by its role:
/// <list type="bullet">
/// <item>page content (content streams, resources, images and fonts a page draws with), the page tree, page properties,
/// the catalog's view-changing entries (layers, open actions, scripts): never permitted, since they change what the
/// signer saw (incremental "shadow attacks" hide, replace or overlay signed content this way);</item>
/// <item>form values and their appearances: permitted unless certified with no changes (DocMDP P=1), and unless the field
/// is locked by the signature (FieldMDP);</item>
/// <item>comments and new signature fields: permitted from P=3 and for approval (non-certifying) signatures;</item>
/// <item>new form fields, bookmarks, page labels, attachments, tags: approval signatures only;</item>
/// <item>metadata (Info, XMP), validation data (/DSS), document timestamps: always.</item>
/// </list>
/// That follows ISO 32000-2 12.8.2.2 (DocMDP) and what Acrobat reports, short of comparing appearance pixels.
/// </summary>
internal static class SignatureChanges
{
    /// <summary>The permission level of an approval (non-certifying) signature: anything that leaves the signed content as it was.</summary>
    private const int Approval = 4;

    /// <summary>A signature dictionary's own entries, as signed.</summary>
    internal sealed record Entries(string? SubFilter, string? Name, string? M, string? Reason, string? Location, int? CertificationLevel);

    /// <param name="Permitted">Every later change is one the signature permits (also true when the analysis failed).</param>
    /// <param name="Problem">What was changed that isn't permitted, or why the changes couldn't be checked.</param>
    /// <param name="Signed">The signature dictionary's entries in the signed revision (null when not found there).</param>
    internal sealed record Analysis(bool Permitted, string? Problem, Entries? Signed);

    internal static Entries EntriesOf(PdfDictionary v) => new(PdfObj.Name(v.Elements["/SubFilter"]), PdfObj.Str(v.Elements["/Name"]),
        PdfObj.Str(v.Elements["/M"]), PdfObj.Str(v.Elements["/Reason"]), PdfObj.Str(v.Elements["/Location"]), CertificationLevel(v));

    /// <summary>The DocMDP permission of a certifying signature dictionary (its /Reference), else null.</summary>
    internal static int? CertificationLevel(PdfDictionary v)
    {
        foreach (var r in PdfObj.Array(v.Elements["/Reference"])?.Elements.Select(PdfObj.Dict).OfType<PdfDictionary>() ?? [])
            if (PdfObj.NameOf(r, "/TransformMethod") == "/DocMDP")
                return PdfObj.Int(PdfObj.Dict(r.Elements["/TransformParams"])?.Elements["/P"]) is { } p and >= 1 and <= 3 ? p : 2;
        return null;
    }

    public static Analysis Analyze(byte[] data, long signedLength, string fieldName, string? password)
    {
        try
        {
            using var before = PdfObj.Open(data.AsSpan(0, (int)signedLength).ToArray(), password, PdfDocumentOpenMode.Import);
            using var after = PdfObj.Open(data, password, PdfDocumentOpenMode.Import);
            var fields = new List<FormEditor.Field>();
            FormEditor.Collect(before, PdfObj.Dict(before.Internals.Catalog.Elements["/AcroForm"]), fields);
            var field = fields.FirstOrDefault(f => f.Name == fieldName && f.Type == "/Sig");
            var v = field is null ? null : PdfObj.Dict(FormEditor.Inherited(field.Dict, "/V"));
            var entries = v is null ? null : EntriesOf(v);
            var level = entries?.CertificationLevel ?? Approval;
            var revision = data.AsSpan(0, (int)signedLength).ToArray();
            var problems = new Comparison(before, after, level, v is null ? _ => false : LockOf(v, field!.Dict),
                Incremental.Tail(revision)?.HasInfo ?? false, Incremental.Tail(data)?.HasInfo ?? false).Run();
            if (problems.Count == 0) return new(true, null, entries);
            var what = string.Join("; ", problems.Distinct().Take(3)) + (problems.Distinct().Count() > 3 ? "; and more" : "");
            return new(false, level switch
            {
                1 => $"The document is certified with no changes allowed, but it was changed after certification: {what}.",
                2 or 3 => $"The document was changed after certification in ways the certification doesn't allow: {what}.",
                _ => $"The document was changed after signing in ways a signature doesn't allow: {what}.",
            }, entries);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new(true, "Inkvolt PDF couldn't work out what was changed after signing.", null);
        }
    }

    /// <summary>Which fields the signature locks: its FieldMDP reference, else the signature field's /Lock.</summary>
    private static Func<string, bool> LockOf(PdfDictionary v, PdfDictionary field)
    {
        var lockDict = PdfObj.Array(v.Elements["/Reference"])?.Elements.Select(PdfObj.Dict).OfType<PdfDictionary>()
            .Where(r => PdfObj.NameOf(r, "/TransformMethod") == "/FieldMDP").Select(r => PdfObj.Dict(r.Elements["/TransformParams"])).FirstOrDefault()
            ?? PdfObj.Dict(field.Elements["/Lock"]);
        if (lockDict is null) return _ => false;
        var names = PdfObj.Array(lockDict.Elements["/Fields"])?.Elements.Select(PdfObj.Text).OfType<string>().ToHashSet() ?? [];
        bool Listed(string name) => names.Any(n => name == n || name.StartsWith(n + ".", StringComparison.Ordinal));
        return PdfObj.NameOf(lockDict, "/Action") switch
        {
            "/All" => _ => true,
            "/Include" => Listed,
            "/Exclude" => name => !Listed(name),
            _ => _ => false,
        };
    }

    /// <param name="infoA">The revision's trailer names an Info dictionary (PDFsharp makes one up when asked for a missing one).</param>
    private sealed class Comparison(PdfDocument a, PdfDocument b, int level, Func<string, bool> locked, bool infoA, bool infoB)
    {
        private static readonly string[] Upward = ["/Parent", "/P", "/IRT", "/Annots", "/Pg", "/Popup"];
        private static readonly HashSet<string> WidgetKeys = ["/V", "/AS", "/AP", "/MK", "/DV", "/P"];
        private static readonly HashSet<string> FieldKeys = ["/V", "/DV", "/Kids"];
        private static readonly HashSet<string> AcroFormKeys = ["/Fields", "/SigFlags", "/DR", "/DA", "/NeedAppearances", "/CO"];
        private static readonly HashSet<string> CatalogKeys = ["/AcroForm", "/DSS", "/Metadata", "/Extensions", "/Perms"];
        private static readonly HashSet<string> ApprovalCatalogKeys =
            ["/Outlines", "/PageLabels", "/StructTreeRoot", "/MarkInfo", "/Lang", "/ViewerPreferences", "/PageMode", "/PageLayout", "/Names", "/Dests"];
        private static readonly HashSet<string> NamesKeys = ["/EmbeddedFiles", "/Dests"];

        private readonly List<string> _problems = [];
        private readonly Dictionary<PdfDictionary, byte[]> _hashes = new(ReferenceEqualityComparer.Instance);

        private sealed class Roles
        {
            public readonly Dictionary<int, PdfObject> Objects = [];
            public readonly Dictionary<int, int> Pages = [];
            public readonly HashSet<int> PageNodes = [];
            /// <summary>Objects page content draws with → the first page using them.</summary>
            public readonly Dictionary<int, int> Content = [];
            public readonly Dictionary<int, (PdfDictionary Dict, bool Widget)> Annots = [];
            public readonly HashSet<int> Arrays = [];
            public readonly Dictionary<int, FormEditor.Field> FieldOf = [];
            public readonly HashSet<int> FieldNodes = [];
            public readonly HashSet<int> SigValues = [];
            public readonly HashSet<int> Widgets = [], Comments = [], Meta = [], Harmless = [];
            public int Catalog = -1, AcroForm = -1, Names = -1, Info = -1;
        }

        public List<string> Run()
        {
            var ra = RolesOf(a, infoA);
            var rb = RolesOf(b, infoB);

            foreach (var (n, objB) in rb.Objects)
            {
                if (!ra.Objects.TryGetValue(n, out var objA))
                {
                    Added(n, rb);
                    continue;
                }
                if (Same(objA, objB)) continue;
                Changed(n, objA, objB, ra, rb);
            }
            foreach (var (n, an) in ra.Annots)
                if (!rb.Annots.ContainsKey(n))
                {
                    if (an.Widget) Problem("form fields removed");
                    else if (level < 3) Problem("comments removed");
                }
            return _problems;
        }

        private void Problem(string what) => _problems.Add(what);

        private void Changed(int n, PdfObject objA, PdfObject objB, Roles ra, Roles rb)
        {
            if (ra.Content.TryGetValue(n, out var page)) Problem($"the content of page {page + 1}");
            else if (n == ra.Catalog) CompareCatalog((PdfDictionary)objA, (PdfDictionary)objB);
            else if (ra.Pages.TryGetValue(n, out page))
            {
                if (!Same(objA, objB, ["/Annots"])) Problem($"page {page + 1} (its size, rotation or resources)");
            }
            else if (ra.PageNodes.Contains(n)) Problem("pages added, removed or moved");
            else if (ra.Annots.TryGetValue(n, out var an))
            {
                if (!rb.Annots.ContainsKey(n)) return; // reported as removed
                if (an.Widget) ChangedField(ra.FieldOf.GetValueOrDefault(n), (PdfDictionary)objA, (PdfDictionary)objB, WidgetKeys);
                else if (level < 3) Problem("comments changed");
            }
            else if (ra.FieldNodes.Contains(n)) ChangedField(ra.FieldOf.GetValueOrDefault(n), (PdfDictionary)objA, (PdfDictionary)objB, FieldKeys);
            else if (n == ra.AcroForm) CompareAcroForm(objA as PdfDictionary, objB as PdfDictionary);
            else if (n == ra.Names) CompareNames(objA as PdfDictionary, objB as PdfDictionary);
            else if (ra.SigValues.Contains(n)) Problem("an earlier signature's details");
            else if (n == ra.Info || ra.Arrays.Contains(n) || rb.Arrays.Contains(n) || ra.Meta.Contains(n)) { }
            else if (ra.Widgets.Contains(n) || rb.Widgets.Contains(n))
            {
                if (level < 2) Problem("form field appearances");
            }
            else if (ra.Comments.Contains(n) || rb.Comments.Contains(n))
            {
                if (level < 3) Problem("comments changed");
            }
            else if (level == Approval && (ra.Harmless.Contains(n) || rb.Harmless.Contains(n))) { }
            else Problem("other document data");
        }

        private void Added(int n, Roles rb)
        {
            if (rb.Annots.TryGetValue(n, out var an))
            {
                if (an.Widget) AddedField(rb.FieldOf.GetValueOrDefault(n));
                else if (level < 3) Problem("comments added");
            }
            else if (rb.FieldOf.TryGetValue(n, out var f)) AddedField(f);
        }

        private void AddedField(FormEditor.Field? f)
        {
            if (f?.Type == "/Sig")
            {
                // A document timestamp (PAdES-LTA) may follow any signature, even a no-changes certification.
                if (PdfObj.Dict(FormEditor.Inherited(f.Dict, "/V")) is { } v && PdfObj.Name(v.Elements["/SubFilter"]) == "ETSI.RFC3161") return;
                if (level < 3) Problem("signature fields added");
            }
            else if (level < Approval) Problem("form fields added");
        }

        private void ChangedField(FormEditor.Field? f, PdfDictionary objA, PdfDictionary objB, HashSet<string> allowed)
        {
            var keys = DiffKeys(objA, objB);
            // Signing locks fields by setting their read-only flag: that alone is fine.
            if (keys.Remove("/Ff") && (PdfObj.Int(objB.Elements["/Ff"]) ?? 0) != ((PdfObj.Int(objA.Elements["/Ff"]) ?? 0) | 1)) keys.Add("/Ff");
            var name = f?.Name ?? "?";
            if (keys.Any(k => !allowed.Contains(k))) Problem($"form field \"{name}\" (its properties)");
            else if (keys.Count == 0) { }
            else if (level < 2) Problem("form fields filled in");
            else if ((keys.Contains("/V") || keys.Contains("/AS")) && f is not null && f.Type != "/Sig" && locked(f.Name)) Problem($"locked field \"{name}\"");
        }

        private void CompareCatalog(PdfDictionary x, PdfDictionary y)
        {
            foreach (var key in DiffKeys(x, y))
            {
                if (key == "/AcroForm") CompareAcroForm(PdfObj.Dict(x.Elements[key]), PdfObj.Dict(y.Elements[key]));
                else if (key == "/Names" && level == Approval) CompareNames(PdfObj.Dict(x.Elements[key]), PdfObj.Dict(y.Elements[key]));
                else if (CatalogKeys.Contains(key) || (level == Approval && ApprovalCatalogKeys.Contains(key))) { }
                else Problem(key switch
                {
                    "/OCProperties" => "which layers are shown",
                    "/OpenAction" or "/AA" => "what happens when the document opens",
                    "/Pages" => "pages added, removed or moved",
                    _ => $"document settings ({key.TrimStart('/')})",
                });
            }
        }

        private void CompareAcroForm(PdfDictionary? x, PdfDictionary? y)
        {
            if (x is null || y is null) return;
            if (DiffKeys(x, y).Any(k => !AcroFormKeys.Contains(k))) Problem("form settings (e.g. XFA data)");
        }

        private void CompareNames(PdfDictionary? x, PdfDictionary? y)
        {
            if (x is null || y is null) return;
            if (level < Approval || DiffKeys(x, y).Any(k => !NamesKeys.Contains(k))) Problem("document scripts or named items");
        }

        private HashSet<string> DiffKeys(PdfDictionary x, PdfDictionary y)
        {
            var keys = x.Elements.Keys.Union(y.Elements.Keys).ToHashSet();
            return [.. keys.Where(k => !Same(x, y, [.. keys.Where(o => o != k)]))];
        }

        private bool Same(PdfObject x, PdfObject y, IReadOnlyCollection<string>? skip = null)
        {
            var set = skip is null ? null : new HashSet<string>(skip);
            return Ser(x, set).AsSpan().SequenceEqual(Ser(y, set));
        }

        private byte[] Ser(PdfObject o, HashSet<string>? skip) =>
            Incremental.Serialize(o, r => (r.ObjectNumber, r.GenerationNumber), _hashes, skip);

        private static Roles RolesOf(PdfDocument doc, bool hasInfo)
        {
            var r = new Roles();
            var catalog = doc.Internals.Catalog;
            var info = hasInfo ? doc.Info : null;
            r.Catalog = catalog.Reference?.ObjectNumber ?? -1;
            r.Info = info?.Reference?.ObjectNumber ?? -1;
            Walk([catalog, info], r.Objects, [], record: true);

            for (var p = 0; p < doc.PageCount; p++)
                if (doc.Pages[p].Reference is { } pr) r.Pages.TryAdd(pr.ObjectNumber, p);
            var nodes = new Stack<PdfItem?>([catalog.Elements["/Pages"]]);
            while (nodes.Count > 0)
                if (nodes.Pop() is PdfReference nr && PdfObj.Dict(nr) is { } node && PdfObj.NameOf(node, "/Type") != "/Page" && r.PageNodes.Add(nr.ObjectNumber))
                    foreach (var kid in PdfObj.Array(node.Elements["/Kids"])?.Elements.ToList() ?? []) nodes.Push(kid);
            var stop = r.Pages.Keys.Concat(r.PageNodes).ToHashSet();

            for (var p = 0; p < doc.PageCount; p++)
            {
                var page = doc.Pages[p];
                var seen = new Dictionary<int, PdfObject>();
                foreach (var key in page.Elements.Keys.Where(k => k is not ("/Parent" or "/Annots")))
                    Walk([page.Elements[key]], seen, stop);
                // Inherited resources live on the page tree nodes.
                var depth = 0;
                for (var node = PdfObj.Dict(page.Elements["/Parent"]); node is not null && depth++ < 64; node = PdfObj.Dict(node.Elements["/Parent"]))
                    Walk([node.Elements["/Resources"]], seen, stop);
                foreach (var n in seen.Keys) r.Content.TryAdd(n, p);

                if (page.Elements["/Annots"] is PdfReference ar) r.Arrays.Add(ar.ObjectNumber);
                foreach (var item in PdfObj.Array(page.Elements["/Annots"])?.Elements.ToList() ?? [])
                    if (item is PdfReference ir && PdfObj.Dict(ir) is { } annot)
                    {
                        var widget = PdfObj.NameOf(annot, "/Subtype") == "/Widget";
                        r.Annots.TryAdd(ir.ObjectNumber, (annot, widget));
                        Walk([annot], widget ? r.Widgets : r.Comments, stop);
                    }
            }

            var acro = PdfObj.Dict(catalog.Elements["/AcroForm"]);
            if (catalog.Elements["/AcroForm"] is PdfReference acroRef) r.AcroForm = acroRef.ObjectNumber;
            if (acro?.Elements["/Fields"] is PdfReference fr) r.Arrays.Add(fr.ObjectNumber);
            var fields = new List<FormEditor.Field>();
            FormEditor.Collect(doc, acro, fields);
            foreach (var f in fields)
            {
                var guard = 0;
                for (var node = f.Dict; node is not null && guard++ < 64; node = PdfObj.Dict(node.Elements["/Parent"]))
                    if (node.Reference is { } nr)
                    {
                        r.FieldNodes.Add(nr.ObjectNumber);
                        if (node.Elements["/Kids"] is PdfReference kr) r.Arrays.Add(kr.ObjectNumber);
                    }
                if (f.Dict.Reference is { } fdr) r.FieldOf.TryAdd(fdr.ObjectNumber, f);
                foreach (var w in f.Widgets)
                    if (w.Reference is { } wr) r.FieldOf.TryAdd(wr.ObjectNumber, f);
                if (f.Type == "/Sig" && FormEditor.Inherited(f.Dict, "/V") is PdfReference vr) r.SigValues.Add(vr.ObjectNumber);
            }

            Walk([catalog.Elements["/DSS"], catalog.Elements["/Metadata"], catalog.Elements["/Extensions"], catalog.Elements["/Perms"], info, acro?.Elements["/DR"]], r.Meta, stop);
            if (catalog.Elements["/Names"] is PdfReference names) r.Names = names.ObjectNumber;
            var namesDict = PdfObj.Dict(catalog.Elements["/Names"]);
            Walk([.. ApprovalCatalogKeys.Where(k => k != "/Names").Select(k => catalog.Elements[k]), namesDict?.Elements["/EmbeddedFiles"], namesDict?.Elements["/Dests"]], r.Harmless, stop);
            return r;
        }

        /// <summary>
        /// Collects the indirect objects reachable from <paramref name="roots"/>, not following references back up the
        /// structure (/Parent, /P, …) nor into <paramref name="stop"/> (pages: their content is judged on its own).
        /// With <paramref name="record"/> everything is followed.
        /// </summary>
        private static void Walk(IEnumerable<PdfItem?> roots, Dictionary<int, PdfObject> into, HashSet<int> stop, bool record = false)
        {
            var set = new HashSet<int>(into.Keys);
            Walk(roots, set, stop, record, into);
        }

        private static void Walk(IEnumerable<PdfItem?> roots, HashSet<int> into, HashSet<int> stop, bool record = false, Dictionary<int, PdfObject>? objects = null)
        {
            var stack = new Stack<PdfItem>(roots.OfType<PdfItem>());
            while (stack.Count > 0)
            {
                var item = stack.Pop();
                PdfItem? container = item;
                if (item is PdfReference r)
                {
                    if (stop.Contains(r.ObjectNumber) || !into.Add(r.ObjectNumber) || r.Value is not { } v) continue;
                    objects?.TryAdd(r.ObjectNumber, v);
                    container = v;
                }
                else if (item is PdfObject { IsIndirect: true } o)
                {
                    var n = o.Reference!.ObjectNumber;
                    if (stop.Contains(n) || !into.Add(n)) continue;
                    objects?.TryAdd(n, o);
                }
                switch (container)
                {
                    case PdfDictionary d:
                        foreach (var key in d.Elements.Keys)
                            if (record || !Upward.Contains(key)) Push(d.Elements[key]);
                        break;
                    case PdfArray arr:
                        foreach (var e in arr.Elements) Push(e);
                        break;
                }
            }

            void Push(PdfItem? child)
            {
                // Direct dictionaries and arrays are walked as part of their container.
                if (child is PdfReference or PdfObject { IsIndirect: true }) stack.Push(child);
                else if (child is PdfDictionary or PdfArray) stack.Push(child);
            }
        }
    }
}
