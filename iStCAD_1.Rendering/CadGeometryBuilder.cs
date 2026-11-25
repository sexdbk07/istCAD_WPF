using System.Globalization;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1.Rendering;

/// <summary>Converts immutable CAD model entities into frozen WPF geometries on worker threads.</summary>
public static class CadGeometryBuilder
{
    public static Geometry? BuildEntityGeometry(CadEntity entity, int lod, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return null;
        Geometry? geometry = entity switch
        {
            LineEntity line => new LineGeometry(ToPoint(line.Start), ToPoint(line.End)),
            CircleEntity circle when double.IsFinite(circle.Radius) => new EllipseGeometry(ToPoint(circle.Center), Math.Abs(circle.Radius), Math.Abs(circle.Radius)),
            ArcEntity arc => CreateArcGeometry(arc, lod),
            PolylineEntity polyline => CreateBulgedPolylineGeometry(polyline, cancellationToken),
            CadTextEntity text => CreateTextGeometry(text.Value, text.Value, false, text.Position, text.Height, text.RotationRadians, 0,
                Math.Clamp(text.WidthFactor * text.TextWidthFactor, 0.05, 10), text.HorizontalAlignment, text.VerticalAlignment, 1, 1,
                text.TextFontFamily, text.TextBold, text.TextItalic, text.TextObliqueAngleRadians),
            CadMTextEntity text => CreateTextGeometry(text.Value, text.FormattedValue, true, text.Position, text.Height, text.RotationRadians,
                text.Width, text.TextWidthFactor, 0, 0, text.LineSpacing, text.AttachmentPoint,
                text.TextFontFamily, text.TextBold, text.TextItalic, text.TextObliqueAngleRadians),
            ProxyCadEntity proxy when proxy.HasBounds || proxy.PreviewPaths is { Count: > 0 } || proxy.ProxyGraphicsPaths is { Count: > 0 } || proxy.ProxyGraphicsTexts is { Count: > 0 } => CreateProxyGeometry(proxy, cancellationToken),
            _ => null
        };
        if (cancellationToken.IsCancellationRequested) return null;
        if (geometry?.CanFreeze == true && !geometry.IsFrozen) geometry.Freeze();
        return geometry;
    }

    /// <summary>Which pieces of a proxy (INSERT/DIMENSION/HATCH...) to build. Plotting strokes the line work, but fills glyphs and solid hatches.</summary>
    private enum ProxyPart { All, Strokes, Text, Fills }

    /// <summary>
    /// Plot geometry for an entity as (geometry, filled) parts: text and solid hatches are filled (like AutoCAD), line work is stroked.
    /// A DIMENSION/INSERT proxy yields separate stroke and text parts so its glyphs are not drawn as thick outlines.
    /// </summary>
    public static List<(Geometry Geometry, bool Fill)> BuildPlotParts(CadEntity entity, CancellationToken cancellationToken = default, bool textIsEditable = false, bool textOnly = false)
    {
        var parts = new List<(Geometry, bool)>(2);
        void Add(Geometry? g, bool fill)
        {
            if (g is null || g.IsEmpty() || g.Bounds.IsEmpty) return;
            if (g.CanFreeze && !g.IsFrozen) g.Freeze();
            parts.Add((g, fill));
        }
        switch (entity)
        {
            case ProxyCadEntity proxy:
                var hasContent = proxy.PreviewPaths is { Count: > 0 } || proxy.ProxyGraphicsPaths is { Count: > 0 } || proxy.ProxyGraphicsTexts is { Count: > 0 } || !string.IsNullOrWhiteSpace(proxy.PreviewText);
                if (textOnly) { if (!textIsEditable && hasContent) Add(CreateProxyGeometry(proxy, cancellationToken, ProxyPart.Text), true); break; }
                if (!hasContent) { Add(BuildEntityGeometry(entity, 0, cancellationToken), false); break; }
                if (proxy.PreviewFilled && proxy.PreviewPaths is { Count: > 0 }) Add(CreateProxyGeometry(proxy, cancellationToken, ProxyPart.Fills), true);
                Add(CreateProxyGeometry(proxy, cancellationToken, ProxyPart.Strokes), false);
                if (!textIsEditable) Add(CreateProxyGeometry(proxy, cancellationToken, ProxyPart.Text), true);
                break;
            case CadTextEntity or CadMTextEntity:
                if (!textIsEditable) Add(BuildEntityGeometry(entity, 0, cancellationToken), true);
                break;
            default:
                if (!textOnly) Add(BuildEntityGeometry(entity, 0, cancellationToken), false);
                break;
        }
        return parts;
    }

    /// <summary>One line of real text in world space: baseline origin (X,Y), em size = text height, plus width factor, oblique and rotation.</summary>
    public sealed record EditableText(string Text, string FontFamily, bool Bold, bool Italic, double X, double Y, double EmSize, double WidthFactor, double ObliqueRadians, double RotationRadians);

    /// <summary>
    /// Lays out TEXT/MTEXT (and the texts of a DIMENSION/INSERT proxy) as plain lines of one font so a PDF can carry real text.
    /// Returns false when the layout cannot be reproduced exactly (mixed fonts/sizes, word-wrapped MTEXT, preview text without a font);
    /// the caller then plots the glyph outlines instead. Placement mirrors <see cref="AddFormattedTextGeometry"/>.
    /// </summary>
    public static bool TryBuildEditableTexts(CadEntity entity, out List<EditableText> result)
    {
        result = new List<EditableText>();
        try
        {
            var ok = entity switch
            {
                CadTextEntity t => TryLayoutText(t.Value, t.Value, false, t.Position, t.Height, t.RotationRadians, 0,
                    Math.Clamp(t.WidthFactor * t.TextWidthFactor, 0.05, 10), t.HorizontalAlignment, t.VerticalAlignment, 1, 1,
                    t.TextFontFamily, t.TextBold, t.TextItalic, t.TextObliqueAngleRadians, result),
                CadMTextEntity m => TryLayoutText(m.Value, m.FormattedValue, true, m.Position, m.Height, m.RotationRadians, m.Width, m.TextWidthFactor, 0, 0,
                    m.LineSpacing, m.AttachmentPoint, m.TextFontFamily, m.TextBold, m.TextItalic, m.TextObliqueAngleRadians, result),
                ProxyCadEntity p when p.ProxyGraphicsTexts is { Count: > 0 } && string.IsNullOrWhiteSpace(p.PreviewText) => TryLayoutProxyTexts(p.ProxyGraphicsTexts, result),
                _ => false
            };
            if (!ok) result.Clear();
            return ok;
        }
        catch { result.Clear(); return false; }
    }

    private static bool TryLayoutProxyTexts(IReadOnlyList<ProxyPreviewText> texts, List<EditableText> output)
    {
        foreach (var t in texts)
        {
            if (t.FontFamily is null) return false;
            if (!TryLayoutText(t.Text, t.Formatted ?? t.Text, t.IsMText, t.Position, t.Height, t.RotationRadians, t.Width, t.WidthFactor, t.HAlign, t.VAlign,
                    t.LineSpacing, t.AttachmentPoint, t.FontFamily, t.Bold, t.Italic, t.Oblique, output)) return false;
        }
        return true;
    }

    private static bool TryLayoutText(string plainValue, string formattedValue, bool isMText, Point2 position, double requestedHeight, double rotationRadians,
        double requestedWidth, double widthFactor, int hAlign, int vAlign, double lineSpacing, int attachmentPoint,
        string fontFamily, bool isBold, bool isItalic, double obliqueAngleRadians, List<EditableText> output)
    {
        var baseHeight = double.IsFinite(requestedHeight) && requestedHeight > 0 ? Math.Clamp(requestedHeight, 1e-6, 100_000) : 1;
        var source = DecodeControlCodes(isMText && !string.IsNullOrEmpty(formattedValue) ? formattedValue : plainValue);
        plainValue = DecodeControlCodes(plainValue);
        var parsed = isMText ? ParseMText(source, fontFamily, isBold, isItalic, baseHeight) : (plainValue, Array.Empty<TextRun>());
        var full = parsed.Item1;
        if (full.Length == 0) return true;
        if (!isMText && full.Contains('\n')) return false;
        if (!IsFinitePoint(position)) return false;
        // One font/size per text only: mixed runs keep their outlines.
        var runFamily = string.IsNullOrWhiteSpace(fontFamily) ? "Arial" : fontFamily; var bold = isBold; var italic = isItalic;
        var first = true;
        foreach (var run in parsed.Item2)
        {
            if (run.Length <= 0) continue;
            if (first) { runFamily = run.FontFamily; bold = run.Bold; italic = run.Italic; first = false; }
            if (!string.Equals(run.FontFamily, runFamily, StringComparison.OrdinalIgnoreCase) || run.Bold != bold || run.Italic != italic ||
                Math.Abs(run.Height - baseHeight) > 1e-9) return false;
        }
        var family = SafeFontFamily(runFamily);
        var constrainedWidth = double.IsFinite(requestedWidth) && requestedWidth > 0 ? Math.Clamp(requestedWidth, 0.01, 100_000) : 0;
        var lines = full.Split('\n');
        if (constrainedWidth > 0)
            foreach (var line in lines)
                if (MeasureTextWidth(line, baseHeight, runFamily, bold, italic) > constrainedWidth) return false; // would wrap
        var k = baseHeight < 64 ? 64 / baseHeight : 1;
        var typeface = new Typeface(family, italic ? FontStyles.Italic : FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var text = new FormattedText(full, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, baseHeight * k, Brushes.White, 1.0);
        var spacing = Math.Clamp(double.IsFinite(lineSpacing) ? lineSpacing : 1, 0.25, 4);
        if (isMText && (full.Contains('\n') || requestedWidth > 0)) text.LineHeight = baseHeight * (5.0 / 3.0) * spacing * k;
        if (constrainedWidth > 0) text.MaxTextWidth = constrainedWidth * k;
        var width = constrainedWidth > 0 ? constrainedWidth * k : Math.Max(baseHeight * k * 0.4, text.WidthIncludingTrailingWhitespace);
        var fontSize = baseHeight * k;
        var B = text.Baseline; var H = text.Height; var cap = 0.716 * fontSize;
        double ox, oy;
        if (isMText)
        {
            var attachment = Math.Clamp(attachmentPoint, 1, 9);
            var horizontalOffset = (attachment - 1) % 3 * 0.5;
            var verticalOffset = (attachment - 1) / 3 * 0.5;
            var blockHeight = Math.Max(cap, H - (B - cap));
            ox = -width * horizontalOffset;
            oy = -(B - cap) - blockHeight * verticalOffset;
        }
        else
        {
            if (hAlign == 4) vAlign = 2;
            ox = hAlign switch { 1 or 4 => -width / 2, 2 => -width, _ => 0 };
            oy = vAlign switch { 1 => -H, 2 => cap / 2 - B, 3 => cap - B, _ => -B };
        }
        var pitch = text.LineHeight > 0 ? text.LineHeight : H;
        var wf = double.IsFinite(widthFactor) ? Math.Clamp(widthFactor, 0.05, 10) : 1;
        var oblique = double.IsFinite(obliqueAngleRadians) ? obliqueAngleRadians : 0;
        var rot = double.IsFinite(rotationRadians) ? rotationRadians : 0;
        var tan = Math.Tan(oblique); var cos = Math.Cos(rot); var sin = Math.Sin(rot);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var lx = ox / k * wf;
            var ly = -((oy + B + i * pitch) / k);   // glyph space is y-down; world is y-up
            lx += ly * tan;                         // oblique skew about the insertion point
            output.Add(new EditableText(line, family.Source ?? "Arial", bold, italic,
                position.X + lx * cos - ly * sin, position.Y + lx * sin + ly * cos, baseHeight, wf, oblique, rot));
        }
        return true;
    }

    private static Geometry CreateProxyGeometry(ProxyCadEntity proxy, CancellationToken cancellationToken, ProxyPart part = ProxyPart.All)
    {
        var preview = new GeometryGroup();
        var wantText = part is ProxyPart.All or ProxyPart.Text;
        var wantGraphicsPaths = part is ProxyPart.All or ProxyPart.Strokes;
        // Solid hatch boundaries are drawn only as fills when plotting (their outline would add a stray stroke).
        var wantPreviewPaths = part is ProxyPart.All or ProxyPart.Fills || (part == ProxyPart.Strokes && !(proxy.PreviewFilled && proxy.PreviewClosed));
        // One StreamGeometry (many figures) per path list instead of one StreamGeometry object per path: an INSERT preview can hold
        // hundreds of paths, and creating that many geometry objects dominated cold-frame build time (~7 ms per INSERT).
        if (wantPreviewPaths && proxy.PreviewPaths is { Count: > 0 })
        {
            var batch = new StreamGeometry();
            var figures = 0;
            using (var ctx = batch.Open())
            {
                var scratch = new List<Point>(256);
                foreach (var path in proxy.PreviewPaths)
                {
                    if (cancellationToken.IsCancellationRequested) return Geometry.Empty;
                    if (AppendPathFigure(ctx, path, proxy.PreviewClosed, proxy.PreviewFilled, scratch)) figures++;
                }
            }
            if (figures > 0) { if (batch.CanFreeze) batch.Freeze(); preview.Children.Add(batch); }
        }
        if (wantGraphicsPaths && proxy.ProxyGraphicsPaths is { Count: > 0 })
        {
            var batch = new StreamGeometry();
            var figures = 0;
            using (var ctx = batch.Open())
            {
                var scratch = new List<Point>(256);
                foreach (var path in proxy.ProxyGraphicsPaths)
                {
                    if (cancellationToken.IsCancellationRequested) return Geometry.Empty;
                    if (AppendPathFigure(ctx, path.Points, path.Closed, false, scratch)) figures++;
                }
            }
            if (figures > 0) { if (batch.CanFreeze) batch.Freeze(); preview.Children.Add(batch); }
        }
        if (wantText && !string.IsNullOrWhiteSpace(proxy.PreviewText))
        {
            if (cancellationToken.IsCancellationRequested) return Geometry.Empty;
            var textPosition = proxy.PreviewTextPosition ?? proxy.BoundsMin;
            var anchor = new Point(textPosition.X, textPosition.Y);
            var height = double.IsFinite(proxy.PreviewTextHeight) && proxy.PreviewTextHeight > 0
                ? proxy.PreviewTextHeight : proxy.HasBounds ? Math.Abs(proxy.BoundsMax.Y - proxy.BoundsMin.Y) : 1;
            AddProxyTextGeometry(preview, proxy.PreviewText, anchor, height, proxy.PreviewTextRotation,
                proxy.PreviewTextWidth, proxy.PreviewTextLineSpacing, proxy.PreviewTextAttachment, cancellationToken: cancellationToken);
        }
        if (wantText && proxy.ProxyGraphicsTexts is not null)
            foreach (var text in proxy.ProxyGraphicsTexts)
            {
                if (cancellationToken.IsCancellationRequested) return Geometry.Empty;
                if (text.FontFamily is not null)
                {
                    // Block text with its real style: font, width factor, oblique, justification, rotation (e.g. DIMENSION text).
                    var g = CreateTextGeometry(text.Text, text.Formatted ?? text.Text, text.IsMText, text.Position, text.Height, text.RotationRadians,
                        text.Width, text.WidthFactor, text.HAlign, text.VAlign, text.LineSpacing, text.AttachmentPoint,
                        text.FontFamily, text.Bold, text.Italic, text.Oblique);
                    preview.Children.Add(g);
                }
                else AddProxyTextGeometry(preview, text.Text, ToPoint(text.Position), text.Height, text.RotationRadians,
                    text.Width, text.LineSpacing, text.AttachmentPoint, cancellationToken: cancellationToken);
            }
        if (preview.Children.Count > 0) return preview;
        if (part != ProxyPart.All) return Geometry.Empty;

        var minX = Math.Min(proxy.BoundsMin.X, proxy.BoundsMax.X); var maxX = Math.Max(proxy.BoundsMin.X, proxy.BoundsMax.X);
        var minY = Math.Min(proxy.BoundsMin.Y, proxy.BoundsMax.Y); var maxY = Math.Max(proxy.BoundsMin.Y, proxy.BoundsMax.Y);
        if (maxX - minX < 1e-9 && maxY - minY < 1e-9)
        {
            var center = new Point2(minX, minY);
            return CreatePolylineGeometry([new Point2(center.X - 1, center.Y), new Point2(center.X + 1, center.Y), new Point2(center.X, center.Y), new Point2(center.X, center.Y - 1), new Point2(center.X, center.Y + 1)], false);
        }
        if (maxX - minX < 1e-9) return new LineGeometry(new Point(minX, minY), new Point(minX, maxY));
        if (maxY - minY < 1e-9) return new LineGeometry(new Point(minX, minY), new Point(maxX, minY));
        return new RectangleGeometry(new Rect(new Point(minX, minY), new Point(maxX, maxY)));
    }

    private static bool AppendPathFigure(StreamGeometryContext ctx, IReadOnlyList<Point2> points, bool closed, bool filled, List<Point> scratch)
    {
        if (points.Count < 2 || !IsFinitePoint(points[0])) return false;
        scratch.Clear();
        for (var i = 1; i < points.Count; i++)
            if (IsFinitePoint(points[i])) scratch.Add(ToPoint(points[i]));
        if (scratch.Count == 0) return false;
        ctx.BeginFigure(ToPoint(points[0]), isFilled: filled && closed, isClosed: closed);
        ctx.PolyLineTo(scratch, isStroked: true, isSmoothJoin: true);
        return true;
    }

    private static void AddProxyTextGeometry(GeometryGroup target, string value, Point anchor, double requestedHeight, double rotationRadians,
        double requestedWidth = 0, double lineSpacing = 1, int attachmentPoint = 1, double widthFactor = 1,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return;
        AddFormattedTextGeometry(target, value, value, true, anchor, requestedHeight, rotationRadians, requestedWidth, widthFactor,
            lineSpacing, attachmentPoint, "Arial", false, false, 0);
    }

    private static Geometry CreateTextGeometry(string value, string formattedValue, bool isMText, Point2 position, double requestedHeight, double rotationRadians,
        double requestedWidth, double widthFactor, int horizontalAlignment, int verticalAlignment, double lineSpacing, int attachmentPoint,
        string fontFamily, bool isBold, bool isItalic, double obliqueAngleRadians)
    {
        var group = new GeometryGroup();
        AddFormattedTextGeometry(group, value, formattedValue, isMText, ToPoint(position), requestedHeight, rotationRadians,
            requestedWidth, widthFactor, lineSpacing, attachmentPoint, fontFamily, isBold, isItalic, obliqueAngleRadians,
            horizontalAlignment, verticalAlignment);
        return group;
    }

    private static double MeasureTextWidth(string value, double height, string fontFamily, bool bold, bool italic)
    {
        var size = double.IsFinite(height) && height > 0 ? Math.Clamp(height, 0.01, 100_000) : 1;
        var typeface = new Typeface(SafeFontFamily(fontFamily), italic ? FontStyles.Italic : FontStyles.Normal,
            bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        return new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, Brushes.White, 1.0)
            .WidthIncludingTrailingWhitespace;
    }

    private static void AddFormattedTextGeometry(GeometryGroup target, string plainValue, string formattedValue, bool isMText, Point anchor,
        double requestedHeight, double rotationRadians, double requestedWidth, double widthFactor, double lineSpacing, int attachmentPoint,
        string fontFamily, bool isBold, bool isItalic, double obliqueAngleRadians, int hAlign = 0, int vAlign = 0)
    {
        try
        {
            var baseHeight = double.IsFinite(requestedHeight) && requestedHeight > 0 ? Math.Clamp(requestedHeight, 1e-6, 100_000) : 1;
            var source = DecodeControlCodes(isMText && !string.IsNullOrEmpty(formattedValue) ? formattedValue : plainValue);
            plainValue = DecodeControlCodes(plainValue);
            var parsed = isMText ? ParseMText(source, fontFamily, isBold, isItalic, baseHeight) : (plainValue, Array.Empty<TextRun>());
            if (parsed.Item1.Length == 0) return;
            // Glyph outlines are built at a large em size in a local space and scaled down: WPF builds coarse, rounded outlines for small em sizes,
            // which showed as broken or vanishing text when zooming in close. Anchor is the local origin; the transform below places the text.
            var k = baseHeight < 64 ? 64 / baseHeight : 1;
            var realAnchor = anchor; anchor = new Point(0, 0);
            var family = SafeFontFamily(fontFamily);
            var typeface = new Typeface(family, isItalic ? FontStyles.Italic : FontStyles.Normal,
                isBold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
            var text = new FormattedText(parsed.Item1, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, baseHeight * k, Brushes.White, 1.0);
            foreach (var run in parsed.Item2)
            {
                if (run.Length <= 0) continue;
                var start = Math.Clamp(run.Start, 0, parsed.Item1.Length - 1);
                var length = Math.Clamp(run.Length, 0, parsed.Item1.Length - start);
                if (length == 0) continue;
                text.SetFontFamily(SafeFontFamily(run.FontFamily), start, length);
                text.SetFontWeight(run.Bold ? FontWeights.Bold : FontWeights.Normal, start, length);
                text.SetFontStyle(run.Italic ? FontStyles.Italic : FontStyles.Normal, start, length);
                if (double.IsFinite(run.Height) && run.Height > 0) text.SetFontSize(Math.Clamp(run.Height, 0.01, 100_000) * k, start, length);
            }
            var spacing = Math.Clamp(double.IsFinite(lineSpacing) ? lineSpacing : 1, 0.25, 4);
            // Single lines keep the font's natural line box so vertical anchoring is exact; multi-line MTEXT uses AutoCAD's 5/3 pitch.
            if (isMText && (parsed.Item1.Contains('\n') || requestedWidth > 0)) text.LineHeight = baseHeight * (5.0 / 3.0) * spacing * k;
            var constrainedWidth = double.IsFinite(requestedWidth) && requestedWidth > 0 ? Math.Clamp(requestedWidth, 0.01, 100_000) : 0;
            if (constrainedWidth > 0) text.MaxTextWidth = constrainedWidth * k;
            var width = constrainedWidth > 0 ? constrainedWidth * k : Math.Max(baseHeight * k * 0.4, text.WidthIncludingTrailingWhitespace);
            // Layout in glyph space (y down) with the insertion point at the origin; the flip below turns it into world space (y up).
            // B = baseline offset of the first line, H = block height, cap = cap height (AutoCAD text height is the cap height).
            var fontSize = baseHeight * k;
            var B = text.Baseline; var H = text.Height; var cap = 0.716 * fontSize;
            double ox, oy;
            if (isMText)
            {
                var attachment = Math.Clamp(attachmentPoint, 1, 9);
                var horizontalOffset = (attachment - 1) % 3 * 0.5;
                var verticalOffset = (attachment - 1) / 3 * 0.5;
                var blockHeight = Math.Max(cap, H - (B - cap));
                ox = -width * horizontalOffset;
                oy = -(B - cap) - blockHeight * verticalOffset;
            }
            else
            {
                if (hAlign == 4) vAlign = 2; // Middle: centred horizontally and vertically
                ox = hAlign switch { 1 or 4 => -width / 2, 2 => -width, _ => 0 };
                oy = vAlign switch { 1 => -H, 2 => cap / 2 - B, 3 => cap - B, _ => -B };
            }
            var layoutAnchor = new Point(anchor.X + ox, anchor.Y + oy);
            var label = text.BuildGeometry(layoutAnchor);
            var transform = new TransformGroup();
            if (k != 1) transform.Children.Add(new ScaleTransform(1 / k, 1 / k, 0, 0));
            if (double.IsFinite(widthFactor) && Math.Abs(widthFactor - 1) > 1e-9)
                transform.Children.Add(new ScaleTransform(Math.Clamp(widthFactor, 0.05, 10), 1, anchor.X, anchor.Y));
            transform.Children.Add(new ScaleTransform(1, -1, anchor.X, anchor.Y));
            if (double.IsFinite(obliqueAngleRadians) && Math.Abs(obliqueAngleRadians) > 1e-9)
                transform.Children.Add(new SkewTransform(obliqueAngleRadians * 180 / Math.PI, 0, anchor.X, anchor.Y));
            if (double.IsFinite(rotationRadians) && Math.Abs(rotationRadians) > 1e-9)
                transform.Children.Add(new RotateTransform(rotationRadians * 180 / Math.PI, anchor.X, anchor.Y)); // world space is y-up: counter-clockwise is positive
            transform.Children.Add(new TranslateTransform(realAnchor.X, realAnchor.Y));
            label.Transform = transform;
            target.Children.Add(label);
        }
        catch { }
    }

    private static (string Text, TextRun[] Runs) ParseMText(string source, string baseFont, bool baseBold, bool baseItalic, double baseHeight)
    {
        var visible = new StringBuilder(Math.Min(source.Length, 65_536));
        var runs = new List<TextRun>();
        var styleStack = new Stack<(string Family, bool Bold, bool Italic, double Height)>();
        var family = string.IsNullOrWhiteSpace(baseFont) ? "Arial" : baseFont;
        var bold = baseBold; var italic = baseItalic; var height = baseHeight;
        var spanStart = 0;
        void Flush() { var length = visible.Length - spanStart; if (length > 0) runs.Add(new TextRun(spanStart, length, family, bold, italic, height)); spanStart = visible.Length; }
        for (var i = 0; i < source.Length && visible.Length < 65_536; i++)
        {
            var ch = source[i];
            if (ch == '{') { Flush(); styleStack.Push((family, bold, italic, height)); spanStart = visible.Length; continue; }
            if (ch == '}')
            {
                Flush();
                if (styleStack.Count > 0) (family, bold, italic, height) = styleStack.Pop();
                spanStart = visible.Length;
                continue;
            }
            if (ch == '%' && i + 2 < source.Length && source[i + 1] == '%')
            {
                var code = char.ToLowerInvariant(source[i + 2]);
                if (code is 'd' or 'p' or 'c') { visible.Append(code == 'd' ? '°' : code == 'p' ? '±' : 'Ø'); i += 2; continue; }
            }
            if (ch != '\\' || i + 1 >= source.Length) { visible.Append(ch); continue; }
            var command = source[++i];
            if (command is '\\' or '{' or '}') { visible.Append(command); continue; }
            if (command == '~') { visible.Append('\u00A0'); continue; }
            if (command == 'U' && i + 5 < source.Length && source[i + 1] == '+' &&
                int.TryParse(source.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var unicode))
            { visible.Append((char)unicode); i += 5; continue; }
            if (command is 'L' or 'l' or 'O' or 'o' or 'K' or 'k') continue;
            if (command is 'P' or 'p')
            {
                Flush();
                if (command == 'P') visible.Append('\n');
                else SkipToTerminator(source, ref i);
                spanStart = visible.Length;
                continue;
            }
            var value = ReadMTextArgument(source, ref i);
            if (value is null) { visible.Append(command); continue; }
            if (command is 'b' or 'i' or 'f' or 'F' or 'H')
            {
                Flush();
                if (command == 'b') bold = value.Trim() != "0";
                else if (command == 'i') italic = value.Trim() != "0";
                else if (command is 'f' or 'F')
                {
                    var fields = value.Split('|');
                    if (!string.IsNullOrWhiteSpace(fields[0])) family = fields[0].Trim();
                    if (fields.Any(field => field.Equals("b0", StringComparison.OrdinalIgnoreCase))) bold = false;
                    else if (fields.Any(field => field.Equals("b1", StringComparison.OrdinalIgnoreCase))) bold = true;
                    if (fields.Any(field => field.Equals("i0", StringComparison.OrdinalIgnoreCase))) italic = false;
                    else if (fields.Any(field => field.Equals("i1", StringComparison.OrdinalIgnoreCase))) italic = true;
                }
                else if (double.TryParse(value.TrimEnd('x', 'X'), NumberStyles.Float, CultureInfo.InvariantCulture, out var size) && size > 0)
                    height = value.EndsWith('x') || value.EndsWith('X') ? baseHeight * size : size;
                spanStart = visible.Length;
                continue;
            }
            if (command == 'S')
            {
                visible.Append(value.Replace('#', '/').Replace('^', '/'));
                continue;
            }
            // Color, tracking, oblique, alignment, and paragraph controls are consumed here;
            // their layout semantics are intentionally handled in later fidelity passes.
        }
        Flush();
        return (visible.ToString(), runs.ToArray());
    }

    private static string? ReadMTextArgument(string source, ref int index)
    {
        var end = source.IndexOf(';', index + 1);
        if (end < 0) return null;
        var value = source[(index + 1)..end];
        index = end;
        return value;
    }

    private static void SkipToTerminator(string source, ref int index)
    {
        var end = source.IndexOf(';', index + 1);
        if (end >= 0) index = end;
    }

    /// <summary>AutoCAD TEXT/MTEXT control codes: %%c diameter, %%d degree, %%p plus-minus, %%% percent, %%nnn char code.</summary>
    internal static string DecodeControlCodes(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.IndexOf("%%", StringComparison.Ordinal) < 0) return value ?? string.Empty;
        var sb = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%' || i + 2 >= value.Length || value[i + 1] != '%') { sb.Append(value[i]); continue; }
            switch (char.ToLowerInvariant(value[i + 2]))
            {
                case 'c': sb.Append('\u00D8'); i += 2; break;
                case 'd': sb.Append('\u00B0'); i += 2; break;
                case 'p': sb.Append('\u00B1'); i += 2; break;
                case '%': sb.Append('%'); i += 2; break;
                case 'u': case 'o': i += 2; break; // underline/overline toggles dropped
                default:
                    if (i + 4 < value.Length && char.IsDigit(value[i + 2]) && char.IsDigit(value[i + 3]) && char.IsDigit(value[i + 4])
                        && int.TryParse(value.AsSpan(i + 2, 3), out var code) && code is > 0 and < 256)
                    { sb.Append((char)code); i += 4; }
                    else sb.Append(value[i]);
                    break;
            }
        }
        return sb.ToString();
    }

    private static readonly System.Collections.Generic.Dictionary<string, string> FontAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["times"] = "Times New Roman", ["timesbd"] = "Times New Roman", ["arialbd"] = "Arial", ["ariali"] = "Arial",
        ["romans"] = "Arial", ["romand"] = "Arial", ["simplex"] = "Arial", ["isocp"] = "Arial", ["isocpeur"] = "Arial",
        ["txt"] = "Consolas", ["monotxt"] = "Consolas", ["cour"] = "Courier New", ["couri"] = "Courier New",
        ["segoeui"] = "Segoe UI", ["romanc"] = "Times New Roman", ["italic"] = "Times New Roman",
    };

    private static FontFamily SafeFontFamily(string? family)
    {
        if (string.IsNullOrWhiteSpace(family) || family.EndsWith(".shx", StringComparison.OrdinalIgnoreCase)) return new FontFamily("Arial");
        var key = family.Trim();
        if (key.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || key.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) || key.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
            key = key[..^4];
        if (FontAliases.TryGetValue(key, out var mapped)) key = mapped;
        try { return new FontFamily(key); } catch { return new FontFamily("Arial"); }
    }

    private sealed record TextRun(int Start, int Length, string FontFamily, bool Bold, bool Italic, double Height);

    private static Geometry CreatePolylineGeometry(IEnumerable<Point2> worldPoints, bool closed, CancellationToken cancellationToken = default, bool filled = false)
    {
        var geometry = new StreamGeometry();
        using var points = worldPoints.GetEnumerator();
        if (!points.MoveNext() || cancellationToken.IsCancellationRequested) return Geometry.Empty;
        using var builder = geometry.Open();
        builder.BeginFigure(ToPoint(points.Current), isFilled: filled && closed, isClosed: closed);
        var batch = new List<Point>(512);
        var count = 1;
        while (points.MoveNext())
        {
            if ((count++ & 255) == 0 && cancellationToken.IsCancellationRequested) return Geometry.Empty;
            batch.Add(ToPoint(points.Current));
            if (batch.Count == 512)
            {
                builder.PolyLineTo(batch, isStroked: true, isSmoothJoin: true);
                batch.Clear();
            }
        }
        if (batch.Count > 0) builder.PolyLineTo(batch, isStroked: true, isSmoothJoin: true);
        return cancellationToken.IsCancellationRequested ? Geometry.Empty : geometry;
    }

    private static Geometry CreateBulgedPolylineGeometry(PolylineEntity polyline, CancellationToken cancellationToken = default)
    {
        var vertices = polyline.Vertices;
        if (vertices.Count < 2) return Geometry.Empty;
        var geometry = new StreamGeometry();
        using var builder = geometry.Open();
        builder.BeginFigure(ToPoint(vertices[0].Point), false, polyline.Closed);
        var count = polyline.Closed ? vertices.Count : vertices.Count - 1;
        for (var i = 0; i < count; i++)
        {
            if ((i & 127) == 0 && cancellationToken.IsCancellationRequested) return Geometry.Empty;
            var current = vertices[i]; var next = vertices[(i + 1) % vertices.Count];
            var bulge = Math.Abs(current.Bulge);
            var chord = Point2.Distance(current.Point, next.Point);
            if (bulge < 1e-12 || chord < 1e-12 || !double.IsFinite(bulge))
            { builder.LineTo(ToPoint(next.Point), true, false); continue; }
            var radius = chord * .25 * (bulge + 1 / bulge);
            if (!double.IsFinite(radius)) { builder.LineTo(ToPoint(next.Point), true, false); continue; }
            // Coordinates here are world XY. The viewport Y flip turns numeric clockwise into CAD CCW.
            builder.ArcTo(ToPoint(next.Point), new Size(radius, radius), 0, bulge > 1,
                current.Bulge > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false);
        }
        return cancellationToken.IsCancellationRequested ? Geometry.Empty : geometry;
    }

    /// <summary>Polylines above this vertex count are screen-simplified when they are far denser than the screen can show.</summary>
    public const int LargePolylineVertexThreshold = 1_500;

    /// <summary>
    /// True when a big polyline has several vertices per screen pixel of its extent. WPF would
    /// otherwise stroke every one of those vertices on every frame while drawing almost nothing extra.
    /// </summary>
    public static bool ShouldSimplifyPolyline(PolylineEntity polyline, double scale, double pixelWidth, double pixelHeight) =>
        polyline.Vertices.Count > LargePolylineVertexThreshold &&
        polyline.Vertices.Count > 4 * (Math.Abs(pixelWidth) + Math.Abs(pixelHeight) + 1);

    /// <summary>
    /// Builds a frozen polyline that skips vertices closer than ~0.75 screen pixels to the previous
    /// kept vertex. Used only for the temporary viewport image; the entity itself is unchanged.
    /// Bulge arcs between skipped vertices are sub-pixel and are drawn as chords.
    /// </summary>
    public static Geometry BuildScreenSimplifiedPolylineGeometry(PolylineEntity polyline, double scale, CancellationToken cancellationToken = default)
    {
        var vertices = polyline.Vertices;
        if (vertices.Count < 2 || !double.IsFinite(scale) || scale <= 0) return Geometry.Empty;
        var minStep = 0.75 / scale;
        var minStepSquared = minStep * minStep;
        var geometry = new StreamGeometry();
        using (var builder = geometry.Open())
        {
            var first = vertices[0].Point;
            builder.BeginFigure(new Point(first.X, first.Y), false, polyline.Closed);
            var last = first;
            var lastIndex = vertices.Count - 1;
            for (var i = 1; i < vertices.Count; i++)
            {
                if ((i & 1023) == 0 && cancellationToken.IsCancellationRequested) return Geometry.Empty;
                var point = vertices[i].Point;
                var dx = point.X - last.X; var dy = point.Y - last.Y;
                if (i != lastIndex && dx * dx + dy * dy < minStepSquared) continue;
                builder.LineTo(new Point(point.X, point.Y), true, false);
                last = point;
            }
        }
        if (cancellationToken.IsCancellationRequested) return Geometry.Empty;
        geometry.Freeze();
        return geometry;
    }

    // ---- Direct figure writers -------------------------------------------------------------
    // Large frames write lines/arcs/circles/polylines straight into ONE StreamGeometry per stroke style
    // instead of allocating one geometry object per entity and wrapping 100k of them in a GeometryGroup.
    private static bool IsFinitePoint(Point2 point) => double.IsFinite(point.X) && double.IsFinite(point.Y);

    public static bool AppendLineFigure(StreamGeometryContext context, Point2 a, Point2 b)
    {
        if (!IsFinitePoint(a) || !IsFinitePoint(b)) return false;
        context.BeginFigure(new Point(a.X, a.Y), false, false);
        context.LineTo(new Point(b.X, b.Y), true, false);
        return true;
    }

    public static bool AppendCircleFigure(StreamGeometryContext context, Point2 center, double radius)
    {
        var r = Math.Abs(radius);
        if (!IsFinitePoint(center) || !double.IsFinite(r) || r <= 1e-12) return false;
        var start = new Point(center.X + r, center.Y);
        var size = new Size(r, r);
        context.BeginFigure(start, false, false);
        context.ArcTo(new Point(center.X - r, center.Y), size, 0, false, SweepDirection.Clockwise, true, false);
        context.ArcTo(start, size, 0, false, SweepDirection.Clockwise, true, false);
        return true;
    }

    public static bool AppendArcFigure(StreamGeometryContext context, ArcEntity arc)
    {
        if (!IsFinitePoint(arc.Center) || !double.IsFinite(arc.Radius) ||
            !double.IsFinite(arc.StartAngleDegrees) || !double.IsFinite(arc.EndAngleDegrees)) return false;
        var rawSweep = arc.EndAngleDegrees - arc.StartAngleDegrees;
        if (!double.IsFinite(rawSweep) || Math.Abs(rawSweep) <= 1e-9) return false;
        var sweep = rawSweep % 360;
        if (sweep < 0) sweep += 360;
        if (sweep <= 1e-9) return false;
        var radius = Math.Abs(arc.Radius);
        if (radius <= 1e-12) return false;
        Point At(double degrees)
        {
            var angle = (degrees % 360) * Math.PI / 180;
            return new Point(arc.Center.X + radius * Math.Cos(angle), arc.Center.Y + radius * Math.Sin(angle));
        }
        context.BeginFigure(At(arc.StartAngleDegrees), false, false);
        context.ArcTo(At(arc.EndAngleDegrees), new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        return true;
    }

    public static bool AppendPolylineFigure(StreamGeometryContext context, PolylineEntity polyline, CancellationToken cancellationToken = default)
    {
        var vertices = polyline.Vertices;
        if (vertices.Count < 2 || !IsFinitePoint(vertices[0].Point)) return false;
        context.BeginFigure(new Point(vertices[0].Point.X, vertices[0].Point.Y), false, polyline.Closed);
        var count = polyline.Closed ? vertices.Count : vertices.Count - 1;
        for (var i = 0; i < count; i++)
        {
            if ((i & 1023) == 0 && cancellationToken.IsCancellationRequested) return true;
            var current = vertices[i]; var next = vertices[(i + 1) % vertices.Count];
            if (!IsFinitePoint(next.Point)) break;
            var nextPoint = new Point(next.Point.X, next.Point.Y);
            var bulge = Math.Abs(current.Bulge);
            var chord = Point2.Distance(current.Point, next.Point);
            if (bulge < 1e-12 || chord < 1e-12 || !double.IsFinite(bulge)) { context.LineTo(nextPoint, true, false); continue; }
            var radius = chord * .25 * (bulge + 1 / bulge);
            if (!double.IsFinite(radius)) { context.LineTo(nextPoint, true, false); continue; }
            context.ArcTo(nextPoint, new Size(radius, radius), 0, bulge > 1,
                current.Bulge > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false);
        }
        return true;
    }

    /// <summary>Like <see cref="AppendPolylineFigure"/> but drops vertices closer than ~0.75 screen pixels.</summary>
    public static bool AppendSimplifiedPolylineFigure(StreamGeometryContext context, PolylineEntity polyline, double scale, CancellationToken cancellationToken = default)
    {
        var vertices = polyline.Vertices;
        if (vertices.Count < 2 || !double.IsFinite(scale) || scale <= 0 || !IsFinitePoint(vertices[0].Point)) return false;
        var minStep = 0.75 / scale;
        var minStepSquared = minStep * minStep;
        var first = vertices[0].Point;
        context.BeginFigure(new Point(first.X, first.Y), false, polyline.Closed);
        var last = first;
        var lastIndex = vertices.Count - 1;
        for (var i = 1; i < vertices.Count; i++)
        {
            if ((i & 1023) == 0 && cancellationToken.IsCancellationRequested) return true;
            var point = vertices[i].Point;
            if (!IsFinitePoint(point)) continue;
            var dx = point.X - last.X; var dy = point.Y - last.Y;
            if (i != lastIndex && dx * dx + dy * dy < minStepSquared) continue;
            context.LineTo(new Point(point.X, point.Y), true, false);
            last = point;
        }
        return true;
    }

    private static Geometry CreateArcGeometry(ArcEntity arc, int lod)
    {
        if (!double.IsFinite(arc.Center.X) || !double.IsFinite(arc.Center.Y) || !double.IsFinite(arc.Radius) ||
            !double.IsFinite(arc.StartAngleDegrees) || !double.IsFinite(arc.EndAngleDegrees)) return Geometry.Empty;
        var rawSweep = arc.EndAngleDegrees - arc.StartAngleDegrees;
        // A zero-sweep ARC is degenerate, not a full circle. Real circles are represented
        // by CircleEntity, so do not synthesize one when start/end angles coincide.
        if (!double.IsFinite(rawSweep) || Math.Abs(rawSweep) <= 1e-9) return Geometry.Empty;
        var sweep = rawSweep % 360;
        if (sweep < 0) sweep += 360;
        if (sweep <= 1e-9) return Geometry.Empty;
        var radius = Math.Abs(arc.Radius);
        if (radius <= 1e-12) return Geometry.Empty;
        Point At(double degrees)
        {
            var angle = (degrees % 360) * Math.PI / 180;
            return new Point(arc.Center.X + radius * Math.Cos(angle), arc.Center.Y + radius * Math.Sin(angle));
        }
        var geometry = new StreamGeometry();
        using var builder = geometry.Open();
        builder.BeginFigure(At(arc.StartAngleDegrees), false, false);
        builder.ArcTo(At(arc.EndAngleDegrees), new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        return geometry;
    }

    private static Point ToPoint(Point2 point) => new(point.X, point.Y);
}
