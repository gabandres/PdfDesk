using System.Security;
using System.Text;
using PdfSharp.Pdf;

namespace InkvoltPdf.Core;

/// <summary>Square (rectangle) or Circle (ellipse) annotation.</summary>
public enum ShapeKind { Rectangle, Ellipse }

/// <summary>A line ending (/LE). New annotations use None, OpenArrow, ClosedArrow, Circle or Square; the rest are read and drawn too.</summary>
public enum LineEnding { None, OpenArrow, ClosedArrow, Circle, Square, Diamond, Butt, ROpenArrow, RClosedArrow, Slash }

/// <summary>FreeText alignment (/Q).</summary>
public enum TextAlign { Left, Center, Right }

/// <summary>
/// How a drawing annotation looks. Shapes, lines and ink use <see cref="Color"/> (/C, the stroke; null = no stroke),
/// <see cref="Width"/> (/BS /W), <see cref="Dash"/> (/BS /D), <see cref="Fill"/> (/IC: shape and polygon interiors,
/// closed line endings) and <see cref="Opacity"/> (/CA). FreeText: <see cref="Color"/> and <see cref="Width"/> are the
/// box border (null = none: a typewriter), <see cref="Fill"/> the background, <see cref="FontSize"/>,
/// <see cref="TextColor"/> and <see cref="Align"/> the text, <see cref="EndEnding"/> the callout's end at its tip.
/// </summary>
public sealed record AnnotStyle
{
    public static readonly AnnotColor Black = new(0, 0, 0);

    public AnnotColor? Color { get; init; } = AnnotColor.Red;
    public AnnotColor? Fill { get; init; }
    public double Width { get; init; } = 2;
    public double Opacity { get; init; } = 1;
    /// <summary>Dash pattern in points (e.g. [3, 3]); null or empty = solid.</summary>
    public IReadOnlyList<double>? Dash { get; init; }
    /// <summary>Ink highlighter: drawn with the Multiply blend mode, so the text underneath stays readable.</summary>
    public bool Highlighter { get; init; }
    public LineEnding StartEnding { get; init; }
    public LineEnding EndEnding { get; init; }
    public double FontSize { get; init; } = 12;
    public AnnotColor TextColor { get; init; } = Black;
    public TextAlign Align { get; init; }

    public static AnnotStyle Pen(AnnotColor color, double width = 2) => new() { Color = color, Width = width };

    public static AnnotStyle Marker(AnnotColor? color = null, double width = 14) =>
        new() { Color = color ?? AnnotColor.Yellow, Width = width, Highlighter = true };

    public static AnnotStyle Arrow(AnnotColor color, double width = 2) => new() { Color = color, Width = width, EndEnding = LineEnding.OpenArrow };

    public static AnnotStyle Typewriter(double fontSize = 12, AnnotColor? textColor = null) =>
        new() { Color = null, Width = 0, FontSize = fontSize, TextColor = textColor ?? Black };

    public static AnnotStyle TextBox(double fontSize = 12, AnnotColor? border = null, AnnotColor? fill = null) =>
        new() { Color = border ?? AnnotColor.Red, Width = 1, Fill = fill, FontSize = fontSize, EndEnding = LineEnding.OpenArrow };
}

/// <summary>
/// Where a drawing annotation is, in page space. Ink: <see cref="Paths"/> = the strokes; Line: one path [start, end];
/// Polygon / PolyLine: one path of vertices; Square / Circle: <see cref="Box"/>; FreeText: <see cref="Box"/> = the text
/// box and <see cref="Callout"/> = the leader line's tip [, knee] (it is joined to the nearest side of the box).
/// For point-based kinds <see cref="Annotations.Read"/> sets <see cref="Box"/> to the points' bounds (ignored on write).
/// </summary>
public sealed record AnnotGeometry
{
    public RectD Box { get; init; }
    public IReadOnlyList<IReadOnlyList<PointD>> Paths { get; init; } = [];
    public IReadOnlyList<PointD> Callout { get; init; } = [];

    /// <summary>Everything the geometry covers: the box, every point and the callout (what a selection frame shows).</summary>
    public RectD Bounds
    {
        get
        {
            double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
            void Add(double x, double y)
            {
                l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); b = Math.Max(b, y);
            }
            if (!Box.IsEmpty)
            {
                Add(Box.X, Box.Y);
                Add(Box.Right, Box.Bottom);
            }
            foreach (var p in Paths.SelectMany(p => p).Concat(Callout)) Add(p.X, p.Y);
            return l > r ? default : new RectD(l, t, r - l, b - t);
        }
    }

    /// <summary>The geometry moved and scaled so that its <see cref="Bounds"/> become <paramref name="bounds"/>.</summary>
    public AnnotGeometry MapTo(RectD bounds)
    {
        var from = Bounds;
        var sx = from.Width > 0 ? bounds.Width / from.Width : 1;
        var sy = from.Height > 0 ? bounds.Height / from.Height : 1;
        PointD M(PointD p) => new(bounds.X + (p.X - from.X) * sx, bounds.Y + (p.Y - from.Y) * sy);
        var box = Box.IsEmpty ? Box : RectD.FromEdges(M(new(Box.X, Box.Y)).X, M(new(Box.X, Box.Y)).Y, M(new(Box.Right, Box.Bottom)).X, M(new(Box.Right, Box.Bottom)).Y);
        return new AnnotGeometry
        {
            Box = box,
            Paths = [.. Paths.Select(p => (IReadOnlyList<PointD>)[.. p.Select(M)])],
            Callout = [.. Callout.Select(M)],
        };
    }
}

/// <summary>
/// Drawing and markup annotations: ink (pen and highlighter), rectangles, ellipses, lines and arrows, polygons and
/// polylines, and FreeText boxes (typewriter, text box, callout). Each gets its own appearance stream (/AP /N), built
/// from the annotation's own entries, so Acrobat, Edge, Chrome and MuPDF draw it the same; editing the geometry or the
/// style rewrites the entries and rebuilds the appearance, keeping the annotation's id.
/// </summary>
public static partial class Annotations
{
    private const int MaxPoints = 200_000;

    // ---------------- Add ----------------

    /// <summary>Adds freehand ink: one or more strokes (page space), drawn smoothed (Catmull-Rom curves through the points).</summary>
    public static byte[] AddInk(byte[] data, string? password, int pageIndex, IReadOnlyList<IReadOnlyList<PointD>> strokes, AnnotStyle style,
        string author, string? id = null) =>
        AddDrawing(data, password, pageIndex, "Ink", new AnnotGeometry { Paths = strokes }, style, author, "", id);

    /// <summary>Adds a rectangle (Square) or ellipse (Circle) filling <paramref name="box"/> (page space; the stroke lies inside it).</summary>
    public static byte[] AddShape(byte[] data, string? password, int pageIndex, ShapeKind kind, RectD box, AnnotStyle style,
        string author, string? id = null) =>
        AddDrawing(data, password, pageIndex, kind == ShapeKind.Ellipse ? "Circle" : "Square", new AnnotGeometry { Box = box }, style, author, "", id);

    /// <summary>Adds a line (an arrow with <see cref="AnnotStyle.EndEnding"/> = OpenArrow, see <see cref="AnnotStyle.Arrow"/>).</summary>
    public static byte[] AddLine(byte[] data, string? password, int pageIndex, PointD start, PointD end, AnnotStyle style,
        string author, string? id = null) =>
        AddDrawing(data, password, pageIndex, "Line", new AnnotGeometry { Paths = [[start, end]] }, style, author, "", id);

    /// <summary>Adds a closed polygon or an open polyline through <paramref name="vertices"/> (page space).</summary>
    public static byte[] AddPolygon(byte[] data, string? password, int pageIndex, IReadOnlyList<PointD> vertices, bool closed, AnnotStyle style,
        string author, string? id = null) =>
        AddDrawing(data, password, pageIndex, closed ? "Polygon" : "PolyLine", new AnnotGeometry { Paths = [vertices] }, style, author, "", id);

    /// <summary>
    /// Adds a FreeText box at <paramref name="box"/> (page space): the text wraps to the box and reads upright on rotated
    /// pages; any script works (an embedded subset font when Helvetica can't encode it). With <paramref name="callout"/>
    /// (tip [, knee]) it is a callout whose leader line runs from the tip to the box.
    /// </summary>
    public static byte[] AddFreeText(byte[] data, string? password, int pageIndex, RectD box, string text, AnnotStyle style, string author,
        IReadOnlyList<PointD>? callout = null, string? id = null) =>
        AddDrawing(data, password, pageIndex, "FreeText", new AnnotGeometry { Box = box, Callout = callout ?? [] }, style, author, text, id);

    /// <summary>The height a FreeText box of <paramref name="width"/> needs to show <paramref name="text"/> without clipping.</summary>
    public static double FreeTextHeight(string text, double width, AnnotStyle style)
    {
        var s = Normalize(style);
        var pad = TextPad(s);
        var painter = new TextPainter(StdFont.Helvetica, [text]);
        var lines = painter.Wrap(text, Math.Max(1, width - 2 * pad), s.FontSize);
        return 2 * pad + (lines.Count - 1) * s.FontSize * LineSpacing + (painter.Ascent - painter.Descent) * s.FontSize;
    }

    // ---------------- Edit ----------------

    /// <summary>Moves / reshapes a drawing annotation (page space) and rebuilds its appearance. The id stays the same.</summary>
    public static byte[] UpdateGeometry(byte[] data, string? password, string id, AnnotGeometry geometry) =>
        Redraw(data, password, id, (g, s) => (geometry, s));

    /// <summary>Moves and resizes a drawing annotation so that its <see cref="AnnotGeometry.Bounds"/> become <paramref name="bounds"/>.</summary>
    public static byte[] SetBounds(byte[] data, string? password, string id, RectD bounds) =>
        Redraw(data, password, id, (g, s) => (g.MapTo(bounds), s));

    /// <summary>Restyles a drawing annotation (color, width, fill, dash, opacity, endings, font size...) and rebuilds its appearance.</summary>
    public static byte[] UpdateStyle(byte[] data, string? password, string id, AnnotStyle style) =>
        Redraw(data, password, id, (g, s) => (g, style));

    private static byte[] Redraw(byte[] data, string? password, string id, Func<AnnotGeometry, AnnotStyle, (AnnotGeometry, AnnotStyle)> change)
    {
        using var doc = OpenForEdit(data, password);
        var (p, annot) = Find(doc, id) ?? throw new InvalidOperationException("That comment no longer exists.");
        var page = doc.Pages[p];
        var subtype = PdfObj.Name(annot.Elements["/Subtype"]) ?? "";
        if (!IsDrawing(annot, subtype) || Describe(page, annot, subtype) is not { } current)
            throw new InvalidOperationException("This comment can't be reshaped or restyled.");
        var (g, s) = change(current.Geometry, current.Style);
        Validate(subtype, g, s);
        Apply(doc, page, annot, subtype, g, s);
        return PdfObj.Save(doc);
    }

    private static byte[] AddDrawing(byte[] data, string? password, int pageIndex, string subtype, AnnotGeometry g, AnnotStyle s,
        string author, string contents, string? id)
    {
        Validate(subtype, g, s);
        using var doc = OpenForEdit(data, password);
        if (pageIndex < 0 || pageIndex >= doc.PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        var page = doc.Pages[pageIndex];
        var annot = NewAnnot(doc, page, subtype, 0, 0, 0, 0, id, contents, author, s.Color ?? AnnotStyle.Black);
        Apply(doc, page, annot, subtype, g, s);
        return PdfObj.Save(doc);
    }

    /// <summary>A drawing annotation Inkvolt PDF can describe and redraw (Fill &amp; Sign stamps are managed by <see cref="TextStamps"/>).</summary>
    private static bool IsDrawing(PdfDictionary a, string subtype) =>
        subtype is "Ink" or "Square" or "Circle" or "Line" or "Polygon" or "PolyLine"
        || subtype == "FreeText" && !TextStamps.IsOwnId(PdfObj.Str(a.Elements["/NM"]));

    private static void Validate(string subtype, AnnotGeometry g, AnnotStyle s)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(s);
        var points = g.Paths.SelectMany(p => p).Concat(g.Callout).ToList();
        if (points.Count > MaxPoints) throw new ArgumentException("Too many points.", nameof(g));
        if (points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y))
            || !double.IsFinite(g.Box.X) || !double.IsFinite(g.Box.Y) || !double.IsFinite(g.Box.Width) || !double.IsFinite(g.Box.Height))
            throw new ArgumentException("The position isn't a number.", nameof(g));
        if (!double.IsFinite(s.Width) || !double.IsFinite(s.Opacity) || !double.IsFinite(s.FontSize) || s.Dash?.Any(d => !double.IsFinite(d)) == true)
            throw new ArgumentException("The style isn't valid.", nameof(s));
        switch (subtype)
        {
            case "Ink":
                if (!g.Paths.Any(p => p.Count > 0)) throw new ArgumentException("Nothing was drawn.", nameof(g));
                break;
            case "Line":
                if (g.Paths.Count != 1 || g.Paths[0].Count != 2) throw new ArgumentException("A line needs a start and an end.", nameof(g));
                break;
            case "Polygon" or "PolyLine":
                if (g.Paths.Count != 1 || g.Paths[0].Count < (subtype == "Polygon" ? 3 : 2)) throw new ArgumentException("Not enough vertices.", nameof(g));
                break;
            case "Square" or "Circle" or "FreeText":
                if (g.Box.IsEmpty) throw new ArgumentException("The box is empty.", nameof(g));
                if (g.Callout.Count > 2) throw new ArgumentException("A callout has a tip and at most one knee.", nameof(g));
                break;
        }
        if (subtype is "Ink" or "Line" or "PolyLine" && (s.Color is null || s.Width <= 0))
            throw new ArgumentException("A line needs a color and a width.", nameof(s));
        if (subtype is "Square" or "Circle" or "Polygon" && (s.Color is null || s.Width <= 0) && s.Fill is null)
            throw new ArgumentException("A shape needs a border or a fill.", nameof(s));
    }

    private static AnnotStyle Normalize(AnnotStyle s) => s with
    {
        Width = Math.Clamp(s.Width, 0, 200),
        Opacity = Math.Clamp(s.Opacity, 0, 1),
        FontSize = Math.Clamp(s.FontSize, 1, 500),
        Dash = s.Dash is { Count: > 0 } d && d.All(v => v >= 0) && d.Any(v => v > 0) ? [.. d.Take(8).Select(v => Math.Min(v, 1000))] : null,
    };

    // ---------------- Write ----------------

    private const double LineSpacing = 1.2;

    private static double TextPad(AnnotStyle s) => (s.Color is null ? 0 : s.Width) + 2;

    private static double EndingSize(double w) => Math.Max(6, w * 4 + 2);

    private static string Rgb(AnnotColor c) => $"{PdfObj.F(c.R)} {PdfObj.F(c.G)} {PdfObj.F(c.B)}";

    /// <summary>Writes the annotation's geometry and style entries and its appearance stream.</summary>
    private static void Apply(PdfDocument doc, PdfPage page, PdfDictionary annot, string subtype, AnnotGeometry g, AnnotStyle style)
    {
        var s = Normalize(style);
        var e = annot.Elements;
        var stroke = s.Color is not null && s.Width > 0;
        var w = stroke ? s.Width : 0;
        var fill = s.Fill is not null && subtype is not ("Ink" or "Line" or "PolyLine");

        if (s.Color is { } c) e["/C"] = PdfObj.Reals(doc, c.R, c.G, c.B);
        else e.Remove("/C");
        if (s.Fill is { } f && subtype != "Ink") e["/IC"] = PdfObj.Reals(doc, f.R, f.G, f.B);
        else e.Remove("/IC");
        var bs = new PdfDictionary(doc);
        bs.Elements["/Type"] = new PdfName("/Border");
        bs.Elements["/W"] = new PdfReal(Math.Round(w, 3));
        bs.Elements["/S"] = new PdfName(s.Dash is null ? "/S" : "/D");
        if (s.Dash is { } dash) bs.Elements["/D"] = PdfObj.Reals(doc, [.. dash]);
        e["/BS"] = bs;
        e.Remove("/Border");
        e.Remove("/BE");
        if (s.Opacity < 1) e["/CA"] = new PdfReal(Math.Round(s.Opacity, 3));
        else e.Remove("/CA");
        e["/M"] = PdfObj.DateString(DateTimeOffset.Now);
        foreach (var key in new[] { "/InkList", "/L", "/LE", "/Vertices", "/CL", "/RD", "/IT", "/Rotate" }) e.Remove(key);

        // Common appearance prologue: colors, width, dash, caps.
        var ops = new StringBuilder();
        if (s.Color is { } sc) ops.Append($"{Rgb(sc)} RG ");
        if (s.Fill is { } fc) ops.Append($"{Rgb(fc)} rg ");
        ops.Append($"{PdfObj.F(w)} w ");
        if (s.Dash is { } d) ops.Append($"[{string.Join(' ', d.Select(PdfObj.F))}] 0 d ");
        var paint = (fill, stroke) switch { (true, true) => "B", (true, false) => "f", (false, true) => "S", _ => "n" };

        if (subtype == "FreeText")
        {
            ApplyFreeText(doc, page, annot, g, s, ops);
            return;
        }

        List<(double X, double Y)> rectPts = [];
        double pad;
        switch (subtype)
        {
            case "Ink":
            {
                var strokes = g.Paths.Where(p => p.Count > 0).Select(p => p.Select(pt => PdfObj.ToPdf(page, pt.X, pt.Y)).ToList()).ToList();
                e["/InkList"] = new PdfArray(doc, [.. strokes.Select(st => (PdfItem)PdfObj.Reals(doc, [.. st.SelectMany(q => new[] { q.X, q.Y })]))]);
                ops.Append("1 J 1 j\n");
                foreach (var st in strokes) Smooth(ops, st);
                if (strokes.Count > 0) ops.Append("S\n");
                rectPts.AddRange(strokes.SelectMany(x => x));
                pad = w / 2 + 1;
                break;
            }
            case "Line" or "Polygon" or "PolyLine":
            {
                var pts = g.Paths[0].Select(pt => PdfObj.ToPdf(page, pt.X, pt.Y)).ToList();
                var flat = PdfObj.Reals(doc, [.. pts.SelectMany(q => new[] { q.X, q.Y })]);
                e[subtype == "Line" ? "/L" : "/Vertices"] = flat;
                var ends = subtype != "Polygon" && (s.StartEnding != LineEnding.None || s.EndEnding != LineEnding.None);
                if (ends) e["/LE"] = new PdfArray(doc, new PdfName("/" + s.StartEnding), new PdfName("/" + s.EndEnding));
                ops.Append(subtype == "Polygon" ? "0 J 1 j\n" : "0 J 0 j\n");
                ops.Append($"{P(pts[0])} m");
                foreach (var q in pts.Skip(1)) ops.Append($" {P(q)} l");
                ops.Append(subtype == "Polygon" ? $" h {paint}\n" : " S\n");
                if (ends)
                {
                    var endFill = s.Fill is not null;
                    Ending(ops, s.StartEnding, pts[0], Dir(pts[1], pts[0]), w, endFill);
                    Ending(ops, s.EndEnding, pts[^1], Dir(pts[^2], pts[^1]), w, endFill);
                }
                rectPts.AddRange(pts);
                pad = w / 2 + (ends ? EndingSize(w) + w : 0) + 1;
                break;
            }
            default: // Square, Circle
            {
                var (x1, y1, x2, y2) = TextStamps.FromPageSpace(page, g.Box);
                e["/Rect"] = PdfObj.Reals(doc, x1, y1, x2, y2);
                var half = Math.Min(w / 2, Math.Min(x2 - x1, y2 - y1) / 2);
                double l = x1 + half, b = y1 + half, r = x2 - half, t = y2 - half;
                if (subtype == "Square") ops.Append($"{PdfObj.F(l)} {PdfObj.F(b)} {PdfObj.F(r - l)} {PdfObj.F(t - b)} re {paint}\n");
                else
                {
                    Ellipse(ops, (l + r) / 2, (b + t) / 2, (r - l) / 2, (t - b) / 2);
                    ops.Append($"{paint}\n");
                }
                var form = Appearance(doc, ops.ToString(), null, x1, y1, x2, y2, null, s);
                SetAppearance(doc, annot, form);
                return;
            }
        }

        double rx1 = rectPts.Min(q => q.X) - pad, ry1 = rectPts.Min(q => q.Y) - pad, rx2 = rectPts.Max(q => q.X) + pad, ry2 = rectPts.Max(q => q.Y) + pad;
        e["/Rect"] = PdfObj.Reals(doc, rx1, ry1, rx2, ry2);
        SetAppearance(doc, annot, Appearance(doc, ops.ToString(), null, rx1, ry1, rx2, ry2, null, s));
    }

    private static void ApplyFreeText(PdfDocument doc, PdfPage page, PdfDictionary annot, AnnotGeometry g, AnnotStyle s, StringBuilder ops)
    {
        var e = annot.Elements;
        var text = PdfObj.Str(e["/Contents"]) ?? "";
        var box = g.Box;
        var border = s.Color is not null && s.Width > 0;
        var w = border ? s.Width : 0;

        // The callout: tip [, knee], then where it meets the box (the middle of the nearest side).
        var callout = g.Callout.ToList();
        if (callout.Count > 0)
        {
            var last = callout[^1];
            PointD[] sides = [new(box.X, box.CenterY), new(box.Right, box.CenterY), new(box.CenterX, box.Y), new(box.CenterX, box.Bottom)];
            callout.Add(sides.MinBy(q => (q.X - last.X) * (q.X - last.X) + (q.Y - last.Y) * (q.Y - last.Y)));
        }
        var lw = Math.Max(w, 1); // a callout line is drawn even without a border
        var extent = EndingSize(lw) + lw;
        var outer = box;
        foreach (var q in callout.Take(callout.Count - 1))
            outer = RectD.FromEdges(Math.Min(outer.X, q.X - extent), Math.Min(outer.Y, q.Y - extent), Math.Max(outer.Right, q.X + extent), Math.Max(outer.Bottom, q.Y + extent));

        var (rx1, ry1, rx2, ry2) = TextStamps.FromPageSpace(page, outer);
        e["/Rect"] = PdfObj.Reals(doc, rx1, ry1, rx2, ry2);
        if (callout.Count > 0)
        {
            var (bx1, by1, bx2, by2) = TextStamps.FromPageSpace(page, box);
            e["/RD"] = PdfObj.Reals(doc, bx1 - rx1, by1 - ry1, rx2 - bx2, ry2 - by2);
            e["/CL"] = PdfObj.Reals(doc, [.. callout.Select(q => PdfObj.ToPdf(page, q.X, q.Y)).SelectMany(q => new[] { q.X, q.Y })]);
            e["/IT"] = new PdfName("/FreeTextCallout");
            e["/LE"] = new PdfName("/" + s.EndEnding);
        }
        else if (!border && s.Fill is null) e["/IT"] = new PdfName("/FreeTextTypewriter");
        var rot = ((page.Rotate % 360) + 360) % 360;
        if (rot != 0) e["/Rotate"] = new PdfInteger(rot);

        var size = s.FontSize;
        var tc = s.TextColor;
        e["/DA"] = new PdfString($"{Rgb(tc)} rg /Helv {PdfObj.F(size)} Tf");
        var align = s.Align switch { TextAlign.Center => "center", TextAlign.Right => "right", _ => "left" };
        var hex = $"#{(int)Math.Round(tc.R * 255):X2}{(int)Math.Round(tc.G * 255):X2}{(int)Math.Round(tc.B * 255):X2}";
        e["/DS"] = new PdfString($"font: Helvetica,sans-serif {PdfObj.F(size)}pt; text-align:{align}; color:{hex}");
        e["/Q"] = new PdfInteger((int)s.Align);
        var paras = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var rc = new StringBuilder("<?xml version=\"1.0\"?><body xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\" xfa:APIVersion=\"Acrobat:11.0.0\" xfa:spec=\"2.0.2\">");
        foreach (var para in paras) rc.Append($"<p dir=\"ltr\">{SecurityElement.Escape(para)}</p>");
        rc.Append("</body>");
        e["/RC"] = PdfObj.MakeString(rc.ToString());

        // The appearance, in the displayed page's orientation (page-space units, y up from the bottom of the rect),
        // counter-rotated by /Matrix on rotated pages so the text reads upright.
        double W = outer.Width, H = outer.Height;
        (double X, double Y) Fr(PointD q) => (q.X - outer.X, outer.Bottom - q.Y);
        double fx = box.X - outer.X, fy = outer.Bottom - box.Bottom, fw = box.Width, fh = box.Height;
        if (s.Fill is { } fill) ops.Append($"\n{Rgb(fill)} rg {PdfObj.F(fx)} {PdfObj.F(fy)} {PdfObj.F(fw)} {PdfObj.F(fh)} re f\n");
        if (border)
        {
            var half = Math.Min(w / 2, Math.Min(fw, fh) / 2);
            ops.Append($"{PdfObj.F(fx + half)} {PdfObj.F(fy + half)} {PdfObj.F(fw - 2 * half)} {PdfObj.F(fh - 2 * half)} re S\n");
        }
        if (callout.Count > 1)
        {
            var color = s.Color ?? tc;
            var pts = callout.Select(Fr).ToList();
            ops.Append($"q [] 0 d {Rgb(color)} RG {Rgb(s.Fill ?? color)} rg {PdfObj.F(lw)} w 0 J 0 j {P(pts[0])} m");
            foreach (var q in pts.Skip(1)) ops.Append($" {P(q)} l");
            ops.Append(" S\n");
            Ending(ops, s.EndEnding, pts[0], Dir(pts[1], pts[0]), lw, true);
            ops.Append("Q\n");
        }

        var pad = TextPad(s);
        var painter = new TextPainter(StdFont.Helvetica, [text]);
        var avail = Math.Max(1, fw - 2 * pad);
        var y = fy + fh - pad - painter.Ascent * size;
        foreach (var line in painter.Wrap(text, avail, size))
        {
            var width = painter.Measure(line) * size;
            var x = s.Align switch
            {
                TextAlign.Center => fx + (fw - width) / 2,
                TextAlign.Right => fx + fw - pad - width,
                _ => fx + pad,
            };
            painter.Draw(line, x, y, size);
            y -= size * LineSpacing;
        }
        var (textOps, resources) = painter.Build(doc, W, H, "/Helv", new PdfColorValue([tc.R, tc.G, tc.B]));
        ops.Append($"q {PdfObj.F(fx + w)} {PdfObj.F(fy + w)} {PdfObj.F(Math.Max(0, fw - 2 * w))} {PdfObj.F(Math.Max(0, fh - 2 * w))} re W n\n");
        byte[] content = [.. Encoding.ASCII.GetBytes(ops.ToString()), .. textOps, .. "Q\n"u8];
        SetAppearance(doc, annot, Appearance(doc, content, resources, 0, 0, W, H, PdfObj.CounterRotation(doc, page), s));
    }

    private static PdfDictionary Appearance(PdfDocument doc, string ops, PdfDictionary? resources, double x1, double y1, double x2, double y2,
        PdfArray? matrix, AnnotStyle s) =>
        Appearance(doc, Encoding.ASCII.GetBytes(ops), resources, x1, y1, x2, y2, matrix, s);

    /// <summary>
    /// The normal appearance. With an opacity or the highlighter's Multiply blend, the drawing is an isolated transparency
    /// group painted through an ExtGState (/CA /ca /BM), so overlapping strokes don't darken each other.
    /// </summary>
    private static PdfDictionary Appearance(PdfDocument doc, byte[] content, PdfDictionary? resources, double x1, double y1, double x2, double y2,
        PdfArray? matrix, AnnotStyle s)
    {
        if (s.Opacity >= 1 && !s.Highlighter) return PdfObj.FormXObject(doc, content, x1, y1, x2, y2, resources, matrix);
        var inner = PdfObj.FormXObject(doc, content, x1, y1, x2, y2, resources);
        var group = new PdfDictionary(doc);
        group.Elements["/Type"] = new PdfName("/Group");
        group.Elements["/S"] = new PdfName("/Transparency");
        group.Elements["/I"] = new PdfBoolean(true);
        inner.Elements["/Group"] = group;
        var gs = new PdfDictionary(doc);
        gs.Elements["/Type"] = new PdfName("/ExtGState");
        gs.Elements["/CA"] = new PdfReal(Math.Round(s.Opacity, 3));
        gs.Elements["/ca"] = new PdfReal(Math.Round(s.Opacity, 3));
        if (s.Highlighter) gs.Elements["/BM"] = new PdfName("/Multiply");
        var gsMap = new PdfDictionary(doc);
        gsMap.Elements["/GS0"] = gs;
        var xobjects = new PdfDictionary(doc);
        xobjects.Elements["/Fm0"] = inner.Reference!;
        var outer = new PdfDictionary(doc);
        outer.Elements["/ExtGState"] = gsMap;
        outer.Elements["/XObject"] = xobjects;
        return PdfObj.FormXObject(doc, "/GS0 gs /Fm0 Do\n"u8.ToArray(), x1, y1, x2, y2, outer, matrix);
    }

    private static string P((double X, double Y) p) => $"{PdfObj.F(p.X)} {PdfObj.F(p.Y)}";

    private static (double X, double Y) Dir((double X, double Y) from, (double X, double Y) to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        return len < 1e-9 ? (1, 0) : (dx / len, dy / len);
    }

    /// <summary>A stroke through the points: Catmull-Rom segments as cubic Béziers (a dot for a single point).</summary>
    private static void Smooth(StringBuilder ops, List<(double X, double Y)> raw)
    {
        var p = new List<(double X, double Y)>(raw.Count);
        foreach (var q in raw)
            if (p.Count == 0 || Math.Abs(p[^1].X - q.X) > 1e-6 || Math.Abs(p[^1].Y - q.Y) > 1e-6) p.Add(q);
        ops.Append($"{P(p[0])} m\n");
        if (p.Count == 1)
        {
            ops.Append($"{P(p[0])} l\n");
            return;
        }
        for (var i = 0; i + 1 < p.Count; i++)
        {
            var p0 = p[Math.Max(i - 1, 0)];
            var p1 = p[i];
            var p2 = p[i + 1];
            var p3 = p[Math.Min(i + 2, p.Count - 1)];
            var c1 = (p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
            var c2 = (p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
            ops.Append($"{P(c1)} {P(c2)} {P(p2)} c\n");
        }
    }

    private static void Ellipse(StringBuilder ops, double cx, double cy, double rx, double ry)
    {
        const double k = 0.5522847498;
        string Pt(double x, double y) => $"{PdfObj.F(x)} {PdfObj.F(y)}";
        ops.Append($"{Pt(cx + rx, cy)} m ");
        ops.Append($"{Pt(cx + rx, cy + k * ry)} {Pt(cx + k * rx, cy + ry)} {Pt(cx, cy + ry)} c ");
        ops.Append($"{Pt(cx - k * rx, cy + ry)} {Pt(cx - rx, cy + k * ry)} {Pt(cx - rx, cy)} c ");
        ops.Append($"{Pt(cx - rx, cy - k * ry)} {Pt(cx - k * rx, cy - ry)} {Pt(cx, cy - ry)} c ");
        ops.Append($"{Pt(cx + k * rx, cy - ry)} {Pt(cx + rx, cy - k * ry)} {Pt(cx + rx, cy)} c h ");
    }

    /// <summary>Draws a line ending at <paramref name="at"/>; <paramref name="dir"/> points out of the line through that end.</summary>
    private static void Ending(StringBuilder ops, LineEnding ending, (double X, double Y) at, (double X, double Y) dir, double w, bool fill)
    {
        if (ending == LineEnding.None) return;
        var s = EndingSize(w);
        var (dx, dy) = dir;
        var (nx, ny) = (-dy, dx);
        (double, double) At(double along, double across) => (at.X + dx * along + nx * across, at.Y + dy * along + ny * across);
        var closedPaint = fill ? "b" : "s";
        switch (ending)
        {
            case LineEnding.OpenArrow or LineEnding.ClosedArrow or LineEnding.ROpenArrow or LineEnding.RClosedArrow:
            {
                var back = ending is LineEnding.ROpenArrow or LineEnding.RClosedArrow ? s : -s;
                var closed = ending is LineEnding.ClosedArrow or LineEnding.RClosedArrow;
                ops.Append($"{P(At(back, s / 2))} m {P(at)} l {P(At(back, -s / 2))} l {(closed ? closedPaint : "S")}\n");
                break;
            }
            case LineEnding.Circle:
                Ellipse(ops, at.X, at.Y, s / 2, s / 2);
                ops.Append($"{closedPaint}\n");
                break;
            case LineEnding.Square:
                ops.Append($"{P(At(-s / 2, -s / 2))} m {P(At(s / 2, -s / 2))} l {P(At(s / 2, s / 2))} l {P(At(-s / 2, s / 2))} l {closedPaint}\n");
                break;
            case LineEnding.Diamond:
                ops.Append($"{P(At(-s / 2, 0))} m {P(At(0, -s / 2))} l {P(At(s / 2, 0))} l {P(At(0, s / 2))} l {closedPaint}\n");
                break;
            case LineEnding.Butt:
                ops.Append($"{P(At(0, s / 2))} m {P(At(0, -s / 2))} l S\n");
                break;
            case LineEnding.Slash:
            {
                // 30° off the perpendicular.
                double c = Math.Cos(Math.PI / 6) * s / 2, t = Math.Sin(Math.PI / 6) * s / 2;
                ops.Append($"{P(At(t, c))} m {P(At(-t, -c))} l S\n");
                break;
            }
        }
    }

    // ---------------- Read ----------------

    /// <summary>The geometry (page space) and style of a drawing annotation, from its entries; null when it has none usable.</summary>
    private static (AnnotGeometry Geometry, AnnotStyle Style)? Describe(PdfPage page, PdfDictionary a, string subtype)
    {
        var e = a.Elements;
        PointD Pt(PdfArray arr, int i)
        {
            var (x, y) = PdfObj.ToPage(page, PdfObj.Num(arr, i), PdfObj.Num(arr, i + 1));
            return new PointD(x, y);
        }
        List<PointD> Points(PdfArray? arr) =>
            arr is null ? [] : [.. Enumerable.Range(0, Math.Min(arr.Elements.Count, 2 * MaxPoints) / 2).Select(i => Pt(arr, 2 * i))];
        RectD Inner()
        {
            if (PdfObj.Array(e["/Rect"]) is not { Elements.Count: >= 4 } r) return default;
            double x1 = Math.Min(PdfObj.Num(r, 0), PdfObj.Num(r, 2)), y1 = Math.Min(PdfObj.Num(r, 1), PdfObj.Num(r, 3));
            double x2 = Math.Max(PdfObj.Num(r, 0), PdfObj.Num(r, 2)), y2 = Math.Max(PdfObj.Num(r, 1), PdfObj.Num(r, 3));
            if (PdfObj.Array(e["/RD"]) is { Elements.Count: 4 } rd)
            {
                double l = PdfObj.Num(rd, 0), b = PdfObj.Num(rd, 1), rr = PdfObj.Num(rd, 2), t = PdfObj.Num(rd, 3);
                if (l >= 0 && b >= 0 && rr >= 0 && t >= 0 && l + rr < x2 - x1 && b + t < y2 - y1)
                    (x1, y1, x2, y2) = (x1 + l, y1 + b, x2 - rr, y2 - t);
            }
            return FormEditor.ToPageSpace(page, x1, y1, x2, y2);
        }

        AnnotGeometry g;
        switch (subtype)
        {
            case "Ink":
            {
                var strokes = PdfObj.Array(e["/InkList"])?.Elements.Select(PdfObj.Array).OfType<PdfArray>().Select(Points).Where(p => p.Count > 0)
                    .Select(p => (IReadOnlyList<PointD>)p).ToList() ?? [];
                if (strokes.Count == 0) return null;
                g = new AnnotGeometry { Paths = strokes };
                break;
            }
            case "Line" or "Polygon" or "PolyLine":
            {
                var pts = Points(PdfObj.Array(e[subtype == "Line" ? "/L" : "/Vertices"]));
                if (pts.Count < 2) return null;
                g = new AnnotGeometry { Paths = [subtype == "Line" ? pts.Take(2).ToList() : pts] };
                break;
            }
            case "Square" or "Circle":
                g = new AnnotGeometry { Box = Inner() };
                if (g.Box.IsEmpty) return null;
                break;
            case "FreeText":
            {
                var cl = Points(PdfObj.Array(e["/CL"]));
                g = new AnnotGeometry { Box = Inner(), Callout = cl.Count >= 2 ? cl.Take(cl.Count >= 3 ? 2 : 1).ToList() : [] };
                if (g.Box.IsEmpty) return null;
                break;
            }
            default:
                return null;
        }
        if (g.Box.IsEmpty) g = g with { Box = g.Bounds };

        var bs = PdfObj.Dict(e["/BS"]);
        var width = bs?.Elements.ContainsKey("/W") == true ? PdfObj.Num(bs.Elements["/W"], 1)
            : PdfObj.Array(e["/Border"]) is { Elements.Count: >= 3 } border ? PdfObj.Num(border, 2) : 1;
        IReadOnlyList<double>? dash = null;
        if (PdfObj.Name(bs?.Elements["/S"]) == "D")
            dash = PdfObj.Array(bs!.Elements["/D"]) is { Elements.Count: > 0 } da ? [.. Enumerable.Range(0, Math.Min(da.Elements.Count, 8)).Select(i => PdfObj.Num(da, i))] : [3.0];
        var le = PdfObj.Array(e["/LE"]);
        LineEnding Le(PdfItem? item) => Enum.TryParse<LineEnding>(PdfObj.Name(item), out var v) && Enum.IsDefined(v) ? v : LineEnding.None;
        var color = ColorOf(PdfObj.Array(e["/C"]));
        if (subtype is "Ink" or "Line" or "PolyLine") color ??= AnnotStyle.Black;
        var style = new AnnotStyle
        {
            Color = color,
            Fill = subtype == "Ink" ? null : ColorOf(PdfObj.Array(e["/IC"])),
            Width = Math.Max(0, width),
            Opacity = Math.Clamp(PdfObj.Num(e["/CA"], 1), 0, 1),
            Dash = dash,
            Highlighter = UsesMultiply(PdfObj.Dict(PdfObj.Dict(e["/AP"])?.Elements["/N"]), 0),
            StartEnding = le is { Elements.Count: >= 1 } ? Le(le.Elements[0]) : LineEnding.None,
            EndEnding = subtype == "FreeText" ? Le(e["/LE"]) : le is { Elements.Count: >= 2 } ? Le(le.Elements[1]) : LineEnding.None,
        };
        if (subtype == "FreeText")
        {
            var da = DefaultAppearance.Parse(PdfObj.Str(e["/DA"]));
            var c = da.Color.C;
            style = style with
            {
                StartEnding = LineEnding.None,
                FontSize = da.Size > 0 ? da.Size : 12,
                TextColor = c.Length switch
                {
                    1 => new AnnotColor(c[0], c[0], c[0]),
                    3 => new AnnotColor(c[0], c[1], c[2]),
                    _ => new AnnotColor((1 - c[0]) * (1 - c[3]), (1 - c[1]) * (1 - c[3]), (1 - c[2]) * (1 - c[3])),
                },
                Align = (PdfObj.Int(e["/Q"]) ?? 0) switch { 1 => TextAlign.Center, 2 => TextAlign.Right, _ => TextAlign.Left },
                Highlighter = false,
            };
        }
        return (g, style);
    }

    /// <summary>The appearance paints through an ExtGState with the Multiply blend mode (a highlighter).</summary>
    private static bool UsesMultiply(PdfDictionary? form, int depth)
    {
        if (form is null || depth > 3) return false;
        var res = PdfObj.Dict(form.Elements["/Resources"]);
        if (PdfObj.Dict(res?.Elements["/ExtGState"]) is { } gsMap)
            foreach (var gs in gsMap.Elements.Values.Select(PdfObj.Dict).OfType<PdfDictionary>())
            {
                var bm = PdfObj.Resolve(gs.Elements["/BM"]);
                if (PdfObj.Name(bm) == "Multiply" || bm is PdfArray arr && arr.Elements.Count > 0 && PdfObj.Name(arr.Elements[0]) == "Multiply") return true;
            }
        if (PdfObj.Dict(res?.Elements["/XObject"]) is { } xo)
            foreach (var x in xo.Elements.Values.Take(8).Select(PdfObj.Dict))
                if (x is not null && PdfObj.NameOf(x, "/Subtype") == "/Form" && UsesMultiply(x, depth + 1)) return true;
        return false;
    }
}
