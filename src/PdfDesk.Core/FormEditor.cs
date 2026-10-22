using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfDesk.Core;

public enum FormFieldKind { Text, CheckBox, Radio, Choice, PushButton, Signature }

/// <summary>One clickable widget of a form field, positioned in page space (points, top-left origin, rotation applied).</summary>
public sealed record FormWidget(
    string FieldName,
    FormFieldKind Kind,
    int PageIndex,
    RectD Rect,
    string? OnState,
    bool Multiline,
    bool ReadOnly,
    bool Editable,
    int MaxLength,
    IReadOnlyList<string> Options,
    string Value,
    int TabOrder);

/// <summary>
/// Reads and fills AcroForm fields (text, check boxes, radio buttons, combo/list boxes). Values are written in place;
/// text and choice fields get NeedAppearances so viewers (MuPDF here, Acrobat, Edge) redraw them from the value.
/// XFA-only forms are not supported (they have no AcroForm widgets).
/// </summary>
public static class FormEditor
{
    private const int FfReadOnly = 1, FfMultiline = 1 << 12, FfRadio = 1 << 15, FfPushButton = 1 << 16, FfCombo = 1 << 17, FfEdit = 1 << 18;

    public static IReadOnlyList<FormWidget> Read(byte[] data, string? password)
    {
        using var doc = Open(data, password, PdfDocumentOpenMode.Import);
        return ReadWidgets(doc);
    }

    /// <summary>Sets field values by full field name. Check boxes take "On"/"Off" (or their export value); radio groups take the chosen state.</summary>
    public static byte[] SetValues(byte[] data, string? password, IReadOnlyDictionary<string, string> values)
    {
        using var doc = Open(data, password, PdfDocumentOpenMode.Modify);
        var acro = Resolve(doc.Internals.Catalog.Elements["/AcroForm"]) as PdfDictionary;
        var fields = new List<(PdfDictionary Field, string Name, string Type, int Flags, List<PdfDictionary> Widgets)>();
        Collect(doc, acro, fields);
        if (fields.Count == 0) throw new InvalidOperationException("This PDF has no fillable form.");

        var needAppearances = false;
        foreach (var (field, name, type, flags, widgets) in fields)
        {
            if (!values.TryGetValue(name, out var value) || (flags & FfReadOnly) != 0) continue;
            switch (type)
            {
                case "/Tx":
                case "/Ch":
                    field.Elements["/V"] = MakeString(value);
                    if (type == "/Ch") field.Elements.Remove("/I"); // selected indices would contradict the new value
                    foreach (var w in widgets) w.Elements.Remove("/AP"); // force a fresh appearance from the value
                    needAppearances = true;
                    break;
                case "/Btn" when (flags & FfPushButton) == 0:
                {
                    // Check box or radio group: the value is the "on" state name of one widget, or Off.
                    var on = value is "Off" or "" ? null : widgets.Select(OnStateOf).FirstOrDefault(s => s == value) ?? (value == "On" ? widgets.Select(OnStateOf).FirstOrDefault(s => s is not null) : null);
                    field.Elements["/V"] = new PdfName("/" + (on ?? "Off"));
                    foreach (var w in widgets)
                        w.Elements["/AS"] = new PdfName("/" + (on is not null && OnStateOf(w) == on ? on : "Off"));
                    break;
                }
            }
        }

        if (needAppearances && acro is not null) acro.Elements["/NeedAppearances"] = new PdfBoolean(true);
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    private static IReadOnlyList<FormWidget> ReadWidgets(PdfDocument doc)
    {
        var acro = Resolve(doc.Internals.Catalog.Elements["/AcroForm"]) as PdfDictionary;
        var fields = new List<(PdfDictionary Field, string Name, string Type, int Flags, List<PdfDictionary> Widgets)>();
        Collect(doc, acro, fields);
        if (fields.Count == 0) return [];

        // Which page each widget sits on (via the pages' /Annots; /P is optional and often wrong).
        var pageOf = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        var annotOrder = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < doc.PageCount; i++)
        {
            if (Resolve(doc.Pages[i].Elements["/Annots"]) is not PdfArray annots) continue;
            for (var k = 0; k < annots.Elements.Count; k++)
                if (Resolve(annots.Elements[k]) is PdfDictionary a)
                {
                    pageOf[a] = i;
                    annotOrder[a] = k;
                }
        }

        var result = new List<FormWidget>();
        foreach (var (field, name, type, flags, widgets) in fields)
        {
            var kind = type switch
            {
                "/Tx" => FormFieldKind.Text,
                "/Ch" => FormFieldKind.Choice,
                "/Sig" => FormFieldKind.Signature,
                "/Btn" when (flags & FfPushButton) != 0 => FormFieldKind.PushButton,
                "/Btn" when (flags & FfRadio) != 0 => FormFieldKind.Radio,
                "/Btn" => FormFieldKind.CheckBox,
                _ => (FormFieldKind?)null,
            };
            if (kind is null) continue;

            var options = ReadOptions(Inherited(field, "/Opt"));
            var value = Resolve(Inherited(field, "/V")) switch
            {
                PdfString s => s.Value,
                PdfName n => n.Value.TrimStart('/'),
                PdfArray arr when arr.Elements.Count > 0 && Resolve(arr.Elements[0]) is PdfString s0 => s0.Value,
                _ => kind is FormFieldKind.CheckBox or FormFieldKind.Radio ? "Off" : "",
            };
            var maxLen = Resolve(Inherited(field, "/MaxLen")) is PdfInteger ml ? ml.Value : 0;

            foreach (var w in widgets)
            {
                if (!pageOf.TryGetValue(w, out var page)) continue;
                if (Resolve(w.Elements["/Rect"]) is not PdfArray r || r.Elements.Count < 4) continue;
                var rect = ToPageSpace(doc.Pages[page], Num(r, 0), Num(r, 1), Num(r, 2), Num(r, 3));
                if (rect.IsEmpty) continue;
                var hidden = (Resolve(w.Elements["/F"]) is PdfInteger f ? f.Value : 0) is var fl && (fl & 2) != 0;
                if (hidden) continue;
                result.Add(new FormWidget(name, kind.Value, page, rect, OnStateOf(w),
                    (flags & FfMultiline) != 0, (flags & FfReadOnly) != 0,
                    kind == FormFieldKind.Choice && (flags & FfCombo) != 0 && (flags & FfEdit) != 0,
                    maxLen, options, value, annotOrder.GetValueOrDefault(w)));
            }
        }

        // Natural fill order: page, then top-to-bottom, then left-to-right (rows within a few points count as one line).
        return [.. result.OrderBy(w => w.PageIndex).ThenBy(w => Math.Round(w.Rect.Y / 6)).ThenBy(w => w.Rect.X)];
    }

    private static void Collect(PdfDocument doc, PdfDictionary? acro, List<(PdfDictionary, string, string, int, List<PdfDictionary>)> into)
    {
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        if (Resolve(acro?.Elements["/Fields"]) is PdfArray top)
            foreach (var item in top.Elements)
                if (Resolve(item) is PdfDictionary d) Walk(d, null, into, seen);

        // Some producers leave /Fields empty (or incomplete) and attach widgets only to pages. Viewers still treat
        // those as fields, so pick up every widget not reached above, starting from its top-most parent field.
        var unnamed = 0;
        for (var p = 0; p < doc.PageCount; p++)
        {
            if (Resolve(doc.Pages[p].Elements["/Annots"]) is not PdfArray annots) continue;
            foreach (var a in annots.Elements.Select(Resolve).OfType<PdfDictionary>())
            {
                if (seen.Contains(a) || a.Elements.GetName("/Subtype") != "/Widget") continue;
                var root = a;
                while (Resolve(root.Elements["/Parent"]) is PdfDictionary parent && !seen.Contains(parent)) root = parent;
                if (seen.Contains(root)) continue;
                var name = root.Elements.ContainsKey("/T") ? null : $"field{++unnamed}_p{p + 1}";
                Walk(root, name, into, seen);
            }
        }
    }

    private static void Walk(PdfDictionary node, string? parentName, List<(PdfDictionary, string, string, int, List<PdfDictionary>)> into, HashSet<PdfDictionary> seen)
    {
        if (!seen.Add(node)) return; // malformed files can contain cycles
        var partial = Resolve(node.Elements["/T"]) is PdfString t ? t.Value : null;
        var name = partial is null ? parentName ?? "" : parentName is null ? partial : $"{parentName}.{partial}";

        var kids = (Resolve(node.Elements["/Kids"]) as PdfArray)?.Elements.Select(Resolve).OfType<PdfDictionary>().ToList() ?? [];
        var fieldKids = kids.Where(k => k.Elements.ContainsKey("/T")).ToList();
        if (fieldKids.Count > 0)
        {
            foreach (var k in fieldKids) Walk(k, name, into, seen);
            return;
        }

        // Terminal field: its widgets are the kids without names, or the field dictionary itself.
        var widgets = kids.Count > 0 ? kids : node.Elements.ContainsKey("/Rect") ? [node] : [];
        foreach (var w in widgets) seen.Add(w);
        var type = Resolve(Inherited(node, "/FT")) is PdfName ft ? ft.Value : "";
        var flags = Resolve(Inherited(node, "/Ff")) is PdfInteger ff ? ff.Value : 0;
        if (name.Length > 0 && type.Length > 0) into.Add((node, name, type, flags, widgets));
    }

    private static PdfItem? Inherited(PdfDictionary node, string key)
    {
        for (var d = node; d is not null; d = Resolve(d.Elements["/Parent"]) as PdfDictionary)
            if (d.Elements.ContainsKey(key)) return d.Elements[key];
        return null;
    }

    private static string? OnStateOf(PdfDictionary widget)
    {
        if (Resolve(widget.Elements["/AP"]) is not PdfDictionary ap) return null;
        var normal = Resolve(ap.Elements["/N"]) as PdfDictionary ?? Resolve(ap.Elements["/D"]) as PdfDictionary;
        return normal?.Elements.Keys.Select(k => k.TrimStart('/')).FirstOrDefault(k => k != "Off");
    }

    private static List<string> ReadOptions(PdfItem? opt)
    {
        var list = new List<string>();
        if (Resolve(opt) is not PdfArray arr) return list;
        foreach (var e in arr.Elements)
        {
            switch (Resolve(e))
            {
                case PdfString s:
                    list.Add(s.Value);
                    break;
                case PdfArray pair when pair.Elements.Count > 0 && Resolve(pair.Elements[0]) is PdfString export:
                    list.Add(export.Value); // value written to /V; the display text is usually the same
                    break;
            }
        }
        return list;
    }

    /// <summary>Maps a PDF-space rectangle to our page space: crop box origin, rotation applied, y down.</summary>
    internal static RectD ToPageSpace(PdfPage page, double x1, double y1, double x2, double y2)
    {
        var box = page.Elements.ContainsKey("/CropBox") ? page.CropBox : page.MediaBox;
        var bx1 = Math.Min(box.X1, box.X2);
        var by2 = Math.Max(box.Y1, box.Y2);
        var w = Math.Abs(box.X2 - box.X1);
        var h = Math.Abs(box.Y2 - box.Y1);
        var rot = ((page.Rotate % 360) + 360) % 360;

        (double X, double Y) Map(double x, double y)
        {
            double u = x - bx1, v = by2 - y; // unrotated, top-left origin
            return rot switch
            {
                90 => (h - v, u),
                180 => (w - u, h - v),
                270 => (v, w - u),
                _ => (u, v),
            };
        }

        var a = Map(x1, y1);
        var b = Map(x2, y2);
        return RectD.FromEdges(a.X, a.Y, b.X, b.Y);
    }

    private static double Num(PdfArray a, int i) => Resolve(a.Elements[i]) switch
    {
        PdfReal r => r.Value,
        PdfInteger n => n.Value,
        _ => 0,
    };

    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

    private static PdfString MakeString(string value) =>
        value.All(c => c < 128) ? new PdfString(value) : new PdfString(value, PdfStringEncoding.Unicode);

    private static PdfDocument Open(byte[] data, string? password, PdfDocumentOpenMode mode)
    {
        try
        {
            var stream = new MemoryStream(data, writable: false);
            return string.IsNullOrEmpty(password) ? PdfReader.Open(stream, mode) : PdfReader.Open(stream, password, mode);
        }
        catch (PdfReaderException ex)
        {
            throw new InvalidOperationException($"This PDF's form can't be read: {ex.Message}", ex);
        }
    }
}
