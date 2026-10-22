using System.Diagnostics;
using System.Globalization;
using System.Text;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;
using PdfSharp.Pdf;

namespace PdfDesk.Core;

public static partial class FormEditor
{
    /// <summary>What a field script did with one event: the resulting event.value and event.rc, the app.alert texts it
    /// raised, and the text color it set (format scripts drawing negatives red).</summary>
    internal sealed record JsEventResult(string? Value, bool Rc, IReadOnlyList<string> Alerts, double[]? TextColor);

    /// <summary>
    /// Runs a form's Acrobat JavaScript (field /AA actions and document-level scripts) in a Jint sandbox: no CLR access,
    /// a time, statement, recursion, array and memory budget per script, and only a small Acrobat object model
    /// (event, getField, field objects, app.alert/console.println collected as messages, util.printf/printd/scand and the
    /// AF* built-ins). Nothing reaches the network, the file system or the UI: anything else Acrobat offers (launchURL,
    /// submitForm, mailDoc, importDataObject, ...) is absent. A script that fails, times out or exceeds a limit returns
    /// null so callers fall back to the parsed fast paths in <see cref="FormScripts"/>; errors never escape.
    /// </summary>
    internal sealed class JsFormHost
    {
        private static readonly TimeSpan ScriptTimeout = TimeSpan.FromMilliseconds(200);
        private const long TotalBudgetMs = 2000; // all scripts of one fill/read together: past it, scripting turns off
        private const int MaxScriptLength = 256 * 1024;

        private readonly PdfDocument _doc;
        private readonly List<Field> _fields;
        private readonly bool _readOnly;
        private readonly Stopwatch _spent = new();
        private readonly Dictionary<string, Dictionary<string, JsValue>> _props = new(StringComparer.Ordinal);
        private Engine? _engine;
        private ObjectInstance? _api;
        private bool _failed;
        private List<string>? _eventAlerts;
        private double[]? _eventColor;
        private string? _eventTarget;

        /// <param name="readOnly">Scripts may read fields but their writes are ignored (reading a form for display).</param>
        public JsFormHost(PdfDocument doc, List<Field> fields, bool readOnly = false)
        {
            _doc = doc;
            _fields = fields;
            _readOnly = readOnly;
        }

        /// <summary>console.println output and script errors, for the app log.</summary>
        public List<string> Log { get; } = [];

        /// <summary>Fields whose value or visibility a script changed (they need saving and fresh appearances).</summary>
        public HashSet<Field> Touched { get; } = [];

        /// <summary>
        /// Runs one field event: <paramref name="name"/> is Format, Keystroke, Validate or Calculate. Null when the script
        /// couldn't run (error, limit, or scripting off), so the caller falls back.
        /// </summary>
        public JsEventResult? Run(string name, Field target, string script, string value, bool willCommit = false)
        {
            if (string.IsNullOrWhiteSpace(script) || script.Length > MaxScriptLength || Engine() is not { } engine) return null;
            _eventAlerts = [];
            _eventColor = null;
            _eventTarget = target.Name;
            try
            {
                Guarded(() => engine.Invoke(_api!.Get("begin"), name, target.Name, value, willCommit));
                Guarded(() => engine.Execute(script));
                var end = Guarded(() => engine.Invoke(_api!.Get("end")));
                if (end is not JsArray arr) return null;
                var v = arr.Get(0);
                string? text = v.IsNumber() ? (double.IsFinite(v.AsNumber()) ? FormScripts.NumberToValue(v.AsNumber()) : null)
                    : v.IsString() ? v.AsString() : v.IsBoolean() ? (v.AsBoolean() ? "true" : "false") : "";
                return new JsEventResult(text, arr.Get(1).IsBoolean() && arr.Get(1).AsBoolean(), _eventAlerts, _eventColor);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Add($"Form script ({name} of {target.Name}) failed: {Describe(ex)}");
                return null;
            }
            finally
            {
                _eventAlerts = null;
                _eventTarget = null;
            }
        }

        private T Guarded<T>(Func<T> run)
        {
            if (_spent.ElapsedMilliseconds > TotalBudgetMs)
            {
                _failed = true;
                throw new TimeoutException("form scripts used up their time budget");
            }
            _spent.Start();
            try
            {
                return run();
            }
            finally
            {
                _spent.Stop();
            }
        }

        private static string Describe(Exception ex) => ex switch
        {
            JavaScriptException js => js.Message,
            TimeoutException => "took too long",
            _ => ex.GetType().Name + ": " + ex.Message,
        };

        private Engine? Engine()
        {
            if (_failed) return null;
            if (_engine is not null) return _engine;
            try
            {
                var engine = new Engine(o =>
                {
                    o.TimeoutInterval(ScriptTimeout);
                    o.MaxStatements(1_000_000);
                    o.LimitRecursion(64);
                    o.LimitMemory(16 * 1024 * 1024);
                    o.Constraints.MaxArraySize = 100_000;
                    o.Culture = CultureInfo.InvariantCulture;
                    o.Interop.Enabled = false; // no CLR types, namespaces or reflection from scripts
                });
                _engine = engine;
                var factory = Guarded(() => engine.Evaluate(JsPrelude.Source));
                _api = Guarded(() => engine.Invoke(factory, Host(engine))).AsObject();
                RunDocumentScripts(engine);
                return engine;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Add($"Form scripting is unavailable: {Describe(ex)}");
                _failed = true;
                _engine = null;
                return null;
            }
        }

        /// <summary>The document-level scripts (/Names /JavaScript), in name order: they mostly define helper functions.</summary>
        private void RunDocumentScripts(Engine engine)
        {
            var names = PdfObj.Dict(_doc.Internals.Catalog.Elements["/Names"]);
            foreach (var (key, item) in PdfObj.NameTree(PdfObj.Dict(names?.Elements["/JavaScript"])))
            {
                if (PdfObj.Dict(item) is not { } action || PdfObj.Text(action.Elements["/JS"]) is not { Length: > 0 and <= MaxScriptLength } js) continue;
                try
                {
                    _eventAlerts = []; // alerts while the document opens aren't the user's doing: drop them
                    Guarded(() => engine.Execute(js));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.Add($"Document script {key} failed: {Describe(ex)}");
                    if (_failed) return;
                }
                finally
                {
                    _eventAlerts = null;
                }
            }
        }

        // ---------------- Host functions (reachable only through the prelude's closure) ----------------

        private JsObject Host(Engine engine)
        {
            var h = new JsObject(engine);
            void Fn(string name, Func<JsValue[], JsValue> body) =>
                h.Set(name, new ClrFunction(engine, name, (_, args) => body(args)));
            static JsValue Arg(JsValue[] a, int i) => i < a.Length ? a[i] : JsValue.Undefined;
            static string S(JsValue[] a, int i) => i < a.Length && !a[i].IsUndefined() && !a[i].IsNull() ? TypeConverter.ToString(a[i]) : "";
            static int I(JsValue[] a, int i, int def = 0) => i < a.Length && !a[i].IsUndefined() && TypeConverter.ToNumber(a[i]) is var d && double.IsFinite(d) ? (int)Math.Clamp(d, -1e6, 1e6) : def;
            static JsValue Str(string? s) => s is null ? JsValue.Null : JsString.Create(s);

            Fn("exists", a => Terminals(S(a, 0)).Any() ? JsBoolean.True : JsBoolean.False);
            Fn("kids", a => new JsArray(engine, [.. Terminals(S(a, 0)).Select(f => (JsValue)JsString.Create(f.Name)).Distinct()]));
            Fn("count", _ => _fields.Count);
            Fn("nth", a => I(a, 0, -1) is var i && i >= 0 && i < _fields.Count ? JsString.Create(_fields[i].Name) : JsValue.Null);
            Fn("get", a => Get(engine, S(a, 0), S(a, 1)));
            Fn("set", a =>
            {
                Set(S(a, 0), S(a, 1), Arg(a, 2));
                return JsValue.Undefined;
            });
            Fn("item", a =>
            {
                if (Find(S(a, 0)) is not { Type: "/Ch" } f) return JsValue.Null;
                var (exports, labels) = ReadOptions(Inherited(f.Dict, "/Opt"));
                var i = I(a, 1, -1);
                if (i < 0 || i >= exports.Count) return JsValue.Null;
                return JsString.Create(TypeConverter.ToBoolean(Arg(a, 2)) ? exports[i] : labels[i]);
            });
            Fn("alert", a =>
            {
                var text = S(a, 0);
                if (_eventAlerts is { Count: < 20 } list && text.Length > 0) list.Add(text.Length > 2000 ? text[..2000] + "…" : text);
                return JsValue.Undefined;
            });
            Fn("log", a =>
            {
                if (Log.Count < 200) Log.Add("console: " + S(a, 0));
                return JsValue.Undefined;
            });
            Fn("makeNumber", a => FormScripts.ToNumber(S(a, 0)) is { } d ? d : JsValue.Null);
            Fn("fmtNumber", a =>
            {
                if (FormScripts.ToNumber(S(a, 0)) is null) return JsValue.Null;
                var fmt = new FieldFormat(FieldFormatKind.Number, Math.Clamp(I(a, 1, 2), 0, 10), I(a, 2), I(a, 3), S(a, 4), !Arg(a, 5).IsBoolean() || Arg(a, 5).AsBoolean());
                var (text, red) = FormScripts.Format(fmt, S(a, 0));
                return new JsArray(engine, [JsString.Create(text), red ? JsBoolean.True : JsBoolean.False]);
            });
            Fn("fmtPercent", a => FormScripts.ToNumber(S(a, 0)) is null ? JsValue.Null
                : JsString.Create(FormScripts.Format(new FieldFormat(FieldFormatKind.Percent, Math.Clamp(I(a, 1, 2), 0, 10), I(a, 2), CurrencyPrepend: TypeConverter.ToBoolean(Arg(a, 3))), S(a, 0)).Text));
            Fn("fmtDate", a => FormScripts.ParseDate(S(a, 0), S(a, 1)) is { } d ? JsString.Create(FormScripts.FormatDate(d, S(a, 1))) : JsValue.Null);
            Fn("fmtSpecial", a => JsString.Create(FormScripts.Format(new FieldFormat(FieldFormatKind.Special, Special: I(a, 1)), S(a, 0)).Text));
            Fn("parseDate", a => FormScripts.ParseDate(S(a, 0), S(a, 1)) is { } d
                ? new JsArray(engine, [d.Year, d.Month, d.Day, d.Hour, d.Minute, d.Second])
                : JsValue.Null);
            Fn("printd", a =>
            {
                try
                {
                    var d = new DateTime(I(a, 1, 2000), Math.Clamp(I(a, 2, 1), 1, 12), 1).AddDays(I(a, 3, 1) - 1).AddHours(I(a, 4)).AddMinutes(I(a, 5)).AddSeconds(I(a, 6));
                    return JsString.Create(FormScripts.FormatDate(d, S(a, 0)));
                }
                catch (ArgumentOutOfRangeException)
                {
                    return JsValue.Null;
                }
            });
            Fn("printf", a => Str(Printf(a)));
            return h;
        }

        private IEnumerable<Field> Terminals(string name) =>
            name.Length == 0 ? [] : _fields.Where(f => f.Name == name || f.Name.StartsWith(name + ".", StringComparison.Ordinal));

        private Field? Find(string name) => _fields.FirstOrDefault(f => f.Name == name);

        private JsValue Get(Engine engine, string name, string prop)
        {
            var f = Find(name);
            if (_props.TryGetValue(name, out var stored) && stored.TryGetValue(prop, out var v) && prop is not ("value" or "valueAsString" or "display" or "hidden"))
                return v;
            if (f is null) return prop == "value" || prop == "valueAsString" ? JsString.Empty : JsValue.Undefined;
            switch (prop)
            {
                case "value":
                {
                    var raw = ValueOf(f.Dict, f.Type);
                    if (f.Type is "/Tx" or "/Ch" && raw.Length > 0 && raw.Trim() == raw
                        && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var num) && double.IsFinite(num))
                        return num;
                    return JsString.Create(raw);
                }
                case "valueAsString": return JsString.Create(ValueOf(f.Dict, f.Type));
                case "type":
                    return JsString.Create(f.Type switch
                    {
                        "/Tx" => "text",
                        "/Ch" => (f.Flags & FfCombo) != 0 ? "combobox" : "listbox",
                        "/Sig" => "signature",
                        "/Btn" when (f.Flags & FfPushButton) != 0 => "button",
                        "/Btn" when (f.Flags & FfRadio) != 0 => "radiobutton",
                        _ => "checkbox",
                    });
                case "readonly": return (f.Flags & FfReadOnly) != 0;
                case "required": return (f.Flags & FfRequired) != 0;
                case "multiline": return (f.Flags & FfMultiline) != 0;
                case "password": return (f.Flags & FfPassword) != 0;
                case "charLimit": return PdfObj.Int(Inherited(f.Dict, "/MaxLen")) ?? 0;
                case "display" or "hidden":
                {
                    var flags = f.Widgets.Count > 0 ? PdfObj.Int(f.Widgets[0].Elements["/F"]) ?? 0 : 0;
                    var display = (flags & AnnotHidden) != 0 ? 1 : (flags & AnnotNoView) != 0 ? 3 : flags != 0 && (flags & AnnotPrint) == 0 ? 2 : 0;
                    return prop == "hidden" ? display == 1 : display;
                }
                case "exportValues":
                {
                    IEnumerable<string> values = f.Type == "/Ch" ? ReadOptions(Inherited(f.Dict, "/Opt")).Exports : f.Widgets.Select(OnStateOf).OfType<string>();
                    return new JsArray(engine, [.. values.Select(s => (JsValue)JsString.Create(s))]);
                }
                case "numItems": return f.Type == "/Ch" ? ReadOptions(Inherited(f.Dict, "/Opt")).Exports.Count : 0;
                case "defaultValue": return JsString.Create(PdfObj.Resolve(Inherited(f.Dict, "/DV")) switch { PdfString s => s.Value, PdfName n => n.Value.TrimStart('/'), _ => "" });
                default: return JsValue.Undefined;
            }
        }

        private void Set(string name, string prop, JsValue v)
        {
            switch (prop)
            {
                case "value":
                    if (_readOnly) return;
                    var text = v.IsNumber() ? (double.IsFinite(v.AsNumber()) ? FormScripts.NumberToValue(v.AsNumber()) : "") : v.IsNull() || v.IsUndefined() ? "" : TypeConverter.ToString(v);
                    foreach (var f in _fields.Where(f => f.Name == name))
                    {
                        switch (f.Type)
                        {
                            case "/Tx":
                                if (ValueOf(f.Dict, f.Type) == text) continue;
                                f.Dict.Elements["/V"] = PdfObj.MakeString(text);
                                f.Dict.Elements.Remove("/RV");
                                break;
                            case "/Ch":
                                if (ValueOf(f.Dict, f.Type) == text) continue;
                                SetChoice(_doc, f, text);
                                break;
                            case "/Btn" when (f.Flags & FfPushButton) == 0:
                                SetButton(f, text);
                                break;
                            default:
                                continue;
                        }
                        Touched.Add(f);
                    }
                    return;
                case "display" or "hidden":
                {
                    if (_readOnly) return;
                    var display = prop == "hidden" ? (TypeConverter.ToBoolean(v) ? 1 : 0) : (int)TypeConverter.ToNumber(v);
                    foreach (var f in Terminals(name))
                    {
                        foreach (var w in f.Widgets)
                        {
                            var flags = PdfObj.Int(w.Elements["/F"]) ?? 0;
                            var updated = display switch
                            {
                                1 => (flags | AnnotHidden) & ~AnnotNoView,
                                2 => flags & ~(AnnotHidden | AnnotNoView | AnnotPrint),
                                3 => (flags | AnnotNoView | AnnotPrint) & ~AnnotHidden,
                                _ => (flags | AnnotPrint) & ~(AnnotHidden | AnnotNoView),
                            };
                            if (updated == flags) continue;
                            w.Elements["/F"] = new PdfInteger(updated);
                            Touched.Add(f);
                        }
                    }
                    return;
                }
                default:
                    // Colors, fonts, readonly/required toggles...: remembered for the script's own reads only.
                    if (!_props.TryGetValue(name, out var stored)) _props[name] = stored = new(StringComparer.Ordinal);
                    if (stored.Count < 64) stored[prop] = v;
                    if (prop == "textColor" && name == _eventTarget) _eventColor = ColorOf(v);
                    return;
            }
        }

        /// <summary>An Acrobat color array (["RGB", 1, 0, 0], ["G", 0], ["CMYK", ...]) as PDF color components.</summary>
        private static double[]? ColorOf(JsValue v)
        {
            if (v is not JsArray arr || arr.Length < 1) return null;
            var space = TypeConverter.ToString(arr.Get(0));
            var n = space switch { "G" => 1, "RGB" => 3, "CMYK" => 4, _ => 0 };
            if (n == 0 || arr.Length < n + 1) return null;
            var c = new double[n];
            for (var i = 0; i < n; i++)
            {
                var d = arr.Get(i + 1) is { } x && x.IsNumber() ? x.AsNumber() : 0;
                c[i] = double.IsFinite(d) ? Math.Clamp(d, 0, 1) : 0;
            }
            return c;
        }

        /// <summary>
        /// Acrobat's util.printf: %[,sep][flags][width][.precision](d|f|s|x), sep 0 = 1,234.56, 1 = 1234.56,
        /// 2 = 1.234,56, 3 = 1234,56 (default 1); flags + (sign), space, 0 (zero pad), # (ignored), - (left align).
        /// </summary>
        internal static string Printf(JsValue[] a)
        {
            if (a.Length == 0) return "";
            var fmt = TypeConverter.ToString(a[0]);
            var sb = new StringBuilder();
            var next = 1;
            for (var i = 0; i < fmt.Length && sb.Length < 100_000; i++)
            {
                if (fmt[i] != '%' || i + 1 >= fmt.Length)
                {
                    sb.Append(fmt[i]);
                    continue;
                }
                if (fmt[i + 1] == '%')
                {
                    sb.Append('%');
                    i++;
                    continue;
                }
                var j = i + 1;
                var sep = 1;
                if (j + 1 < fmt.Length && fmt[j] == ',' && char.IsDigit(fmt[j + 1]))
                {
                    sep = fmt[j + 1] - '0';
                    j += 2;
                }
                bool plus = false, space = false, zero = false, left = false;
                for (; j < fmt.Length && fmt[j] is '+' or ' ' or '0' or '#' or '-'; j++)
                {
                    plus |= fmt[j] == '+';
                    space |= fmt[j] == ' ';
                    zero |= fmt[j] == '0';
                    left |= fmt[j] == '-';
                }
                var width = 0;
                for (; j < fmt.Length && char.IsDigit(fmt[j]); j++) width = Math.Min(width * 10 + (fmt[j] - '0'), 1000);
                var precision = -1;
                if (j < fmt.Length && fmt[j] == '.')
                {
                    precision = 0;
                    for (j++; j < fmt.Length && char.IsDigit(fmt[j]); j++) precision = Math.Min(precision * 10 + (fmt[j] - '0'), 20);
                }
                if (j >= fmt.Length || fmt[j] is not ('d' or 'f' or 's' or 'x'))
                {
                    sb.Append(fmt, i, Math.Min(j + 1, fmt.Length) - i);
                    i = j;
                    continue;
                }
                var arg = next < a.Length ? a[next++] : JsValue.Undefined;
                string body;
                var sign = "";
                if (fmt[j] == 's') body = arg.IsUndefined() ? "" : TypeConverter.ToString(arg);
                else
                {
                    var d = TypeConverter.ToNumber(arg);
                    if (!double.IsFinite(d)) d = 0;
                    if (fmt[j] == 'x') body = ((long)Math.Truncate(Math.Clamp(d, long.MinValue, long.MaxValue))).ToString("X", CultureInfo.InvariantCulture);
                    else
                    {
                        var decimals = fmt[j] == 'd' ? 0 : precision < 0 ? 6 : precision;
                        if (fmt[j] == 'd') d = Math.Truncate(d);
                        body = FormScripts.Format(new FieldFormat(FieldFormatKind.Number, decimals, Math.Clamp(sep, 0, 4)), FormScripts.NumberToValue(Math.Abs(d))).Text;
                        if (FormScripts.ToNumber(FormScripts.NumberToValue(Math.Abs(d))) is null) body = "0";
                    }
                    sign = d < 0 && body.Any(c => c is >= '1' and <= '9') ? "-" : plus ? "+" : space ? " " : "";
                }
                var len = sign.Length + body.Length;
                if (width > len)
                {
                    if (left) body += new string(' ', width - len);
                    else if (zero && fmt[j] != 's') body = new string('0', width - len) + body;
                    else sign = new string(' ', width - len) + sign;
                }
                sb.Append(sign).Append(body);
                i = j;
            }
            return sb.ToString();
        }
    }
}
