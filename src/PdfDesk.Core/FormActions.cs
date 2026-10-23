using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfDesk.Core;

/// <summary>Which actions a form widget carries (its /A and the /AA entries of the widget and its field).</summary>
[Flags]
public enum FormTriggers
{
    None = 0,
    /// <summary>Mouse down (/AA /D).</summary>
    Down = 1,
    /// <summary>Mouse up or activation (/AA /U, /A).</summary>
    Up = 2,
    /// <summary>Pointer enters the widget (/AA /E).</summary>
    Enter = 4,
    /// <summary>Pointer leaves the widget (/AA /X).</summary>
    Exit = 8,
    /// <summary>The field gets the input focus (/AA /Fo).</summary>
    Focus = 16,
    /// <summary>The field loses the input focus (/AA /Bl).</summary>
    Blur = 32,
    /// <summary>A keystroke script checks typing (/AA /K, JavaScript).</summary>
    Keystroke = 64,
}

/// <summary>The user event that runs a widget's actions.</summary>
public enum FormTrigger
{
    Down,
    Up,
    /// <summary>Down then up at once (keyboard activation, taps).</summary>
    Click,
    Enter,
    Exit,
    Focus,
    Blur,
}

public enum SubmitFormat { Html, Fdf, Xfdf, Pdf }

/// <summary>Form data a SubmitForm action (or this.submitForm) wants to send.</summary>
/// <param name="Method">"POST" or "GET" (for GET the data is in <paramref name="Url"/> and <paramref name="Body"/> is empty).</param>
/// <param name="FieldCount">How many fields' values the data carries.</param>
public sealed record FormSubmission(string Url, string Method, string ContentType, byte[] Body, SubmitFormat Format, int FieldCount);

/// <summary>An email the form wants to send (mailDoc, mailForm, app.mailMsg, or a mailto: submit).</summary>
/// <param name="Attachment">The document or its form data; null for a plain message.</param>
public sealed record FormMail(string To, string Cc, string Bcc, string Subject, string Message, byte[]? Attachment, string? AttachmentName);

/// <summary>app.alert: <paramref name="Icon"/> 0 error, 1 warning, 2 question, 3 status; <paramref name="Buttons"/>
/// 0 OK, 1 OK/Cancel, 2 Yes/No, 3 Yes/No/Cancel.</summary>
public sealed record FormAlert(string Message, string Title, int Icon, int Buttons);

/// <summary>app.response: a question with a text box.</summary>
public sealed record FormPrompt(string Question, string Title, string Default, bool Password, string Label);

/// <summary>
/// What a form's actions may do outside the document. The app implements it: every outward step (open a URL, send
/// data, open or launch a file, write an email, import data) must ask the user first, naming the destination. Calls
/// come from a background thread; modal ones block the script (its time limit is paused meanwhile).
/// </summary>
public interface IFormActionHost
{
    /// <summary>The page shown (0-based).</summary>
    int CurrentPage { get; }

    void GoToPage(int page, double? y);

    /// <summary>app.launchURL and URI actions.</summary>
    void OpenUrl(string url);

    /// <summary>Asks, then sends (http/https only); the task gives the HTTP status, 0 when nothing was sent.</summary>
    Task<int> Submit(FormSubmission submission);

    void Mail(FormMail mail);

    /// <summary>Opens the print dialog (pages 0-based, null = the user picks).</summary>
    void Print(int? fromPage, int? toPage);

    /// <summary>Returns 1 OK, 2 Cancel, 3 No, 4 Yes.</summary>
    int Alert(FormAlert alert);

    /// <summary>The text entered, or null when cancelled.</summary>
    string? Response(FormPrompt prompt);

    /// <summary>A GoToR action: ask, then open that PDF (relative paths are from this document's folder).</summary>
    void OpenRemote(string path, int page);

    /// <summary>A Launch action: never runs programs; asks, then opens a document file.</summary>
    void Launch(string path);

    /// <summary>Named actions the app carries out: Print, GoBack, GoForward, Find, FullScreen.</summary>
    void Named(string name);

    /// <summary>Asks, then lets the user pick an FDF/XFDF file (suggested by the form); its bytes, or null.</summary>
    byte[]? ImportData(string? suggestedPath);

    /// <summary>app.beep: 0 error, 1 warning, 2 question, 3 status, 4 default.</summary>
    void Beep(int type);
}

/// <summary>
/// What running a widget's actions did: the new document when fields changed (null = unchanged), an undo label, alert
/// texts no dialog showed, log lines, the field a script asked to focus, and whether a change was refused because the
/// document's permissions password is needed.
/// </summary>
public sealed record FormActionResult(byte[]? Data, string Label, IReadOnlyList<string> Alerts, IReadOnlyList<string> Log, string? FocusField, bool NeedsPermission);

public static partial class FormEditor
{
    private const int FfNoExport = 4;

    /// <summary>
    /// Runs the actions a user event triggers on a widget (identified by field name, page and its index in the page's
    /// /Annots): /AA /D, /U (+ /A), /E, /X, /Fo, /Bl, with their /Next chains. JavaScript runs in the sandbox; ResetForm,
    /// Hide and ImportData change the form; SubmitForm, URI, Launch, GoToR, Named and mail go through
    /// <paramref name="host"/>. A document whose permissions password isn't known runs read-only: navigation and outward
    /// actions still work, field changes are dropped (<see cref="FormActionResult.NeedsPermission"/>).
    /// </summary>
    public static FormActionResult RunAction(byte[] data, string? password, string fieldName, int pageIndex, int annotIndex, FormTrigger trigger, IFormActionHost host)
    {
        PdfDocument doc;
        var readOnly = false;
        try
        {
            doc = Open(data, password, PdfDocumentOpenMode.Modify);
        }
        catch (OwnerPasswordRequiredException)
        {
            doc = Open(data, password, PdfDocumentOpenMode.Import);
            readOnly = true;
        }
        using (doc)
        {
            var acro = PdfObj.Dict(doc.Internals.Catalog.Elements["/AcroForm"]);
            var fields = new List<Field>();
            Collect(doc, acro, fields);
            if (fields.FirstOrDefault(f => f.Name == fieldName) is not { } field)
                return new FormActionResult(null, "", [], [$"Form action: no field {fieldName}"], null, false);
            var widget = field.Widgets.FirstOrDefault() ?? field.Dict;
            if (pageIndex >= 0 && pageIndex < doc.PageCount && PdfObj.Array(doc.Pages[pageIndex].Elements["/Annots"]) is { } annots
                && annotIndex >= 0 && annotIndex < annots.Elements.Count && PdfObj.Dict(annots.Elements[annotIndex]) is { } a
                && field.Widgets.Any(w => ReferenceEquals(w, a)))
                widget = a;

            var runner = new FormActionRunner(doc, acro, fields, host, data, readOnly);
            PdfItem? Additional(string key) => PdfObj.Dict(widget.Elements["/AA"])?.Elements[key] ?? PdfObj.Dict(field.Dict.Elements["/AA"])?.Elements[key];
            void Step(string key, string name)
            {
                runner.Execute(Additional(key), field, name);
                if (key == "/U") runner.Execute(widget.Elements["/A"], field, name);
            }
            switch (trigger)
            {
                case FormTrigger.Down: Step("/D", "Mouse Down"); break;
                case FormTrigger.Up: Step("/U", "Mouse Up"); break;
                case FormTrigger.Click:
                    Step("/D", "Mouse Down");
                    Step("/U", "Mouse Up");
                    break;
                case FormTrigger.Enter: Step("/E", "Mouse Enter"); break;
                case FormTrigger.Exit: Step("/X", "Mouse Exit"); break;
                case FormTrigger.Focus: Step("/Fo", "Focus"); break;
                case FormTrigger.Blur: Step("/Bl", "Blur"); break;
            }
            return runner.Finish();
        }
    }

    /// <summary>Which triggers a widget has actions for.</summary>
    private static FormTriggers TriggersOf(Field f, PdfDictionary widget)
    {
        var t = FormTriggers.None;
        if (PdfObj.Dict(widget.Elements["/A"]) is not null || PdfObj.Array(widget.Elements["/A"]) is not null) t |= FormTriggers.Up;
        foreach (var holder in new[] { widget, f.Dict })
        {
            if (PdfObj.Dict(holder.Elements["/AA"]) is not { } aa) continue;
            foreach (var (key, bit) in new[] { ("/D", FormTriggers.Down), ("/U", FormTriggers.Up), ("/E", FormTriggers.Enter), ("/X", FormTriggers.Exit), ("/Fo", FormTriggers.Focus), ("/Bl", FormTriggers.Blur) })
                if (aa.Elements.ContainsKey(key)) t |= bit;
            if (PdfObj.Dict(aa.Elements["/K"]) is { } k && PdfObj.Name(k.Elements["/S"]) == "JavaScript") t |= FormTriggers.Keystroke;
        }
        return t;
    }

    /// <summary>Writes a choice field's options: plain strings, or [export display] pairs where they differ.</summary>
    private static void WriteOptions(Field f, List<string> exports, List<string> labels)
    {
        var doc = f.Dict.Owner;
        var opt = new PdfArray(doc);
        for (var i = 0; i < exports.Count; i++)
        {
            if (exports[i] == labels[i]) opt.Elements.Add(PdfObj.MakeString(exports[i]));
            else opt.Elements.Add(new PdfArray(doc, PdfObj.MakeString(exports[i]), PdfObj.MakeString(labels[i])));
        }
        // The options may be inherited: write them where they live, so the field's kids keep sharing them.
        var holder = f.Dict;
        for (var d = f.Dict; d is not null; d = PdfObj.Dict(d.Elements["/Parent"]))
            if (d.Elements.ContainsKey("/Opt"))
            {
                holder = d;
                break;
            }
        holder.Elements["/Opt"] = opt;
        // A selection index past the new list would point at the wrong option.
        if (PdfObj.Array(Inherited(f.Dict, "/I")) is { } sel && sel.Elements.Any(e => (PdfObj.Int(e) ?? -1) >= exports.Count)) f.Dict.Elements.Remove("/I");
    }

    /// <summary>
    /// Carries out a run's actions on one open document: the JavaScript host, ResetForm, SubmitForm, Hide, ImportData,
    /// navigation and the outward calls (through <see cref="Host"/>). Fill uses one too, so field scripts can reset or
    /// submit. Actions that need the finished document (submit as PDF, mailDoc) wait for <see cref="RunDeferred"/>.
    /// </summary>
    internal sealed class FormActionRunner
    {
        private const int MaxActions = 64, MaxDepth = 32;

        private readonly PdfDocument _doc;
        private readonly PdfDictionary? _acro;
        private readonly List<Field> _fields;
        private readonly byte[] _source;
        private readonly List<Action<byte[]>> _deferred = [];
        private readonly HashSet<PdfDictionary> _ran = new(ReferenceEqualityComparer.Instance);
        private readonly List<string> _alerts = [];
        private int _actions;

        public FormActionRunner(PdfDocument doc, PdfDictionary? acro, List<Field> fields, IFormActionHost? host, byte[] source, bool readOnly = false)
        {
            _doc = doc;
            _acro = acro;
            _fields = fields;
            _source = source;
            Host = host;
            ReadOnly = readOnly;
            Js = new JsFormHost(doc, fields, readOnly, this);
        }

        public IFormActionHost? Host { get; }
        public JsFormHost Js { get; }
        public bool ReadOnly { get; }

        /// <summary>Fields changed outside the script's own writes (reset, import, hide).</summary>
        public bool Changed { get; private set; }

        /// <summary>A change was dropped because the document is read-only here.</summary>
        public bool WantedChange { get; private set; }

        public string? FocusField { get; set; }
        public string Label { get; private set; } = "Form action";

        public List<string> Log => Js.Log;

        /// <summary>Runs an action (dictionary or array of them) and its /Next chain; each action runs once per run.</summary>
        public void Execute(PdfItem? item, Field target, string eventName, int depth = 0)
        {
            if (depth > MaxDepth) return;
            switch (PdfObj.Resolve(item))
            {
                case PdfArray arr:
                    foreach (var e in arr.Elements.ToList()) Execute(e, target, eventName, depth + 1);
                    return;
                case PdfDictionary a:
                    if (!_ran.Add(a)) return;
                    if (++_actions > MaxActions)
                    {
                        if (_actions == MaxActions + 1) Log.Add("Form actions: too many in one go; the rest were skipped");
                        return;
                    }
                    try
                    {
                        Perform(a, target, eventName);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        Log.Add($"Form action {PdfObj.Name(a.Elements["/S"])} failed: {ex.Message}");
                    }
                    Execute(a.Elements["/Next"], target, eventName, depth + 1);
                    return;
            }
        }

        private void Perform(PdfDictionary a, Field target, string eventName)
        {
            var type = PdfObj.Name(a.Elements["/S"]) ?? "";
            switch (type)
            {
                case "JavaScript":
                    if (PdfObj.Text(a.Elements["/JS"]) is { Length: > 0 } script
                        && Js.Run(eventName, target, script, ValueOf(target.Dict, target.Type)) is { } r)
                        _alerts.AddRange(r.Alerts);
                    break;
                case "ResetForm":
                    Reset(FieldNames(a.Elements["/Fields"]), ((PdfObj.Int(a.Elements["/Flags"]) ?? 0) & 1) != 0);
                    break;
                case "SubmitForm":
                {
                    var flags = PdfObj.Int(a.Elements["/Flags"]) ?? 0;
                    var format = (flags & 256) != 0 ? "PDF" : (flags & 32) != 0 ? "XFDF" : (flags & 4) != 0 ? "HTML" : "FDF";
                    Submit(FileSpec(a.Elements["/F"]) ?? "", format, (flags & 2) != 0, FieldNames(a.Elements["/Fields"]), (flags & 1) != 0, (flags & 8) != 0, "");
                    break;
                }
                case "URI":
                    if (PdfObj.Str(a.Elements["/URI"]) is { Length: > 0 } uri) OpenUrl(uri);
                    break;
                case "Launch":
                {
                    var path = FileSpec(a.Elements["/F"]) ?? FileSpec(PdfObj.Dict(a.Elements["/Win"])?.Elements["/F"]);
                    if (path is { Length: > 0 }) Host?.Launch(path);
                    break;
                }
                case "GoTo":
                    if (DocumentFeatures.ResolveDestination(_doc, a.Elements["/D"]) is { } dest) Host?.GoToPage(dest.PageIndex, dest.Y);
                    break;
                case "GoToR":
                {
                    if (FileSpec(a.Elements["/F"]) is not { Length: > 0 } file) break;
                    var page = PdfObj.Resolve(a.Elements["/D"]) switch
                    {
                        PdfArray d when d.Elements.Count > 0 => PdfObj.Int(d.Elements[0]) ?? 0,
                        var d => PdfObj.Int(d) ?? 0,
                    };
                    Host?.OpenRemote(file, Math.Max(0, page));
                    break;
                }
                case "Named":
                    Named(PdfObj.Name(a.Elements["/N"]) ?? "");
                    break;
                case "Hide":
                    Hide(a.Elements["/T"], PdfObj.Resolve(a.Elements["/H"]) is not PdfBoolean { Value: false });
                    break;
                case "ImportData":
                    Import(FileSpec(a.Elements["/F"]) ?? "");
                    break;
                default:
                    Log.Add($"Form action {type} isn't supported");
                    break;
            }
        }

        // ---------------- Navigation and outward calls ----------------

        public void GoToPage(int page)
        {
            if (Host is null || _doc.PageCount == 0) return;
            Host.GoToPage(Math.Clamp(page, 0, _doc.PageCount - 1), null);
        }

        private void Named(string name)
        {
            if (Host is null) return;
            switch (name)
            {
                case "NextPage": GoToPage(Host.CurrentPage + 1); break;
                case "PrevPage": GoToPage(Host.CurrentPage - 1); break;
                case "FirstPage": GoToPage(0); break;
                case "LastPage": GoToPage(_doc.PageCount - 1); break;
                case "Print" or "GoBack" or "GoForward" or "Find" or "FullScreen": Host.Named(name); break;
                default: Log.Add($"Named action {name} isn't supported"); break;
            }
        }

        public void OpenUrl(string url)
        {
            url = url.Trim();
            if (url.Length == 0 || Host is null) return;
            // Relative URIs resolve against the document's /URI /Base.
            if (!Uri.TryCreate(url, UriKind.Absolute, out _) && PdfObj.Str(PdfObj.Dict(_doc.Internals.Catalog.Elements["/URI"])?.Elements["/Base"]) is { } b
                && Uri.TryCreate(b, UriKind.Absolute, out var baseUri) && Uri.TryCreate(baseUri, url, out var full))
                url = full.ToString();
            Host.OpenUrl(url);
        }

        public void Print(int from, int to) => Host?.Print(from >= 0 ? from : null, to >= 0 ? to : null);

        public void Mail(string kind, string to, string cc, string bcc, string subject, string message)
        {
            if (Host is not { } host) return;
            switch (kind)
            {
                case "doc":
                    _deferred.Add(bytes => host.Mail(new FormMail(to, cc, bcc, subject, message, bytes, "form.pdf")));
                    break;
                case "form":
                {
                    var entries = Entries(null, exclude: false, includeEmpty: false);
                    host.Mail(new FormMail(to, cc, bcc, subject, message, FormData.Fdf(entries, null), "form.fdf"));
                    break;
                }
                default:
                    host.Mail(new FormMail(to, cc, bcc, subject, message, null, null));
                    break;
            }
        }

        /// <summary>Builds the data a submit sends and hands it to the host (which asks the user first).</summary>
        public void Submit(string url, string format, bool includeEmpty, IReadOnlyList<string>? names, bool exclude, bool get, string charset)
        {
            if (Host is not { } host)
            {
                Log.Add("submitForm: nothing to send it with");
                return;
            }
            url = url.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out _) && PdfObj.Str(PdfObj.Dict(_doc.Internals.Catalog.Elements["/URI"])?.Elements["/Base"]) is { } b
                && Uri.TryCreate(b, UriKind.Absolute, out var baseUri) && Uri.TryCreate(baseUri, url, out var full))
                url = full.ToString();
            var kind = format switch { "HTML" => SubmitFormat.Html, "XFDF" or "XML" or "XDP" => SubmitFormat.Xfdf, "PDF" => SubmitFormat.Pdf, _ => SubmitFormat.Fdf };
            var entries = Entries(names, exclude, includeEmpty);

            if (url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                // An emailed submission: the data goes along as an attachment the user adds.
                var address = Uri.UnescapeDataString(url[7..].Split('?')[0]);
                if (kind == SubmitFormat.Pdf) _deferred.Add(bytes => host.Mail(new FormMail(address, "", "", "Form", "", bytes, "form.pdf")));
                else
                {
                    var (data, name) = kind == SubmitFormat.Xfdf ? (FormData.Xfdf(entries, null), "form.xfdf")
                        : kind == SubmitFormat.Html ? (Encoding.UTF8.GetBytes(FormData.Html(entries, Encoding.UTF8)), "form.txt")
                        : (FormData.Fdf(entries, null), "form.fdf");
                    host.Mail(new FormMail(address, "", "", "Form", "", data, name));
                }
                return;
            }

            switch (kind)
            {
                case SubmitFormat.Pdf:
                    _deferred.Add(bytes => Observe(host.Submit(new FormSubmission(url, "POST", "application/pdf", bytes, SubmitFormat.Pdf, entries.Count))));
                    break;
                case SubmitFormat.Html:
                {
                    Encoding enc;
                    try
                    {
                        enc = charset.Length == 0 ? Encoding.UTF8 : Encoding.GetEncoding(charset);
                    }
                    catch (ArgumentException)
                    {
                        enc = Encoding.UTF8;
                    }
                    var query = FormData.Html(entries, enc);
                    if (get)
                    {
                        var target = url.Contains('?') ? url + "&" + query : url + "?" + query;
                        Observe(host.Submit(new FormSubmission(target, "GET", "application/x-www-form-urlencoded", [], SubmitFormat.Html, entries.Count)));
                    }
                    else Observe(host.Submit(new FormSubmission(url, "POST", "application/x-www-form-urlencoded", Encoding.ASCII.GetBytes(query), SubmitFormat.Html, entries.Count)));
                    break;
                }
                case SubmitFormat.Xfdf:
                    Observe(host.Submit(new FormSubmission(url, "POST", "application/vnd.adobe.xfdf", FormData.Xfdf(entries, null), SubmitFormat.Xfdf, entries.Count)));
                    break;
                default:
                    Observe(host.Submit(new FormSubmission(url, "POST", "application/vnd.fdf", FormData.Fdf(entries, null), SubmitFormat.Fdf, entries.Count)));
                    break;
            }
        }

        private static void Observe(Task task) => task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

        /// <summary>The fields a submit sends: exportable ones (no push buttons, signatures or NoExport), listed or not.</summary>
        private List<FormData.Entry> Entries(IReadOnlyList<string>? names, bool exclude, bool includeEmpty)
        {
            var list = new List<FormData.Entry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in Pick(names, exclude))
            {
                if (f.Type is not ("/Tx" or "/Ch" or "/Btn") || (f.Type == "/Btn" && (f.Flags & FfPushButton) != 0)) continue;
                if (((PdfObj.Int(Inherited(f.Dict, "/Ff")) ?? 0) & FfNoExport) != 0) continue;
                if (!seen.Add(f.Name)) continue;
                var hasValue = PdfObj.Resolve(Inherited(f.Dict, "/V")) is not null;
                var value = ValueOf(f.Dict, f.Type);
                if (!hasValue || (f.Type != "/Btn" && value.Length == 0))
                {
                    if (includeEmpty) list.Add(new FormData.Entry(f.Name, null, false));
                    continue;
                }
                var multi = f.Type == "/Ch" && (f.Flags & FfMultiSelect) != 0;
                list.Add(new FormData.Entry(f.Name, multi ? value.Split('\n') : [value], f.Type == "/Btn"));
            }
            return list;
        }

        /// <summary>Terminal fields named in <paramref name="names"/> (or under them), or all others with <paramref name="exclude"/>; null = all.</summary>
        private IEnumerable<Field> Pick(IReadOnlyList<string>? names, bool exclude)
        {
            if (names is null) return _fields;
            bool Listed(Field f) => names.Any(n => f.Name == n || f.Name.StartsWith(n + ".", StringComparison.Ordinal));
            return _fields.Where(f => Listed(f) != exclude);
        }

        /// <summary>The full names an action's /Fields lists (text strings, or field dictionaries).</summary>
        private static List<string>? FieldNames(PdfItem? item)
        {
            if (PdfObj.Array(item) is not { } arr) return null;
            var names = new List<string>();
            foreach (var e in arr.Elements)
            {
                if (PdfObj.Str(e) is { } s) names.Add(s);
                else if (PdfObj.Dict(e) is { } d && FullName(d) is { Length: > 0 } n) names.Add(n);
            }
            return names;
        }

        private static string? FullName(PdfDictionary d)
        {
            var parts = new List<string>();
            var guard = 0;
            for (var n = d; n is not null && guard++ < 64; n = PdfObj.Dict(n.Elements["/Parent"]))
                if (PdfObj.Str(n.Elements["/T"]) is { } t) parts.Insert(0, t);
            return parts.Count == 0 ? null : string.Join('.', parts);
        }

        /// <summary>A file specification's path or URL (string, or dictionary with /UF, /F).</summary>
        private static string? FileSpec(PdfItem? item) => PdfObj.Resolve(item) switch
        {
            PdfString s => s.Value,
            PdfDictionary d => PdfObj.Text(d.Elements["/UF"]) ?? PdfObj.Text(d.Elements["/F"]) ?? PdfObj.Text(d.Elements["/DOS"]),
            _ => null,
        };

        // ---------------- Changes to the form ----------------

        /// <summary>ResetForm and this.resetForm: listed fields (or all, or all but the listed) back to their defaults.</summary>
        public void Reset(IReadOnlyList<string>? names, bool exclude)
        {
            if (ReadOnly)
            {
                WantedChange = true;
                return;
            }
            Label = "Reset form";
            foreach (var f in Pick(names, exclude).ToList())
            {
                if (f.Type is not ("/Tx" or "/Ch" or "/Btn") || (f.Type == "/Btn" && (f.Flags & FfPushButton) != 0)) continue;
                var dv = PdfObj.Resolve(Inherited(f.Dict, "/DV"));
                switch (f.Type)
                {
                    case "/Tx":
                        if (dv is PdfString s) f.Dict.Elements["/V"] = PdfObj.MakeString(s.Value);
                        else f.Dict.Elements.Remove("/V");
                        f.Dict.Elements.Remove("/RV");
                        break;
                    case "/Ch":
                        SetChoice(_doc, f, dv switch
                        {
                            PdfString ds => ds.Value,
                            PdfArray arr => string.Join('\n', arr.Elements.Select(PdfObj.Str).OfType<string>()),
                            _ => "",
                        });
                        break;
                    default:
                    {
                        var state = dv is PdfName n ? n.Value.TrimStart('/') : "Off";
                        f.Dict.Elements["/V"] = new PdfName("/" + state);
                        foreach (var w in f.Widgets)
                            if (w.Elements.ContainsKey("/AS") || w.Elements.ContainsKey("/AP"))
                                w.Elements["/AS"] = new PdfName("/" + (OnStateOf(w) == state ? state : "Off"));
                        break;
                    }
                }
                Js.Touched.Add(f);
                Changed = true;
            }
        }

        /// <summary>The Hide action: shows or hides the widgets of the named fields (or the given annotations).</summary>
        private void Hide(PdfItem? targets, bool hide)
        {
            var widgets = new List<PdfDictionary>();
            void Add(PdfItem? t, int depth)
            {
                if (depth > 8) return;
                switch (PdfObj.Resolve(t))
                {
                    case PdfArray arr:
                        foreach (var e in arr.Elements) Add(e, depth + 1);
                        break;
                    case PdfString s:
                        foreach (var f in Pick([s.Value], exclude: false)) widgets.AddRange(f.Widgets);
                        break;
                    case PdfDictionary d:
                        if (_fields.FirstOrDefault(f => f.Widgets.Any(w => ReferenceEquals(w, d))) is not null || PdfObj.NameOf(d, "/Subtype").Length > 0) widgets.Add(d);
                        else if (FullName(d) is { } name) foreach (var f in Pick([name], exclude: false)) widgets.AddRange(f.Widgets);
                        break;
                }
            }
            Add(targets, 0);
            if (widgets.Count == 0) return;
            if (ReadOnly)
            {
                WantedChange = true;
                return;
            }
            foreach (var w in widgets.Distinct(ReferenceEqualityComparer.Instance).Cast<PdfDictionary>())
            {
                var flags = PdfObj.Int(w.Elements["/F"]) ?? 0;
                var updated = hide ? flags | AnnotHidden : flags & ~(AnnotHidden | AnnotNoView);
                if (updated == flags) continue;
                w.Elements["/F"] = new PdfInteger(updated);
                Changed = true;
            }
        }

        /// <summary>ImportData and this.importAnFDF: the user picks an FDF/XFDF file (the host asks first); its values fill the form.</summary>
        public void Import(string suggested)
        {
            if (Host is null) return;
            if (ReadOnly)
            {
                WantedChange = true;
                return;
            }
            if (Host.ImportData(suggested.Length == 0 ? null : suggested) is not { } bytes) return;
            if (FormData.Parse(bytes) is not { } values)
            {
                Log.Add("Import: the file isn't FDF or XFDF form data");
                return;
            }
            Label = "Import form data";
            foreach (var f in _fields)
            {
                if (!values.TryGetValue(f.Name, out var v) || v.Count == 0) continue;
                switch (f.Type)
                {
                    case "/Tx":
                        f.Dict.Elements["/V"] = PdfObj.MakeString(v[0]);
                        f.Dict.Elements.Remove("/RV");
                        break;
                    case "/Ch":
                        SetChoice(_doc, f, string.Join('\n', v));
                        break;
                    case "/Btn" when (f.Flags & FfPushButton) == 0:
                        SetButton(f, v[0]);
                        break;
                    default:
                        continue;
                }
                Js.Touched.Add(f);
                Changed = true;
            }
        }

        // ---------------- Finishing ----------------

        /// <summary>
        /// After the run: when fields changed, the calculations run, changed fields get fresh appearances, and the document
        /// is saved; then actions waiting for the finished document (PDF submits, mailDoc) go out.
        /// </summary>
        public FormActionResult Finish()
        {
            byte[]? saved = null;
            if (!ReadOnly && (Js.Touched.Count > 0 || Changed))
            {
                var dirty = Calculate(_doc, _acro, _fields, Js, _alerts);
                foreach (var f in Js.Touched)
                    if (!dirty.Contains(f)) dirty.Add(f);
                foreach (var f in dirty) WriteAppearances(_doc, _acro, f, Js);
                if (_acro is not null)
                {
                    _acro.Elements.Remove("/XFA"); // the AcroForm is now the truth
                    if (dirty.Count > 0)
                    {
                        _acro.Elements["/NeedAppearances"] = new PdfBoolean(true);
                        EnsureDefaultFonts(_doc, _acro);
                    }
                }
                saved = PdfObj.Save(_doc);
            }
            RunDeferred(saved ?? _source);
            var label = Label == "Form action" && Js.Touched.Count > 0 ? "Form script" : Label;
            return new FormActionResult(saved, label, _alerts, Log, FocusField, WantedChange || (ReadOnly && Js.Touched.Count > 0));
        }

        /// <summary>Sends what waited for the finished document (PDF submits, mailDoc).</summary>
        public void RunDeferred(byte[] document)
        {
            foreach (var send in _deferred)
            {
                try
                {
                    send(document);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.Add($"Form action failed: {ex.Message}");
                }
            }
            _deferred.Clear();
        }
    }
}
