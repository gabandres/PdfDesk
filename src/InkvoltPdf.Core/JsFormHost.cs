using System.Diagnostics;
using System.Globalization;
using System.Text;
using Acornima.Ast;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;
using PdfSharp.Pdf;

namespace InkvoltPdf.Core;

public static partial class FormEditor
{
    /// <summary>What a field script did with one event: the resulting event.value and event.rc, the app.alert texts it
    /// raised (when no UI host showed them), the text color it set (format scripts drawing negatives red), the keystroke
    /// change and selection it left (live keystrokes), and how many dialogs the host showed for it.</summary>
    internal sealed record JsEventResult(string? Value, bool Rc, IReadOnlyList<string> Alerts, double[]? TextColor,
        string Change = "", int SelStart = 0, int SelEnd = 0, int Shown = 0);

    /// <summary>
    /// Runs a form's Acrobat JavaScript (field /AA actions, button actions and document-level scripts) in a Jint sandbox:
    /// no CLR access, a time, statement, recursion, array and memory budget per script, and only a small Acrobat object
    /// model (event, getField, field objects, app/util/console and the AF* built-ins). Everything that leaves the
    /// document (launchURL, submitForm, mailDoc, print, imports) goes through a <see cref="FormActionRunner"/> and its
    /// <see cref="IFormActionHost"/>, which asks the user first; without a runner (reading a form, live keystrokes) those
    /// calls do nothing, and document-level scripts can never reach them; outward calls are capped per run and per
    /// document per minute, and one the user declines ends the script. Hovering (Mouse Enter/Exit) runs scripts without
    /// dialogs, printing or outward calls, and setFocus is ignored while focus moves (Focus/Blur), so scripts can't bounce
    /// it between fields. Built-ins that build huge strings in one call are refused before they allocate (see the
    /// prelude). Modal dialogs (app.alert, app.response) are
    /// limited per event and per run, and the time they are open doesn't count against the script's budget. A script
    /// that fails, times out or exceeds a limit returns null so callers fall back to the parsed fast paths in
    /// <see cref="FormScripts"/>; errors never escape.
    /// </summary>
    internal sealed class JsFormHost
    {
        private static readonly TimeSpan ScriptTimeout = TimeSpan.FromMilliseconds(500);
        /// <summary>
        /// Setting up the engine (the prelude, the host API) is our own code, but its first run includes JIT compiling Jint
        /// itself, which on a cold or busy machine takes far longer than any form script should: it gets its own limit and
        /// doesn't count against the scripts' budget.
        /// </summary>
        private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(10);
        private const long TotalBudgetMs = 2000; // all scripts of one fill/read together: past it, scripting turns off
        private const int MaxScriptLength = 256 * 1024;
        internal const int MaxDialogsPerEvent = 3;
        private const int MaxDialogsPerRun = 10;

        private readonly PdfDocument _doc;
        private readonly List<Field> _fields;
        private readonly bool _readOnly;
        private readonly FormActionRunner? _runner;
        private readonly Stopwatch _spent = new();
        private readonly PausableTimeout _timeout = new(ScriptTimeout);
        private readonly Dictionary<string, Dictionary<string, JsValue>> _props = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Prepared<Script>> _prepared = new(StringComparer.Ordinal);
        private Engine? _engine;
        private ObjectInstance? _api;
        private bool _failed;
        private bool _inDocumentScripts;
        private List<string>? _eventAlerts;
        private double[]? _eventColor;
        private string? _eventTarget;
        private string? _eventName;
        private int _eventDialogs, _runDialogs, _prints;

        /// <param name="readOnly">Scripts may read fields but their writes are ignored (reading a form for display).</param>
        /// <param name="runner">Carries out document actions (reset, submit, navigation...); null = none reachable.</param>
        public JsFormHost(PdfDocument doc, List<Field> fields, bool readOnly = false, FormActionRunner? runner = null)
        {
            _doc = doc;
            _fields = fields;
            _readOnly = readOnly;
            _runner = runner;
        }

        /// <summary>The engine is running (prelude and document scripts done).</summary>
        internal bool Started => _engine is not null;

        /// <summary>console.println output and script errors, for the app log.</summary>
        public List<string> Log { get; } = [];

        /// <summary>Fields whose value, look or visibility a script changed (they need saving and fresh appearances).</summary>
        public HashSet<Field> Touched { get; } = [];

        /// <summary>Starts a fresh time budget (each live keystroke is its own short run).</summary>
        public void ResetBudget()
        {
            _spent.Reset();
            _runDialogs = 0;
        }

        /// <summary>
        /// Runs one field event: <paramref name="name"/> is Format, Keystroke, Validate, Calculate, or a button event
        /// (Mouse Up, Mouse Down, Mouse Enter, Mouse Exit, Focus, Blur). Null when the script couldn't run (error, limit,
        /// or scripting off), so the caller falls back.
        /// </summary>
        public JsEventResult? Run(string name, Field target, string script, string value, bool willCommit = false,
            string change = "", int selStart = 0, int selEnd = 0)
        {
            if (string.IsNullOrWhiteSpace(script) || script.Length > MaxScriptLength || Engine() is not { } engine) return null;
            _eventAlerts = [];
            _eventColor = null;
            _eventTarget = target.Name;
            _eventName = name;
            _eventDialogs = 0;
            try
            {
                Guarded(() => engine.Invoke(_api!.Get("begin"), name, target.Name, value, willCommit, change, selStart, selEnd));
                if (!_prepared.TryGetValue(script, out var prepared))
                {
                    prepared = Jint.Engine.PrepareScript(script);
                    if (_prepared.Count < 256) _prepared[script] = prepared;
                }
                Guarded(() => engine.Execute(in prepared));
                var end = Guarded(() => engine.Invoke(_api!.Get("end")));
                if (end is not JsArray arr) return null;
                var v = arr.Get(0);
                string? text = v.IsNumber() ? (double.IsFinite(v.AsNumber()) ? FormScripts.NumberToValue(v.AsNumber()) : null)
                    : v.IsString() ? v.AsString() : v.IsBoolean() ? (v.AsBoolean() ? "true" : "false") : "";
                var newChange = arr.Get(2) is var c && c.IsString() ? c.AsString() : change;
                int Pos(JsValue p, int fallback) => p.IsNumber() && double.IsFinite(p.AsNumber()) ? (int)Math.Clamp(p.AsNumber(), -1, 1e7) : fallback;
                return new JsEventResult(text, arr.Get(1).IsBoolean() && arr.Get(1).AsBoolean(), _eventAlerts, _eventColor,
                    newChange, Pos(arr.Get(3), selStart), Pos(arr.Get(4), selEnd), _eventDialogs);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Add($"Form script ({name} of {target.Name}) {(ex is ScriptStoppedException ? "stopped" : "failed")}: {Describe(ex)}");
                return null;
            }
            finally
            {
                _eventAlerts = null;
                _eventTarget = null;
                _eventName = null;
            }
        }

        /// <summary>
        /// Starts the engine now (prelude and document-level scripts) and compiles <paramref name="script"/>, so the first
        /// event doesn't pay for them: call it off the UI thread before live keystrokes start.
        /// </summary>
        public void Prewarm(string? script = null)
        {
            if (Engine() is null || string.IsNullOrWhiteSpace(script) || script.Length > MaxScriptLength || _prepared.ContainsKey(script)) return;
            try
            {
                _prepared[script] = Jint.Engine.PrepareScript(script);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A script that doesn't parse fails (and is logged) when it runs.
            }
        }

        /// <summary>Thrown into a running script to end it at once (script code can't catch it): an outward step was
        /// declined or went over the caps.</summary>
        private sealed class ScriptStoppedException(string message) : Exception(message);

        /// <summary>Ends the running script when the runner was stopped (see <see cref="FormActionRunner.Stopped"/>).</summary>
        private void StopIfStopped(FormActionRunner r)
        {
            if (r.Stopped) throw new ScriptStoppedException("an outward action was declined or the form tried too many");
        }

        private T Guarded<T>(Func<T> run)
        {
            if (_spent.ElapsedMilliseconds > TotalBudgetMs)
            {
                _failed = true;
                throw new TimeoutException("form scripts used up their time budget");
            }
            _spent.Start();
            _timeout.Arm();
            try
            {
                return run();
            }
            finally
            {
                _spent.Stop();
            }
        }

        /// <summary>Engine setup: under <see cref="SetupTimeout"/>, not charged to the scripts' budget.</summary>
        private T Setup<T>(Func<T> run)
        {
            _timeout.Longer = SetupTimeout;
            _timeout.Arm();
            try
            {
                return run();
            }
            finally
            {
                _timeout.Longer = null;
                _timeout.Arm();
            }
        }

        /// <summary>Runs host work (a dialog, a request) without charging its time to the script.</summary>
        private T Outside<T>(Func<T> run)
        {
            var running = _spent.IsRunning;
            _spent.Stop();
            _timeout.Pause();
            try
            {
                return run();
            }
            finally
            {
                _timeout.Resume();
                if (running) _spent.Start();
            }
        }

        private void Outside(Action run) => Outside(() =>
        {
            run();
            return 0;
        });

        private static string Describe(Exception ex) => ex switch
        {
            JavaScriptException js => js.Message,
            TimeoutException => "took too long",
            ScriptStoppedException => ex.Message,
            _ => ex.GetType().Name + ": " + ex.Message,
        };

        private Engine? Engine()
        {
            if (_failed) return null;
            if (_engine is not null) return _engine;
            try
            {
                var timeout = _timeout;
                var engine = new Engine(o =>
                {
                    o.Constraint(timeout);
                    o.MaxStatements(1_000_000);
                    o.LimitRecursion(64);
                    o.LimitMemory(16 * 1024 * 1024);
                    o.Constraints.MaxArraySize = 100_000;
                    o.Constraints.RegexTimeout = TimeSpan.FromMilliseconds(200);
                    o.Culture = CultureInfo.InvariantCulture;
                    o.Interop.Enabled = false; // no CLR types, namespaces or reflection from scripts
                });
                _engine = engine;
                var factory = Setup(() => engine.Evaluate(JsPrelude.Source));
                _api = Setup(() => engine.Invoke(factory, Host(engine))).AsObject();
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
                    // Alerts while the document opens aren't the user's doing: drop them; and nothing may leave the document.
                    _eventAlerts = [];
                    _inDocumentScripts = true;
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
                    _inDocumentScripts = false;
                }
            }
        }

        /// <summary>
        /// The runner, when this script may act on the document or the world: not while document-level scripts run, and
        /// on hover (pointer enter/exit) only for <paramref name="navigation"/>.
        /// </summary>
        private FormActionRunner? Actor(string what, bool navigation = false)
        {
            if (_runner is null) return null;
            if (_inDocumentScripts || _eventAlerts is null)
            {
                Log.Add($"Form script: {what} ignored (not allowed outside a user event)");
                return null;
            }
            if (_runner.Hover && !navigation)
            {
                Log.Add($"Form script: {what} ignored (not run when the pointer only passes over a field)");
                return null;
            }
            return _runner;
        }

        /// <summary>Whether this event may open dialogs (not document-level scripts, not hover).</summary>
        private IFormActionHost? DialogHost => _runner is { Host: { } host, Hover: false } && !_inDocumentScripts && _eventAlerts is not null ? host : null;

        /// <summary>Whether another modal dialog may open for this event; counts it.</summary>
        private bool TakeDialog(string what)
        {
            if (_eventDialogs >= MaxDialogsPerEvent || _runDialogs >= MaxDialogsPerRun)
            {
                Log.Add($"Form script: {what} suppressed (too many dialogs)");
                return false;
            }
            _eventDialogs++;
            _runDialogs++;
            return true;
        }

        // ---------------- Host functions (reachable only through the prelude's closure) ----------------

        private JsObject Host(Engine engine)
        {
            var h = new JsObject(engine);
            void Fn(string name, Func<JsValue[], JsValue> body) =>
                h.Set(name, new ClrFunction(engine, name, (_, args) => body(args)));
            static JsValue Arg(JsValue[] a, int i) => i < a.Length ? a[i] : JsValue.Undefined;
            static string S(JsValue[] a, int i) => i < a.Length && !a[i].IsUndefined() && !a[i].IsNull() ? TypeConverter.ToString(a[i]) : "";
            static int I(JsValue[] a, int i, int def = 0) => i < a.Length && !a[i].IsUndefined() && !a[i].IsNull() && TypeConverter.ToNumber(a[i]) is var d && double.IsFinite(d) ? (int)Math.Clamp(d, -1e6, 1e6) : def;
            static bool B(JsValue[] a, int i) => i < a.Length && TypeConverter.ToBoolean(a[i]);
            static JsValue Str(string? s) => s is null ? JsValue.Null : JsString.Create(s);
            static List<string>? Names(JsValue v) => v is JsArray arr
                ? [.. Enumerable.Range(0, (int)Math.Min(arr.Length, 10_000)).Select(i => arr.Get(i)).Where(x => !x.IsNull() && !x.IsUndefined()).Select(TypeConverter.ToString)]
                : null;

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
            Fn("items", a =>
            {
                // setItems / insertItemAt / deleteItemAt / clearItems: op, field, then the op's arguments.
                if (_readOnly || Find(S(a, 1)) is not { Type: "/Ch" } f) return JsValue.Undefined;
                var (exports, labels) = ReadOptions(Inherited(f.Dict, "/Opt"));
                switch (S(a, 0))
                {
                    case "set":
                        exports.Clear();
                        labels.Clear();
                        if (Arg(a, 2) is JsArray list)
                            for (var i = 0; i < Math.Min(list.Length, 10_000); i++)
                            {
                                var item = list.Get(i);
                                if (item is JsArray pair && pair.Length > 0)
                                {
                                    var label = TypeConverter.ToString(pair.Get(0));
                                    labels.Add(label);
                                    exports.Add(pair.Length > 1 ? TypeConverter.ToString(pair.Get(1)) : label);
                                }
                                else if (!item.IsNull() && !item.IsUndefined())
                                {
                                    labels.Add(TypeConverter.ToString(item));
                                    exports.Add(TypeConverter.ToString(item));
                                }
                            }
                        break;
                    case "insert":
                    {
                        var label = S(a, 2);
                        var export = Arg(a, 3).IsUndefined() || Arg(a, 3).IsNull() ? label : S(a, 3);
                        var at = I(a, 4, 0);
                        at = at < 0 || at > exports.Count ? exports.Count : at;
                        labels.Insert(at, label);
                        exports.Insert(at, export);
                        break;
                    }
                    case "delete":
                    {
                        var at = Arg(a, 2).IsUndefined() ? exports.IndexOf(ValueOf(f.Dict, f.Type)) : I(a, 2, -1);
                        if (at < 0 || at >= exports.Count) return JsValue.Undefined;
                        labels.RemoveAt(at);
                        exports.RemoveAt(at);
                        break;
                    }
                    case "clear":
                        exports.Clear();
                        labels.Clear();
                        break;
                    default:
                        return JsValue.Undefined;
                }
                WriteOptions(f, exports, labels);
                Touched.Add(f);
                return JsValue.Undefined;
            });
            Fn("alert", a =>
            {
                var text = S(a, 0);
                var type = Math.Clamp(I(a, 2), 0, 3);
                // Without a dialog the answer is the cautious one: OK for a notice, Cancel / No for a question.
                var fallback = type switch { 1 => 2, 2 or 3 => 3, _ => 1 };
                if (_runner?.Hover == true)
                {
                    Log.Add("Form script: app.alert suppressed (the pointer only passed over a field)");
                    return fallback;
                }
                if (DialogHost is { } host)
                {
                    if (text.Length == 0 || !TakeDialog("app.alert")) return fallback;
                    var alert = new FormAlert(Clip(text, 4000), Clip(S(a, 3), 200), Math.Clamp(I(a, 1), 0, 3), type);
                    return Outside(() => host.Alert(alert)) is var r and >= 1 and <= 4 ? r : fallback;
                }
                if (_eventAlerts is { Count: < 20 } list && text.Length > 0) list.Add(Clip(text, 2000));
                return fallback;
            });
            Fn("response", a =>
            {
                if (DialogHost is not { } host || !TakeDialog("app.response")) return JsValue.Null;
                var prompt = new FormPrompt(Clip(S(a, 0), 4000), Clip(S(a, 1), 200), Clip(S(a, 2), 10_000), B(a, 3), Clip(S(a, 4), 200));
                return Str(Outside(() => host.Response(prompt)));
            });
            Fn("log", a =>
            {
                if (Log.Count < 200) Log.Add("console: " + S(a, 0));
                return JsValue.Undefined;
            });
            // Outward calls: each counts against the run's caps; when one is declined or capped the script ends here.
            Fn("launchURL", a =>
            {
                if (Actor("app.launchURL") is { } r)
                {
                    Outside(() => r.OpenUrl(S(a, 0)));
                    StopIfStopped(r);
                }
                return JsValue.Undefined;
            });
            Fn("submit", a =>
            {
                // url, format (FDF, XFDF, HTML, PDF...), include empty fields, field names, GET, charset
                if (Actor("submitForm") is { } r && !_readOnly)
                {
                    Outside(() => r.Submit(S(a, 0), S(a, 1), B(a, 2), Names(Arg(a, 3)), exclude: false, B(a, 4), S(a, 5)));
                    StopIfStopped(r);
                }
                return JsValue.Undefined;
            });
            Fn("reset", a =>
            {
                if (_readOnly || _inDocumentScripts || Actor("resetForm") is not { } r) return JsValue.Undefined;
                r.Reset(Names(Arg(a, 0)), exclude: false);
                return JsValue.Undefined;
            });
            Fn("print", a =>
            {
                if (Actor("print") is { } r && _prints++ == 0) Outside(() => r.Print(I(a, 0, -1), I(a, 1, -1)));
                return JsValue.Undefined;
            });
            Fn("mail", a =>
            {
                // kind (doc, form, msg), to, cc, bcc, subject, message
                if (Actor("mail") is { } r)
                {
                    Outside(() => r.Mail(S(a, 0), Clip(S(a, 1), 2000), Clip(S(a, 2), 2000), Clip(S(a, 3), 2000), Clip(S(a, 4), 500), Clip(S(a, 5), 10_000)));
                    StopIfStopped(r);
                }
                return JsValue.Undefined;
            });
            Fn("importData", a =>
            {
                if (Actor("importAnFDF") is { } r && !_readOnly)
                {
                    Outside(() => r.Import(S(a, 0)));
                    StopIfStopped(r);
                }
                return JsValue.Undefined;
            });
            Fn("beep", a =>
            {
                if (Actor("app.beep") is { } r) r.Host?.Beep(Math.Clamp(I(a, 0, 4), 0, 4));
                return JsValue.Undefined;
            });
            Fn("pageNum", _ => _runner?.Host is { } host ? Outside(() => host.CurrentPage) : 0);
            Fn("setPageNum", a =>
            {
                if (Actor("pageNum", navigation: true) is { } r) Outside(() => r.GoToPage(I(a, 0, -1)));
                return JsValue.Undefined;
            });
            Fn("numPages", _ => _doc.PageCount);
            Fn("info", a => Str(_doc.Info.Elements[S(a, 0) switch
            {
                "Title" or "title" => "/Title",
                "Author" or "author" => "/Author",
                "Subject" or "subject" => "/Subject",
                "Keywords" or "keywords" => "/Keywords",
                "Creator" or "creator" => "/Creator",
                "Producer" or "producer" => "/Producer",
                _ => "/None",
            }] is { } item ? PdfObj.Text(item) : null));
            Fn("focus", a =>
            {
                // Moving the focus fires Focus and Blur again: a setFocus from those (or from hovering) would bounce the
                // focus between fields forever, so only other events may move it.
                if (_eventName is "Focus" or "Blur" or "Mouse Enter" or "Mouse Exit")
                {
                    Log.Add($"Form script: setFocus ignored during {_eventName}");
                    return JsValue.Undefined;
                }
                if (_runner is not null && !_inDocumentScripts && Find(S(a, 0)) is not null) _runner.FocusField = S(a, 0);
                return JsValue.Undefined;
            });
            Fn("boxChecked", a =>
            {
                if (Find(S(a, 0)) is not { Type: "/Btn" } f) return JsBoolean.False;
                var current = ValueOf(f.Dict, f.Type);
                if (current is "Off" or "") return JsBoolean.False;
                var i = I(a, 1, -1);
                var states = f.Widgets.Select(OnStateOf).ToList();
                if (states.All(s => s is null)) return JsBoolean.True; // no on looks to tell the widgets apart
                return i >= 0 && i < states.Count && (states[i] ?? i.ToString(CultureInfo.InvariantCulture)) == current ? JsBoolean.True : JsBoolean.False;
            });
            Fn("checkBox", a =>
            {
                // checkThisBox(widget, on): the widget's own state, so widgets sharing an export value stay apart.
                if (_readOnly || Find(S(a, 0)) is not { Type: "/Btn" } f || (f.Flags & FfPushButton) != 0) return JsValue.Undefined;
                var i = I(a, 1, -1);
                if (i < 0 || i >= f.Widgets.Count) return JsValue.Undefined;
                var state = OnStateOf(f.Widgets[i]) ?? i.ToString(CultureInfo.InvariantCulture);
                var current = ValueOf(f.Dict, f.Type);
                if (B(a, 2)) SetButton(f, state);
                else if (current == state) SetButton(f, "Off");
                if (ValueOf(f.Dict, f.Type) != current) Touched.Add(f);
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

        private static string Clip(string s, int max) => s.Length > max ? s[..max] + "…" : s;

        private IEnumerable<Field> Terminals(string name) =>
            name.Length == 0 ? [] : _fields.Where(f => f.Name == name || f.Name.StartsWith(name + ".", StringComparison.Ordinal));

        private Field? Find(string name) => _fields.FirstOrDefault(f => f.Name == name);

        private static int LiveFlags(Field f) => PdfObj.Int(Inherited(f.Dict, "/Ff")) ?? 0;

        private JsValue Get(Engine engine, string name, string prop)
        {
            var f = Find(name);
            if (_props.TryGetValue(name, out var stored) && stored.TryGetValue(prop, out var v)
                && prop is not ("value" or "valueAsString" or "display" or "hidden" or "readonly" or "required" or "numItems"))
                return v;
            if (f is null)
            {
                // A parent name (getField("group")) reads the first of its terminal fields.
                if (Terminals(name).FirstOrDefault() is { } first && prop is not ("value" or "valueAsString")) return Get(engine, first.Name, prop);
                return prop == "value" || prop == "valueAsString" ? JsString.Empty : JsValue.Undefined;
            }
            switch (prop)
            {
                case "value":
                {
                    var raw = ExportValueOf(f);
                    if (f.Type is "/Tx" or "/Ch" && raw.Length > 0 && raw.Trim() == raw
                        && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var num) && double.IsFinite(num))
                        return num;
                    return JsString.Create(raw);
                }
                case "valueAsString": return JsString.Create(ExportValueOf(f));
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
                case "readonly": return (LiveFlags(f) & FfReadOnly) != 0;
                case "required": return (LiveFlags(f) & FfRequired) != 0;
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
                    IEnumerable<string> values = f.Type == "/Ch" ? ReadOptions(Inherited(f.Dict, "/Opt")).Exports
                        : ButtonOptions(f) ?? f.Widgets.Select(OnStateOf).OfType<string>();
                    return new JsArray(engine, [.. values.Select(s => (JsValue)JsString.Create(s))]);
                }
                case "numItems": return f.Type == "/Ch" ? ReadOptions(Inherited(f.Dict, "/Opt")).Exports.Count : 0;
                case "defaultValue":
                {
                    var dv = PdfObj.Resolve(Inherited(f.Dict, "/DV")) switch { PdfString s => s.Value, PdfName n => n.Value.TrimStart('/'), _ => "" };
                    return JsString.Create(f.Type == "/Btn" ? ButtonExport(f, dv) : dv);
                }
                case "fillColor" or "borderColor" or "strokeColor":
                {
                    var mk = f.Widgets.Count > 0 ? PdfObj.Dict(f.Widgets[0].Elements["/MK"]) : null;
                    return ColorArray(engine, PdfObj.Array(mk?.Elements[prop == "fillColor" ? "/BG" : "/BC"]) is { } a
                        ? [.. Enumerable.Range(0, a.Elements.Count).Select(i => PdfObj.Num(a, i))] : []);
                }
                case "textColor":
                {
                    var da = f.Widgets.Count > 0 ? PdfObj.Str(f.Widgets[0].Elements["/DA"]) : null;
                    return ColorArray(engine, DefaultAppearance.Parse(da ?? PdfObj.Str(Inherited(f.Dict, "/DA"))).Color.C);
                }
                default: return JsValue.Undefined;
            }
        }

        private static JsArray ColorArray(Engine engine, double[] c) => c.Length switch
        {
            1 => new JsArray(engine, [JsString.Create("G"), c[0]]),
            3 => new JsArray(engine, [JsString.Create("RGB"), c[0], c[1], c[2]]),
            4 => new JsArray(engine, [JsString.Create("CMYK"), c[0], c[1], c[2], c[3]]),
            _ => new JsArray(engine, [JsString.Create("T")]),
        };

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
                case "readonly" or "required":
                {
                    if (_readOnly) return;
                    var bit = prop == "readonly" ? FfReadOnly : FfRequired;
                    foreach (var f in Terminals(name))
                    {
                        var flags = LiveFlags(f);
                        var updated = TypeConverter.ToBoolean(v) ? flags | bit : flags & ~bit;
                        if (updated == flags) continue;
                        f.Dict.Elements["/Ff"] = new PdfInteger(updated);
                        Touched.Add(f);
                    }
                    return;
                }
                case "textColor" or "fillColor" or "borderColor" or "strokeColor":
                    if (prop == "textColor" && name == _eventTarget) _eventColor = ColorOf(v);
                    if (_readOnly) return; // (a format script's red negatives still apply through the event color)
                    foreach (var f in Terminals(name))
                        if (ApplyColor(f, prop, v)) Touched.Add(f);
                    return;
                default:
                    // Fonts, alignment, ...: remembered for the script's own reads only.
                    if (!_props.TryGetValue(name, out var stored)) _props[name] = stored = new(StringComparer.Ordinal);
                    if (stored.Count < 64) stored[prop] = v;
                    return;
            }
        }

        /// <summary>Writes a field color into its widgets: text color into /DA, fill and border into /MK /BG and /BC.</summary>
        private bool ApplyColor(Field f, string prop, JsValue v)
        {
            var transparent = v is JsArray { Length: >= 1 } t && TypeConverter.ToString(t.Get(0)) == "T";
            var c = ColorOf(v);
            if (c is null && !transparent) return false;
            var changed = false;
            foreach (var w in f.Widgets)
            {
                if (prop == "textColor")
                {
                    if (c is null) continue;
                    var da = DefaultAppearance.Parse(PdfObj.Str(w.Elements["/DA"]) ?? PdfObj.Str(Inherited(f.Dict, "/DA")) ?? "/Helv 0 Tf 0 g");
                    var op = c.Length switch { 1 => "g", 3 => "rg", _ => "k" };
                    var text = $"{da.FontName} {PdfObj.F(da.Size)} Tf {string.Join(' ', c.Select(PdfObj.F))} {op}";
                    w.Elements["/DA"] = new PdfString(text);
                    changed = true;
                    continue;
                }
                if (PdfObj.Dict(w.Elements["/MK"]) is not { } mk)
                {
                    if (transparent) continue;
                    mk = new PdfDictionary(_doc);
                    w.Elements["/MK"] = mk;
                }
                var key = prop == "fillColor" ? "/BG" : "/BC";
                if (c is null) mk.Elements.Remove(key);
                else mk.Elements[key] = PdfObj.Reals(_doc, c);
                changed = true;
            }
            return changed;
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

        /// <summary>
        /// The per-script time limit, measured only while script code runs: the clock stops while the host shows a dialog
        /// or sends a request (see <see cref="Outside{T}"/>), so waiting for the user never times a script out.
        /// </summary>
        private sealed class PausableTimeout(TimeSpan limit) : Constraint
        {
            private readonly Stopwatch _clock = new();
            private int _paused;
            private TimeSpan _limit = TimeSpan.Zero;

            public override bool IsAmortizable => true;

            public override void Check()
            {
                if (_clock.Elapsed > _limit) throw new TimeoutException("form script took too long");
            }

            /// <summary>A longer limit while set (engine setup); Jint re-arms the constraint at every run, so it must stick.</summary>
            public TimeSpan? Longer { get; set; }

            public override void Reset() => Arm();

            public void Arm()
            {
                _limit = Longer ?? limit;
                _paused = 0;
                _clock.Restart();
            }

            public void Pause()
            {
                if (_paused++ == 0) _clock.Stop();
            }

            public void Resume()
            {
                if (_paused > 0 && --_paused == 0) _clock.Start();
            }
        }
    }
}
