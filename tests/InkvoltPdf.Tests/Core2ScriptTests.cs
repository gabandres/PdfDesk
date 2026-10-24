using InkvoltPdf.Core;

namespace InkvoltPdf.Tests;

public class Core2ScriptTests
{
    private static readonly Dictionary<string, string[]> Values = new()
    {
        ["a"] = ["10"],
        ["b"] = ["2.5"],
        ["c"] = [""],
        ["row.1"] = ["1"],
        ["row.2"] = ["4"],
    };

    private static IReadOnlyList<string> Lookup(string name) =>
        Values.TryGetValue(name, out var v) ? v : [.. Values.Where(kv => kv.Key.StartsWith(name + ".", StringComparison.Ordinal)).SelectMany(kv => kv.Value)];

    [Theory]
    [InlineData("AFSimple_Calculate(\"SUM\", new Array(\"a\", \"b\"));", 12.5)]
    [InlineData("AFSimple_Calculate('PRD', ['a', 'b'])", 25)]
    [InlineData("AFSimple_Calculate(\"AVG\", new Array(\"a\", \"b\", \"c\"));", 12.5 / 3)]
    [InlineData("AFSimple_Calculate(\"MIN\", \"a, b\");", 2.5)]
    [InlineData("AFSimple_Calculate(\"MAX\", new Array(\"a\", \"b\"));", 10)]
    [InlineData("AFSimple_Calculate(\"SUM\", new Array(\"row\"));", 5)]
    [InlineData("event.value = this.getField(\"a\").value * this.getField(\"b\").value;", 25)]
    [InlineData("var x = getField(\"a\").value; var y = getField(\"b\").value; event.value = (x + y) / 5 - -1;", 3.5)]
    [InlineData("event.value = Math.round(AFMakeNumber(getField(\"b\").value)) % 2 // comment", 1)]
    [InlineData("/* total */ event.value = Math.max(getField(\"a\").value, 99);", 99)]
    public void EvaluatesCommonCalculations(string script, double expected)
    {
        var calc = FormScripts.ParseCalculation(script);
        Assert.NotNull(calc);
        Assert.Equal(expected, calc.Evaluate(Lookup)!.Value, 6);
    }

    [Theory]
    [InlineData("if (x > 1) event.value = 2;")]
    [InlineData("event.value = util.printf('%d', 3);")]
    [InlineData("app.alert('hi');")]
    [InlineData("var x = 1;")]
    [InlineData("")]
    public void IgnoresScriptsItCannotRun(string script) => Assert.Null(FormScripts.ParseCalculation(script));

    [Fact]
    public void HostileScriptsNeitherCrashNorThrow()
    {
        // Each of these would overflow the stack (killing the process) while parsing or evaluating if not refused.
        Assert.Null(FormScripts.ParseCalculation("event.value = " + new string('-', 200_000) + "1;"));
        Assert.Null(FormScripts.ParseCalculation("event.value = " + new string('(', 200_000) + "1" + new string(')', 200_000) + ";"));
        Assert.Null(FormScripts.ParseCalculation("event.value = 1" + string.Concat(Enumerable.Repeat("+1", 200_000)) + ";"));
        Assert.Equal((double?)3, FormScripts.ParseCalculation("event.value = ((1 + 2));")!.Evaluate(_ => []));
        Assert.Null(FormScripts.ParseFormat("AFNumber_Format(1.2.3, 0, 0, 0, \"\", true);"));
    }

    [Fact]
    public void ReportsInputs() =>
        Assert.Equal(["a", "b"], FormScripts.ParseCalculation("AFSimple_Calculate(\"SUM\", new Array(\"a\", \"b\"));")!.Inputs);

    [Theory]
    [InlineData("AFNumber_Format(2, 0, 0, 0, \"$\", true);", "1234.5", "$1,234.50", false)]
    [InlineData("AFNumber_Format(2, 2, 0, 0, \" €\", false);", "1234.5", "1.234,50 €", false)]
    [InlineData("AFNumber_Format(0, 1, 2, 0, \"\", true);", "-42.4", "(42)", false)]
    [InlineData("AFNumber_Format(1, 0, 3, 0, \"\", true);", "-1000", "(1,000.0)", true)]
    [InlineData("AFNumber_Format(1, 0, 1, 0, \"\", true);", "-5", "5.0", true)]
    [InlineData("AFNumber_Format(2, 0, 0, 0, \"$\", true);", "-3", "-$3.00", false)]
    [InlineData("AFNumber_Format(2, 0, 0, 0, \"$\", true);", "abc", "abc", false)]
    [InlineData("AFPercent_Format(1, 0);", "0.256", "25.6%", false)]
    [InlineData("AFDate_FormatEx(\"dd mmm yyyy\");", "2026-03-07", "07 Mar 2026", false)]
    [InlineData("AFDate_FormatEx(\"mm/dd/yyyy\");", "3/7/2026", "03/07/2026", false)]
    [InlineData("AFDate_Format(10);", "2026-03-07", "Mar 7, 2026", false)]
    [InlineData("AFDate_FormatEx(\"yyyy-mm-dd HH:MM\");", "2026-03-07T14:05:00", "2026-03-07 14:05", false)]
    [InlineData("AFSpecial_Format(2);", "7875551234", "(787) 555-1234", false)]
    [InlineData("AFSpecial_Format(3);", "123456789", "123-45-6789", false)]
    [InlineData("AFNumber_Format(2, 0, 0, 0, \"$\", true);", "", "", false)]
    public void FormatsValues(string script, string raw, string expected, bool red)
    {
        var fmt = FormScripts.ParseFormat(script);
        Assert.NotNull(fmt);
        Assert.Equal((expected, red), FormScripts.Format(fmt, raw));
    }

    [Theory]
    [InlineData("1,234.50", 1234.5)]
    [InlineData("1.234,50", 1234.5)]
    [InlineData("3,5", 3.5)]
    [InlineData("$ 12", 12)]
    [InlineData("(7)", -7)]
    [InlineData("-0.25", -0.25)]
    public void MakesNumbers(string text, double expected) => Assert.Equal(expected, FormScripts.ToNumber(text));

    [Fact]
    public void NonNumbersAreNull()
    {
        Assert.Null(FormScripts.ToNumber(""));
        Assert.Null(FormScripts.ToNumber("n/a"));
        Assert.Equal("0.3", FormScripts.NumberToValue(0.1 + 0.2));
    }
}
