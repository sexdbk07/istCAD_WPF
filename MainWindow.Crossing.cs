using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1;

public partial class MainWindow
{
    /// <summary>
    /// Crossing (right-to-left) selection: an object is selected only when the window actually touches its geometry. The R-tree query only
    /// proves that bounding boxes overlap, so a block was selected by sweeping any empty area inside its box; now a block is selected
    /// when the window crosses (or lies on) one of the objects drawn inside it.
    /// </summary>
    private bool CrossesWindow(CadEntity entity, double minX, double minY, double maxX, double maxY)
    {
        bool Hit(Point2 a, Point2 b) => ClipSegmentToRect(a, b, minX, minY, maxX, maxY, out _, out _);
        bool Inside(Point2 p) => p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY;
        switch (entity)
        {
            case LineEntity line: return Hit(line.Start, line.End);
            case PolylineEntity polyline:
            {
                var v = polyline.Vertices;
                if (v.Count == 0) return false;
                if (v.Count == 1) return Inside(v[0].Point);
                for (var i = 1; i < v.Count; i++) if (Hit(v[i - 1].Point, v[i].Point)) return true;
                return polyline.Closed && v.Count > 2 && Hit(v[^1].Point, v[0].Point);
            }
            case CircleEntity circle: return RingTouchesRect(circle.Center, Math.Abs(circle.Radius), minX, minY, maxX, maxY);
            case ArcEntity arc:
            {
                if (!RingTouchesRect(arc.Center, Math.Abs(arc.Radius), minX, minY, maxX, maxY)) return false;
                var sweep = ((arc.EndAngleDegrees - arc.StartAngleDegrees) % 360 + 360) % 360; if (sweep < 1e-9) sweep = 360;
                var steps = Math.Clamp((int)Math.Ceiling(sweep / 4), 4, 90);
                Point2 previous = default;
                for (var i = 0; i <= steps; i++)
                {
                    var angle = (arc.StartAngleDegrees + sweep * i / steps) * Math.PI / 180;
                    var point = new Point2(arc.Center.X + arc.Radius * Math.Cos(angle), arc.Center.Y + arc.Radius * Math.Sin(angle));
                    if (i > 0 ? Hit(previous, point) : Inside(point)) return true;
                    previous = point;
                }
                return false;
            }
            case ProxyCadEntity proxy:
            {
                if (proxy.PreviewFilled) return true; // solid hatch: touching its area counts; bounds overlap is already established
                var hasGeometry = false; var budget = 200_000;
                bool PathHit(IReadOnlyList<Point2> points, bool closed)
                {
                    hasGeometry = true;
                    for (var i = 1; i < points.Count; i++) { if (--budget < 0) return true; if (Hit(points[i - 1], points[i])) return true; }
                    return closed && points.Count > 2 && Hit(points[^1], points[0]);
                }
                if (proxy.PreviewPaths is { Count: > 0 }) foreach (var path in proxy.PreviewPaths) if (PathHit(path, proxy.PreviewClosed)) return true;
                if (proxy.ProxyGraphicsPaths is { Count: > 0 }) foreach (var path in proxy.ProxyGraphicsPaths) if (PathHit(path.Points, path.Closed)) return true;
                bool TextBoxHit(Point2 position, string text, double height, double width, double rotation = 0, double spacing = 1, int attachment = 1)
                {
                    hasGeometry = true;
                    var corners = CadTextExtents.WrappedTextCorners(text, position, height, rotation, width, spacing, attachment);
                    return corners.Max(c => c.X) >= minX && corners.Min(c => c.X) <= maxX && corners.Max(c => c.Y) >= minY && corners.Min(c => c.Y) <= maxY;
                }
                if (!string.IsNullOrWhiteSpace(proxy.PreviewText) &&
                    TextBoxHit(proxy.PreviewTextPosition ?? proxy.BoundsMin, proxy.PreviewText, proxy.PreviewTextHeight, proxy.PreviewTextWidth, proxy.PreviewTextRotation, proxy.PreviewTextLineSpacing, proxy.PreviewTextAttachment)) return true;
                if (proxy.ProxyGraphicsTexts is { Count: > 0 })
                    foreach (var text in proxy.ProxyGraphicsTexts) if (TextBoxHit(text.Position, text.Text, text.Height, text.Width, text.RotationRadians, text.LineSpacing, text.AttachmentPoint)) return true;
                return !hasGeometry; // nothing drawn inside to test against: fall back to the bounding-box overlap
            }
        }
        return true; // text and anything else: bounding-box overlap (the text box is the object)
    }

    private static bool RingTouchesRect(Point2 center, double radius, double minX, double minY, double maxX, double maxY)
    {
        var nearestX = Math.Clamp(center.X, minX, maxX); var nearestY = Math.Clamp(center.Y, minY, maxY);
        var near = Math.Sqrt((center.X - nearestX) * (center.X - nearestX) + (center.Y - nearestY) * (center.Y - nearestY));
        var farX = Math.Max(Math.Abs(center.X - minX), Math.Abs(center.X - maxX)); var farY = Math.Max(Math.Abs(center.Y - minY), Math.Abs(center.Y - maxY));
        var far = Math.Sqrt(farX * farX + farY * farY);
        return near <= radius && radius <= far;
    }
}
