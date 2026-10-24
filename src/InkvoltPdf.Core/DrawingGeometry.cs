namespace InkvoltPdf.Core;

/// <summary>
/// Geometry for the drawing tools and for editing drawing annotations: constraining a drag (Shift), hit-testing strokes
/// and shapes, the eight resize handles, moving inside the page, and thinning freehand strokes. Page space (points,
/// top-left origin) unless a method says otherwise; the rectangle helpers work in any axis-aligned space.
/// </summary>
public static class DrawingGeometry
{
    /// <summary>The handles of a selection frame, clockwise from the top-left corner.</summary>
    public const int TopLeft = 0, Top = 1, TopRight = 2, Right = 3, BottomRight = 4, Bottom = 5, BottomLeft = 6, Left = 7;

    /// <summary>
    /// The end of a line from <paramref name="from"/> towards <paramref name="to"/>, turned to the nearest multiple of
    /// <paramref name="stepDegrees"/> (45: horizontal, vertical or diagonal), keeping its length.
    /// </summary>
    public static PointD SnapAngle(PointD from, PointD to, double stepDegrees = 45)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length == 0 || stepDegrees <= 0) return to;
        var step = stepDegrees * Math.PI / 180;
        var angle = Math.Round(Math.Atan2(dy, dx) / step) * step;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        if (Math.Abs(cos) < 1e-12) cos = 0;
        if (Math.Abs(sin) < 1e-12) sin = 0;
        return new PointD(from.X + length * cos, from.Y + length * sin);
    }

    /// <summary>The box dragged from <paramref name="from"/> to <paramref name="to"/>; with <paramref name="square"/> a square (the larger side) growing towards the pointer.</summary>
    public static RectD DragBox(PointD from, PointD to, bool square)
    {
        if (!square) return RectD.FromEdges(from.X, from.Y, to.X, to.Y);
        var side = Math.Max(Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y));
        var x = to.X >= from.X ? from.X : from.X - side;
        var y = to.Y >= from.Y ? from.Y : from.Y - side;
        return new RectD(x, y, side, side);
    }

    /// <summary>Where handle <paramref name="handle"/> (<see cref="TopLeft"/>…<see cref="Left"/>) of a frame is.</summary>
    public static PointD HandlePoint(RectD r, int handle) => handle switch
    {
        TopLeft => new(r.X, r.Y),
        Top => new(r.CenterX, r.Y),
        TopRight => new(r.Right, r.Y),
        Right => new(r.Right, r.CenterY),
        BottomRight => new(r.Right, r.Bottom),
        Bottom => new(r.CenterX, r.Bottom),
        BottomLeft => new(r.X, r.Bottom),
        Left => new(r.X, r.CenterY),
        _ => throw new ArgumentOutOfRangeException(nameof(handle)),
    };

    /// <summary>
    /// The frame <paramref name="r"/> with handle <paramref name="handle"/> dragged to <paramref name="p"/>: the opposite side
    /// (or corner) stays put; no side gets shorter than <paramref name="minSize"/> (the handle stops instead of flipping).
    /// <paramref name="keepAspect"/>: a corner handle keeps the frame's proportions.
    /// </summary>
    public static RectD Resize(RectD r, int handle, PointD p, double minSize = 1, bool keepAspect = false)
    {
        if (handle is < TopLeft or > Left) throw new ArgumentOutOfRangeException(nameof(handle));
        double l = r.X, t = r.Y, rt = r.Right, b = r.Bottom;
        if (handle is TopLeft or BottomLeft or Left) l = Math.Min(p.X, rt - minSize);
        if (handle is TopRight or Right or BottomRight) rt = Math.Max(p.X, l + minSize);
        if (handle is TopLeft or Top or TopRight) t = Math.Min(p.Y, b - minSize);
        if (handle is BottomRight or Bottom or BottomLeft) b = Math.Max(p.Y, t + minSize);
        if (keepAspect && handle is TopLeft or TopRight or BottomRight or BottomLeft && r.Width > 0 && r.Height > 0)
        {
            var k = Math.Max((rt - l) / r.Width, (b - t) / r.Height);
            double w = r.Width * k, h = r.Height * k;
            if (handle is TopLeft or BottomLeft) l = rt - w;
            else rt = l + w;
            if (handle is TopLeft or TopRight) t = b - h;
            else b = t + h;
        }
        return new RectD(l, t, rt - l, b - t);
    }

    /// <summary>
    /// A move by (<paramref name="dx"/>, <paramref name="dy"/>) cut short so that <paramref name="bounds"/> stays on a
    /// <paramref name="pageWidth"/> x <paramref name="pageHeight"/> page (something already sticking out may move back in, never further out).
    /// </summary>
    public static (double Dx, double Dy) ClampMove(RectD bounds, double dx, double dy, double pageWidth, double pageHeight)
    {
        static double Clamp(double d, double start, double end, double size)
        {
            var lo = Math.Min(0, -start);           // as far back as the page's start
            var hi = Math.Max(0, size - end);        // as far on as the page's end
            return Math.Clamp(d, lo, hi);
        }
        return (Clamp(dx, bounds.X, bounds.Right, pageWidth), Clamp(dy, bounds.Y, bounds.Bottom, pageHeight));
    }

    /// <summary>The geometry moved by (<paramref name="dx"/>, <paramref name="dy"/>).</summary>
    public static AnnotGeometry Translate(AnnotGeometry g, double dx, double dy)
    {
        PointD M(PointD p) => new(p.X + dx, p.Y + dy);
        return new AnnotGeometry
        {
            Box = g.Box.IsEmpty ? g.Box : g.Box with { X = g.Box.X + dx, Y = g.Box.Y + dy },
            Paths = [.. g.Paths.Select(p => (IReadOnlyList<PointD>)[.. p.Select(M)])],
            Callout = [.. g.Callout.Select(M)],
        };
    }

    /// <summary>
    /// The frame a selection shows (and resizes): a FreeText's text box (its callout has its own handle), else everything
    /// the geometry covers.
    /// </summary>
    public static RectD Frame(string subtype, AnnotGeometry g) => subtype == "FreeText" && !g.Box.IsEmpty ? g.Box : g.Bounds;

    /// <summary>
    /// The geometry resized so its <see cref="Frame"/> becomes <paramref name="frame"/>. A FreeText keeps its callout's tip
    /// where it is (only the box changes); everything else is scaled.
    /// </summary>
    public static AnnotGeometry WithFrame(string subtype, AnnotGeometry g, RectD frame) =>
        subtype == "FreeText" ? new AnnotGeometry { Box = frame, Paths = g.Paths, Callout = g.Callout } : g.MapTo(frame);

    /// <summary>Distance from <paramref name="p"/> to the segment <paramref name="a"/>–<paramref name="b"/>.</summary>
    public static double DistanceToSegment(PointD p, PointD a, PointD b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        var t = len2 == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        double x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
        return Math.Sqrt(x * x + y * y);
    }

    /// <summary>Distance from <paramref name="p"/> to an open (or <paramref name="closed"/>) polyline.</summary>
    public static double DistanceToPath(PointD p, IReadOnlyList<PointD> path, bool closed = false)
    {
        if (path.Count == 0) return double.PositiveInfinity;
        if (path.Count == 1) return DistanceToSegment(p, path[0], path[0]);
        var best = double.PositiveInfinity;
        for (var i = 1; i < path.Count; i++) best = Math.Min(best, DistanceToSegment(p, path[i - 1], path[i]));
        if (closed) best = Math.Min(best, DistanceToSegment(p, path[^1], path[0]));
        return best;
    }

    /// <summary>True when <paramref name="p"/> is inside the polygon (even-odd rule).</summary>
    public static bool InsidePolygon(PointD p, IReadOnlyList<PointD> poly)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var (a, b) = (poly[i], poly[j]);
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// True when a press at <paramref name="p"/> picks the drawing annotation: on its stroke (within its half width plus
    /// <paramref name="tolerance"/>), inside a filled shape, anywhere in a FreeText box, or on a callout's leader line.
    /// </summary>
    public static bool Hit(string subtype, AnnotGeometry g, AnnotStyle? style, PointD p, double tolerance)
    {
        var stroke = style is { Color: not null } s && s.Width > 0 ? style.Width : 0;
        var filled = style?.Fill is not null;
        var near = tolerance + stroke / 2;
        switch (subtype)
        {
            case "Ink" or "PolyLine" or "Line":
                return g.Paths.Any(path => DistanceToPath(p, path) <= near);
            case "Polygon":
                return g.Paths.Any(path => DistanceToPath(p, path, closed: true) <= near || (filled && InsidePolygon(p, path)));
            case "Square":
            {
                var b = g.Box;
                if (filled && b.Inflate(tolerance).Contains(p.X, p.Y)) return true;
                // The stroke lies inside the box: its middle runs half a width in.
                var inner = b.Width > stroke && b.Height > stroke ? b.Inflate(-stroke / 2) : b;
                PointD[] corners = [new(inner.X, inner.Y), new(inner.Right, inner.Y), new(inner.Right, inner.Bottom), new(inner.X, inner.Bottom)];
                return DistanceToPath(p, corners, closed: true) <= near;
            }
            case "Circle":
            {
                var b = g.Box;
                double rx = Math.Max((b.Width - stroke) / 2, 0.01), ry = Math.Max((b.Height - stroke) / 2, 0.01);
                double nx = (p.X - b.CenterX) / rx, ny = (p.Y - b.CenterY) / ry;
                var d = Math.Sqrt(nx * nx + ny * ny);
                if (filled && d <= 1) return true;
                // Distance to the ellipse, approximately (exact on circles): radial gap scaled by the radius in that direction.
                var radius = d == 0 ? Math.Min(rx, ry) : Math.Sqrt(Math.Pow(nx / d * rx, 2) + Math.Pow(ny / d * ry, 2));
                return Math.Abs(d - 1) * radius <= near;
            }
            case "FreeText":
            {
                if (g.Box.Inflate(tolerance).Contains(p.X, p.Y)) return true;
                if (g.Callout.Count == 0) return false;
                var leader = g.Callout.ToList();
                leader.Add(NearestOnRect(g.Box, leader[^1]));
                return DistanceToPath(p, leader) <= near;
            }
            default:
                return g.Bounds.Inflate(tolerance + stroke / 2).Contains(p.X, p.Y);
        }
    }

    /// <summary>The point of <paramref name="r"/>'s outline nearest to <paramref name="p"/> (outside it: the nearest point of the box).</summary>
    public static PointD NearestOnRect(RectD r, PointD p) => new(Math.Clamp(p.X, r.X, r.Right), Math.Clamp(p.Y, r.Y, r.Bottom));

    /// <summary>
    /// A freehand stroke thinned (Ramer–Douglas–Peucker): points closer than <paramref name="tolerance"/> to the line
    /// through their neighbours are dropped; the first and last points always stay.
    /// </summary>
    public static IReadOnlyList<PointD> Simplify(IReadOnlyList<PointD> points, double tolerance)
    {
        if (points.Count < 3 || tolerance <= 0) return [.. points];
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int From, int To)>();
        stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            var (from, to) = stack.Pop();
            var worst = -1;
            var worstD = tolerance;
            for (var i = from + 1; i < to; i++)
            {
                var d = DistanceToSegment(points[i], points[from], points[to]);
                if (d > worstD)
                {
                    worstD = d;
                    worst = i;
                }
            }
            if (worst < 0) continue;
            keep[worst] = true;
            stack.Push((from, worst));
            stack.Push((worst, to));
        }
        return [.. points.Where((_, i) => keep[i])];
    }
}
