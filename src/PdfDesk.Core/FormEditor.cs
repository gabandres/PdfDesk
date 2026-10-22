using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfDesk.Core;

public enum FormFieldKind { Text, CheckBox, Radio, Choice, PushButton, Signature }

/// <summary>One clickable widget of a form field, positioned in page space (points, top-left origin, rotation applied).</summary>
/// <param name="Options">Choice fields: the export values (what goes into the value).</param>
/// <param name="TabOrder">Position of the widget in fill order (0-based, across the document); the list from
/// <see cref="FormEditor.Read"/> is sorted by it.</param>
/// <param name="OptionLabels">Choice fields: the display text of each option, parallel to <paramref name="Options"/>.</param>
/// <param name="MultiSelect">List box allowing several selections; its value is the selected export values joined by '\n'.</param>
/// <param name="NoToggleToOff">Radio group that can't be switched back to "nothing selected".</param>
/// <param name="Password">Text field whose value should be masked.</param>
/// <param name="DisplayValue">The value as the field's format script shows it ("$1,234.50"), or null when it has none.</param>
/// <param name="Calculated">The field's value is computed from other fields (filled in by <see cref="FormEditor.SetValues"/>).</param>
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
    int TabOrder,
    IReadOnlyList<string>? OptionLabels = null,
    bool MultiSelect = false,
    bool NoToggleToOff = false,
    bool Password = false,
    string? DisplayValue = null,
    bool Calculated = false);

/// <summary>
/// Reads and fills AcroForm fields (text, check boxes, radio buttons, combo/list boxes). Values are written in place,
/// and every text/choice widget that changes gets a freshly generated appearance stream (fonts from the form's /DR,
/// or an embedded Unicode font for other scripts), so viewers that ignore NeedAppearances (Preview, iOS, print
/// pipelines) still show the values; NeedAppearances is set too. Simple Acrobat calculations and formats are
/// honored (see <see cref="FormScripts"/>). Hybrid AcroForm+XFA forms lose their XFA when filled; XFA-only forms
/// (no AcroForm widgets) are not fillable: check <see cref="HasXfa"/>.
/// </summary>
public static class FormEditor
{
    private const int FfReadOnly = 1, FfMultiline = 1 << 12, FfPassword = 1 << 13, FfNoToggleToOff = 1 << 14, FfRadio = 1 << 15,
        FfPushButton = 1 << 16, FfCombo = 1 << 17, FfEdit = 1 << 18, FfMultiSelect = 1 << 21, FfComb = 1 << 24, FfRadiosInUnison = 1 << 25;
    private const int AnnotHidden = 2, AnnotNoView = 32, AnnotReadOnly = 64;

    internal sealed record Field(PdfDictionary Dict, string Name, string Type, int Flags, List<PdfDictionary> Widgets);

    public static IReadOnlyList<FormWidget> Read(byte[] data, string? password)
    {
        using var doc = Open(data, password, PdfDocumentOpenMode.Import);
        return ReadWidgets(doc);
    }

    /// <summary>
    /// Whether the document carries an XFA form, and whether it is XFA-only (no AcroForm widgets to fill, or a dynamic
    /// form that needs an XFA renderer): such forms can't be filled here.
    /// </summary>
    public static (bool HasXfa, bool XfaOnly) HasXfa(byte[] data, string? password)
    {
        using var doc = Open(data, password, PdfDocumentOpenMode.Import);
        var catalog = doc.Internals.Catalog;
        var acro = PdfObj.Dict(catalog.Elements["/AcroForm"]);
        if (acro?.Elements.ContainsKey("/XFA") != true) return (false, false);
        var dynamic = PdfObj.Resolve(catalog.Elements["/NeedsRendering"]) is PdfBoolean { Value: true };
        var fields = new List<Field>();
        Collect(doc, acro, fields);
        var hasWidgets = fields.Any(f => f.Widgets.Count > 0);
        return (true, dynamic || !hasWidgets);
    }

    /// <summary>
    /// Sets field values by full field name. Check boxes take "On"/"Off" (or their export value); radio groups take the
    /// chosen state; choice fields take an export value (or its display text); multi-select list boxes take the selected
    /// export values joined by '\n'. Afterwards the form's calculations run (in /CO order) and their results are written.
    /// </summary>
    public static byte[] SetValues(byte[] data, string? password, IReadOnlyDictionary<string, string> values)
    {
        using var doc = Open(data, password, PdfDocumentOpenMode.Modify);
        var acro = PdfObj.Dict(doc.Internals.Catalog.Elements["/AcroForm"]);
        var fields = new List<Field>();
        Collect(doc, acro, fields);
        if (fields.Count == 0) throw new InvalidOperationException("This PDF has no fillable form.");

        // A hybrid form's XFA would keep showing the old values in Acrobat; the AcroForm is now the truth.
        acro?.Elements.Remove("/XFA");

        var dirty = new List<Field>();
        foreach (var f in fields)
        {
            if (!values.TryGetValue(f.Name, out var value) || (f.Flags & FfReadOnly) != 0) continue;
            switch (f.Type)
            {
                case "/Tx":
                    f.Dict.Elements["/V"] = PdfObj.MakeString(value);
                    f.Dict.Elements.Remove("/RV"); // rich text would override the plain value in Acrobat
                    dirty.Add(f);
                    break;
                case "/Ch":
                    SetChoice(doc, f, value);
                    dirty.Add(f);
                    break;
                case "/Btn" when (f.Flags & FfPushButton) == 0:
                    SetButton(f, value);
                    break;
            }
        }

        foreach (var f in Calculate(doc, acro, fields))
            if (!dirty.Contains(f)) dirty.Add(f);

        foreach (var f in dirty) WriteAppearances(doc, acro, f);

        if (dirty.Count > 0 && acro is not null)
        {
            acro.Elements["/NeedAppearances"] = new PdfBoolean(true);
            EnsureDefaultFonts(doc, acro);
        }
        return PdfObj.Save(doc);
    }

    private static void SetChoice(PdfDocument doc, Field f, string value)
    {
        var (exports, labels) = ReadOptions(Inherited(f.Dict, "/Opt"));
        var multi = (f.Flags & FfMultiSelect) != 0;
        var parts = multi ? value.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList() : value.Length == 0 ? [] : [value];
        // Accept display text too: map it to its export value.
        for (var i = 0; i < parts.Count; i++)
            if (!exports.Contains(parts[i]) && labels.IndexOf(parts[i]) is var li and >= 0) parts[i] = exports[li];
        if (!multi && parts.Count > 1) parts = [parts[0]];
        parts = [.. parts.Distinct().OrderBy(p => exports.IndexOf(p) is var i and >= 0 ? i : int.MaxValue)]; // option order

        if (parts.Count == 0) f.Dict.Elements.Remove("/V");
        else if (parts.Count == 1) f.Dict.Elements["/V"] = PdfObj.MakeString(parts[0]);
        else f.Dict.Elements["/V"] = new PdfArray(doc, [.. parts.Select(p => (PdfItem)PdfObj.MakeString(p))]);

        var indices = parts.Select(p => exports.IndexOf(p)).Where(i => i >= 0).Distinct().Order().ToList();
        if (indices.Count > 0 && indices.Count == parts.Count)
            f.Dict.Elements["/I"] = new PdfArray(doc, [.. indices.Select(i => (PdfItem)new PdfInteger(i))]);
        else f.Dict.Elements.Remove("/I"); // selected indices would contradict a custom value
    }

    private static void SetButton(Field f, string value)
    {
        var radio = (f.Flags & FfRadio) != 0;
        var off = value is "Off" or "";
        if (off && radio && (f.Flags & FfNoToggleToOff) != 0) return; // the group must keep a selection
        var states = f.Widgets.Select(OnStateOf).ToList();
        var on = off ? null : states.FirstOrDefault(s => s == value) ?? (value == "On" ? states.FirstOrDefault(s => s is not null) : null);
        f.Dict.Elements["/V"] = new PdfName("/" + (on ?? "Off"));
        // Radios sharing an on-state turn on together only with RadiosInUnison; otherwise just the first one.
        var unison = !radio || (f.Flags & FfRadiosInUnison) != 0;
        var turnedOn = false;
        foreach (var w in f.Widgets)
        {
            var isOn = on is not null && OnStateOf(w) == on && (unison || !turnedOn);
            turnedOn |= isOn;
            w.Elements["/AS"] = new PdfName("/" + (isOn ? on : "Off"));
        }
    }

    /// <summary>Runs the form's calculate scripts (in /CO order) and returns the fields whose value changed.</summary>
    private static List<Field> Calculate(PdfDocument doc, PdfDictionary? acro, List<Field> fields)
    {
        var changed = new List<Field>();
        var calcs = new List<(Field Field, FieldCalculation Calc)>();
        var byDict = new Dictionary<PdfDictionary, Field>(ReferenceEqualityComparer.Instance);
        foreach (var f in fields) byDict.TryAdd(f.Dict, f);
        var order = new List<Field>();
        if (PdfObj.Array(acro?.Elements["/CO"]) is { } co)
            foreach (var item in co.Elements)
                if (PdfObj.Dict(item) is { } d && byDict.TryGetValue(d, out var f) && !order.Contains(f)) order.Add(f);
        if (order.Count == 0) order = fields;
        foreach (var f in order)
            if (FormScripts.ParseCalculation(ActionScript(f, "/C")) is { } calc) calcs.Add((f, calc));
        if (calcs.Count == 0) return changed;

        IReadOnlyList<string> ValuesOf(string name) =>
            [.. fields.Where(f => f.Name == name || f.Name.StartsWith(name + ".", StringComparison.Ordinal)).Select(f => ValueOf(f.Dict, f.Type))];

        foreach (var (f, calc) in calcs)
        {
            if (f.Type is not ("/Tx" or "/Ch")) continue;
            if (calc.Evaluate(ValuesOf) is not { } result || double.IsNaN(result) || double.IsInfinity(result)) continue;
            var text = FormScripts.NumberToValue(result);
            if (ValueOf(f.Dict, f.Type) == text) continue;
            f.Dict.Elements["/V"] = PdfObj.MakeString(text);
            f.Dict.Elements.Remove("/RV");
            changed.Add(f);
        }
        return changed;
    }

    /// <summary>The JavaScript of a field's additional action (/AA /C calculate, /F format, ...), if any.</summary>
    private static string? ActionScript(Field f, string key)
    {
        foreach (var holder in f.Widgets.Prepend(f.Dict))
        {
            if (PdfObj.Dict(PdfObj.Dict(holder.Elements["/AA"])?.Elements[key]) is not { } action) continue;
            if (PdfObj.Name(action.Elements["/S"]) != "JavaScript") continue;
            return PdfObj.Text(action.Elements["/JS"]);
        }
        return null;
    }

    private static string ValueOf(PdfDictionary field, string type) => PdfObj.Resolve(Inherited(field, "/V")) switch
    {
        PdfString s => s.Value,
        PdfName n => n.Value.TrimStart('/'),
        PdfArray arr => string.Join('\n', arr.Elements.Select(PdfObj.Str).OfType<string>()),
        PdfInteger i => i.Value.ToString(PdfObj.Inv),
        PdfReal r => FormScripts.NumberToValue(r.Value),
        _ => type == "/Btn" ? "Off" : "",
    };

    private static (string Text, bool Red) Display(Field f, string raw)
    {
        if (FormScripts.ParseFormat(ActionScript(f, "/F")) is { } fmt) return FormScripts.Format(fmt, raw);
        return (raw, false);
    }

    /// <summary>Generates the normal appearance of every widget of a text or choice field from its current value.</summary>
    private static void WriteAppearances(PdfDocument doc, PdfDictionary? acro, Field f)
    {
        if (f.Type is not ("/Tx" or "/Ch")) return;
        var raw = ValueOf(f.Dict, f.Type);
        var (exports, labels) = ReadOptions(Inherited(f.Dict, "/Opt"));
        var listBox = f.Type == "/Ch" && (f.Flags & FfCombo) == 0;
        var (text, red) = f.Type == "/Tx" ? Display(f, raw) : (raw, false);
        if (f.Type == "/Ch" && !listBox && exports.IndexOf(raw) is var ci and >= 0) text = labels[ci];

        var selected = new HashSet<int>();
        if (listBox)
        {
            foreach (var part in raw.Split('\n'))
                if (exports.IndexOf(part) is var i and >= 0) selected.Add(i);
        }
        var topIndex = PdfObj.Int(Inherited(f.Dict, "/TI")) ?? 0;
        if (listBox && selected.Count > 0 && topIndex > selected.Min()) topIndex = selected.Min();
        var drFonts = PdfObj.Dict(PdfObj.Dict(acro?.Elements["/DR"])?.Elements["/Font"]);

        foreach (var w in f.Widgets)
        {
            var da = PdfObj.Str(w.Elements["/DA"]) ?? PdfObj.Str(Inherited(f.Dict, "/DA")) ?? PdfObj.Str(acro?.Elements["/DA"]) ?? "/Helv 0 Tf 0 g";
            var fontName = DefaultAppearance.Parse(da).FontName;
            var baseFont = PdfObj.Dict(drFonts?.Elements[fontName]) is { } drFont ? PdfObj.NameOf(drFont, "/BaseFont") : null;
            var q = PdfObj.Int(w.Elements["/Q"]) ?? PdfObj.Int(Inherited(f.Dict, "/Q")) ?? PdfObj.Int(acro?.Elements["/Q"]) ?? 0;
            var look = new FieldLook
            {
                Da = da,
                DrBaseFont = string.IsNullOrEmpty(baseFont) ? null : baseFont,
                Quadding = q,
                Multiline = f.Type == "/Tx" && (f.Flags & FfMultiline) != 0,
                Comb = f.Type == "/Tx" && (f.Flags & FfComb) != 0 && (f.Flags & (FfMultiline | FfPassword)) == 0,
                Password = f.Type == "/Tx" && (f.Flags & FfPassword) != 0,
                MaxLen = PdfObj.Int(Inherited(f.Dict, "/MaxLen")) ?? 0,
                Text = text,
                ListBox = listBox,
                Items = labels,
                Selected = selected,
                TopIndex = topIndex,
                ColorOverride = red ? new PdfColorValue([1, 0, 0]) : null,
            };
            if (FieldAppearance.Build(doc, w, look) is not { } ap) continue;
            var apDict = new PdfDictionary(doc);
            apDict.Elements["/N"] = ap.Reference!;
            w.Elements["/AP"] = apDict;
        }
    }

    /// <summary>
    /// With NeedAppearances, viewers redraw every field (check boxes too) from the form's default resources;
    /// make sure the fonts they look for (Helvetica for text, ZapfDingbats for check marks) are there.
    /// </summary>
    private static void EnsureDefaultFonts(PdfDocument doc, PdfDictionary acro)
    {
        if (PdfObj.Dict(acro.Elements["/DR"]) is not { } dr)
        {
            dr = new PdfDictionary(doc);
            acro.Elements["/DR"] = dr;
        }
        if (PdfObj.Dict(dr.Elements["/Font"]) is not { } fonts)
        {
            fonts = new PdfDictionary(doc);
            dr.Elements["/Font"] = fonts;
        }
        foreach (var (key, name) in new[] { ("/Helv", "Helvetica"), ("/ZaDb", "ZapfDingbats") })
            if (!fonts.Elements.ContainsKey(key)) fonts.Elements[key] = FontResources.Standard(doc, name);
        if (!acro.Elements.ContainsKey("/DA")) acro.Elements["/DA"] = new PdfString("/Helv 0 Tf 0 g");
    }

    private static IReadOnlyList<FormWidget> ReadWidgets(PdfDocument doc)
    {
        var acro = PdfObj.Dict(doc.Internals.Catalog.Elements["/AcroForm"]);
        var fields = new List<Field>();
        Collect(doc, acro, fields);
        if (fields.Count == 0) return [];

        // Which page each widget sits on (via the pages' /Annots; /P is optional and often wrong).
        var pageOf = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        var annotOrder = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        var tabs = new string?[doc.PageCount];
        for (var i = 0; i < doc.PageCount; i++)
        {
            tabs[i] = PdfObj.Name(doc.Pages[i].Elements["/Tabs"]);
            if (PdfObj.Array(doc.Pages[i].Elements["/Annots"]) is not { } annots) continue;
            for (var k = 0; k < annots.Elements.Count; k++)
                if (PdfObj.Dict(annots.Elements[k]) is { } a && pageOf.TryAdd(a, i)) annotOrder[a] = k;
        }
        var structOrder = tabs.Any(t => t == "S") ? StructureOrder(doc) : null;
        var calculated = CalculatedFields(fields);

        var result = new List<(FormWidget W, PdfDictionary Annot)>();
        foreach (var f in fields)
        {
            var kind = f.Type switch
            {
                "/Tx" => FormFieldKind.Text,
                "/Ch" => FormFieldKind.Choice,
                "/Sig" => FormFieldKind.Signature,
                "/Btn" when (f.Flags & FfPushButton) != 0 => FormFieldKind.PushButton,
                "/Btn" when (f.Flags & FfRadio) != 0 => FormFieldKind.Radio,
                "/Btn" => FormFieldKind.CheckBox,
                _ => (FormFieldKind?)null,
            };
            if (kind is null) continue;

            var (options, labels) = ReadOptions(Inherited(f.Dict, "/Opt"));
            var multi = kind == FormFieldKind.Choice && (f.Flags & FfMultiSelect) != 0;
            var value = PdfObj.Resolve(Inherited(f.Dict, "/V")) switch
            {
                PdfString s => s.Value,
                PdfName n => n.Value.TrimStart('/'),
                PdfArray arr when multi => string.Join('\n', arr.Elements.Select(PdfObj.Str).OfType<string>()),
                PdfArray arr when arr.Elements.Count > 0 && PdfObj.Resolve(arr.Elements[0]) is PdfString s0 => s0.Value,
                PdfInteger n => n.Value.ToString(PdfObj.Inv),
                PdfReal r => FormScripts.NumberToValue(r.Value),
                null when kind == FormFieldKind.Choice && PdfObj.Array(Inherited(f.Dict, "/I")) is { } sel =>
                    string.Join('\n', sel.Elements.Select(e => PdfObj.Int(e) ?? -1).Where(i => i >= 0 && i < options.Count).Select(i => options[i]).Take(multi ? int.MaxValue : 1)),
                _ => kind is FormFieldKind.CheckBox or FormFieldKind.Radio ? "Off" : "",
            };
            var maxLen = PdfObj.Int(Inherited(f.Dict, "/MaxLen")) ?? 0;
            string? display = null;
            if (kind == FormFieldKind.Text && FormScripts.ParseFormat(ActionScript(f, "/F")) is { } fmt)
                display = FormScripts.Format(fmt, value).Text;

            foreach (var w in f.Widgets)
            {
                if (!pageOf.TryGetValue(w, out var page)) continue;
                if (PdfObj.Array(w.Elements["/Rect"]) is not { Elements.Count: >= 4 } r) continue;
                var rect = ToPageSpace(doc.Pages[page], PdfObj.Num(r, 0), PdfObj.Num(r, 1), PdfObj.Num(r, 2), PdfObj.Num(r, 3));
                if (rect.IsEmpty) continue;
                var annotFlags = PdfObj.Int(w.Elements["/F"]) ?? 0;
                if ((annotFlags & (AnnotHidden | AnnotNoView)) != 0) continue;
                result.Add((new FormWidget(f.Name, kind.Value, page, rect, OnStateOf(w),
                    (f.Flags & FfMultiline) != 0, (f.Flags & FfReadOnly) != 0 || (annotFlags & AnnotReadOnly) != 0,
                    kind == FormFieldKind.Choice && (f.Flags & FfCombo) != 0 && (f.Flags & FfEdit) != 0,
                    maxLen, options, value, 0,
                    labels.SequenceEqual(options) ? null : labels, multi,
                    kind == FormFieldKind.Radio && (f.Flags & FfNoToggleToOff) != 0,
                    kind == FormFieldKind.Text && (f.Flags & FfPassword) != 0,
                    display, calculated.Contains(f)), w));
            }
        }

        // Fill order, page by page: the page's /Tabs when it asks for structure (/S), annotation (/A, /W) or column (/C)
        // order; otherwise (and for /R) the natural reading order: top-to-bottom, then left-to-right, rows within a few
        // points counting as one line.
        var ordered = new List<FormWidget>();
        foreach (var group in result.GroupBy(x => x.W.PageIndex).OrderBy(g => g.Key))
        {
            var tab = tabs[group.Key];
            IEnumerable<(FormWidget W, PdfDictionary Annot)> items = tab switch
            {
                "S" => group.OrderBy(x => structOrder!.TryGetValue(x.Annot, out var s) ? s : int.MaxValue).ThenBy(x => annotOrder.GetValueOrDefault(x.Annot)),
                "A" or "W" => group.OrderBy(x => annotOrder.GetValueOrDefault(x.Annot)),
                "C" => group.OrderBy(x => Math.Round(x.W.Rect.X / 6)).ThenBy(x => x.W.Rect.Y),
                _ => group.OrderBy(x => Math.Round(x.W.Rect.Y / 6)).ThenBy(x => x.W.Rect.X),
            };
            ordered.AddRange(items.Select(x => x.W));
        }
        return [.. ordered.Select((w, i) => w with { TabOrder = i })];
    }

    private static HashSet<Field> CalculatedFields(List<Field> fields)
    {
        var set = new HashSet<Field>();
        foreach (var f in fields)
            if (FormScripts.ParseCalculation(ActionScript(f, "/C")) is not null) set.Add(f);
        return set;
    }

    /// <summary>Order in which annotations appear in the structure tree (OBJR references), for /Tabs /S.</summary>
    private static Dictionary<PdfDictionary, int> StructureOrder(PdfDocument doc)
    {
        var order = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        var seen = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        void Visit(PdfItem? item, int depth)
        {
            if (depth > 64) return;
            switch (PdfObj.Resolve(item))
            {
                case PdfArray arr:
                    foreach (var e in arr.Elements) Visit(e, depth + 1);
                    break;
                case PdfDictionary d when seen.Add(d):
                    if (PdfObj.NameOf(d, "/Type") == "/OBJR")
                    {
                        if (PdfObj.Dict(d.Elements["/Obj"]) is { } target) order.TryAdd(target, order.Count);
                    }
                    else Visit(d.Elements["/K"], depth + 1);
                    break;
            }
        }
        Visit(PdfObj.Dict(doc.Internals.Catalog.Elements["/StructTreeRoot"])?.Elements["/K"], 0);
        return order;
    }

    /// <summary>
    /// Empties every field that has one of <paramref name="widgets"/> (and every field sharing its value through a
    /// parent): /V, /RV and /I go, check boxes and radios turn off on all their widgets, and text and choice widgets get
    /// fresh (empty) appearances on every page, so the value survives nowhere. Used by redaction. True if any field changed.
    /// </summary>
    internal static bool ClearFields(PdfDocument doc, IReadOnlySet<PdfDictionary> widgets)
    {
        var acro = PdfObj.Dict(doc.Internals.Catalog.Elements["/AcroForm"]);
        var fields = new List<Field>();
        Collect(doc, acro, fields);

        static IEnumerable<PdfDictionary> Chain(PdfDictionary d)
        {
            var guard = 0;
            for (var n = d; n is not null && guard++ < 64; n = PdfObj.Dict(n.Elements["/Parent"])) yield return n;
        }

        // Where the values live: the hit fields, and any ancestor whose /V they inherit.
        var holders = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var f in fields.Where(f => f.Widgets.Any(widgets.Contains)))
            foreach (var n in Chain(f.Dict))
                if (n == f.Dict || n.Elements.ContainsKey("/V")) holders.Add(n);
        if (holders.Count == 0) return false;

        var cleared = fields.Where(f => Chain(f.Dict).Any(holders.Contains)).ToList();
        foreach (var n in holders.Concat(cleared.SelectMany(f => f.Widgets)))
            foreach (var key in new[] { "/V", "/RV", "/I" }) n.Elements.Remove(key);
        foreach (var f in cleared)
        {
            switch (f.Type)
            {
                case "/Btn" when (f.Flags & FfPushButton) == 0:
                    f.Dict.Elements["/V"] = new PdfName("/Off");
                    foreach (var w in f.Widgets)
                        if (w.Elements.ContainsKey("/AS") || w.Elements.ContainsKey("/AP")) w.Elements["/AS"] = new PdfName("/Off");
                    break;
                case "/Tx" or "/Ch":
                    foreach (var w in f.Widgets) w.Elements.Remove("/AP"); // a widget whose appearance can't be rebuilt shows nothing
                    WriteAppearances(doc, acro, f);
                    break;
            }
        }
        // XFA datasets keep their own copy of every value.
        acro?.Elements.Remove("/XFA");
        return true;
    }

    internal static void Collect(PdfDocument doc, PdfDictionary? acro, List<Field> into)
    {
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        if (PdfObj.Array(acro?.Elements["/Fields"]) is { } top)
            foreach (var item in top.Elements)
                if (PdfObj.Dict(item) is { } d) Walk(d, null, into, seen, 0);

        // Some producers leave /Fields empty (or incomplete) and attach widgets only to pages. Viewers still treat
        // those as fields, so pick up every widget not reached above, starting from its top-most parent field.
        var unnamed = 0;
        for (var p = 0; p < doc.PageCount; p++)
        {
            if (PdfObj.Array(doc.Pages[p].Elements["/Annots"]) is not { } annots) continue;
            foreach (var a in annots.Elements.Select(PdfObj.Dict).OfType<PdfDictionary>())
            {
                if (seen.Contains(a) || PdfObj.NameOf(a, "/Subtype") != "/Widget") continue;
                var root = a;
                var guard = 0;
                while (PdfObj.Dict(root.Elements["/Parent"]) is { } parent && !seen.Contains(parent) && guard++ < 64) root = parent;
                if (seen.Contains(root)) continue;
                var name = root.Elements.ContainsKey("/T") ? null : $"field{++unnamed}_p{p + 1}";
                Walk(root, name, into, seen, 0);
            }
        }
    }

    private static void Walk(PdfDictionary node, string? parentName, List<Field> into, HashSet<PdfDictionary> seen, int depth)
    {
        if (!seen.Add(node) || depth > 64) return; // malformed files can contain cycles
        var partial = PdfObj.Str(node.Elements["/T"]);
        var name = partial is null ? parentName ?? "" : parentName is null ? partial : $"{parentName}.{partial}";

        var kids = PdfObj.Array(node.Elements["/Kids"])?.Elements.Select(PdfObj.Dict).OfType<PdfDictionary>().ToList() ?? [];
        var fieldKids = kids.Where(k => k.Elements.ContainsKey("/T")).ToList();
        if (fieldKids.Count > 0)
        {
            foreach (var k in fieldKids) Walk(k, name, into, seen, depth + 1);
            return;
        }

        // Terminal field: its widgets are the kids without names, or the field dictionary itself.
        var widgets = kids.Count > 0 ? kids : node.Elements.ContainsKey("/Rect") ? [node] : [];
        foreach (var w in widgets) seen.Add(w);
        var type = PdfObj.Resolve(Inherited(node, "/FT")) is PdfName ft ? ft.Value : "";
        var flags = PdfObj.Int(Inherited(node, "/Ff")) ?? 0;
        if (name.Length > 0 && type.Length > 0) into.Add(new Field(node, name, type, flags, widgets));
    }

    internal static PdfItem? Inherited(PdfDictionary node, string key)
    {
        var guard = 0;
        for (var d = node; d is not null && guard++ < 64; d = PdfObj.Dict(d.Elements["/Parent"]))
            if (d.Elements.ContainsKey(key)) return d.Elements[key];
        return null;
    }

    private static string? OnStateOf(PdfDictionary widget)
    {
        if (PdfObj.Dict(widget.Elements["/AP"]) is not { } ap) return null;
        var normal = PdfObj.Dict(ap.Elements["/N"]) ?? PdfObj.Dict(ap.Elements["/D"]);
        return normal?.Elements.Keys.Select(k => k.TrimStart('/')).FirstOrDefault(k => k != "Off");
    }

    /// <summary>Export values and display labels of a choice field's /Opt (plain strings or [export display] pairs).</summary>
    private static (List<string> Exports, List<string> Labels) ReadOptions(PdfItem? opt)
    {
        var exports = new List<string>();
        var labels = new List<string>();
        if (PdfObj.Array(opt) is not { } arr) return (exports, labels);
        foreach (var e in arr.Elements)
        {
            switch (PdfObj.Resolve(e))
            {
                case PdfString s:
                    exports.Add(s.Value);
                    labels.Add(s.Value);
                    break;
                case PdfArray pair when pair.Elements.Count > 0 && PdfObj.Str(pair.Elements[0]) is { } export:
                    exports.Add(export);
                    labels.Add(pair.Elements.Count > 1 && PdfObj.Str(pair.Elements[1]) is { } label ? label : export);
                    break;
            }
        }
        return (exports, labels);
    }

    /// <summary>Maps a PDF-space rectangle to our page space: crop box origin, rotation applied, y down.</summary>
    internal static RectD ToPageSpace(PdfPage page, double x1, double y1, double x2, double y2)
    {
        var a = PdfObj.ToPage(page, x1, y1);
        var b = PdfObj.ToPage(page, x2, y2);
        return RectD.FromEdges(a.X, a.Y, b.X, b.Y);
    }

    private static PdfDocument Open(byte[] data, string? password, PdfDocumentOpenMode mode) =>
        PdfObj.Open(data, password, mode, "This PDF's form can't be read");
}
