using System.Globalization;
using iStCAD_1.Core.Geometry;

namespace iStCAD_1.Application.Input;

/// <summary>Parses AutoCAD-style absolute/relative coordinate and polar input.</summary>
public static class DynamicInputParser
{
    public static bool TryParsePoint(string? text, Point2 reference, Point2 pointer, out Point2 point)
    {
        point = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        var relative = text.StartsWith('@');
        if (relative) text = text[1..];
        if (text.Contains('<'))
        {
            var parts = text.Split('<', 2);
            if (!TryNumber(parts[0], out var distance) || !TryNumber(parts[1], out var angle)) return false;
            var radians = angle * Math.PI / 180.0;
            point = new Point2(reference.X + distance * Math.Cos(radians), reference.Y + distance * Math.Sin(radians));
            return true;
        }
        var xy = text.Split(',', 2);
        if (xy.Length == 2 && TryNumber(xy[0], out var x) && TryNumber(xy[1], out var y))
        {
            point = relative ? new Point2(reference.X + x, reference.Y + y) : new Point2(x, y);
            return true;
        }
        if (TryNumber(text, out var length))
        {
            var angle = Math.Atan2(pointer.Y - reference.Y, pointer.X - reference.X);
            point = new Point2(reference.X + length * Math.Cos(angle), reference.Y + length * Math.Sin(angle));
            return true;
        }
        return false;
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value);
}
