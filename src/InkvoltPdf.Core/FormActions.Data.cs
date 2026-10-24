using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace InkvoltPdf.Core;

/// <summary>
/// Form data exchange formats: FDF and XFDF (written for submits and mailForm, read for ImportData) and HTML form
/// encoding (written for submits). Field names are full names; hierarchy (a.b.c) becomes nested FDF /Kids and XFDF
/// field elements.
/// </summary>
internal static class FormData
{
    /// <param name="Values">The value (several for a multi-select list); null = the field has no value.</param>
    /// <param name="IsName">Check box / radio state: written as a PDF name in FDF.</param>
    internal sealed record Entry(string Name, IReadOnlyList<string>? Values, bool IsName);

    private sealed class Node(string part)
    {
        public string Part { get; } = part;
        public Entry? Entry { get; set; }
        public List<Node> Kids { get; } = [];
    }

    private static List<Node> Tree(IReadOnlyList<Entry> entries)
    {
        var roots = new List<Node>();
        foreach (var e in entries)
        {
            var level = roots;
            Node? node = null;
            foreach (var part in e.Name.Split('.'))
            {
                node = level.FirstOrDefault(n => n.Part == part);
                if (node is null)
                {
                    node = new Node(part);
                    level.Add(node);
                }
                level = node.Kids;
            }
            node!.Entry = e;
        }
        return roots;
    }

    // ---------------- FDF ----------------

    public static byte[] Fdf(IReadOnlyList<Entry> entries, string? file)
    {
        var sb = new StringBuilder();
        sb.Append("%FDF-1.2\n%âãÏÓ\n1 0 obj\n<< /FDF << /Fields [");
        foreach (var n in Tree(entries)) WriteNode(sb, n);
        sb.Append(']');
        if (file is not null) sb.Append(" /F ").Append(PdfString(file));
        sb.Append(" >> >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static void WriteNode(StringBuilder sb, Node n)
    {
        sb.Append("\n<< /T ").Append(PdfString(n.Part));
        if (n.Entry?.Values is { } values)
        {
            sb.Append(" /V ");
            if (values.Count == 1) sb.Append(n.Entry.IsName ? PdfName(values[0]) : PdfString(values[0]));
            else
            {
                sb.Append('[');
                foreach (var v in values) sb.Append(PdfString(v)).Append(' ');
                sb.Append(']');
            }
        }
        if (n.Kids.Count > 0)
        {
            sb.Append(" /Kids [");
            foreach (var k in n.Kids) WriteNode(sb, k);
            sb.Append(']');
        }
        sb.Append(" >>");
    }

    /// <summary>A PDF string: literal for printable ASCII, else UTF-16BE hex with a byte order mark.</summary>
    private static string PdfString(string s)
    {
        if (s.All(c => c is >= ' ' and < '\u007f' or '\n' or '\r' or '\t'))
        {
            var sb = new StringBuilder("(");
            foreach (var c in s)
                sb.Append(c switch { '(' => "\\(", ')' => "\\)", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => c.ToString() });
            return sb.Append(')').ToString();
        }
        return "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(s)) + ">";
    }

    private static string PdfName(string s)
    {
        var sb = new StringBuilder("/");
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            if (b is > 0x20 and < 0x7f && "#()<>[]{}/%".IndexOf((char)b) < 0) sb.Append((char)b);
            else sb.Append('#').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    // ---------------- XFDF ----------------

    private static readonly XNamespace XfdfNs = "http://ns.adobe.com/xfdf/";

    public static byte[] Xfdf(IReadOnlyList<Entry> entries, string? file)
    {
        XElement Element(Node n)
        {
            var e = new XElement(XfdfNs + "field", new XAttribute("name", n.Part));
            foreach (var k in n.Kids) e.Add(Element(k));
            if (n.Entry?.Values is { } values)
                foreach (var v in values) e.Add(new XElement(XfdfNs + "value", v));
            return e;
        }
        var root = new XElement(XfdfNs + "xfdf", new XAttribute(XNamespace.Xml + "space", "preserve"),
            new XElement(XfdfNs + "fields", Tree(entries).Select(Element)));
        if (file is not null) root.Add(new XElement(XfdfNs + "f", new XAttribute("href", file)));
        using var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
            new XDocument(root).Save(w);
        return ms.ToArray();
    }

    // ---------------- HTML form encoding ----------------

    public static string Html(IReadOnlyList<Entry> entries, Encoding encoding)
    {
        var sb = new StringBuilder();
        foreach (var e in entries)
            foreach (var v in e.Values ?? [""])
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Escape(e.Name, encoding)).Append('=').Append(Escape(v, encoding));
            }
        return sb.ToString();
    }

    private static string Escape(string s, Encoding encoding)
    {
        var sb = new StringBuilder();
        foreach (var b in encoding.GetBytes(s))
        {
            if (b is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9') || b is (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~' or (byte)'*')
                sb.Append((char)b);
            else if (b == (byte)' ') sb.Append('+');
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    // ---------------- Reading FDF / XFDF ----------------

    private const int MaxImportBytes = 16 * 1024 * 1024;

    /// <summary>Field values (full name → values) from FDF or XFDF data, or null if it is neither.</summary>
    public static Dictionary<string, List<string>>? Parse(byte[] data)
    {
        if (data.Length == 0 || data.Length > MaxImportBytes) return null;
        var start = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
        while (start < data.Length && data[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') start++;
        try
        {
            if (start < data.Length && data[start] == (byte)'<') return ParseXfdf(data);
            var text = Encoding.Latin1.GetString(data);
            return text.AsSpan(start).StartsWith("%FDF") ? new FdfParser(text).Fields() : null;
        }
        catch (Exception ex) when (ex is XmlException or FormatException or InvalidOperationException or IndexOutOfRangeException or ArgumentException)
        {
            return null;
        }
    }

    private static Dictionary<string, List<string>> ParseXfdf(byte[] data)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxImportBytes };
        using var reader = XmlReader.Create(new MemoryStream(data), settings);
        var doc = XDocument.Load(reader);
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Walk(XElement e, string? parent, int depth)
        {
            if (depth > 64) return;
            foreach (var f in e.Elements().Where(x => x.Name.LocalName == "field"))
            {
                var part = (string?)f.Attribute("name") ?? "";
                var name = parent is null ? part : $"{parent}.{part}";
                var values = f.Elements().Where(x => x.Name.LocalName is "value" or "value-richtext").Select(x => x.Value).ToList();
                if (values.Count > 0) result[name] = values;
                Walk(f, name, depth + 1);
            }
        }
        foreach (var fields in doc.Descendants().Where(x => x.Name.LocalName == "fields")) Walk(fields, null, 0);
        return result;
    }

    /// <summary>Just enough of the PDF object syntax to read an FDF file's /Fields.</summary>
    private sealed class FdfParser(string s)
    {
        private sealed record Name(string Value);
        private sealed record Ref(int Id);
        private sealed record Keyword(string Value);
        private static readonly object Open = new(), Close = new(), ArrOpen = new(), ArrClose = new();

        private int _p;
        private readonly List<object?> _tokens = [];
        private readonly Dictionary<int, object?> _objects = [];

        public Dictionary<string, List<string>> Fields()
        {
            Lex();
            object? root = null;
            var i = 0;
            while (i < _tokens.Count)
            {
                if (i + 2 < _tokens.Count && _tokens[i] is double id && _tokens[i + 1] is double && _tokens[i + 2] is Keyword { Value: "obj" })
                {
                    i += 3;
                    _objects[(int)id] = Value(ref i);
                }
                else if (_tokens[i] is Keyword { Value: "trailer" })
                {
                    i++;
                    if (Value(ref i) is Dictionary<string, object?> t) root = t.GetValueOrDefault("Root");
                }
                else i++;
            }
            var catalog = Resolve(root) as Dictionary<string, object?>
                ?? _objects.Values.OfType<Dictionary<string, object?>>().FirstOrDefault(d => d.ContainsKey("FDF"));
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (Resolve(catalog?.GetValueOrDefault("FDF")) is Dictionary<string, object?> fdf && Resolve(fdf.GetValueOrDefault("Fields")) is List<object?> fields)
                foreach (var f in fields) Walk(f, null, result, 0);
            return result;
        }

        private object? Resolve(object? o)
        {
            for (var guard = 0; o is Ref r && guard < 32; guard++) o = _objects.GetValueOrDefault(r.Id);
            return o is Ref ? null : o;
        }

        private void Walk(object? item, string? parent, Dictionary<string, List<string>> into, int depth)
        {
            if (depth > 64 || Resolve(item) is not Dictionary<string, object?> d) return;
            var part = Resolve(d.GetValueOrDefault("T")) as string;
            var name = part is null ? parent : parent is null ? part : $"{parent}.{part}";
            if (name is not null && d.TryGetValue("V", out var v))
            {
                var values = Resolve(v) switch
                {
                    string str => [str],
                    Name n => [n.Value],
                    List<object?> list => list.Select(Resolve).Select(x => x switch { string str => str, Name n => n.Value, _ => null }).OfType<string>().ToList(),
                    _ => new List<string>(),
                };
                if (values.Count > 0) into[name] = values;
            }
            if (Resolve(d.GetValueOrDefault("Kids")) is List<object?> kids)
                foreach (var k in kids) Walk(k, name, into, depth + 1);
        }

        private object? Value(ref int i)
        {
            if (i >= _tokens.Count) return null;
            var t = _tokens[i++];
            if (t is double n && i + 1 < _tokens.Count && _tokens[i] is double && _tokens[i + 1] is Keyword { Value: "R" })
            {
                i += 2;
                return new Ref((int)n);
            }
            if (ReferenceEquals(t, Open))
            {
                var d = new Dictionary<string, object?>(StringComparer.Ordinal);
                while (i < _tokens.Count && !ReferenceEquals(_tokens[i], Close))
                {
                    if (_tokens[i++] is not Name key) continue;
                    d[key.Value] = Value(ref i);
                }
                i++;
                return d;
            }
            if (ReferenceEquals(t, ArrOpen))
            {
                var list = new List<object?>();
                while (i < _tokens.Count && !ReferenceEquals(_tokens[i], ArrClose)) list.Add(Value(ref i));
                i++;
                return list;
            }
            return t switch
            {
                Keyword { Value: "true" } => true,
                Keyword { Value: "false" } => false,
                Keyword { Value: "null" } => null,
                _ => t,
            };
        }

        private void Lex()
        {
            while (_p < s.Length && _tokens.Count < 1_000_000)
            {
                var c = s[_p];
                if (char.IsWhiteSpace(c) || c == '\0') _p++;
                else if (c == '%') while (_p < s.Length && s[_p] is not ('\r' or '\n')) _p++;
                else if (c == '<' && At(1) == '<') { _tokens.Add(Open); _p += 2; }
                else if (c == '>' && At(1) == '>') { _tokens.Add(Close); _p += 2; }
                else if (c == '[') { _tokens.Add(ArrOpen); _p++; }
                else if (c == ']') { _tokens.Add(ArrClose); _p++; }
                else if (c == '(') _tokens.Add(Decode(Literal()));
                else if (c == '<') _tokens.Add(Decode(Hex()));
                else if (c == '/') _tokens.Add(new Name(NameToken()));
                else if (c is (>= '0' and <= '9') or '+' or '-' or '.') _tokens.Add(Number());
                else
                {
                    var start = _p;
                    while (_p < s.Length && !char.IsWhiteSpace(s[_p]) && "()<>[]{}/%".IndexOf(s[_p]) < 0) _p++;
                    if (_p == start) _p++;
                    var word = s[start.._p];
                    if (word == "stream")
                    {
                        var end = s.IndexOf("endstream", _p, StringComparison.Ordinal);
                        _p = end < 0 ? s.Length : end + 9;
                    }
                    else _tokens.Add(new Keyword(word));
                }
            }
        }

        private char At(int ahead) => _p + ahead < s.Length ? s[_p + ahead] : '\0';

        private object Number()
        {
            var start = _p;
            _p++;
            while (_p < s.Length && (char.IsDigit(s[_p]) || s[_p] == '.')) _p++;
            return double.TryParse(s.AsSpan(start, _p - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0.0;
        }

        private string NameToken()
        {
            _p++;
            var bytes = new List<byte>();
            while (_p < s.Length && !char.IsWhiteSpace(s[_p]) && "()<>[]{}/%".IndexOf(s[_p]) < 0)
            {
                if (s[_p] == '#' && _p + 2 < s.Length && byte.TryParse(s.AsSpan(_p + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                {
                    bytes.Add(b);
                    _p += 3;
                }
                else bytes.Add((byte)s[_p++]);
            }
            return Encoding.UTF8.GetString([.. bytes]);
        }

        private byte[] Literal()
        {
            _p++;
            var bytes = new List<byte>();
            var depth = 1;
            while (_p < s.Length)
            {
                var c = s[_p++];
                if (c == '\\' && _p < s.Length)
                {
                    var e = s[_p++];
                    switch (e)
                    {
                        case 'n': bytes.Add((byte)'\n'); break;
                        case 'r': bytes.Add((byte)'\r'); break;
                        case 't': bytes.Add((byte)'\t'); break;
                        case 'b': bytes.Add((byte)'\b'); break;
                        case 'f': bytes.Add((byte)'\f'); break;
                        case '\r':
                            if (_p < s.Length && s[_p] == '\n') _p++;
                            break;
                        case '\n': break;
                        case >= '0' and <= '7':
                        {
                            var v = e - '0';
                            for (var k = 0; k < 2 && _p < s.Length && s[_p] is >= '0' and <= '7'; k++) v = v * 8 + (s[_p++] - '0');
                            bytes.Add((byte)v);
                            break;
                        }
                        default: bytes.Add((byte)e); break;
                    }
                    continue;
                }
                if (c == '(') depth++;
                else if (c == ')' && --depth == 0) break;
                bytes.Add((byte)c);
            }
            return [.. bytes];
        }

        private byte[] Hex()
        {
            _p++;
            var digits = new StringBuilder();
            while (_p < s.Length && s[_p] != '>')
            {
                if (Uri.IsHexDigit(s[_p])) digits.Append(s[_p]);
                _p++;
            }
            _p++;
            if (digits.Length % 2 == 1) digits.Append('0');
            return Convert.FromHexString(digits.ToString());
        }

        /// <summary>PDF text string bytes: UTF-16BE or UTF-8 with a byte order mark, else PDFDocEncoding (as Latin-1).</summary>
        private static string Decode(byte[] b) =>
            b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF ? Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2)
            : b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? Encoding.UTF8.GetString(b, 3, b.Length - 3)
            : Encoding.Latin1.GetString(b);
    }
}
