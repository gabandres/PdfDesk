namespace PdfDesk.Core;

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
