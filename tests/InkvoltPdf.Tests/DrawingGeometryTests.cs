using InkvoltPdf.Core;

namespace InkvoltPdf.Tests;

public class DrawingGeometryTests
{
    private static void Near(double expected, double actual) => Assert.Equal(expected, actual, 6);

    [Theory]
    [InlineData(10, 1, 10, 0)]      // almost horizontal → horizontal
    [InlineData(10, 9, 9.5, 9.5)]   // almost diagonal → diagonal
    [InlineData(-1, -10, 0, -10)]   // almost vertical (up) → vertical
    public void SnapAngleTurnsToMultiplesOf45(double x, double y, double ex, double ey)
    {
        var p = DrawingGeometry.SnapAngle(new PointD(0, 0), new PointD(x, y));
        var length = Math.Sqrt(x * x + y * y);
        Near(length, Math.Sqrt(p.X * p.X + p.Y * p.Y));
        if (ex == 0) Near(0, p.X);
        if (ey == 0) Near(0, p.Y);
        if (ex != 0 && ey != 0) Near(Math.Abs(p.X), Math.Abs(p.Y));
        Assert.Equal(Math.Sign(ex), Math.Sign(Math.Round(p.X, 6)));
        Assert.Equal(Math.Sign(ey), Math.Sign(Math.Round(p.Y, 6)));
    }

    [Fact]
    public void DragBoxSquaresTowardsThePointer()
    {
        Assert.Equal(new RectD(10, 10, 30, 30), DrawingGeometry.DragBox(new(10, 10), new(40, 20), square: true));
        Assert.Equal(new RectD(-20, -20, 30, 30), DrawingGeometry.DragBox(new(10, 10), new(-20, 0), square: true));
        Assert.Equal(new RectD(5, 2, 5, 8), DrawingGeometry.DragBox(new(10, 2), new(5, 10), square: false));
    }

    [Fact]
    public void ResizeMovesOnlyTheDraggedSides()
    {
        var r = new RectD(10, 10, 100, 50);
        Assert.Equal(new RectD(10, 10, 140, 70), DrawingGeometry.Resize(r, DrawingGeometry.BottomRight, new(150, 80)));
        Assert.Equal(new RectD(0, 10, 110, 50), DrawingGeometry.Resize(r, DrawingGeometry.Left, new(0, 999)));
        Assert.Equal(new RectD(10, 30, 100, 30), DrawingGeometry.Resize(r, DrawingGeometry.Top, new(-5, 30)));
        // Dragged past the opposite side: stops at the minimum size instead of flipping.
        Assert.Equal(new RectD(10, 10, 4, 50), DrawingGeometry.Resize(r, DrawingGeometry.Right, new(-100, 0), minSize: 4));
    }

    [Fact]
    public void ResizeKeepsTheAspectFromTheOppositeCorner()
    {
        var r = new RectD(10, 10, 100, 50);
        var a = DrawingGeometry.Resize(r, DrawingGeometry.TopLeft, new(-90, 0), keepAspect: true);
        Assert.Equal(110, a.Right, 6);
        Assert.Equal(60, a.Bottom, 6);
        Assert.Equal(2, a.Width / a.Height, 6);
        Assert.Equal(200, a.Width, 6);
    }

    [Fact]
    public void HandlesGoClockwiseFromTheTopLeft()
    {
        var r = new RectD(0, 0, 10, 20);
        Assert.Equal(new PointD(0, 0), DrawingGeometry.HandlePoint(r, DrawingGeometry.TopLeft));
        Assert.Equal(new PointD(5, 0), DrawingGeometry.HandlePoint(r, DrawingGeometry.Top));
        Assert.Equal(new PointD(10, 10), DrawingGeometry.HandlePoint(r, DrawingGeometry.Right));
        Assert.Equal(new PointD(0, 20), DrawingGeometry.HandlePoint(r, DrawingGeometry.BottomLeft));
    }

    [Fact]
    public void ClampMoveKeepsTheBoundsOnThePage()
    {
        var b = new RectD(10, 10, 50, 50);
        Assert.Equal((-10.0, 40.0), DrawingGeometry.ClampMove(b, -30, 40, 200, 100));
        Assert.Equal((140.0, -10.0), DrawingGeometry.ClampMove(b, 500, -500, 200, 100));
        // Already sticking out on the left: may come back in, never go further out.
        var outside = new RectD(-20, 10, 50, 50);
        Assert.Equal((0.0, 0.0), DrawingGeometry.ClampMove(outside, -5, 0, 200, 100));
        Assert.Equal((15.0, 0.0), DrawingGeometry.ClampMove(outside, 15, 0, 200, 100));
    }

    [Fact]
    public void TranslateMovesEveryPart()
    {
        var g = new AnnotGeometry { Box = new RectD(0, 0, 10, 10), Paths = [[new(1, 1), new(2, 3)]], Callout = [new(-5, -5)] };
        var m = DrawingGeometry.Translate(g, 3, 4);
        Assert.Equal(new RectD(3, 4, 10, 10), m.Box);
        Assert.Equal(new PointD(5, 7), m.Paths[0][1]);
        Assert.Equal(new PointD(-2, -1), m.Callout[0]);
    }

    [Fact]
    public void FreeTextFrameIsItsBoxAndResizingKeepsTheTip()
    {
        var g = new AnnotGeometry { Box = new RectD(100, 100, 80, 30), Callout = [new(20, 20)] };
        Assert.Equal(new RectD(100, 100, 80, 30), DrawingGeometry.Frame("FreeText", g));
        Assert.Equal(new RectD(20, 20, 160, 110), DrawingGeometry.Frame("Square", g));
        var r = DrawingGeometry.WithFrame("FreeText", g, new RectD(120, 100, 60, 40));
        Assert.Equal(new RectD(120, 100, 60, 40), r.Box);
        Assert.Equal(new PointD(20, 20), r.Callout[0]);
        var ink = new AnnotGeometry { Paths = [[new(0, 0), new(10, 10)]] };
        var scaled = DrawingGeometry.WithFrame("Ink", ink, new RectD(5, 5, 20, 40));
        Assert.Equal(new PointD(25, 45), scaled.Paths[0][1]);
    }

    [Fact]
    public void HitTestsStrokesNotEmptyInteriors()
    {
        var style = new AnnotStyle { Color = AnnotColor.Red, Width = 2 };
        var square = new AnnotGeometry { Box = new RectD(0, 0, 100, 100) };
        Assert.True(DrawingGeometry.Hit("Square", square, style, new(1, 50), 2));
        Assert.False(DrawingGeometry.Hit("Square", square, style, new(50, 50), 2));
        Assert.True(DrawingGeometry.Hit("Square", square, style with { Fill = AnnotColor.Yellow }, new(50, 50), 2));

        var circle = new AnnotGeometry { Box = new RectD(0, 0, 100, 100) };
        Assert.True(DrawingGeometry.Hit("Circle", circle, style, new(50, 1.5), 2));
        Assert.False(DrawingGeometry.Hit("Circle", circle, style, new(50, 50), 2));
        Assert.False(DrawingGeometry.Hit("Circle", circle, style, new(2, 2), 2)); // the box's corner is outside the ellipse

        var line = new AnnotGeometry { Paths = [[new(0, 0), new(100, 0)]] };
        Assert.True(DrawingGeometry.Hit("Line", line, style, new(50, 2.5), 2));
        Assert.False(DrawingGeometry.Hit("Line", line, style, new(50, 6), 2));
        Assert.False(DrawingGeometry.Hit("Line", line, style, new(110, 0), 2));
    }

    [Fact]
    public void HitTestsTextBoxesAndCalloutLeaders()
    {
        var g = new AnnotGeometry { Box = new RectD(100, 100, 80, 30), Callout = [new(20, 115)] };
        var style = AnnotStyle.TextBox();
        Assert.True(DrawingGeometry.Hit("FreeText", g, style, new(140, 110), 1));
        Assert.True(DrawingGeometry.Hit("FreeText", g, style, new(60, 115.5), 1)); // on the leader
        Assert.False(DrawingGeometry.Hit("FreeText", g, style, new(60, 140), 1));
    }

    [Fact]
    public void HitTestsFilledPolygonsInside()
    {
        var tri = new AnnotGeometry { Paths = [[new(0, 0), new(100, 0), new(50, 80)]] };
        var stroke = new AnnotStyle { Color = AnnotColor.Red, Width = 1 };
        Assert.False(DrawingGeometry.Hit("Polygon", tri, stroke, new(50, 30), 1));
        Assert.True(DrawingGeometry.Hit("Polygon", tri, stroke with { Fill = AnnotColor.Red }, new(50, 30), 1));
        Assert.True(DrawingGeometry.Hit("Polygon", tri, stroke, new(25, 40), 1.5)); // on the closing edge
    }

    [Fact]
    public void SimplifyDropsPointsOnAStraightRun()
    {
        var pts = Enumerable.Range(0, 50).Select(i => new PointD(i, i < 25 ? 0 : i - 25)).ToList();
        var s = DrawingGeometry.Simplify(pts, 0.1);
        Assert.Equal(new[] { new PointD(0, 0), new PointD(25, 0), new PointD(49, 24) }, s);
        Assert.Equal(2, DrawingGeometry.Simplify([new(0, 0), new(1, 1)], 1).Count);
    }
}
