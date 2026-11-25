using System.IO;
using System.Globalization;
using System.Text;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1.Infras;

/// <summary>Creates a single page vector PDF of the current model space.</summary>
public static class PdfPlotExporter
{
    public static void Export(CadDrawing drawing, string path, double paperWidthMm, double paperHeightMm, bool monochrome, bool includeHidden = false)
    {
        const double mmToPt = 72.0 / 25.4;
        var pageWidth = paperWidthMm * mmToPt;
        var pageHeight = paperHeightMm * mmToPt;
        const double margin = 28.35;
        var entities = drawing.Entities.Where(e => includeHidden || !drawing.HiddenEntityIds.Contains(e.Id)).ToArray();
        var bounds = GetBounds(entities);
        var spanX = Math.Max(1e-6, bounds.Max.X - bounds.Min.X);
        var spanY = Math.Max(1e-6, bounds.Max.Y - bounds.Min.Y);
        var scale = Math.Min((pageWidth - 2 * margin) / spanX, (pageHeight - 2 * margin) / spanY);
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        var offsetX = (pageWidth - spanX * scale) / 2 - bounds.Min.X * scale;
        var offsetY = (pageHeight - spanY * scale) / 2 + bounds.Max.Y * scale;
        double X(double x) => offsetX + x * scale;
        double Y(double y) => offsetY - y * scale;
        var content = new StringBuilder();
        content.AppendLine("1 J 1 j 0.65 w");
        foreach (var entity in entities)
        {
            content.AppendLine(monochrome ? "0.08 G" : LayerColor(entity.Layer));
            switch (entity)
            {
                case LineEntity line:
                    content.AppendLine($"{F(X(line.Start.X))} {F(Y(line.Start.Y))} m {F(X(line.End.X))} {F(Y(line.End.Y))} l S"); break;
                case CircleEntity circle:
                    AppendEllipse(content, X(circle.Center.X), Y(circle.Center.Y), circle.Radius * scale, circle.Radius * scale, 0, 360); break;
                case ArcEntity arc:
                    AppendEllipse(content, X(arc.Center.X), Y(arc.Center.Y), arc.Radius * scale, arc.Radius * scale,
                        arc.StartAngleDegrees, arc.EndAngleDegrees); break;
                case PolylineEntity polyline when polyline.Vertices.Count > 1:
                    var pts = polyline.Vertices.Select(v => (X(v.Point.X), Y(v.Point.Y))).ToArray();
                    content.AppendLine($"{F(pts[0].Item1)} {F(pts[0].Item2)} m");
                    foreach (var pt in pts.Skip(1)) content.AppendLine($"{F(pt.Item1)} {F(pt.Item2)} l");
                    if (polyline.Closed) content.AppendLine("h");
                    content.AppendLine("S"); break;
                case ProxyCadEntity proxy when proxy.HasBounds:
                    var left = X(proxy.BoundsMin.X); var top = Y(proxy.BoundsMax.Y);
                    var width = Math.Max(0.1, (proxy.BoundsMax.X - proxy.BoundsMin.X) * scale);
                    var height = Math.Max(0.1, (proxy.BoundsMax.Y - proxy.BoundsMin.Y) * scale);
                    content.AppendLine($"{F(left)} {F(top)} {F(width)} {F(height)} re S"); break;
            }
        }
        WritePdf(path, pageWidth, pageHeight, content.ToString());
    }

    private static (Point2 Min, Point2 Max) GetBounds(IEnumerable<CadEntity> entities)
    {
        var points = new List<Point2>();
        foreach (var entity in entities)
        {
            switch (entity)
            {
                case LineEntity line: points.Add(line.Start); points.Add(line.End); break;
                case CircleEntity c: points.Add(new(c.Center.X-c.Radius,c.Center.Y-c.Radius)); points.Add(new(c.Center.X+c.Radius,c.Center.Y+c.Radius)); break;
                case ArcEntity a: points.Add(new(a.Center.X-a.Radius,a.Center.Y-a.Radius)); points.Add(new(a.Center.X+a.Radius,a.Center.Y+a.Radius)); break;
                case PolylineEntity p: points.AddRange(p.Vertices.Select(v=>v.Point)); break;
                case ProxyCadEntity proxy when proxy.HasBounds: points.Add(proxy.BoundsMin); points.Add(proxy.BoundsMax); break;
            }
        }
        if (points.Count == 0) return (new(-50,-35), new(50,35));
        return (new(points.Min(p=>p.X), points.Min(p=>p.Y)), new(points.Max(p=>p.X), points.Max(p=>p.Y)));
    }

    private static void AppendEllipse(StringBuilder b, double cx, double cy, double rx, double ry, double start, double end)
    {
        var sweep = end - start;
        while (sweep <= 0) sweep += 360;
        var segments = Math.Clamp((int)Math.Ceiling(sweep / 8), 4, 90);
        for (var i = 0; i <= segments; i++)
        {
            var angle = (start + sweep * i / segments) * Math.PI / 180.0;
            var x = cx + rx * Math.Cos(angle); var y = cy - ry * Math.Sin(angle);
            b.AppendLine($"{F(x)} {F(y)} {(i == 0 ? "m" : "l")}");
        }
        if (Math.Abs(sweep - 360) < 0.001) b.AppendLine("h");
        b.AppendLine("S");
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string LayerColor(string layer)
    {
        ReadOnlySpan<(double R, double G, double B)> palette =
        [
            (0.20, 0.72, 0.62), (0.35, 0.63, 0.90), (0.91, 0.58, 0.25),
            (0.75, 0.53, 0.84), (0.86, 0.73, 0.30), (0.31, 0.76, 0.43),
            (0.88, 0.42, 0.46), (0.52, 0.70, 0.74)
        ];
        uint hash = 2166136261;
        foreach (var ch in layer ?? string.Empty) hash = (hash ^ ch) * 16777619;
        var color = palette[(int)(hash % (uint)palette.Length)];
        return $"{F(color.R)} {F(color.G)} {F(color.B)} RG";
    }

    private static void WritePdf(string path, double width, double height, string content)
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(width)} {F(height)}] /Resources << >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream"
        };
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, Encoding.ASCII, 1024, leaveOpen: true) { NewLine = "\n" };
        writer.WriteLine("%PDF-1.4"); writer.Flush();
        var offsets = new List<long> { 0 };
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(stream.Position); writer.WriteLine($"{i + 1} 0 obj"); writer.WriteLine(objects[i]); writer.WriteLine("endobj"); writer.Flush();
        }
        var xref = stream.Position;
        writer.WriteLine($"xref\n0 {objects.Length + 1}\n0000000000 65535 f ");
        foreach (var offset in offsets.Skip(1)) writer.WriteLine($"{offset:0000000000} 00000 n ");
        writer.WriteLine($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        writer.Flush();
    }
}
