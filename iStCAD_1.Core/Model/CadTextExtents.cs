using System.Text.RegularExpressions;
using iStCAD_1.Core.Geometry;

namespace iStCAD_1.Core.Model;

/// <summary>
/// Oriented extents of TEXT/MTEXT (insertion alignment, width factor, rotation, line count). Used for the spatial
/// index, picking and window selection so they agree with where the glyphs are actually drawn.
/// </summary>
public static class CadTextExtents
{
    public static Point2[] GetCorners(CadEntity entity) => entity switch
    {
        CadTextEntity t => Corners(t.Position, t.Value, t.Height, t.RotationRadians, t.WidthFactor, 0, t.HorizontalAlignment, t.VerticalAlignment, 0),
        CadMTextEntity m => MTextCorners(m),
        _ => Array.Empty<Point2>()
    };

    public static (double MinX, double MinY, double MaxX, double MaxY) GetBounds(CadEntity entity)
    {
        var c = GetCorners(entity);
        if (c.Length == 0) return default;
        return (c.Min(p => p.X), c.Min(p => p.Y), c.Max(p => p.X), c.Max(p => p.Y));
    }

    private static readonly Regex MTextCodes = new(@"\\[A-Za-z][^;\\{}]*;|\\[LlOoKk~]|[{}]", RegexOptions.Compiled);

    /// <summary>MTEXT box that accounts for word-wrapping inside the requested width and the real ~1.67h line pitch, so clicking any line selects it
    /// (the old estimate counted only explicit line breaks, so wrapped lines below the first were outside the pick box).</summary>
    private static Point2[] MTextCorners(CadMTextEntity m)
    {
        var text = !string.IsNullOrEmpty(m.Value) ? m.Value : MTextCodes.Replace((m.FormattedValue ?? string.Empty).Replace("\\P", "\n"), string.Empty);
        return WrappedTextCorners(text, m.Position, m.Height, m.RotationRadians, m.Width, m.LineSpacing, m.AttachmentPoint);
    }

    /// <summary>Oriented pick box for MTEXT-like text (also used for block/DIMENSION preview texts, which carry their own rotation, width and attachment point).</summary>
    public static Point2[] WrappedTextCorners(string? text, Point2 position, double height, double rotationRadians, double requestedWidth, double lineSpacing, int attachmentPoint)
    {
        var h = double.IsFinite(height) && height > 0 ? height : 1;
        text ??= string.Empty;
        var width = requestedWidth > 0 && double.IsFinite(requestedWidth) ? requestedWidth : 0;
        var charWidth = h * 0.62;
        var lines = 0; var longest = 0.0;
        foreach (var paragraph in text.Split('\n'))
        {
            var length = paragraph.Length * charWidth;
            if (width > 0) { lines += Math.Max(1, (int)Math.Ceiling(length / width)); longest = Math.Max(longest, Math.Min(length, width)); }
            else { lines++; longest = Math.Max(longest, length); }
        }
        var w = width > 0 ? width : Math.Max(h * 0.6, longest);
        var spacing = lineSpacing > 0 && double.IsFinite(lineSpacing) ? lineSpacing : 1;
        var totalHeight = h * 1.2 + (lines - 1) * h * 1.667 * spacing;
        var attachment = attachmentPoint > 0 ? attachmentPoint : 1;
        var column = (attachment - 1) % 3; var row = (attachment - 1) / 3;
        var x0 = -w * column * 0.5;
        var y0 = row switch { 0 => -totalHeight, 1 => -totalHeight / 2, _ => 0 };
        var rotation = double.IsFinite(rotationRadians) ? rotationRadians : 0;
        var cos = Math.Cos(rotation); var sin = Math.Sin(rotation);
        Point2 P(double x, double y) => new(position.X + x * cos - y * sin, position.Y + x * sin + y * cos);
        return [P(x0, y0), P(x0 + w, y0), P(x0 + w, y0 + totalHeight), P(x0, y0 + totalHeight)];
    }

    private static Point2[] Corners(Point2 position, string? value, double height, double rotation, double widthFactor,
        double requestedWidth, int horizontalAlignment, int verticalAlignment, int attachmentPoint)
    {
        var h = double.IsFinite(height) && height > 0 ? height : 1;
        value ??= string.Empty;
        var lines = 1; var longest = 0; var current = 0;
        foreach (var ch in value)
        {
            if (ch == '\n') { lines++; longest = Math.Max(longest, current); current = 0; }
            else current++;
        }
        longest = Math.Max(longest, current);
        var wf = double.IsFinite(widthFactor) && widthFactor > 0 ? widthFactor : 1;
        var w = requestedWidth > 0 && double.IsFinite(requestedWidth) ? requestedWidth : Math.Max(h * 0.6, longest * h * 0.62 * wf);
        double x0, y0, totalHeight;
        if (attachmentPoint > 0)
        {
            totalHeight = h * lines * 1.4;
            var column = (attachmentPoint - 1) % 3; var row = (attachmentPoint - 1) / 3;
            x0 = -w * column * 0.5;
            y0 = row switch { 0 => -totalHeight, 1 => -totalHeight / 2, _ => 0 };
        }
        else
        {
            totalHeight = h * 1.2 + (lines - 1) * h * 1.4;
            x0 = horizontalAlignment switch { 1 or 4 => -w / 2, 2 => -w, _ => 0 };
            y0 = verticalAlignment switch { 1 => 0, 2 => -totalHeight / 2, 3 => -totalHeight, _ => -h * 0.2 - (lines - 1) * h * 1.4 };
        }
        var cos = Math.Cos(double.IsFinite(rotation) ? rotation : 0); var sin = Math.Sin(double.IsFinite(rotation) ? rotation : 0);
        Point2 P(double x, double y) => new(position.X + x * cos - y * sin, position.Y + x * sin + y * cos);
        return [P(x0, y0), P(x0 + w, y0), P(x0 + w, y0 + totalHeight), P(x0, y0 + totalHeight)];
    }
}
