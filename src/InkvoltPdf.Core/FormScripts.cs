using System.Globalization;
using System.Text;

namespace InkvoltPdf.Core;

public enum FieldFormatKind { Number, Percent, Date, Special }

/// <summary>
/// A field's display format, from its Acrobat format script (/AA /F): AFNumber_Format, AFPercent_Format,
/// AFDate_FormatEx / AFDate_Format, AFSpecial_Format. The field value (/V) stays raw; only the appearance changes.
/// </summary>
/// <param name="Decimals">Number/percent: digits after the decimal separator.</param>
/// <param name="SeparatorStyle">0 = 1,234.56; 1 = 1234.56; 2 = 1.234,56; 3 = 1234,56; 4 = 1'234.56.</param>
/// <param name="NegativeStyle">0 = minus sign; 1 = red; 2 = parentheses; 3 = red parentheses.</param>
/// <param name="DatePattern">Acrobat date pattern (d, dd, ddd, dddd, m, mm, mmm, mmmm, yy, yyyy, H, HH, h, hh, M, MM, s, ss, t, tt).</param>
/// <param name="Special">AFSpecial_Format: 0 = ZIP, 1 = ZIP+4, 2 = phone, 3 = SSN.</param>
public sealed record FieldFormat(
    FieldFormatKind Kind,
    int Decimals = 2,
    int SeparatorStyle = 0,
    int NegativeStyle = 0,
    string Currency = "",
    bool CurrencyPrepend = true,
    string DatePattern = "",
    int Special = 0);

/// <summary>A field's calculate script (/AA /C) in a form we can evaluate without a JavaScript engine.</summary>
public sealed class FieldCalculation
{
    private readonly Func<Func<string, IReadOnlyList<string>>, double?> _eval;

    internal FieldCalculation(IReadOnlyList<string> inputs, Func<Func<string, IReadOnlyList<string>>, double?> eval)
    {
        Inputs = inputs;
        _eval = eval;
    }

    /// <summary>The field names the script reads.</summary>
    public IReadOnlyList<string> Inputs { get; }

    /// <summary>
    /// Evaluates the script. <paramref name="valuesOf"/> returns the raw values of the terminal fields a name refers to
    /// (the field itself, or every field under it when it names a parent). Null when it can't be computed.
    /// </summary>
    public double? Evaluate(Func<string, IReadOnlyList<string>> valuesOf) => _eval(valuesOf);
}

/// <summary>
/// The common Acrobat form built-ins, interpreted without a JavaScript engine:
/// calculations <c>AFSimple_Calculate("SUM"|"AVG"|"PRD"|"MIN"|"MAX", new Array("a","b"))</c> and simple arithmetic
/// scripts (<c>event.value = this.getField("a").value * getField("b").value + 1;</c>, with <c>var</c> locals,
/// <c>AFMakeNumber</c>, <c>Number</c>, <c>Math.round/floor/ceil/abs/min/max</c>); formats AFNumber_Format,
/// AFPercent_Format, AFDate_FormatEx, AFDate_Format, AFSpecial_Format. Anything else is ignored (returns null).
/// Form scripts normally run in the JavaScript sandbox (<see cref="FormEditor"/>'s JsFormHost); these are the fallback
/// when a script can't run there, and the formatting helpers the sandbox's AF* built-ins use.
/// </summary>
public static class FormScripts
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---------------- Calculations ----------------

    public static FieldCalculation? ParseCalculation(string? script)
    {
        if (string.IsNullOrWhiteSpace(script)) return null;
        try
        {
            var tokens = Tokenize(script);
            // A chain like "1+1+1+…" evaluates as nested closures, one level per operator: keep it far from the stack limit.
            if (tokens.Count > 2000) return null;
            var p = new Parser(tokens);
            return p.Program();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Acrobat's AFMakeNumber: a number from a field value ("1,234.50", "$12", "3,5"), or null when it isn't one.</summary>
    public static double? ToNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        var negative = s.StartsWith('(') && s.EndsWith(')');
        var sb = new StringBuilder();
        foreach (var c in s)
            if (char.IsDigit(c) || c is '.' or ',' or '-' or '+' or 'e' or 'E') sb.Append(c);
        var t = sb.ToString();
        int comma = t.LastIndexOf(','), dot = t.LastIndexOf('.');
        if (comma >= 0 && dot >= 0) t = comma > dot ? t.Replace(".", "").Replace(',', '.') : t.Replace(",", "");
        else if (comma >= 0) t = t.Count(c => c == ',') == 1 ? t.Replace(',', '.') : t.Replace(",", "");
        if (!double.TryParse(t, NumberStyles.Float, Inv, out var v) || double.IsNaN(v) || double.IsInfinity(v)) return null;
        return negative ? -Math.Abs(v) : v;
    }

    /// <summary>A computed number as Acrobat stores it in /V ("15", "0.3", "-2.5").</summary>
    public static string NumberToValue(double v) => Math.Round(v, 10).ToString("0.##########", Inv);

    private sealed class Parser(List<Token> tokens)
    {
        private int _i;
        private readonly List<string> _inputs = [];
        private readonly List<Func<Env, bool>> _statements = [];

        private Token Peek(int ahead = 0) => _i + ahead < tokens.Count ? tokens[_i + ahead] : Token.End;
        private Token Next() => _i < tokens.Count ? tokens[_i++] : Token.End;
        private bool Accept(string text)
        {
            if (Peek().Text != text || Peek().Kind == TokenKind.Str) return false;
            _i++;
            return true;
        }
        private void Expect(string text)
        {
            if (!Accept(text)) throw new FormatException($"expected {text}");
        }

        public FieldCalculation? Program()
        {
            var assignsResult = false;
            while (Peek().Kind != TokenKind.End)
            {
                if (Accept(";")) continue;
                var t = Peek();
                if (t is { Kind: TokenKind.Ident, Text: "AFSimple_Calculate" })
                {
                    _statements.Add(SimpleCalculate());
                    assignsResult = true;
                }
                else if (t is { Kind: TokenKind.Ident, Text: "var" })
                {
                    Next();
                    var name = Ident();
                    Expect("=");
                    var e = Expr();
                    _statements.Add(env => (env.Vars[name] = e(env)) is not null);
                }
                else if (t is { Kind: TokenKind.Ident, Text: "event.value" })
                {
                    Next();
                    Expect("=");
                    var e = Expr();
                    _statements.Add(env => (env.Result = e(env)) is not null);
                    assignsResult = true;
                }
                else if (t.Kind == TokenKind.Ident && Peek(1).Text == "=" && !t.Text.Contains('.'))
                {
                    var name = Ident();
                    Expect("=");
                    var e = Expr();
                    _statements.Add(env => (env.Vars[name] = e(env)) is not null);
                }
                else throw new FormatException("unsupported statement");
                if (Peek().Kind != TokenKind.End) Expect(";");
            }
            if (!assignsResult) return null;
            var statements = _statements.ToList();
            return new FieldCalculation(_inputs.Distinct().ToList(), valuesOf =>
            {
                var env = new Env(valuesOf);
                foreach (var s in statements)
                    if (!s(env)) return null;
                return env.Result;
            });
        }

        private string Ident()
        {
            var t = Next();
            if (t.Kind != TokenKind.Ident || t.Text.Contains('.')) throw new FormatException("expected a name");
            return t.Text;
        }

        private Func<Env, bool> SimpleCalculate()
        {
            Next();
            Expect("(");
            var op = Next();
            if (op.Kind != TokenKind.Str) throw new FormatException("operation");
            Expect(",");
            var names = new List<string>();
            if (Accept("new"))
            {
                if (Next().Text != "Array") throw new FormatException("Array");
                Expect("(");
                StringList(names, ")");
            }
            else if (Accept("[")) StringList(names, "]");
            else if (Peek().Kind == TokenKind.Str)
                names.AddRange(Next().Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            else throw new FormatException("field list");
            Expect(")");
            _inputs.AddRange(names);
            var operation = op.Text.ToUpperInvariant();
            if (operation is not ("SUM" or "AVG" or "PRD" or "MIN" or "MAX")) throw new FormatException("operation");
            return env =>
            {
                var values = names.SelectMany(n => env.ValuesOf(n)).Select(v => ToNumber(v) ?? 0).ToList();
                env.Result = values.Count == 0 ? 0 : operation switch
                {
                    "SUM" => values.Sum(),
                    "AVG" => values.Average(),
                    "PRD" => values.Aggregate(1.0, (a, b) => a * b),
                    "MIN" => values.Min(),
                    _ => values.Max(),
                };
                return true;
            };
        }

        private void StringList(List<string> into, string close)
        {
            while (!Accept(close))
            {
                var t = Next();
                if (t.Kind != TokenKind.Str) throw new FormatException("field name");
                into.Add(t.Text);
                if (!Accept(",") && Peek().Text != close) throw new FormatException("list");
            }
        }

        private Func<Env, double?> Expr()
        {
            var left = Term();
            while (Peek().Text is "+" or "-" && Peek().Kind == TokenKind.Op)
            {
                var op = Next().Text;
                var right = Term();
                var l = left;
                left = op == "+" ? env => l(env) + right(env) : env => l(env) - right(env);
            }
            return left;
        }

        private Func<Env, double?> Term()
        {
            var left = Unary();
            while (Peek().Text is "*" or "/" or "%" && Peek().Kind == TokenKind.Op)
            {
                var op = Next().Text;
                var right = Unary();
                var l = left;
                left = op switch
                {
                    "*" => env => l(env) * right(env),
                    "/" => env => right(env) is { } d && d != 0 ? l(env) / d : 0, // Acrobat shows nothing useful for x/0; 0 keeps totals sane
                    _ => env => right(env) is { } d && d != 0 ? l(env) % d : 0,
                };
            }
            return left;
        }

        // Nesting allowed in one expression ("((((" or "----"): a hostile script must fail to parse, not overflow the stack
        // (which would kill the process) while parsing or evaluating.
        private const int MaxDepth = 64;
        private int _depth;

        private Func<Env, double?> Unary()
        {
            if (++_depth > MaxDepth) throw new FormatException("expression nested too deeply");
            try
            {
                return UnaryCore();
            }
            finally
            {
                _depth--;
            }
        }

        private Func<Env, double?> UnaryCore()
        {
            if (Peek().Kind == TokenKind.Op && Peek().Text == "-")
            {
                Next();
                var e = Unary();
                return env => -e(env);
            }
            if (Peek().Kind == TokenKind.Op && Peek().Text == "+")
            {
                Next();
                return Unary();
            }
            return Primary();
        }

        private Func<Env, double?> Primary()
        {
            var t = Next();
            switch (t.Kind)
            {
                case TokenKind.Num:
                    var n = double.Parse(t.Text, Inv);
                    return _ => n;
                case TokenKind.Op when t.Text == "(":
                    var inner = Expr();
                    Expect(")");
                    return inner;
                case TokenKind.Ident:
                    switch (t.Text)
                    {
                        case "this.getField" or "getField":
                        {
                            Expect("(");
                            var name = Next();
                            if (name.Kind != TokenKind.Str) throw new FormatException("getField");
                            Expect(")");
                            var prop = Next();
                            if (prop.Kind != TokenKind.Ident || prop.Text is not (".value" or ".valueAsString")) throw new FormatException("value");
                            _inputs.Add(name.Text);
                            var field = name.Text;
                            return env => env.ValuesOf(field) is { Count: > 0 } v ? ToNumber(v[0]) ?? 0 : 0;
                        }
                        case "AFMakeNumber" or "Number" or "parseFloat" or "Math.abs" or "Math.round" or "Math.floor" or "Math.ceil":
                        {
                            Expect("(");
                            var arg = Expr();
                            Expect(")");
                            return t.Text switch
                            {
                                "Math.abs" => env => arg(env) is { } v ? Math.Abs(v) : null,
                                "Math.round" => env => arg(env) is { } v ? Math.Floor(v + 0.5) : null,
                                "Math.floor" => env => arg(env) is { } v ? Math.Floor(v) : null,
                                "Math.ceil" => env => arg(env) is { } v ? Math.Ceiling(v) : null,
                                _ => arg,
                            };
                        }
                        case "Math.min" or "Math.max":
                        {
                            Expect("(");
                            var args = new List<Func<Env, double?>> { Expr() };
                            while (Accept(",")) args.Add(Expr());
                            Expect(")");
                            var min = t.Text == "Math.min";
                            return env =>
                            {
                                var vals = args.Select(a => a(env)).ToList();
                                if (vals.Any(v => v is null)) return null;
                                return min ? vals.Min() : vals.Max();
                            };
                        }
                        default:
                            if (t.Text.Contains('.')) throw new FormatException("unsupported member");
                            var local = t.Text;
                            return env => env.Vars.TryGetValue(local, out var v) ? v : null;
                    }
                default:
                    throw new FormatException("unexpected token");
            }
        }
    }

    private sealed class Env(Func<string, IReadOnlyList<string>> valuesOf)
    {
        public Func<string, IReadOnlyList<string>> ValuesOf { get; } = valuesOf;
        public Dictionary<string, double?> Vars { get; } = [];
        public double? Result { get; set; }
    }

    private enum TokenKind { Ident, Num, Str, Op, End }

    private readonly record struct Token(TokenKind Kind, string Text)
    {
        public static readonly Token End = new(TokenKind.End, "");
    }

    /// <summary>JavaScript-ish tokens. Dotted member chains (this.getField, event.value, .value, Math.round) are one identifier.</summary>
    private static List<Token> Tokenize(string s)
    {
        var list = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? s.Length : end + 2;
                continue;
            }
            if (char.IsLetter(c) || c is '_' or '$' || c == '.' && i + 1 < s.Length && char.IsLetter(s[i + 1]))
            {
                var start = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '$' or '.')) i++;
                list.Add(new Token(TokenKind.Ident, s[start..i]));
                continue;
            }
            if (char.IsDigit(c) || c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1]))
            {
                var start = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                if (i < s.Length && s[i] is 'e' or 'E')
                {
                    i++;
                    if (i < s.Length && s[i] is '+' or '-') i++;
                    while (i < s.Length && char.IsDigit(s[i])) i++;
                }
                list.Add(new Token(TokenKind.Num, s[start..i]));
                continue;
            }
            if (c is '"' or '\'')
            {
                var sb = new StringBuilder();
                i++;
                while (i < s.Length && s[i] != c)
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                    {
                        i++;
                        sb.Append(s[i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', var x => x });
                    }
                    else sb.Append(s[i]);
                    i++;
                }
                if (i >= s.Length) throw new FormatException("unterminated string");
                i++;
                list.Add(new Token(TokenKind.Str, sb.ToString()));
                continue;
            }
            if ("()[],;=+-*/%".Contains(c))
            {
                if (c == '=' && i + 1 < s.Length && s[i + 1] == '=') throw new FormatException("comparison");
                list.Add(new Token(TokenKind.Op, c.ToString()));
                i++;
                continue;
            }
            throw new FormatException($"unsupported character {c}");
        }
        // "this.getField(...).value": the tokenizer reads ".value" after ')' as its own identifier.
        return list;
    }

    // ---------------- Formats ----------------

    public static FieldFormat? ParseFormat(string? script)
    {
        if (string.IsNullOrWhiteSpace(script)) return null;
        List<Token> tokens;
        try
        {
            tokens = Tokenize(script);
        }
        catch (FormatException)
        {
            return null;
        }
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Kind != TokenKind.Ident || tokens[i + 1].Text != "(") continue;
            var args = Args(tokens, i + 2);
            if (args is null) continue;
            int Int(int k, int def) => k < args.Count && args[k] is double d ? (int)d : def;
            string Str(int k) => k < args.Count && args[k] is string s ? s : "";
            bool Bool(int k, bool def) => k < args.Count ? args[k] switch { bool b => b, double d => d != 0, _ => def } : def;
            switch (tokens[i].Text)
            {
                case "AFNumber_Format":
                    return new FieldFormat(FieldFormatKind.Number, Math.Clamp(Int(0, 2), 0, 10), Int(1, 0), Int(2, 0), Str(4), Bool(5, true));
                case "AFPercent_Format":
                    return new FieldFormat(FieldFormatKind.Percent, Math.Clamp(Int(0, 2), 0, 10), Int(1, 0), CurrencyPrepend: Bool(2, false));
                case "AFDate_FormatEx" or "AFDate_Format" when args.Count > 0:
                {
                    var pattern = args[0] is string p ? p : DateFormats.ElementAtOrDefault(Int(0, 0)) ?? "m/d/yy";
                    return new FieldFormat(FieldFormatKind.Date, DatePattern: pattern);
                }
                case "AFSpecial_Format":
                    return new FieldFormat(FieldFormatKind.Special, Special: Int(0, 0));
            }
        }
        return null;
    }

    private static readonly string[] DateFormats =
        ["m/d", "m/d/yy", "mm/dd/yy", "mm/yy", "d-mmm", "d-mmm-yy", "dd-mmm-yy", "yy-mm-dd", "mmm-yy", "mmmm-yy", "mmm d, yyyy", "mmmm d, yyyy", "m/d/yy h:MM tt", "m/d/yy HH:MM"];

    private static List<object>? Args(List<Token> tokens, int i)
    {
        var args = new List<object>();
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.Text == ")" && t.Kind == TokenKind.Op) return args;
            var negative = false;
            if (t is { Kind: TokenKind.Op, Text: "-" })
            {
                negative = true;
                t = tokens.ElementAtOrDefault(++i);
            }
            object value = t.Kind switch
            {
                // "1.2.3" tokenizes as a number too: a malformed script must not make every fill of the form fail.
                TokenKind.Num when double.TryParse(t.Text, NumberStyles.Float, Inv, out var num) => num * (negative ? -1 : 1),
                TokenKind.Str => t.Text,
                TokenKind.Ident when t.Text == "true" => true,
                TokenKind.Ident when t.Text == "false" => false,
                _ => null!,
            };
            if (value is null) return null;
            args.Add(value);
            i++;
            if (i < tokens.Count && tokens[i].Text == ",") i++;
        }
        return null;
    }

    /// <summary>The text a format displays for a raw value, and whether it should be drawn red (negative styles 1 and 3).</summary>
    public static (string Text, bool Red) Format(FieldFormat format, string raw)
    {
        if (string.IsNullOrEmpty(raw)) return ("", false);
        switch (format.Kind)
        {
            case FieldFormatKind.Number:
            {
                if (ToNumber(raw) is not { } v) return (raw, false);
                var body = Grouped(Math.Abs(v), format.Decimals, format.SeparatorStyle);
                var rounded = Math.Round(Math.Abs(v), format.Decimals, MidpointRounding.AwayFromZero);
                var negative = v < 0 && rounded != 0;
                if (format.Currency.Length > 0) body = format.CurrencyPrepend ? format.Currency + body : body + format.Currency;
                if (!negative) return (body, false);
                return format.NegativeStyle switch
                {
                    1 => (body, true),
                    2 => ($"({body})", false),
                    3 => ($"({body})", true),
                    _ => ("-" + body, false),
                };
            }
            case FieldFormatKind.Percent:
            {
                if (ToNumber(raw) is not { } v) return (raw, false);
                var body = Grouped(Math.Abs(v * 100), format.Decimals, format.SeparatorStyle);
                var rounded = Math.Round(Math.Abs(v * 100), format.Decimals, MidpointRounding.AwayFromZero);
                var sign = v < 0 && rounded != 0 ? "-" : "";
                return (format.CurrencyPrepend ? sign + "%" + body : sign + body + "%", false);
            }
            case FieldFormatKind.Date:
                return (ParseDate(raw, format.DatePattern) is { } d ? FormatDate(d, format.DatePattern) : raw, false);
            case FieldFormatKind.Special:
            {
                var digits = new string(raw.Where(char.IsDigit).ToArray());
                var text = (format.Special, digits.Length) switch
                {
                    (0, 5) => digits,
                    (1, 9) => $"{digits[..5]}-{digits[5..]}",
                    (2, 10) => $"({digits[..3]}) {digits[3..6]}-{digits[6..]}",
                    (2, 7) => $"{digits[..3]}-{digits[3..]}",
                    (3, 9) => $"{digits[..3]}-{digits[3..5]}-{digits[5..]}",
                    _ => raw,
                };
                return (text, false);
            }
            default:
                return (raw, false);
        }
    }

    private static string Grouped(double v, int decimals, int style)
    {
        var s = Math.Round(v, decimals, MidpointRounding.AwayFromZero).ToString("F" + decimals, Inv);
        var dot = s.IndexOf('.');
        var intPart = dot < 0 ? s : s[..dot];
        var frac = dot < 0 ? "" : s[(dot + 1)..];
        var (group, dec) = style switch { 1 => ("", "."), 2 => (".", ","), 3 => ("", ","), 4 => ("'", "."), _ => (",", ".") };
        var sb = new StringBuilder();
        for (var i = 0; i < intPart.Length; i++)
        {
            if (i > 0 && (intPart.Length - i) % 3 == 0) sb.Append(group);
            sb.Append(intPart[i]);
        }
        return frac.Length > 0 ? sb + dec + frac : sb.ToString();
    }

    private static readonly string[] DateTokens = ["mmmm", "mmm", "mm", "m", "dddd", "ddd", "dd", "d", "yyyy", "yy", "HH", "H", "hh", "h", "MM", "M", "ss", "s", "tt", "t"];

    private static List<(bool Token, string Text)> SplitPattern(string pattern)
    {
        var parts = new List<(bool, string)>();
        for (var i = 0; i < pattern.Length;)
        {
            if (pattern[i] == '\\' && i + 1 < pattern.Length)
            {
                parts.Add((false, pattern[i + 1].ToString()));
                i += 2;
                continue;
            }
            var tok = DateTokens.FirstOrDefault(t => string.CompareOrdinal(pattern, i, t, 0, t.Length) == 0);
            if (tok is not null)
            {
                parts.Add((true, tok));
                i += tok.Length;
            }
            else
            {
                parts.Add((false, pattern[i].ToString()));
                i++;
            }
        }
        return parts;
    }

    /// <summary>Formats a date with an Acrobat pattern (util.printd style; English month/day names).</summary>
    public static string FormatDate(DateTime d, string pattern)
    {
        var sb = new StringBuilder();
        foreach (var (isToken, text) in SplitPattern(pattern))
        {
            if (!isToken)
            {
                sb.Append(text);
                continue;
            }
            sb.Append(text switch
            {
                "mmmm" => d.ToString("MMMM", Inv),
                "mmm" => d.ToString("MMM", Inv),
                "mm" => d.Month.ToString("00", Inv),
                "m" => d.Month.ToString(Inv),
                "dddd" => d.ToString("dddd", Inv),
                "ddd" => d.ToString("ddd", Inv),
                "dd" => d.Day.ToString("00", Inv),
                "d" => d.Day.ToString(Inv),
                "yyyy" => d.Year.ToString("0000", Inv),
                "yy" => (d.Year % 100).ToString("00", Inv),
                "HH" => d.Hour.ToString("00", Inv),
                "H" => d.Hour.ToString(Inv),
                "hh" => (d.Hour % 12 == 0 ? 12 : d.Hour % 12).ToString("00", Inv),
                "h" => (d.Hour % 12 == 0 ? 12 : d.Hour % 12).ToString(Inv),
                "MM" => d.Minute.ToString("00", Inv),
                "M" => d.Minute.ToString(Inv),
                "ss" => d.Second.ToString("00", Inv),
                "s" => d.Second.ToString(Inv),
                "tt" => d.Hour < 12 ? "am" : "pm",
                "t" => d.Hour < 12 ? "a" : "p",
                _ => text,
            });
        }
        return sb.ToString();
    }

    /// <summary>
    /// Reads a typed date the way Acrobat's AFParseDateEx does: in the field's own pattern, then leniently in the same
    /// order (single-digit days and months, two-digit years, any separators: "3/4/25" for dd/mm/yyyy is 3 April), then
    /// ISO and spelled-out month forms. Only a pattern without a day/month order falls back to US and culture parsing.
    /// </summary>
    public static DateTime? ParseDate(string raw, string pattern)
    {
        raw = raw.Trim();
        var parts = SplitPattern(pattern);
        var styles = DateTimeStyles.AllowWhiteSpaces;
        // Exact first, then with every number allowed one or two digits (dd → d, mm → m, HH → H...), same order.
        foreach (var lenient in new[] { false, true })
            if (DateTime.TryParseExact(raw, NetPattern(parts, lenient), Inv, styles, out var exact)) return exact;
        if (DateTime.TryParseExact(raw, ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyyMMdd"], Inv, styles, out var d)) return d;
        if (ParseInOrder(raw, parts) is { } ordered) return ordered;
        string[] named = ["d-MMM-yyyy", "d MMM yyyy", "d MMMM yyyy", "MMM d, yyyy", "MMMM d, yyyy"];
        if (DateTime.TryParseExact(raw, named, Inv, styles, out d)) return d;
        // A pattern that doesn't say whether the day or the month comes first: the usual US and culture forms.
        if (parts.Any(p => p.Token && p.Text is "d" or "dd" or "m" or "mm")) return null;
        string[] common = ["M/d/yyyy", "M/d/yy", "MM/dd/yyyy"];
        if (DateTime.TryParseExact(raw, common, Inv, styles, out d)) return d;
        if (DateTime.TryParse(raw, Inv, styles, out d)) return d;
        return DateTime.TryParse(raw, CultureInfo.CurrentCulture, styles, out d) ? d : null;
    }

    /// <summary>An Acrobat date pattern as a .NET one; <paramref name="lenient"/> lets every number have one or two digits.</summary>
    private static string NetPattern(List<(bool Token, string Text)> parts, bool lenient)
    {
        var net = new StringBuilder();
        foreach (var (isToken, text) in parts)
        {
            if (!isToken)
            {
                net.Append(char.IsLetterOrDigit(text[0]) || text[0] is '\'' or '"' or '%' or '\\' or '/' or ':' ? "'" + text + "'" : text);
                continue;
            }
            net.Append(text switch
            {
                "mmmm" => "MMMM", "mmm" => "MMM",
                "mm" => lenient ? "M" : "MM", "m" => "M",
                "dd" when lenient => "d",
                "HH" when lenient => "H", "hh" when lenient => "h",
                "MM" => lenient ? "m" : "mm", "M" => "m",
                "ss" when lenient => "s",
                "t" => "tt", _ => text,
            });
        }
        return net.ToString();
    }

    private static readonly string[] MonthNames = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames;

    /// <summary>
    /// AFParseDateEx's fallback: the numbers in the text, taken in the order the pattern puts day, month and year (and
    /// then hours, minutes, seconds), a spelled-out month wherever it is; a missing year is this year, a missing day the
    /// 1st; two-digit years are 19xx from 50 up, else 20xx.
    /// </summary>
    private static DateTime? ParseInOrder(string raw, List<(bool Token, string Text)> parts)
    {
        var order = new List<char>();
        foreach (var (isToken, text) in parts)
        {
            if (!isToken) continue;
            var slot = text switch
            {
                "d" or "dd" => 'd',
                "m" or "mm" or "mmm" or "mmmm" => 'm',
                "yy" or "yyyy" => 'y',
                "H" or "HH" or "h" or "hh" => 'H',
                "M" or "MM" => 'M',
                "s" or "ss" => 's',
                _ => '\0',
            };
            if (slot != '\0' && !order.Contains(slot)) order.Add(slot);
        }
        if (!order.Contains('d') && !order.Contains('m')) return null;

        // A month name (or its first three letters) anywhere fills the month.
        int? month = null;
        foreach (System.Text.RegularExpressions.Match word in System.Text.RegularExpressions.Regex.Matches(raw, @"\p{L}+"))
        {
            if (word.Length < 3) continue;
            var i = Array.FindIndex(MonthNames, n => n.Length > 0 && n.StartsWith(word.Value, StringComparison.OrdinalIgnoreCase)
                && (word.Length == n.Length || word.Length == 3));
            if (i >= 0)
            {
                month = i + 1;
                break;
            }
        }
        var numbers = System.Text.RegularExpressions.Regex.Matches(raw, @"\d+").Select(m => m.Value).ToList();
        var slots = order.Where(c => c != 'm' || month is null).ToList();
        if (numbers.Count == 0 || numbers.Count > slots.Count || numbers.Any(n => n.Length > 4)) return null;
        var values = new Dictionary<char, int>();
        for (var i = 0; i < numbers.Count; i++) values[slots[i]] = int.Parse(numbers[i], Inv);
        if (month is { } named) values['m'] = named;
        // A date part left out (other than the year) means the text is too short to read safely.
        if ((order.Contains('d') && !values.ContainsKey('d')) || !values.ContainsKey('m')) return null;
        var year = values.TryGetValue('y', out var y) ? y < 100 && numbers[slots.IndexOf('y')].Length <= 2 ? (y >= 50 ? 1900 + y : 2000 + y) : y : DateTime.Today.Year;
        var hour = values.GetValueOrDefault('H');
        if (parts.Any(p => p.Token && p.Text is "tt" or "t") && hour < 12 && System.Text.RegularExpressions.Regex.IsMatch(raw, @"\bp", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            hour += 12;
        try
        {
            return new DateTime(year, values['m'], values.GetValueOrDefault('d', 1), hour, values.GetValueOrDefault('M'), values.GetValueOrDefault('s'));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
