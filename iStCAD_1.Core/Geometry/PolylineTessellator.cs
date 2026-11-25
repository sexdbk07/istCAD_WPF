using iStCAD_1.Core.Model;

namespace iStCAD_1.Core.Geometry;

/// <summary>Shared tessellation for bulged polyline segments used by rendering and hit testing.</summary>
public static class PolylineTessellator
{
    public static IEnumerable<Point2> Tessellate(PolylineEntity polyline, int maxArcSteps = 72)
    {
        var vertices = polyline.Vertices;
        if (vertices.Count == 0) yield break;
        yield return vertices[0].Point;
        var segmentCount = polyline.Closed ? vertices.Count : vertices.Count - 1;
        for (var i = 0; i < segmentCount; i++)
        {
            var current = vertices[i]; var next = vertices[(i + 1) % vertices.Count];
            if (Math.Abs(current.Bulge) < 1e-10) { yield return next.Point; continue; }
            var theta = 4 * Math.Atan(current.Bulge);
            var dx = next.Point.X - current.Point.X; var dy = next.Point.Y - current.Point.Y;
            var chord = Math.Sqrt(dx * dx + dy * dy);
            if (chord < 1e-10) { yield return next.Point; continue; }
            var offset = chord / (2 * Math.Tan(theta / 2));
            var center = new Point2((current.Point.X + next.Point.X) / 2 - dy / chord * offset,
                                    (current.Point.Y + next.Point.Y) / 2 + dx / chord * offset);
            var startAngle = Math.Atan2(current.Point.Y - center.Y, current.Point.X - center.X);
            var steps = Math.Clamp((int)Math.Ceiling(Math.Abs(theta) / (Math.PI / Math.Max(4, maxArcSteps / 2))), 2, maxArcSteps);
            var radius = Point2.Distance(center, current.Point);
            for (var step = 1; step <= steps; step++)
            {
                var angle = startAngle + theta * step / steps;
                yield return new Point2(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
            }
        }
    }
}
