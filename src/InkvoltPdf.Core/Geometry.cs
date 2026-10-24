namespace InkvoltPdf.Core;

/// <summary>Axis-aligned rectangle in page space: PDF points, origin at the page's top-left, y down.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static RectD FromEdges(double left, double top, double right, double bottom) =>
        new(Math.Min(left, right), Math.Min(top, bottom), Math.Abs(right - left), Math.Abs(bottom - top));

    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;

    public RectD Union(RectD other) =>
        IsEmpty ? other : other.IsEmpty ? this
            : FromEdges(Math.Min(X, other.X), Math.Min(Y, other.Y), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));

    public RectD Intersect(RectD other)
    {
        var l = Math.Max(X, other.X);
        var t = Math.Max(Y, other.Y);
        var r = Math.Min(Right, other.Right);
        var b = Math.Min(Bottom, other.Bottom);
        return r > l && b > t ? new RectD(l, t, r - l, b - t) : default;
    }

    public RectD Inflate(double d) => new(X - d, Y - d, Width + 2 * d, Height + 2 * d);
}

/// <summary>Page box size in points, as displayed (rotation applied).</summary>
public readonly record struct PageGeometry(double OriginX, double OriginY, double Width, double Height);

/// <summary>A point in page space: PDF points, origin at the page's top-left, y down.</summary>
public readonly record struct PointD(double X, double Y);

/// <summary>
/// A view turned by a multiple of 90° clockwise (the file is unchanged): maps page-space points and rectangles of a
/// <c>width</c> x <c>height</c> page into the turned page's own space (origin at its top-left, y down) and back.
/// </summary>
public static class RotatedView
{
    /// <summary>0, 90, 180 or 270 (any multiple of 90, negative too; other angles snap down to one).</summary>
    public static int Normalize(int degrees) => ((degrees % 360) + 360) % 360 / 90 * 90;

    /// <summary>The turned page's size.</summary>
    public static (double Width, double Height) Size(double width, double height, int rotation) =>
        rotation is 90 or 270 ? (height, width) : (width, height);

    public static (double X, double Y) ToView(double x, double y, double width, double height, int rotation) => rotation switch
    {
        90 => (height - y, x),
        180 => (width - x, height - y),
        270 => (y, width - x),
        _ => (x, y),
    };

    public static (double X, double Y) FromView(double x, double y, double width, double height, int rotation) => rotation switch
    {
        90 => (y, height - x),
        180 => (width - x, height - y),
        270 => (width - y, x),
        _ => (x, y),
    };

    public static RectD ToView(RectD r, double width, double height, int rotation)
    {
        var (x0, y0) = ToView(r.X, r.Y, width, height, rotation);
        var (x1, y1) = ToView(r.Right, r.Bottom, width, height, rotation);
        return RectD.FromEdges(x0, y0, x1, y1);
    }

    public static RectD FromView(RectD r, double width, double height, int rotation)
    {
        var (x0, y0) = FromView(r.X, r.Y, width, height, rotation);
        var (x1, y1) = FromView(r.Right, r.Bottom, width, height, rotation);
        return RectD.FromEdges(x0, y0, x1, y1);
    }
}
