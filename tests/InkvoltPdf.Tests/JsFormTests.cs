using System.Diagnostics;
using System.Text;
using InkvoltPdf.Core;

namespace InkvoltPdf.Tests;

/// <summary>AcroForm JavaScript through the Jint sandbox (FormEditor.Fill / Read).</summary>
public class JsFormTests
{
    private const string W = "/Type /Annot /Subtype /Widget /F 4 /P 3 0 R";

    private sealed record F(string Name, string Rect, Dictionary<string, string>? Scripts = null, string Extra = "");

    /// <summary>A one-page form whose scripts (/AA key → JS) live in streams; optional document-level scripts and /CO.</summary>
    private static byte[] Form(IReadOnlyList<F> fields, IReadOnlyList<string>? docScripts = null, IReadOnlyList<string>? co = null)
    {
        var more = new List<string>();
        var first = 5 + fields.Count;
        int Add(string body)
        {
            more.Add(body);
            return first + more.Count - 1;
        }
        var defs = new List<string>();
        foreach (var f in fields)
        {
            var aa = new StringBuilder();
            foreach (var (key, js) in f.Scripts ?? [])
                aa.Append($"/{key} << /S /JavaScript /JS {Add(Core2Pdf.Stream(js))} 0 R >> ");
            defs.Add($"<< {W} /FT /Tx /T ({f.Name}) /Rect [{f.Rect}] /DA (/Helv 10 Tf 0 g) {f.Extra} {(aa.Length > 0 ? $"/AA << {aa}>>" : "")} >>");
        }
        var catalog = "";
        if (docScripts is { Count: > 0 })
        {
            var entries = docScripts.Select((js, i) => $"(s{i}) {Add($"<< /S /JavaScript /JS {Add(Core2Pdf.Stream(js))} 0 R >>")} 0 R");
            catalog = $"/Names << /JavaScript << /Names [{string.Join(' ', entries)}] >> >>";
        }
        var coRefs = co is null ? "" : $"/CO [{string.Join(' ', co.Select(n => $"{5 + fields.ToList().FindIndex(f => f.Name == n)} 0 R"))}]";
        return Core2Pdf.Form(defs, coRefs, catalogExtra: catalog, more: more);
    }

    private static string Rect(int row) => $"72 {700 - row * 30} 250 {720 - row * 30}";

    private static string ValueOf(byte[] data, string name) => FormEditor.Read(data, null).First(w => w.FieldName == name).Value;

    private static FormFillResult Fill(byte[] form, params (string Name, string Value)[] values) =>
        FormEditor.Fill(form, null, values.ToDictionary(v => v.Name, v => v.Value));

    [Fact]
    public void CustomCalculationRunsInJavaScript()
    {
        var form = Form([
            new("a", Rect(0)),
            new("b", Rect(1), new() { ["C"] = "event.value = this.getField(\"a\").value * 2" }),
            // Beyond the parsed fast path: control flow and string methods.
            new("c", Rect(2), new() { ["C"] = "var v = getField('a').value; if (v > 10) { event.value = 'big:' + String(v).padStart(4, '0'); } else event.value = 'small';" }),
        ], co: ["b", "c"]);
        var r = Fill(form, ("a", "21"));
        Assert.Empty(r.Rejected);
        Assert.Equal("42", ValueOf(r.Data, "b"));
        Assert.Equal("big:0021", ValueOf(r.Data, "c"));
        Assert.True(FormEditor.Read(r.Data, null).First(w => w.FieldName == "c").Calculated);
    }

    [Fact]
    public void FormatScriptsRunThroughJavaScript()
    {
        var form = Form([
            new("amount", Rect(0), new() { ["F"] = "AFNumber_Format(2, 0, 0, 0, \"$\", true); event.value = event.value + ' USD';" }),
            new("plain", Rect(1), new() { ["F"] = "AFNumber_Format(2, 0, 0, 0, \"$\", true);" }),
            new("when", Rect(2), new() { ["F"] = "AFDate_FormatEx('dd mmm yyyy');" }),
            new("phone", Rect(3), new() { ["F"] = "AFSpecial_Format(2);" }),
            new("time", Rect(4), new() { ["F"] = "AFTime_Format(1);" }),
            new("pct", Rect(5), new() { ["F"] = "AFPercent_Format(1, 0);" }),
        ]);
        var r = Fill(form, ("amount", "1234.5"), ("plain", "-3"), ("when", "2026-03-07"), ("phone", "7875551234"), ("time", "14:05"), ("pct", "0.256"));
        var widgets = FormEditor.Read(r.Data, null);
        string Shown(string n) => widgets.First(w => w.FieldName == n).DisplayValue!;
        Assert.Equal("$1,234.50 USD", Shown("amount"));
        Assert.Equal("-$3.00", Shown("plain"));
        Assert.Equal("07 Mar 2026", Shown("when"));
        Assert.Equal("(787) 555-1234", Shown("phone"));
        Assert.Equal("2:05 pm", Shown("time"));
        Assert.Equal("25.6%", Shown("pct"));
        Assert.Contains("$1,234.50 USD", Core2Pdf.AllText(Core2Pdf.WithoutNeedAppearances(r.Data)));
    }

    [Fact]
    public void KeystrokeScriptsCanRejectOrAdjustACommittedValue()
    {
        var form = Form([
            new("n", Rect(0), new() { ["K"] = "AFNumber_Keystroke(2, 0, 0, 0, '', true);" }, "/V (5)"),
            new("code", Rect(1), new() { ["K"] = "if (event.willCommit) { if (event.value.length > 3) { app.alert('Three letters at most.'); event.rc = false; } else event.value = event.value.toUpperCase(); }" }),
            new("zip", Rect(2), new() { ["K"] = "AFSpecial_Keystroke(0);" }),
            new("date", Rect(3), new() { ["K"] = "AFDate_KeystrokeEx('mm/dd/yyyy');" }),
        ]);
        var r = Fill(form, ("n", "abc"));
        Assert.Same(form, r.Data); // nothing else changed: no new document
        Assert.Equal("n", Assert.Single(r.Rejected).FieldName);
        Assert.Contains("does not match the format of the field [ n ]", r.Rejected[0].Message);

        r = Fill(form, ("n", "1,234.5"), ("code", "abcd"), ("zip", "123"), ("date", "02/30/2026"));
        Assert.Equal(["code", "date", "zip"], r.Rejected.Select(x => x.FieldName).Order());
        Assert.Equal("Three letters at most.", r.Rejected.First(x => x.FieldName == "code").Message);
        Assert.Equal("1,234.5", ValueOf(r.Data, "n"));
        Assert.Equal("", ValueOf(r.Data, "code"));

        r = Fill(form, ("code", "abc"), ("zip", "00901"), ("date", "03/07/2026"));
        Assert.Empty(r.Rejected);
        Assert.Equal("ABC", ValueOf(r.Data, "code"));
        Assert.Equal("00901", ValueOf(r.Data, "zip"));
    }

    [Fact]
    public void ValidateScriptsCanRejectAValue()
    {
        var form = Form([
            new("pct", Rect(0), new() { ["V"] = "AFRange_Validate(true, 0, true, 100);" }),
            new("even", Rect(1), new() { ["V"] = "if (event.value % 2) { app.alert({ cMsg: 'Even numbers only', nIcon: 0 }); event.rc = false; }" }),
            new("total", Rect(2), new() { ["C"] = "AFSimple_Calculate('SUM', ['pct', 'even']);" }),
        ]);
        var r = Fill(form, ("pct", "150"), ("even", "3"));
        Assert.Equal(2, r.Rejected.Count);
        Assert.Equal("The value entered must be greater than or equal to 0 and less than or equal to 100.", r.Rejected.First(x => x.FieldName == "pct").Message);
        Assert.Equal("Even numbers only", r.Rejected.First(x => x.FieldName == "even").Message);

        r = Fill(form, ("pct", "50"), ("even", "4"));
        Assert.Empty(r.Rejected);
        Assert.Equal("54", ValueOf(r.Data, "total"));
    }

    [Fact]
    public void DocumentScriptsDefineHelpersAndAlertsAreCollected()
    {
        var form = Form([
            new("a", Rect(0)),
            new("b", Rect(1), new() { ["C"] = "event.value = twice(getField('a').value); if (event.value > 100) app.alert('Over ' + LIMIT);" }),
            new("c", Rect(2)),
            new("d", Rect(3), new() { ["C"] = "getField('c').display = getField('b').value > 100 ? display.hidden : display.visible; console.println('a=' + getField('a').value);" }),
        ], docScripts: ["function twice(x) { return x * 2; }", "var LIMIT = 100; app.alert('opening');"]);
        var r = Fill(form, ("a", "60"));
        Assert.Equal("120", ValueOf(r.Data, "b"));
        Assert.Equal(["Over 100"], r.Alerts); // the document script's own alert isn't the user's doing
        Assert.Contains("console: a=60", r.Log);
        Assert.DoesNotContain(FormEditor.Read(r.Data, null), w => w.FieldName == "c");

        r = Fill(r.Data, ("a", "7"));
        Assert.Contains(FormEditor.Read(r.Data, null), w => w.FieldName == "c");
        Assert.Empty(r.Alerts);
    }

    [Fact]
    public void UtilFunctions()
    {
        var form = Form([
            new("a", Rect(0)),
            new("p", Rect(1), new() { ["C"] = "event.value = util.printf('%,0.2f|%d|%s|%05.1f|%x|%+d', 1234.567, 3.9, 'x', 2.25, 255, 4);" }),
            new("d", Rect(2), new() { ["C"] = "event.value = util.printd('yyyy-mm-dd', new Date(2026, 2, 7)) + ' ' + util.printd('dd/mm/yyyy', util.scand('yyyy-mm-dd', '2026-03-07'));" }),
            new("g", Rect(3), new() { ["C"] = "var f = getField('row'); event.value = f.getArray().length + ':' + f.getArray().map(function (k) { return k.name; }).join(',') + ':' + getField('a').type;" }),
            new("row.1", Rect(4)),
            new("row.2", Rect(5)),
        ]);
        var r = Fill(form, ("a", "1"));
        Assert.Equal("1,234.57|3|x|002.3|FF|+4", ValueOf(r.Data, "p"));
        Assert.Equal("2026-03-07 07/03/2026", ValueOf(r.Data, "d"));
        Assert.Equal("2:row.1,row.2:text", ValueOf(r.Data, "g"));
    }

    [Theory]
    [InlineData("app.openDoc('C:/x.pdf'); event.value = 'ran';")]
    [InlineData("SOAP.request({ cURL: 'http://x' }); event.value = 'ran';")]
    [InlineData("app.execMenuItem('Quit'); event.value = 'ran';")]
    [InlineData("this.exportDataObject({ cName: 'a' }); event.value = 'ran';")]
    [InlineData("while (true) {}")]
    [InlineData("for (;;) { event.value = 'ran'; }")]
    [InlineData("function f(n) { return f(n + 1) + 1; } event.value = f(0);")]
    [InlineData("var a = []; while (true) { a.push(1); }")]
    [InlineData("var s = 'x'; while (true) { s = s + s; }")]
    [InlineData("event.value = { toString: function () { while (true) {} } };")]
    [InlineData("event.value = System.IO.File.ReadAllText('C:/Windows/win.ini');")]
    [InlineData("var File = importNamespace('System.IO').File; event.value = 'ran';")]
    [InlineData("event.value = getField('a').constructor.constructor('return System')().Environment.MachineName;")]
    [InlineData("event.value = require('fs');")]
    public void HostileScriptsTerminateQuicklyWithoutSideEffects(string script)
    {
        var form = Form([new("a", Rect(0)), new("b", Rect(1), new() { ["C"] = script }, "/V (old)")]);
        var sw = Stopwatch.StartNew();
        var r = Fill(form, ("a", "1"));
        Assert.True(sw.ElapsedMilliseconds < 3000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Equal("old", ValueOf(r.Data, "b"));
        Assert.Equal("1", ValueOf(r.Data, "a"));
        Assert.Contains(r.Log, l => l.Contains("failed", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyTheAcrobatModelIsReachable()
    {
        const string probe = "event.value = [typeof System, typeof importNamespace, typeof require, typeof clr, typeof host, typeof h, typeof fetch, typeof XMLHttpRequest, typeof Net, typeof SOAP, " +
            "typeof this.importDataObject, typeof this.exportDataObject, typeof app.openDoc, typeof util.readFileIntoStream, typeof this.saveAs, typeof app.execMenuItem, " +
            "typeof getField, typeof app.alert, typeof AFNumber_Format, typeof app.launchURL, typeof this.submitForm, typeof this.mailDoc, typeof this.print].join(',');";
        var form = Form([new("a", Rect(0)), new("b", Rect(1), new() { ["C"] = probe })]);
        var r = Fill(form, ("a", "1"));
        // Outward calls exist now, but only reach the app's host (which asks the user); without one they do nothing.
        Assert.Equal(string.Join(',', Enumerable.Repeat("undefined", 16)) + ",function,function,function,function,function,function,function", ValueOf(r.Data, "b"));
    }

    [Fact]
    public void BrokenScriptsFallBackToTheParsedFastPaths()
    {
        // A document script that breaks the built-ins makes the scripts throw: the parsed fast paths still compute and format.
        var form = Form([
            new("a", Rect(0)),
            new("b", Rect(1)),
            new("t", Rect(2), new() { ["C"] = "AFSimple_Calculate(\"SUM\", new Array(\"a\", \"b\"));", ["F"] = "AFNumber_Format(2, 0, 0, 0, \"$\", true);" }),
        ], docScripts: ["AFSimple_Calculate = null; AFNumber_Format = null;"]);
        var r = Fill(form, ("a", "2"), ("b", "3"));
        var t = FormEditor.Read(r.Data, null).First(w => w.FieldName == "t");
        Assert.Equal("5", t.Value);
        Assert.Equal("$5.00", t.DisplayValue);
    }

    [Theory]
    [InlineData("'a'.repeat(Math.pow(2, 26)).length")]
    [InlineData("'a'.padStart(Math.pow(2, 26), 'b').length")]
    [InlineData("'a'.padEnd(Math.pow(2, 26)).length")]
    [InlineData("var s = 'x'.repeat(1000000); [s, s, s].join('').length")]
    [InlineData("var s = 'x'.repeat(1000000); String([s, s]).length")]
    [InlineData("var s = 'x'.repeat(600000); s.concat(s).length")]
    [InlineData("var s = 'x'.repeat(100000); s.replace(/x/g, 'yyyyyyyyyyyy').length")]
    [InlineData("var s = 'x'.repeat(1000); s.replaceAll('x', '$&'.repeat(2000)).length")]
    [InlineData("var s = 'x'.repeat(1000); s.replace(/x/g, function () { return 'y'.repeat(5000); }).length")]
    [InlineData("new Array(Math.pow(2, 26)).fill(1).length")]
    [InlineData("Array(Math.pow(2, 26)).join('a').length")]
    public void HugeStringsAreRefusedBeforeTheyAreAllocated(string expression)
    {
        var form = Form([new("a", Rect(0)), new("b", Rect(1), new() { ["C"] = $"event.value = {expression};" })], co: ["b"]);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var r = Fill(form, ("a", "1"));
        Assert.True(sw.ElapsedMilliseconds < 3000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 * 1024 * 1024, "allocated too much");
        Assert.Equal("", ValueOf(r.Data, "b"));
        Assert.Contains(r.Log, l => l.Contains("failed", StringComparison.Ordinal));
    }

    [Fact]
    public void OrdinaryStringBuildingStillWorks()
    {
        var form = Form([
            new("a", Rect(0)),
            new("b", Rect(1), new() { ["C"] = "var p = [1, [2, 3]]; p.push(p); event.value = ['x'.repeat(3), '7'.padStart(3, '0'), [1, 2].join('-'), String(p), " +
                "'a-b'.replace('-', '$&$&'), 'aXbX'.replace(/X/g, function (m) { return m.toLowerCase(); }), 'q'.concat(1, 2), 'ab'.replaceAll('b', 'c')].join('|');" }),
        ], co: ["b"]);
        Assert.Equal("xxx|007|1-2|1,2,3,|a--b|axbx|q12|ac", ValueOf(Fill(form, ("a", "1")).Data, "b"));
    }

    [Theory]
    [InlineData("3/04/2025", "dd/mm/yyyy", 2025, 4, 3)]
    [InlineData("03/4/2025", "dd/mm/yyyy", 2025, 4, 3)]
    [InlineData("3.4.25", "dd.mm.yyyy", 2025, 4, 3)]
    [InlineData("3.04.2025", "dd.mm.yyyy", 2025, 4, 3)]
    [InlineData("3-4-2025", "dd/mm/yyyy", 2025, 4, 3)]
    [InlineData("4/3/2025", "mm/dd/yyyy", 2025, 4, 3)]
    [InlineData("2025-04-03", "dd/mm/yyyy", 2025, 4, 3)]
    [InlineData("3 April 2025", "dd/mm/yyyy", 2025, 4, 3)]
    [InlineData("April 3, 2025", "mm/dd/yyyy", 2025, 4, 3)]
    [InlineData("3 apr 25", "d-mmm-yy", 2025, 4, 3)]
    [InlineData("2025/4/3", "yyyy/mm/dd", 2025, 4, 3)]
    [InlineData("12/1999", "mm/yyyy", 1999, 12, 1)]
    [InlineData("3/4/99", "dd/mm/yy", 1999, 4, 3)]
    public void DatesFollowTheFieldsDayAndMonthOrder(string typed, string pattern, int year, int month, int day)
    {
        Assert.Equal(new DateTime(year, month, day), FormScripts.ParseDate(typed, pattern));
    }

    [Theory]
    [InlineData("13/13/2025", "dd/mm/yyyy")]
    [InlineData("31/02/2025", "dd/mm/yyyy")]
    [InlineData("3", "dd/mm/yyyy")]
    [InlineData("1/2/3/4", "dd/mm/yyyy")]
    public void ImpossibleDatesAreRefused(string typed, string pattern) => Assert.Null(FormScripts.ParseDate(typed, pattern));

    [Fact]
    public void DateKeystrokeAcceptsShortFormsInTheFieldsOrder()
    {
        var form = Form([new("d", Rect(0), new() { ["K"] = "AFDate_KeystrokeEx(\"dd/mm/yyyy\");", ["F"] = "AFDate_FormatEx(\"dd/mm/yyyy\");" })]);
        var r = Fill(form, ("d", "3/4/2025"));
        Assert.Empty(r.Rejected);
        Assert.Equal("03/04/2025", FormEditor.Read(r.Data, null).First(w => w.FieldName == "d").DisplayValue);
    }
}
