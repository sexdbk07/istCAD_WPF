using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using iStCAD_1.Core.Model;

namespace iStCAD_1.Rendering;

/// <summary>One stroked or filled batch of polylines in paper millimetres (origin lower-left, Y up).</summary>
public sealed class PlotItem
{
    public List<double[]> Figures { get; } = new();
    public List<bool> Closed { get; } = new();
    public byte R, G, B;
    public double WidthMm = 0.25;
    public double[]? DashMm;
    public bool Fill;
    /// <summary>Even-odd fill (hatch islands); text uses non-zero so overlapping glyph contours merge.</summary>
    public bool EvenOdd;
    public (double X0, double Y0, double X1, double Y1)? Clip;

    public bool SameStyle(PlotItem other) =>
        R == other.R && G == other.G && B == other.B && Math.Abs(WidthMm - other.WidthMm) < 1e-9 && Fill == other.Fill && EvenOdd == other.EvenOdd &&
        Clip.Equals(other.Clip) && ((DashMm is null && other.DashMm is null) || (DashMm is not null && other.DashMm is not null && DashMm.SequenceEqual(other.DashMm)));
}

/// <summary>One line of real (editable) text on the sheet, in paper millimetres: baseline origin, em size, width factor, oblique and rotation.</summary>
public sealed class PlotTextRun
{
    public string Text = "";
    public string FontFamily = "Arial";
    public bool Bold, Italic;
    public double X, Y, EmMm, WidthFactor = 1, ObliqueRad, RotationRad;
    public byte R, G, B;
    public (double X0, double Y0, double X1, double Y1) Clip;
}

public sealed class PlotSheet
{
    public string SpaceName { get; init; } = "Model";
    public double WidthMm { get; init; }
    public double HeightMm { get; init; }
    public bool UpsideDown { get; init; }
    public List<PlotItem> Items { get; } = new();
    /// <summary>Real text lines (drawn above <see cref="Items"/>); the PDF embeds the fonts so they stay editable.</summary>
    public List<PlotTextRun> Texts { get; } = new();
    public (double X0, double Y0, double X1, double Y1) Printable { get; init; }
    public double ScaleUsed { get; set; } = 1;
    public List<string> StampLines { get; } = new();
    public int PlottedEntities { get; set; }
    public int SkippedEntities { get; set; }
    /// <summary>Per-type counts and skip reasons for plot-audit.log.</summary>
    public List<string> Diagnostics { get; } = new();
    /// <summary>Why objects were skipped, e.g. "hidden/off layer 120, layer not plotted 30, no geometry 24".</summary>
    public string SkipSummary { get; set; } = "";
    /// <summary>Parts of objects whose bounds fall outside the Window/Display area (not plotted, not counted as skipped).</summary>
    public int OutsideWindowParts { get; set; }
}

/// <summary>Current view, used by the "Display" plot area.</summary>
public readonly record struct PlotView(double CenterX, double CenterY, double ScalePxPerUnit, double WidthPx, double HeightPx);

/// <summary>
/// Converts a drawing space into a paper-space description (vector paths, pens, clip rectangles) honoring page setup:
/// paper size/orientation, margins, plot area, scale, offset, plot style, layer and pen rules, layout viewport.
/// </summary>
public static class PlotEngine
{
    private const double FlattenToleranceMm = 0.02;

    /// <summary>World-space bounds of what a model plot would cover (Window/Display from the setup, otherwise the plottable objects' extents). Empty when nothing plots.</summary>
    public static Rect ModelBounds(CadDrawing drawing, string spaceName, PlotSetup setup, PlotView? view = null, CancellationToken cancellationToken = default)
    {
        if (setup.Area == PlotAreaKind.Window)
            return new Rect(new Point(Math.Min(setup.WindowMinX, setup.WindowMaxX), Math.Min(setup.WindowMinY, setup.WindowMaxY)),
                new Point(Math.Max(setup.WindowMinX, setup.WindowMaxX), Math.Max(setup.WindowMinY, setup.WindowMaxY)));
        if (setup.Area == PlotAreaKind.Display && view is { ScalePxPerUnit: > 0 } v)
        {
            var halfW = v.WidthPx / (2 * v.ScalePxPerUnit); var halfH = v.HeightPx / (2 * v.ScalePxPerUnit);
            return new Rect(new Point(v.CenterX - halfW, v.CenterY - halfH), new Point(v.CenterX + halfW, v.CenterY + halfH));
        }
        var matchers = new Dictionary<PlotLayerRule, Regex>();
        var bounds = Rect.Empty;
        foreach (var entity in drawing.GetSpaceEntities(spaceName))
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!setup.IncludeHiddenObjects && drawing.HiddenEntityIds.Contains(entity.Id)) continue;
            if (entity.Layer.Equals("Defpoints", StringComparison.OrdinalIgnoreCase)) continue;
            if (drawing.LayerStates.TryGetValue(entity.Layer, out var state) && !state.Plot) continue;
            if (MatchRule(setup, entity.Layer, matchers) is { Include: false }) continue;
            var pg = PlotGeometry.Build(entity, 0);
            if (pg is { HasBounds: true }) { bounds.Union(new Rect(new Point(pg.MinX, pg.MinY), new Point(pg.MaxX, pg.MaxY))); continue; }
            Geometry? g = null;
            try { g = CadGeometryBuilder.BuildEntityGeometry(entity, 0, cancellationToken); } catch { }
            if (g is not null && !g.IsEmpty() && !g.Bounds.IsEmpty) bounds.Union(g.Bounds);
        }
        return bounds;
    }

    public static PlotSheet Build(CadDrawing drawing, string spaceName, PlotSetup setup, PlotView? view = null, CancellationToken cancellationToken = default)
    {
        var width = setup.PaperWidthMm; var height = setup.PaperHeightMm;
        var printable = (X0: setup.MarginLeftMm, Y0: setup.MarginBottomMm, X1: width - setup.MarginRightMm, Y1: height - setup.MarginTopMm);
        var sheet = new PlotSheet { SpaceName = spaceName, WidthMm = width, HeightMm = height, UpsideDown = setup.UpsideDown, Printable = printable };
        var isModel = spaceName.Equals("Model", StringComparison.OrdinalIgnoreCase);
        var ruleMatchers = new Dictionary<PlotLayerRule, Regex>();
        var geometryCache = new Dictionary<Guid, Geometry?>();
        var penLookup = setup.PenRules.GroupBy(r => (r.ColorHex ?? "").Trim().ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First());

        var skipByReason = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var skipByLayer = new Dictionary<string, int>(StringComparer.Ordinal);
        void Skip(string reason, string layer)
        {
            sheet.SkippedEntities++;
            skipByReason[reason] = skipByReason.GetValueOrDefault(reason) + 1;
            var key = reason + " | layer " + layer;
            skipByLayer[key] = skipByLayer.GetValueOrDefault(key) + 1;
        }

        IEnumerable<CadEntity> Select(IReadOnlyList<CadEntity> source)
        {
            foreach (var entity in source)
            {
                if (cancellationToken.IsCancellationRequested) yield break;
                if (!setup.IncludeHiddenObjects && drawing.HiddenEntityIds.Contains(entity.Id)) { Skip("hidden/off/frozen layer", entity.Layer); continue; }
                if (entity.Layer.Equals("Defpoints", StringComparison.OrdinalIgnoreCase)) { Skip("Defpoints", entity.Layer); continue; }
                if (drawing.LayerStates.TryGetValue(entity.Layer, out var layerState) && !layerState.Plot) { Skip("layer plot flag off", entity.Layer); continue; }
                var rule = MatchRule(setup, entity.Layer, ruleMatchers);
                if (rule is { Include: false }) { Skip("excluded by layer rule", entity.Layer); continue; }
                yield return entity;
            }
        }

        Geometry? GeometryOf(CadEntity entity)
        {
            if (geometryCache.TryGetValue(entity.Id, out var cached)) return cached;
            Geometry? geometry = null;
            try { geometry = CadGeometryBuilder.BuildEntityGeometry(entity, 0, cancellationToken); } catch { geometry = null; }
            if (geometry is not null && (geometry.IsEmpty() || geometry.Bounds.IsEmpty)) geometry = null;
            geometryCache[entity.Id] = geometry;
            return geometry;
        }

        var outsideByType = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var outsideBounds = Rect.Empty; var insideBounds = Rect.Empty; var windowUsed = Rect.Empty;
        var partsCache = new Dictionary<Guid, List<(Geometry Geometry, bool Fill)>>();
        var plottedByType = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var emptyByType = new SortedDictionary<string, int>(StringComparer.Ordinal);
        List<(Geometry Geometry, bool Fill)> PartsOf(CadEntity entity)
        {
            if (partsCache.TryGetValue(entity.Id, out var cached)) return cached;
            List<(Geometry, bool)> parts;
            try { parts = CadGeometryBuilder.BuildPlotParts(entity, cancellationToken, textIsEditable: EditableOf(entity) is not null, textOnly: true); } catch { parts = new(); }
            partsCache[entity.Id] = parts;
            return parts;
        }

        var strokeCache = new Dictionary<Guid, PlotGeometry?>();
        PlotGeometry? StrokesOf(CadEntity entity, double tolerance)
        {
            if (strokeCache.TryGetValue(entity.Id, out var cached)) return cached;
            var built = PlotGeometry.Build(entity, tolerance);
            strokeCache[entity.Id] = built;
            return built;
        }

        var editableCache = new Dictionary<Guid, List<CadGeometryBuilder.EditableText>?>();
        List<CadGeometryBuilder.EditableText>? EditableOf(CadEntity entity)
        {
            if (editableCache.TryGetValue(entity.Id, out var cached)) return cached;
            List<CadGeometryBuilder.EditableText>? result = null;
            if (entity is CadTextEntity or CadMTextEntity or ProxyCadEntity &&
                CadGeometryBuilder.TryBuildEditableTexts(entity, out var list) && list.All(PdfFontCatalog.CanEncode)) result = list;
            editableCache[entity.Id] = result;
            return result;
        }

        void Emit(IEnumerable<CadEntity> entities, double s, double tx, double ty, (double X0, double Y0, double X1, double Y1) clip, Rect? worldReject)
        {
            PlotItem? current = null;
            var tolerance = Math.Max(1e-9, FlattenToleranceMm / Math.Max(s, 1e-12));
            foreach (var entity in entities)
            {
                if (cancellationToken.IsCancellationRequested) return;
                var typeName = entity.GetType().Name + (entity is ProxyCadEntity pc ? ":" + pc.NativeType : "");
                var parts = PartsOf(entity);   // glyph outlines only (text that cannot be plotted as real text)
                var strokes = StrokesOf(entity, tolerance);
                var drew = false;
                if (EditableOf(entity) is { Count: > 0 } editable)
                {
                    var textStyle = ResolveStyle(drawing, setup, entity, ruleMatchers, penLookup, s, isModelScale: isModel || worldReject is not null);
                    foreach (var t in editable)
                    {
                        if (worldReject is { } rj)
                        {
                            var reach = (t.Text.Length * t.WidthFactor + 2) * t.EmSize;
                            if (t.X + reach < rj.Left || t.X - reach > rj.Right || t.Y + reach < rj.Top || t.Y - reach > rj.Bottom) continue;
                        }
                        sheet.Texts.Add(new PlotTextRun
                        {
                            Text = t.Text, FontFamily = t.FontFamily, Bold = t.Bold, Italic = t.Italic,
                            X = tx + t.X * s, Y = ty + t.Y * s, EmMm = t.EmSize * s, WidthFactor = t.WidthFactor, ObliqueRad = t.ObliqueRadians, RotationRad = t.RotationRadians,
                            R = textStyle.R, G = textStyle.G, B = textStyle.B, Clip = clip
                        });
                        drew = true;
                    }
                }
                if (parts.Count == 0 && strokes is null && !drew) { sheet.SkippedEntities++; emptyByType[typeName] = emptyByType.GetValueOrDefault(typeName) + 1; continue; }

                bool Inside(double x0, double y0, double x1, double y1)
                {
                    if (worldReject is not { } reject) return true;
                    windowUsed = reject;
                    if (x1 < reject.Left || x0 > reject.Right || y1 < reject.Top || y0 > reject.Bottom)
                    {
                        outsideByType[typeName] = outsideByType.GetValueOrDefault(typeName) + 1;
                        outsideBounds.Union(new Rect(new Point(x0, y0), new Point(x1, y1)));
                        return false;
                    }
                    insideBounds.Union(new Rect(new Point(x0, y0), new Point(x1, y1)));
                    return true;
                }
                PlotItem StyleItem(bool fill, bool evenOdd)
                {
                    var style = ResolveStyle(drawing, setup, entity, ruleMatchers, penLookup, s, isModelScale: isModel || worldReject is not null);
                    if (fill) { style.Fill = true; style.DashMm = null; style.EvenOdd = evenOdd; }
                    style.Clip = clip;
                    if (current is null || !current.SameStyle(style)) { current = style; sheet.Items.Add(current); }
                    return current;
                }
                void AddFigures(List<PlotGeometry.Figure> figures, bool fill, bool evenOdd)
                {
                    if (figures.Count == 0) return;
                    var item = StyleItem(fill, evenOdd);
                    foreach (var figure in figures)
                    {
                        var src = figure.Points; var dst = new double[src.Length];
                        for (var k = 0; k + 1 < src.Length; k += 2) { dst[k] = tx + src[k] * s; dst[k + 1] = ty + src[k + 1] * s; }
                        item.Figures.Add(dst); item.Closed.Add(figure.Closed);
                    }
                    drew = true;
                }
                if (strokes is not null && strokes.HasBounds && Inside(strokes.MinX, strokes.MinY, strokes.MaxX, strokes.MaxY))
                {
                    AddFigures(strokes.Fills, true, true);     // solid hatch: even-odd keeps islands
                    AddFigures(strokes.Strokes, false, false);
                }
                foreach (var (geometry, fill) in parts)
                {
                    var b = geometry.Bounds;
                    if (!Inside(b.Left, b.Top, b.Right, b.Bottom)) continue;
                    var item = StyleItem(true, false);
                    AppendFlattened(geometry, tolerance, s, tx, ty, item);
                    drew = true;
                }
                if (drew) { sheet.PlottedEntities++; plottedByType[typeName] = plottedByType.GetValueOrDefault(typeName) + 1; }
            }
        }

        if (isModel)
        {
            var entities = Select(drawing.GetSpaceEntities(spaceName)).ToList();
            Rect world;
            switch (setup.Area)
            {
                case PlotAreaKind.Window:
                    world = new Rect(new Point(Math.Min(setup.WindowMinX, setup.WindowMaxX), Math.Min(setup.WindowMinY, setup.WindowMaxY)),
                        new Point(Math.Max(setup.WindowMinX, setup.WindowMaxX), Math.Max(setup.WindowMinY, setup.WindowMaxY)));
                    break;
                case PlotAreaKind.Display when view is { } v && v.ScalePxPerUnit > 0:
                    var halfW = v.WidthPx / (2 * v.ScalePxPerUnit); var halfH = v.HeightPx / (2 * v.ScalePxPerUnit);
                    world = new Rect(new Point(v.CenterX - halfW, v.CenterY - halfH), new Point(v.CenterX + halfW, v.CenterY + halfH));
                    break;
                default:
                    world = Rect.Empty;
                    foreach (var entity in entities)
                    {
                        var pg = PlotGeometry.Build(entity, 0);
                        if (pg is { HasBounds: true }) { world.Union(new Rect(new Point(pg.MinX, pg.MinY), new Point(pg.MaxX, pg.MaxY))); continue; }
                        var g = GeometryOf(entity);
                        if (g is not null) world.Union(g.Bounds);
                    }
                    break;
            }
            if (world.IsEmpty || world.Width <= 0 && world.Height <= 0) world = new Rect(0, 0, 100, 100);
            var worldWidth = Math.Max(world.Width, 1e-9); var worldHeight = Math.Max(world.Height, 1e-9);
            var printW = Math.Max(1e-6, printable.X1 - printable.X0); var printH = Math.Max(1e-6, printable.Y1 - printable.Y0);
            var scale = setup.FitToPaper
                ? Math.Min(printW / worldWidth, printH / worldHeight)
                : (setup.ScaleDrawingUnits > 0 && setup.ScalePaperMm > 0 ? setup.ScalePaperMm / setup.ScaleDrawingUnits : 1);
            if (!double.IsFinite(scale) || scale <= 0) scale = 1;
            var plotW = worldWidth * scale; var plotH = worldHeight * scale;
            var originX = setup.CenterPlot ? printable.X0 + (printW - plotW) / 2 : printable.X0 + setup.OffsetXMm;
            var originY = setup.CenterPlot ? printable.Y0 + (printH - plotH) / 2 : printable.Y0 + setup.OffsetYMm;
            var tx = originX - world.Left * scale; var ty = originY - world.Top * scale;
            sheet.ScaleUsed = scale;
            var clip = printable;
            Rect? reject = null;
            if (setup.Area is PlotAreaKind.Window or PlotAreaKind.Display)
            {
                clip = (Math.Max(printable.X0, originX), Math.Max(printable.Y0, originY), Math.Min(printable.X1, originX + plotW), Math.Min(printable.Y1, originY + plotH));
                reject = world;
            }
            Emit(entities, scale, tx, ty, clip, reject);
        }
        else
        {
            sheet.ScaleUsed = 1;
            Emit(Select(drawing.GetSpaceEntities(spaceName)), 1, 0, 0, printable, null);
            if (setup.UseViewport && setup.ViewportScale > 0 && setup.ViewportWidthMm > 0 && setup.ViewportHeightMm > 0)
            {
                var vs = setup.ViewportScale;
                var vp = (X0: setup.ViewportXMm, Y0: setup.ViewportYMm, X1: setup.ViewportXMm + setup.ViewportWidthMm, Y1: setup.ViewportYMm + setup.ViewportHeightMm);
                var clip = (X0: Math.Max(vp.X0, printable.X0), Y0: Math.Max(vp.Y0, printable.Y0), X1: Math.Min(vp.X1, printable.X1), Y1: Math.Min(vp.Y1, printable.Y1));
                var halfW = setup.ViewportWidthMm / (2 * vs); var halfH = setup.ViewportHeightMm / (2 * vs);
                var world = new Rect(new Point(setup.ViewportCenterX - halfW, setup.ViewportCenterY - halfH), new Point(setup.ViewportCenterX + halfW, setup.ViewportCenterY + halfH));
                var tx = setup.ViewportXMm + setup.ViewportWidthMm / 2 - setup.ViewportCenterX * vs;
                var ty = setup.ViewportYMm + setup.ViewportHeightMm / 2 - setup.ViewportCenterY * vs;
                Emit(Select(drawing.GetSpaceEntities("Model")), vs, tx, ty, clip, world);
                sheet.ScaleUsed = vs;
                if (setup.ViewportFrame) AddRectangle(sheet, vp.X0, vp.Y0, vp.X1, vp.Y1, 0.35, null);
            }
        }

        foreach (var kv in emptyByType) skipByReason["no geometry"] = skipByReason.GetValueOrDefault("no geometry") + kv.Value;
        sheet.SkipSummary = string.Join(", ", skipByReason.Select(kv => $"{kv.Key} {kv.Value}"));
        sheet.Diagnostics.Add($"{spaceName}: plotted {sheet.PlottedEntities}, skipped {sheet.SkippedEntities}, scale {sheet.ScaleUsed:0.#####}, paper {width:0.##}x{height:0.##} mm");
        sheet.OutsideWindowParts = outsideByType.Values.Sum();
        if (!windowUsed.IsEmpty)
        {
            string R(Rect r) => r.IsEmpty ? "(none)" : $"({r.Left:0.##}, {r.Top:0.##}) - ({r.Right:0.##}, {r.Bottom:0.##})";
            sheet.Diagnostics.Add($"  window used {R(windowUsed)}");
            sheet.Diagnostics.Add($"  geometry inside window {R(insideBounds)}");
            sheet.Diagnostics.Add($"  geometry OUTSIDE window {R(outsideBounds)}  ({outsideByType.Values.Sum()} parts: {string.Join(", ", outsideByType.Select(kv => kv.Key + " x" + kv.Value))})");
        }
        sheet.Diagnostics.Add($"  real text lines: {sheet.Texts.Count}");
        foreach (var kv in plottedByType) sheet.Diagnostics.Add($"  plotted  {kv.Key} x{kv.Value}");
        foreach (var kv in skipByLayer.OrderByDescending(k => k.Value).Take(25)) sheet.Diagnostics.Add($"  skipped  {kv.Key} x{kv.Value}");
        foreach (var kv in emptyByType) sheet.Diagnostics.Add($"  NO GEOMETRY  {kv.Key} x{kv.Value}");
        if (setup.PlotFrame) AddRectangle(sheet, printable.X0, printable.Y0, printable.X1, printable.Y1, 0.5, null);
        if (setup.PlotStamp)
        {
            if (!string.IsNullOrWhiteSpace(setup.StampText)) sheet.StampLines.Add(setup.StampText.Trim());
            sheet.StampLines.Add($"{drawing.Name}  -  {spaceName}  -  {DateTime.Now:yyyy-MM-dd HH:mm}");
        }
        return sheet;
    }

    /// <summary>Wildcard (* ?) layer rule lookup; the first matching rule wins.</summary>
    public static PlotLayerRule? MatchRule(PlotSetup setup, string layer, Dictionary<PlotLayerRule, Regex>? cache = null)
    {
        foreach (var rule in setup.LayerRules)
        {
            if (string.IsNullOrWhiteSpace(rule.Pattern)) continue;
            Regex? regex = null;
            if (cache is null || !cache.TryGetValue(rule, out regex))
            {
                regex = new Regex("^" + Regex.Escape(rule.Pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (cache is not null) cache[rule] = regex;
            }
            if (regex.IsMatch(layer ?? string.Empty)) return rule;
        }
        return null;
    }

    private static PlotItem ResolveStyle(CadDrawing drawing, PlotSetup setup, CadEntity entity, Dictionary<PlotLayerRule, Regex> ruleMatchers,
        Dictionary<string, PlotPenRule> penLookup, double scale, bool isModelScale)
    {
        CadColor source = entity.ExplicitColor ?? (drawing.LayerColors.TryGetValue(entity.Layer, out var layerColor) ? layerColor : entity.DisplayColor ?? new CadColor(255, 255, 255));
        var rule = MatchRule(setup, entity.Layer, ruleMatchers);
        double? weight = rule?.LineWeightMm ?? entity.LineWeightMm ?? (drawing.LayerStates.TryGetValue(entity.Layer, out var layerSt) ? layerSt.LineWeightMm : null);
        var color = source;
        if (rule?.ColorHex is { } overrideHex && TryParseHex(overrideHex, out var overrideColor)) color = overrideColor;
        if (setup.ColorMode == PlotColorMode.PenTable && penLookup.TryGetValue($"#{color.R:X2}{color.G:X2}{color.B:X2}", out var pen))
        {
            if (pen.OutputColorHex is { } outHex && TryParseHex(outHex, out var outColor)) color = outColor;
            if (pen.LineWeightMm > 0) weight = pen.LineWeightMm; // pen lineweight overrides the object's; 0 = "use object lineweight"
        }
        switch (setup.ColorMode)
        {
            case PlotColorMode.Monochrome: color = new CadColor(0, 0, 0); break;
            case PlotColorMode.Grayscale:
                var gray = (byte)Math.Clamp(Math.Round(color.R * 0.299 + color.G * 0.587 + color.B * 0.114), 0, 255);
                color = new CadColor(gray, gray, gray); break;
        }
        // AutoCAD plots "white" (color 7) as black on white paper; very light colors would vanish.
        if (color.R * 0.299 + color.G * 0.587 + color.B * 0.114 > 235) color = new CadColor(0, 0, 0);
        var width = setup.PlotLineweights ? (weight ?? setup.DefaultLineWeightMm) : 0.13;
        if (setup.ScaleLineweights && isModelScale) width *= scale;
        var item = new PlotItem { R = color.R, G = color.G, B = color.B, WidthMm = Math.Max(0.05, width), Fill = false };
        if (!item.Fill && entity.LineTypePattern is { Count: > 0 } pattern && pattern.All(double.IsFinite) && pattern.Any(v => v != 0))
        {
            var ls = double.IsFinite(entity.LineTypeScale) && entity.LineTypeScale > 0 ? entity.LineTypeScale : 1;
            var dash = pattern.Select(v => Math.Abs(v) * ls * scale).ToList();
            if (pattern[0] < 0) dash.Insert(0, 0);
            if (dash.Count % 2 != 0) dash.AddRange(dash.ToArray());
            var period = dash.Sum();
            if (period >= 0.3 && period <= 10_000)
                item.DashMm = dash.Select(v => Math.Max(v, 0.01)).ToArray();
        }
        return item;
    }

    private static bool TryParseHex(string hex, out CadColor color)
    {
        color = default;
        var text = hex.Trim().TrimStart('#');
        if (text.Length != 6 || !int.TryParse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value)) return false;
        color = new CadColor((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    private static void AppendFlattened(Geometry geometry, double tolerance, double s, double tx, double ty, PlotItem item)
    {
        PathGeometry flat;
        try { flat = geometry.GetFlattenedPathGeometry(tolerance, ToleranceType.Absolute); } catch { return; }
        foreach (var figure in flat.Figures)
        {
            var points = new List<double>(16) { tx + figure.StartPoint.X * s, ty + figure.StartPoint.Y * s };
            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment line: points.Add(tx + line.Point.X * s); points.Add(ty + line.Point.Y * s); break;
                    case PolyLineSegment poly:
                        foreach (var p in poly.Points) { points.Add(tx + p.X * s); points.Add(ty + p.Y * s); }
                        break;
                }
            }
            if (points.Count < 4) continue;
            item.Figures.Add(points.ToArray());
            item.Closed.Add(figure.IsClosed);
        }
    }

    private static void AddRectangle(PlotSheet sheet, double x0, double y0, double x1, double y1, double width, (double, double, double, double)? clip)
    {
        var item = new PlotItem { WidthMm = width };
        item.Figures.Add(new[] { x0, y0, x1, y0, x1, y1, x0, y1 });
        item.Closed.Add(true);
        sheet.Items.Add(item);
    }
}
