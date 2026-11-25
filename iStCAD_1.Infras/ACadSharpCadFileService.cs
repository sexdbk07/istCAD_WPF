using ACadSharp;
using System.Diagnostics;
using System.IO;
using ACadSharp.Entities;
using ACadSharp.Entities.ProxyGraphics;
using ACadSharp.IO;
using ACadSharp.Objects;
using ACadSharp.Objects.Evaluations;
using ACadSharp.Tables;
using ACadSharp.Types;
using CSMath;
using iStCAD_1.Application.Files;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;
using NativeArc = ACadSharp.Entities.Arc;
using NativeCircle = ACadSharp.Entities.Circle;
using NativeLine = ACadSharp.Entities.Line;
using NativePolyline = ACadSharp.Entities.LwPolyline;
using NativeText = ACadSharp.Entities.TextEntity;
using NativeMText = ACadSharp.Entities.MText;

namespace iStCAD_1.Infras;

/// <summary>In-process DWG/DXF adapter backed by the vendored ACadSharp source project.</summary>
public sealed class ACadSharpCadFileService : ICadFileService
{
    // Keep per-instance previews modest: these limits affect only the temporary viewport
    // representation. Original DWG entities and block definitions remain untouched.
    private const int MaxInsertPreviewDepth = 8;
    private const int MaxInsertPreviewEntities = 20_000;
    private const int MaxInsertPreviewPoints = 30_000;
    private const int MaxInsertPreviewPaths = 4_000;
    private const int MaxInsertPreviewTexts = 512;
    private const int MaxProxyPreviewPoints = 2_048;
    private const int MaxProxyPreviewPaths = 512;

    private sealed record InsertPreviewContent(
        IReadOnlyList<ProxyPreviewPath> Paths,
        IReadOnlyList<ProxyPreviewText> Texts,
        bool WasTruncated);

    // Shared across the entire drawing import. Per-INSERT caps alone still allow a DWG
    // containing thousands of repeated INSERTs to allocate an unbounded preview.
    [ThreadStatic] private static long[]? t_insertStageTicks;
    private sealed record LocalInsertPreview(ProxyPreviewPath[] Paths, ProxyPreviewText[] Texts);
    // Per-thread (Open runs on one thread): block-local preview geometry shared by all INSERTs of the same block + layer.
    [ThreadStatic] private static Dictionary<(BlockRecord Block, string LayerName), LocalInsertPreview>? t_localInsertCache;

    private sealed class PreviewWorkBudget
    {
        private readonly int MaxEntities;
        private readonly int MaxPoints;
        private readonly int MaxPaths;
        private readonly int MaxTexts;
        private readonly int MaxProxyItems;
        public PreviewWorkBudget(int scale = 1)
        {
            MaxEntities = 75_000 * scale; MaxPoints = 100_000 * scale; MaxPaths = 8_000 * scale;
            MaxTexts = 2_000 * scale; MaxProxyItems = 20_000 * scale;
        }
        private int _entities;
        private int _points;
        private int _paths;
        private int _texts;
        private int _proxyItems;
        private int _hatchPreviews;
        public bool IsExhausted { get; private set; }
        public int VisitedEntities => _entities;
        public int PreviewPoints => _points;
        public int PreviewPaths => _paths;
        public int PreviewTexts => _texts;
        public int ProxyItems => _proxyItems;
        public int HatchPreviews => _hatchPreviews;
        public void RecordHatchPreview() => _hatchPreviews++;
        public void Exhaust() => IsExhausted = true;

        public bool TryVisitEntity()
        {
            if (_entities >= MaxEntities) { IsExhausted = true; return false; }
            _entities++;
            return true;
        }

        public bool TryAddGeometryPath(int pointCount)
        {
            if (pointCount < 0 || _paths >= MaxPaths || pointCount > MaxPoints - _points)
            { IsExhausted = true; return false; }
            _paths++;
            _points += pointCount;
            return true;
        }

        public bool TryAddText()
        {
            if (_texts >= MaxTexts) { IsExhausted = true; return false; }
            _texts++;
            return true;
        }

        public bool TryVisitProxyItem()
        {
            if (_proxyItems >= MaxProxyItems) { IsExhausted = true; return false; }
            _proxyItems++;
            return true;
        }
    }

    public IReadOnlyList<string> GetLayouts(CadDrawing drawing)
    {
        var context = drawing.NativeContext as DocumentContext;
        if (context is null) return [Layout.ModelLayoutName, "Layout1", "Layout2"];
        lock (context.SyncRoot)
            return context.Document.Layouts.OrderBy(layout => layout.TabOrder).Select(layout => layout.Name).ToArray();
    }

    public string CreateLayout(CadDrawing drawing)
    {
        DocumentContext context;
        lock (drawing)
        {
            context = drawing.NativeContext as DocumentContext ?? new DocumentContext(CreateDefaultDocument());
            drawing.NativeContext = context;
        }
        lock (context.SyncRoot)
        {
            var document = context.Document;
            var index = 1;
            string name;
            do name = $"Layout{index++}"; while (document.Layouts.ContainsKey(name));
            var record = BlockRecord.PaperSpace;
            record.Name = $"*Paper_Space{index}";
            record.Layout.Name = name;
            record.Layout.TabOrder = document.Layouts.Count();
            document.BlockRecords.Add(record);
            document.Layouts.Add(record.Layout);
            context.SpaceRecords[name] = record;
            drawing.SetSpaceEntities(name, Array.Empty<CadEntity>());
            return name;
        }
    }

    public void SetPlotSettings(CadDrawing drawing, string layoutName, double widthMm, double heightMm, string plotStyle)
    {
        if (drawing.NativeContext is not DocumentContext context) return;
        lock (context.SyncRoot)
        {
            if (!context.Document.Layouts.ContainsKey(layoutName)) return;
            var layout = context.Document.Layouts.GetEntry(layoutName);
            layout.PaperWidth = widthMm;
            layout.PaperHeight = heightMm;
            layout.PaperSize = $"ISO_A{(widthMm > 300 || heightMm > 300 ? "3" : "4")}_({widthMm:0.00}_x_{heightMm:0.00}_MM)";
            layout.PaperRotation = widthMm >= heightMm ? PlotRotation.NoRotation : PlotRotation.Degrees90;
            layout.PlotType = PlotType.DrawingExtents;
            layout.StyleSheet = plotStyle;
            layout.NumeratorScale = 1;
            layout.DenominatorScale = 1;
        }
    }

    public CadDrawing Open(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(path);
        using ICadReader reader = extension.Equals(".dwg", StringComparison.OrdinalIgnoreCase)
            ? new DwgReader(path)
            : extension.Equals(".dxf", StringComparison.OrdinalIgnoreCase)
                ? new DxfReader(path)
                : throw new NotSupportedException($"Unsupported CAD file type: {extension}");
        t_localInsertCache = new Dictionary<(BlockRecord Block, string LayerName), LocalInsertPreview>();
        t_insertStageTicks = new long[4];
        var readDiagnostics = new List<string>();
        reader.OnNotification += (_, notification) =>
        {
            var message = $"{notification.NotificationType}: {notification.Message}";
            if (notification.Exception is not null) message += $" ({notification.Exception.Message})";
            // Keep every real failure ("Could not read/decode", errors); cap the repetitive warnings.
            var important = notification.NotificationType == NotificationType.Error || message.Contains("Could not", StringComparison.OrdinalIgnoreCase);
            if (important ? readDiagnostics.Count < 400 : readDiagnostics.Count < 100 && !readDiagnostics.Contains(message)) readDiagnostics.Add(message);
        };
        if (reader is DwgReader dwgReader)
        {
            dwgReader.Configuration.KeepUnknownEntities = true;
            dwgReader.Configuration.IgnoreProxyGraphics = false;
        }
        if (reader is DxfReader dxfReader)
        {
            dxfReader.Configuration.KeepUnknownEntities = true;
            dxfReader.Configuration.IgnoreProxyGraphics = false;
        }
        var readTimer = Stopwatch.StartNew();
        var native = reader.Read();
        readTimer.Stop();
        cancellationToken.ThrowIfCancellationRequested();
        var mappingTimer = Stopwatch.StartNew();
        var drawing = new CadDrawing
        {
            Name = Path.GetFileNameWithoutExtension(path),
            NativeEntityCount = native.Entities.Count
        };
        drawing.ReadDiagnostics.AddRange(readDiagnostics.OrderByDescending(d => d.Contains("Could not", StringComparison.OrdinalIgnoreCase)).Take(400));
        // This is the database-wide count, including block definitions. Do not mix these
        // counts into NativeEntityTypes: that property is intentionally Model-Space-only.
        foreach (var block in native.BlockRecords)
            drawing.DocumentEntityCount += block.Entities.Count; // O(blocks): no per-entity enumeration pass
        var context = new DocumentContext(native);
        // The same block may be inserted on different layers. Layer-0 children inherit
        // the INSERT layer, so cache native-preview classification per block + inherited layer.
        var nativeBlockPreviewCache = new Dictionary<(BlockRecord Block, string LayerName), bool>();
        // Visibility-state classification is independent of INSERT scale/rotation. Cache it
        // per block and inherited layer so repeated references do not rescan the same trees.
        var unresolvedVisibilityCache = new Dictionary<(BlockRecord Block, string LayerName), bool>();
        // Heavier drawings get smaller preview budgets so memory grows sub-linearly instead of freezing the machine.
        var budgetScale = drawing.DocumentEntityCount <= 300_000 ? 12 : drawing.DocumentEntityCount <= 600_000 ? 8 : drawing.DocumentEntityCount <= 1_200_000 ? 5 : 3;
        var previewWorkBudget = new PreviewWorkBudget(budgetScale); // a drawing with thousands of INSERTs must not exhaust it and blank later blocks
        // Dimensions and top-level proxy graphics used a brand-new budget PER ENTITY, so thousands of them had no
        // drawing-wide limit. They now share one (larger) budget; once spent they fall back to bounds-only.
        var topLevelPreviewBudget = new PreviewWorkBudget(Math.Min(4, budgetScale / 2 + 1));
        var memoryGuardTripped = false; var mappedSinceMemoryCheck = 0;
        var insertBlockCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var mapStageTicks = new long[4]; var mapStageCounts = new int[4]; // native / INSERT / DIMENSION / other proxies
        var unresolvedDynamicInsertCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in native.Layers) drawing.LayerColors[layer.Name] = new CadColor(layer.Color.R, layer.Color.G, layer.Color.B);
        foreach (var layer in native.Layers)
            drawing.LayerStates[layer.Name] = new CadLayerState
            {
                Name = layer.Name, On = layer.IsOn, Frozen = layer.Flags.HasFlag(LayerFlags.Frozen), Locked = layer.Flags.HasFlag(LayerFlags.Locked),
                NewViewportFreeze = layer.Flags.HasFlag(LayerFlags.FrozenNewViewports),
                LineType = layer.LineType?.Name ?? "Continuous", Plot = layer.PlotFlag,
                LineWeightMm = layer.LineWeight >= 0 ? (double)(short)layer.LineWeight / 100.0 : null,
                Transparency = 0
            };
        foreach (var lineType in native.LineTypes)
        {
            if (string.IsNullOrWhiteSpace(lineType.Name) || lineType.Name is "ByLayer" or "ByBlock") continue;
            drawing.LineStyles[lineType.Name] = new CadLineStyleDefinition(lineType.Name, NormalizeLineTypePattern(lineType));
        }
        foreach (var textStyle in native.TextStyles)
        {
            if (string.IsNullOrWhiteSpace(textStyle.Name)) continue;
            drawing.TextStyles[textStyle.Name] = new CadTextStyleDefinition(textStyle.Name, ResolveFontFamily(textStyle.Filename ?? string.Empty),
                textStyle.Height > 0 ? textStyle.Height : 2.5, Math.Clamp(textStyle.Width, 0.05, 10));
        }
        foreach (var dimStyle in native.DimensionStyles)
        {
            if (string.IsNullOrWhiteSpace(dimStyle.Name)) continue;
            drawing.DimensionStyles[dimStyle.Name] = new CadDimensionStyleDefinition(dimStyle.Name,
                dimStyle.TextHeight > 0 ? dimStyle.TextHeight : 2.5, dimStyle.ArrowSize > 0 ? dimStyle.ArrowSize : 2.5, dimStyle.ScaleFactor > 0 ? dimStyle.ScaleFactor : 1);
        }
        if (!drawing.DimensionStyles.ContainsKey(drawing.CurrentDimensionStyle)) drawing.CurrentDimensionStyle = drawing.DimensionStyles.Keys.FirstOrDefault() ?? drawing.CurrentDimensionStyle;
        if (!drawing.TextStyles.ContainsKey(drawing.CurrentTextStyle)) drawing.CurrentTextStyle = drawing.TextStyles.Keys.FirstOrDefault() ?? drawing.CurrentTextStyle;
        foreach (var layout in native.Layouts)
        {
            if (layout.Name.Equals(Layout.ModelLayoutName, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var unit = layout.PaperUnits == PlotPaperUnits.Inches ? 25.4 : 1.0;
                double paperW = layout.PaperWidth * unit, paperH = layout.PaperHeight * unit;
                if (layout.PaperRotation is PlotRotation.Degrees90 or PlotRotation.Degrees270) (paperW, paperH) = (paperH, paperW);
                if (!(paperW > 1 && paperH > 1) || !double.IsFinite(paperW) || !double.IsFinite(paperH)) (paperW, paperH) = (420, 297);
                drawing.LayoutPapers[layout.Name] = (paperW, paperH);
                var list = new List<CadLayoutViewport>();
                foreach (var vp in layout.AssociatedBlock?.Entities.OfType<Viewport>() ?? Enumerable.Empty<Viewport>())
                {
                    if (vp.Id <= 1 || !(vp.Width > 0) || !(vp.Height > 0) || !(vp.ViewHeight > 0) || vp.ActiveStatus <= 0) continue;
                    list.Add(new CadLayoutViewport(vp.Id, vp.Center.X, vp.Center.Y, vp.Width, vp.Height, vp.ViewCenter.X, vp.ViewCenter.Y, vp.ViewHeight,
                        vp.FrozenLayers.Select(l => l.Name).ToArray()));
                }
                if (list.Count > 0) drawing.LayoutViewports[layout.Name] = list;
            }
            catch { }
        }
        var layoutSpaces = native.Layouts.OrderBy(layout => layout.TabOrder)
            .Select(layout => (Name: layout.Name, Record: layout.AssociatedBlock))
            .Where(space => space.Record is not null)
            .ToList();
        if (!layoutSpaces.Any(space => space.Name.Equals(Layout.ModelLayoutName, StringComparison.OrdinalIgnoreCase)))
            layoutSpaces.Insert(0, (Layout.ModelLayoutName, native.ModelSpace));
        foreach (var space in layoutSpaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.SpaceRecords[space.Name] = space.Record;
            var mappedSpaceEntities = new List<CadEntity>(space.Record.Entities.Count);
            var spaceTypeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var entity in space.Record.Entities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nativeType = string.IsNullOrWhiteSpace(entity.ObjectName) ? entity.GetType().Name : entity.ObjectName;
                spaceTypeCounts[nativeType] = spaceTypeCounts.GetValueOrDefault(nativeType) + 1;
                if (space.Name.Equals(Layout.ModelLayoutName, StringComparison.OrdinalIgnoreCase))
                {
                    drawing.NativeEntityTypes[nativeType] = drawing.NativeEntityTypes.GetValueOrDefault(nativeType) + 1;
                    var layerName = string.IsNullOrWhiteSpace(entity.Layer?.Name) ? "0" : entity.Layer.Name;
                    drawing.NativeModelSpaceLayerCounts[layerName] = drawing.NativeModelSpaceLayerCounts.GetValueOrDefault(layerName) + 1;
                }
                if (entity is Insert insertEntity)
                {
                    var blockName = string.IsNullOrWhiteSpace(insertEntity.Block?.Name) ? "<no block>" : insertEntity.Block.Name;
                    insertBlockCounts[blockName] = insertBlockCounts.GetValueOrDefault(blockName) + 1;
                }
                var id = GetStableEntityId(entity);
                if (context.MappedEntities.ContainsKey(id))
                {
                    if (drawing.ReadDiagnostics.Count < 100)
                        drawing.ReadDiagnostics.Add($"Duplicate native handle {entity.Handle:X}; this entity uses a session-only editor ID to avoid overwriting another object mapping.");
                    id = Guid.NewGuid();
                }
                if (!memoryGuardTripped && ++mappedSinceMemoryCheck >= 2000)
                {
                    mappedSinceMemoryCheck = 0;
                    // Memory guard: if the managed heap passes ~55% of the machine's available memory, stop building nested previews
                    // (entities stay native/bounds-only) rather than pushing the PC into paging.
                    var memoryInfo = GC.GetGCMemoryInfo();
                    if (memoryInfo.TotalAvailableMemoryBytes > 0 && GC.GetTotalMemory(false) > memoryInfo.TotalAvailableMemoryBytes * 0.55)
                    {
                        memoryGuardTripped = true;
                        previewWorkBudget.Exhaust(); topLevelPreviewBudget.Exhaust();
                        drawing.ReadDiagnostics.Insert(0, "Memory guard: heap exceeded 55% of available memory while opening; remaining INSERT/DIMENSION previews are bounds-only to keep the system responsive.");
                    }
                }
                var mapStart = Stopwatch.GetTimestamp();
                var mapped = MapEntity(entity, id, out var supported, out var unresolvedDynamicInsert,
                    nativeBlockPreviewCache, unresolvedVisibilityCache, previewWorkBudget, topLevelPreviewBudget, cancellationToken);
                var mapTicks = Stopwatch.GetTimestamp() - mapStart;
                var mapKind = entity is Insert ? 1 : entity is Dimension ? 2 : supported ? 0 : 3;
                mapStageTicks[mapKind] += mapTicks; mapStageCounts[mapKind]++;
                if (unresolvedDynamicInsert && entity is Insert unresolvedInsert)
                {
                    var unresolvedName = string.IsNullOrWhiteSpace(unresolvedInsert.Block?.Name) ? "<no block>" : unresolvedInsert.Block.Name;
                    unresolvedDynamicInsertCounts[unresolvedName] = unresolvedDynamicInsertCounts.GetValueOrDefault(unresolvedName) + 1;
                }
                if (entity is Dimension importedDimension && mapped is ProxyCadEntity importedProxy && TryReadDimensionData(importedDimension) is { } importedData)
                {
                    mapped = importedProxy with { Dimension = importedData };
                    context.ImportedDimensions[id] = importedData;
                }
                // A real native ELLIPSE read from the file: keep its exact parametric data (not just the generic tessellated
                // preview outline already computed above) so MOVE/COPY/ROTATE/SCALE/MIRROR stay exact and re-saving writes
                // back a true ELLIPSE again instead of silently downgrading it to a polyline.
                if (entity is ACadSharp.Entities.Ellipse importedEllipse && mapped is ProxyCadEntity importedEllipseProxy)
                {
                    var center = new Point2(importedEllipse.Center.X, importedEllipse.Center.Y);
                    var axisEndpoint = new Point2(center.X + importedEllipse.MajorAxisEndPoint.X, center.Y + importedEllipse.MajorAxisEndPoint.Y);
                    mapped = importedEllipseProxy with { Ellipse = new CadEllipseData(center, axisEndpoint, Math.Clamp(importedEllipse.RadiusRatio, 1e-6, 1), importedEllipse.StartParameter, importedEllipse.EndParameter) };
                }
                // A real native SPLINE read from the file: keep its exact control points/knots/weights (not just the
                // generic tessellated preview outline already computed above) so MOVE/COPY/ROTATE/SCALE/MIRROR stay
                // exact and re-saving writes back a true SPLINE again instead of silently downgrading it to a polyline.
                if (entity is ACadSharp.Entities.Spline importedSpline && mapped is ProxyCadEntity importedSplineProxy && importedSpline.ControlPoints.Count > 0)
                {
                    mapped = importedSplineProxy with { Spline = new CadSplineData(
                        importedSpline.FitPoints.Select(p => new Point2(p.X, p.Y)).ToArray(),
                        importedSpline.ControlPoints.Select(p => new Point2(p.X, p.Y)).ToArray(),
                        importedSpline.Knots.ToArray(), importedSpline.Weights.ToArray(),
                        importedSpline.Degree, importedSpline.IsClosed) };
                }
                // A real native WIPEOUT read from the file: recover its boundary in world coordinates (InsertPoint plus
                // the local U/V basis - not assumed to be axis-aligned, since a file from real AutoCAD may use any basis)
                // so MOVE/COPY/ROTATE/SCALE/MIRROR stay exact and re-saving writes back a true WIPEOUT, not a polyline.
                if (entity is ACadSharp.Entities.Wipeout importedWipeout && mapped is ProxyCadEntity importedWipeoutProxy && importedWipeout.ClipBoundaryVertices.Count >= 3)
                {
                    var boundary = importedWipeout.ClipBoundaryVertices.Select(v =>
                    {
                        var world = importedWipeout.InsertPoint + importedWipeout.UVector * v.X + importedWipeout.VVector * v.Y;
                        return new Point2(world.X, world.Y);
                    }).ToArray();
                    mapped = importedWipeoutProxy with { Wipeout = new CadWipeoutData(boundary) };
                }
                // A real native VIEWPORT read from the file (same Id > 1 / Width,Height,ViewHeight > 0 / ActiveStatus > 0
                // filter the pre-existing PLOT-dialog extraction loop above already uses to skip the automatic "represents
                // the paper" Id-1 viewport): keep its exact parametric data, not just the generic unsupported/bounds-only
                // fallback MapEntity's default branch produces for it, so MOVE/COPY/SCALE stay exact and re-saving writes
                // back a true VIEWPORT again. The border outline is rebuilt explicitly here (4 line segments) rather than
                // relying on whatever bounds-only preview the default branch may or may not have produced.
                if (entity is ACadSharp.Entities.Viewport importedViewport && mapped is ProxyCadEntity importedViewportProxy
                    && importedViewport.Id > 1 && importedViewport.Width > 0 && importedViewport.Height > 0 && importedViewport.ViewHeight > 0 && importedViewport.ActiveStatus > 0)
                {
                    var viewportData = new CadViewportData(
                        new Point2(importedViewport.Center.X, importedViewport.Center.Y), importedViewport.Width, importedViewport.Height,
                        new Point2(importedViewport.ViewCenter.X, importedViewport.ViewCenter.Y), importedViewport.ViewHeight,
                        importedViewport.FrozenLayers.Select(l => l.Name).ToArray());
                    var vMinX = viewportData.Center.X - viewportData.Width / 2; var vMaxX = viewportData.Center.X + viewportData.Width / 2;
                    var vMinY = viewportData.Center.Y - viewportData.Height / 2; var vMaxY = viewportData.Center.Y + viewportData.Height / 2;
                    var vBorder = new ProxyPreviewPath(new[] { new Point2(vMinX, vMinY), new Point2(vMaxX, vMinY), new Point2(vMaxX, vMaxY), new Point2(vMinX, vMaxY), new Point2(vMinX, vMinY) });
                    mapped = importedViewportProxy with
                    {
                        Viewport = viewportData, ProxyGraphicsPaths = new[] { vBorder },
                        BoundsMin = new Point2(vMinX, vMinY), BoundsMax = new Point2(vMaxX, vMaxY), HasBounds = true
                    };
                    context.ViewportNatives[id] = (viewportData, importedViewport);
                }
                mappedSpaceEntities.Add(mapped);
                context.MappedEntities[id] = entity;
                if (!supported) drawing.UnsupportedEntityCount++;
                // AutoCAD does not display entities that are explicitly invisible, on an off
                // layer, or on a frozen layer. Freeze must be treated like Off in both the
                // viewport index and nested INSERT previews.
                if (!IsNativeEntityVisible(entity)) drawing.HiddenEntityIds.Add(id);
            }
            drawing.SetSpaceEntities(space.Name, mappedSpaceEntities);
            drawing.NativeEntityTypesBySpace[space.Name] = spaceTypeCounts;
        }
        if (unresolvedDynamicInsertCounts.Count > 0)
        {
            var unresolvedSummary = string.Join(", ", unresolvedDynamicInsertCounts.OrderByDescending(pair => pair.Value).Take(10)
                .Select(pair => $"{pair.Key}: {pair.Value:N0}"));
            drawing.ReadDiagnostics.Insert(0, $"Dynamic INSERT preview suppressed because active Visibility State is unresolved (top 10): {unresolvedSummary}. Bounds-only preview is used; native DWG objects are preserved.");
        }
        if (insertBlockCounts.Count > 0)
        {
            var topInsertBlocks = string.Join(", ", insertBlockCounts.OrderByDescending(pair => pair.Value).Take(10)
                .Select(pair => $"{pair.Key}: {pair.Value:N0}"));
            drawing.ReadDiagnostics.Insert(0, $"INSERT block-name counts (top 10): {topInsertBlocks}.");
        }
        static string StageText(string label, long ticks, int count) => $"{label} {count:N0} in {Stopwatch.GetElapsedTime(0, ticks).TotalMilliseconds:0} ms";
        var insertParts = t_insertStageTicks ?? new long[4];
        static double Ms(long ticks) => Stopwatch.GetElapsedTime(0, ticks).TotalMilliseconds;
        drawing.ReadDiagnostics.Insert(0, $"INSERT map breakdown: bounds {Ms(insertParts[0]):0} ms, bounds+visibility {Ms(insertParts[1]):0} ms (cumulative), native-check {Ms(insertParts[2]):0} ms, walk/extract {Ms(insertParts[3]):0} ms.");
        drawing.ReadDiagnostics.Insert(0, "Map stages: " + StageText("native", mapStageTicks[0], mapStageCounts[0]) + ", " + StageText("INSERT", mapStageTicks[1], mapStageCounts[1]) +
            ", " + StageText("DIMENSION", mapStageTicks[2], mapStageCounts[2]) + ", " + StageText("other proxy", mapStageTicks[3], mapStageCounts[3]) + ".");
        drawing.NativeEntityCount = context.SpaceRecords.TryGetValue(Layout.ModelLayoutName, out var modelRecord) ? modelRecord.Entities.Count : native.ModelSpace.Entities.Count;
        drawing.DatabaseSummary = SummarizeDatabaseFast(native);
        if (drawing.ReadDiagnostics.Count < 100)
            drawing.ReadDiagnostics.Add($"Preview allocation: {previewWorkBudget.VisitedEntities:N0} nested entities, {previewWorkBudget.PreviewPoints:N0} points, {previewWorkBudget.PreviewPaths:N0} paths, {previewWorkBudget.PreviewTexts:N0} texts, {previewWorkBudget.ProxyItems:N0} proxy items; {previewWorkBudget.HatchPreviews:N0} Hatch boundaries previewed.");
        if (drawing.ReadDiagnostics.Count < 100)
            drawing.ReadDiagnostics.Insert(0, $"Top-level preview allocation (dimensions/proxies): {topLevelPreviewBudget.VisitedEntities:N0} entities, {topLevelPreviewBudget.PreviewPoints:N0} points, {topLevelPreviewBudget.PreviewPaths:N0} paths{(topLevelPreviewBudget.IsExhausted ? " (budget exhausted; later ones are bounds-only)" : "")}.");
        if (previewWorkBudget.IsExhausted && drawing.ReadDiagnostics.Count < 100)
            drawing.ReadDiagnostics.Add("Preview safety budget reached: some large INSERT/proxy previews were clipped. Native DWG entities and block definitions are preserved.");
        drawing.NativeContext = context;
        t_localInsertCache = null; t_insertStageTicks = null;
        mappingTimer.Stop();
        drawing.OpenMetrics = new CadOpenMetrics(readTimer.Elapsed, mappingTimer.Elapsed,
            drawing.DocumentEntityCount, drawing.AllSpaceEntities.Count);
        return drawing;
    }

    // Keep import diagnostics O(number of tables), not O(all reachable DWG objects).
    // Walking XDictionary/Reactor/XData graphs is expensive and does not affect viewport
    // geometry; on consultant drawings those graphs can be much larger than model space.
    private static CadDatabaseSummary SummarizeDatabaseFast(CadDocument document)
    {
        var entityCount = 0;
        foreach (var block in document.BlockRecords) entityCount += block.Entities.Count;
        return new CadDatabaseSummary(entityCount, document.Layers.Count, document.BlockRecords.Count,
            document.Layouts.Count(), document.LineTypes.Count, document.TextStyles.Count, document.DimensionStyles.Count,
            document.UCSs.Count, document.Views.Count, document.VPorts.Count, document.AppIds.Count,
            0, 0, 0, 0, 0);
    }

    /// <summary>Notes from the last Save: objects removed because they held invalid numbers (NaN/Infinity), ownership problems found, etc.</summary>
    public IReadOnlyList<string> LastSaveNotes { get; private set; } = Array.Empty<string>();

    /// <summary>AutoCAD reports "errors found during open" for objects holding NaN/Infinity or similar invalid values (typically produced by a
    /// bad transform or an imported damaged entity). Remove such objects from the native document before writing, and report anything structural.</summary>
    private static List<string> SanitizeNativeForWrite(CadDocument native)
    {
        var notes = new List<string>();
        static bool F(double v) => double.IsFinite(v);
        static bool F3(XYZ p) => F(p.X) && F(p.Y) && F(p.Z);
        var removed = 0; var wrongOwner = 0;
        foreach (var block in native.BlockRecords)
            foreach (var entity in block.Entities.ToList())
            {
                var bad = entity switch
                {
                    NativeLine l => !F3(l.StartPoint) || !F3(l.EndPoint),
                    NativeArc a => !F3(a.Center) || !F(a.Radius) || !F(a.StartAngle) || !F(a.EndAngle), // Arc derives from Circle: it must come first
                    NativeCircle c => !F3(c.Center) || !F(c.Radius),
                    NativePolyline p => p.Vertices.Count == 0 || p.Vertices.Any(v => !F(v.Location.X) || !F(v.Location.Y) || !F(v.Bulge)),
                    NativeText t => !F3(t.InsertPoint) || !F(t.Height) || !F(t.Rotation),
                    NativeMText m => !F3(m.InsertPoint) || !F(m.Height) || !F(m.Rotation),
                    Insert i => !F3(i.InsertPoint) || !F(i.XScale) || !F(i.YScale) || !F(i.ZScale) || !F(i.Rotation),
                    _ => false
                };
                if (bad)
                {
                    block.Entities.Remove(entity); removed++;
                    if (notes.Count < 6) notes.Add($"Removed invalid {entity.ObjectName} (handle {entity.Handle:X}) in block {block.Name}: non-finite coordinates/values.");
                }
                else if (!ReferenceEquals(entity.Owner, block)) wrongOwner++;
            }
        if (removed > 0) notes.Insert(0, $"Save: removed {removed:N0} object(s) with invalid (NaN/Infinity) values that AutoCAD would report as errors.");
        if (wrongOwner > 0) notes.Add($"Save audit: {wrongOwner:N0} entit(ies) have an owner different from the block record that lists them.");
        return notes;
    }

    /// <summary>
    /// Writes a "zoom extents" view of model space (header extents + the *Active viewport) so the file opens showing its
    /// objects instead of whatever stale view the original file carried.
    /// </summary>
    private static void RefreshSavedModelView(CadDocument native)
    {
        try
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            var any = false;
            foreach (var entity in native.ModelSpace.Entities)
            {
                if (entity is UnknownEntity) continue;
                BoundingBox box;
                try { box = entity.GetBoundingBox(); } catch { continue; }
                var lo = box.Min; var hi = box.Max;
                if (!double.IsFinite(lo.X) || !double.IsFinite(lo.Y) || !double.IsFinite(hi.X) || !double.IsFinite(hi.Y)) continue;
                if (Math.Abs(lo.X) > 1e12 || Math.Abs(lo.Y) > 1e12 || Math.Abs(hi.X) > 1e12 || Math.Abs(hi.Y) > 1e12) continue; // XLINE/RAY style infinities
                minX = Math.Min(minX, lo.X); minY = Math.Min(minY, lo.Y); maxX = Math.Max(maxX, hi.X); maxY = Math.Max(maxY, hi.Y); any = true;
            }
            if (!any) return;
            native.Header.ModelSpaceExtMin = new XYZ(minX, minY, 0);
            native.Header.ModelSpaceExtMax = new XYZ(maxX, maxY, 0);
            if (!native.VPorts.TryGetValue("*Active", out var viewport)) return;
            var width = Math.Max(maxX - minX, 1e-6); var height = Math.Max(maxY - minY, 1e-6);
            var aspect = viewport.AspectRatio > 1e-6 ? viewport.AspectRatio : 1.6;
            viewport.Center = new XY((minX + maxX) / 2, (minY + maxY) / 2);
            viewport.ViewHeight = Math.Max(height, width / aspect) * 1.05;
            viewport.AspectRatio = aspect;
        }
        catch
        {
            // The view is cosmetic; never block saving.
        }
    }

    /// <summary>
    /// Diagnostic for AutoCAD reporting "AcDbMText ... eDwgObjectImproperlyRead" on saved files: counts MTEXT objects per feature
    /// (annotative, background fill, columns, no style, huge text ...) and lists sample handles so the failing set can be matched.
    /// </summary>
    private static List<string> MTextFeatureAudit(CadDocument native)
    {
        var stats = new SortedDictionary<string, (int Count, List<string> Samples)>(StringComparer.Ordinal);
        var total = 0;
        void Add(string feature, NativeMText text)
        {
            if (!stats.TryGetValue(feature, out var entry)) entry = (0, new List<string>());
            if (entry.Samples.Count < 6) entry.Samples.Add(text.Handle.ToString("X"));
            stats[feature] = (entry.Count + 1, entry.Samples);
        }
        foreach (var record in native.BlockRecords)
            foreach (var text in record.Entities.OfType<NativeMText>())
            {
                total++;
                Add("all", text);
                if (text.IsAnnotative) Add("annotative", text);
                Add($"background flags={(int)text.BackgroundFillFlags}", text);
                if (text.BackgroundFillFlags != BackgroundFillFlags.None) Add($"background transparency={text.BackgroundTransparency.Value}", text);
                if (text.ColumnData.ColumnType != ColumnType.NoColumns) Add($"columns type={text.ColumnData.ColumnType}", text);
                if (text.Style is null) Add("style missing", text);
                if ((text.Value?.Length ?? 0) > 2000) Add("text longer than 2000", text);
                if (text.HasExtendedData) Add("has xdata", text);
                if (text.XDictionary is not null) Add("has xdictionary", text);
                if (text.Reactors.Any()) Add("has reactors", text);
                if (text.RectangleHeight == 0) Add("rect height 0", text);
                if (text.DrawingDirection != DrawingDirectionType.LeftToRight) Add($"drawing direction={text.DrawingDirection}", text);
                if (text.LineSpacingStyle != LineSpacingStyleType.AtLeast) Add($"line spacing style={text.LineSpacingStyle}", text);
                if (!record.Name.Equals("*Model_Space", StringComparison.OrdinalIgnoreCase) && !record.Name.StartsWith("*Paper_Space", StringComparison.OrdinalIgnoreCase)) Add("inside block definition", text);
            }
        var lines = new List<string> { $"MTEXT audit: {total:N0} MTEXT objects (feature: count, sample handles)" };
        lines.AddRange(stats.Select(kv => $"  {kv.Key}: {kv.Value.Count:N0}  [{string.Join(",", kv.Value.Samples)}]"));
        return lines;
    }

    /// <summary>
    /// Annotation-scale / block-reference context data (ACDB_*CONTEXTDATA_CLASS) and other non-graphical custom objects are
    /// not readable by ACadSharp. They are not drawing geometry, the original file already had them, and the entity-count check
    /// below still guards the real content, so these notifications must not block saving.
    /// </summary>
    private static bool IsBenignObjectReadError(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        return message.Contains("CONTEXTDATA", StringComparison.OrdinalIgnoreCase)
            || (message.StartsWith("Could not read", StringComparison.OrdinalIgnoreCase) && message.Contains("_CLASS", StringComparison.OrdinalIgnoreCase));
    }

    private static readonly object SaveNotesGate = new();
    private static List<string>? s_pendingSaveNotes;

    public void Save(CadDrawing drawing, string path)
    {
        LastSaveNotes = Array.Empty<string>(); lock (SaveNotesGate) s_pendingSaveNotes = null;
        DocumentContext context;
        lock (drawing)
        {
            context = drawing.NativeContext as DocumentContext ?? new DocumentContext(CreateDefaultDocument());
            drawing.NativeContext = context;
        }
        lock (context.SyncRoot) SaveLocked(drawing, path, context);
        lock (SaveNotesGate) { LastSaveNotes = s_pendingSaveNotes ?? (IReadOnlyList<string>)Array.Empty<string>(); s_pendingSaveNotes = null; }
    }

    /// <summary>Writes the Layer Properties Manager state (on/freeze/lock/color/linetype/lineweight/plot) back to the native layer table.</summary>
    private static void SyncLayerStates(CadDocument document, CadDrawing drawing)
    {
        foreach (var state in drawing.LayerStates.Values)
        {
            try
            {
                if (!document.Layers.TryGetValue(state.Name, out var layer)) layer = document.Layers.TryAdd(new Layer(state.Name));
                layer.IsOn = state.On;
                var flags = layer.Flags & ~(LayerFlags.Frozen | LayerFlags.Locked | LayerFlags.FrozenNewViewports);
                if (state.Frozen) flags |= LayerFlags.Frozen;
                if (state.Locked) flags |= LayerFlags.Locked;
                if (state.NewViewportFreeze) flags |= LayerFlags.FrozenNewViewports;
                layer.Flags = flags;
                layer.PlotFlag = state.Plot;
                if (state.LineWeightMm is { } mm)
                {
                    var code = (short)Math.Round(mm * 100);
                    layer.LineWeight = Enum.IsDefined(typeof(LineWeightType), code) ? (LineWeightType)code : LineWeightType.Default;
                }
                else layer.LineWeight = LineWeightType.Default;
                if (!string.IsNullOrWhiteSpace(state.LineType) && document.LineTypes.TryGetValue(state.LineType, out var lineType)) layer.LineType = lineType;
                // Only rewrite the layer color when the user changed it; keep the file's own (indexed) color otherwise.
                if (drawing.LayerColors.TryGetValue(state.Name, out var color) && (layer.Color.R != color.R || layer.Color.G != color.G || layer.Color.B != color.B))
                    layer.Color = new ACadSharp.Color(color.R, color.G, color.B);
            }
            catch { /* a single bad layer must not block saving */ }
        }
    }

    private static void SaveLocked(CadDrawing drawing, string path, DocumentContext context)
    {
        var native = context.Document;
        foreach (var layout in native.Layouts) context.SpaceRecords[layout.Name] = layout.AssociatedBlock;
        var currentEntities = drawing.AllSpaceEntities;
        var currentIds = currentEntities.Select(e => e.Id).ToHashSet();

        // Keep unrecognized ACadSharp entities in the original native document.
        foreach (var removed in context.MappedEntities.Where(pair => !currentIds.Contains(pair.Key)).ToArray())
            {
                foreach (var spaceRecord in context.SpaceRecords.Values) spaceRecord.Entities.Remove(removed.Value);
                context.MappedEntities.Remove(removed.Key);
            }

        foreach (var removedDim in context.DimensionNatives.Where(pair => !currentIds.Contains(pair.Key)).ToArray())
        {
            RemoveDimensionNatives(native, context, removedDim.Value.Natives);
            context.DimensionNatives.Remove(removedDim.Key);
        }

        foreach (var removedLeader in context.LeaderNatives.Where(pair => !currentIds.Contains(pair.Key)).ToArray())
        {
            foreach (var n in removedLeader.Value.Natives) foreach (var record in context.SpaceRecords.Values) record.Entities.Remove(n);
            context.LeaderNatives.Remove(removedLeader.Key);
        }

        foreach (var removedMLeader in context.MultiLeaderNatives.Where(pair => !currentIds.Contains(pair.Key)).ToArray())
        {
            foreach (var record in context.SpaceRecords.Values) record.Entities.Remove(removedMLeader.Value.Native);
            context.MultiLeaderNatives.Remove(removedMLeader.Key);
        }

        foreach (var removedTable in context.TableNatives.Where(pair => !currentIds.Contains(pair.Key)).ToArray())
        {
            foreach (var record in context.SpaceRecords.Values) record.Entities.Remove(removedTable.Value.Native);
            if (native.BlockRecords.Contains(removedTable.Value.Block.Name)) native.BlockRecords.Remove(removedTable.Value.Block.Name);
            context.TableNatives.Remove(removedTable.Key);
        }

        foreach (var removedViewport in context.ViewportNatives.Where(pair => !currentIds.Contains(pair.Key)).ToArray())
        {
            foreach (var record in context.SpaceRecords.Values) record.Entities.Remove(removedViewport.Value.Native);
            context.ViewportNatives.Remove(removedViewport.Key);
        }

        EnsureBlockRecordsExist(native, drawing, currentEntities);

        foreach (var entity in currentEntities)
        {
            if (entity is ProxyCadEntity { Dimension: { } dimension } dimensionProxy)
            {
                if (context.ImportedDimensions.TryGetValue(entity.Id, out var imported) && !ReferenceEquals(imported, dimension) && !imported.Equals(dimension))
                {
                    // Edited imported dimension: replace the original native object by a regenerated one.
                    if (context.MappedEntities.Remove(entity.Id, out var oldNative)) RemoveDimensionNatives(native, context, new List<Entity> { oldNative });
                    context.ImportedDimensions.Remove(entity.Id);
                }
                if (!context.ImportedDimensions.ContainsKey(entity.Id))
                {
                    SyncDimension(native, context, drawing, dimensionProxy, dimension);
                    continue;
                }
            }
            if (entity is ProxyCadEntity { Leader: { IsMultiLeader: true } mLeaderData } mLeaderProxy)
            {
                SyncMultiLeader(native, context, drawing, mLeaderProxy, mLeaderData);
                continue;
            }
            if (entity is ProxyCadEntity { Leader: { } leaderData } leaderProxy)
            {
                SyncLeader(native, context, drawing, leaderProxy, leaderData);
                continue;
            }
            if (entity is ProxyCadEntity { Table: { } tableData } tableProxy)
            {
                SyncTable(native, context, drawing, tableProxy, tableData);
                continue;
            }
            if (entity is ProxyCadEntity { Viewport: { } viewportData } viewportProxy)
            {
                SyncViewport(native, context, drawing, viewportProxy, viewportData);
                continue;
            }
            if (context.MappedEntities.TryGetValue(entity.Id, out var original))
            {
                UpdateNative(original, entity);
                ApplyLayer(native, original, entity.Layer);
                ApplyCommonProperties(native, original, entity);
                continue;
            }
            var created = entity is ProxyCadEntity { InsertData: { } insertData } ? CreateInsertNative(native, insertData) : CreateNative(entity);
            if (created is null) continue;
            // A freshly-placed block reference gets its ATTRIB values from the block's own AttributeDefinition
            // defaults (ACadSharp's own Insert constructor behaviour); push the actual typed-in values now, on first save.
            if (created is Insert freshInsert && entity is ProxyCadEntity { InsertData: { Attributes: { Count: > 0 } } freshInsertData })
                UpdateInsert(freshInsert, freshInsertData);
            var targetRecord = context.SpaceRecords.GetValueOrDefault(drawing.GetSpaceName(entity)) ?? native.ModelSpace;
            targetRecord.Entities.Add(created);
            ApplyLayer(native, created, entity.Layer);
            ApplyCommonProperties(native, created, entity);
            context.MappedEntities.Add(entity.Id, created);
        }

        var extension = Path.GetExtension(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory,
            $".{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}.tmp{extension}");
        try
        {
            if (extension.Equals(".dwg", StringComparison.OrdinalIgnoreCase))
            {
                // The DWG writer silently skips unknown/proxy/visual-style/dimension-association/dynamic-block objects.
                // References to them (dictionary entries, reactors) would be left dangling, which AutoCAD reports as a
                // damaged file. Remove those references first.
                SyncLayerStates(native, drawing);
                RefreshSavedModelView(native);
                var sanitizeNotes = SanitizeNativeForWrite(native);
                if (sanitizeNotes.Count > 0) lock (SaveNotesGate) s_pendingSaveNotes = sanitizeNotes;
                native.PruneReferencesToUnwrittenObjects(writeDynamicBlockData: false);
                try
                {
                    var audit = native.AuditWriteReferences(writeDynamicBlockData: false);
                    var mtextAudit = MTextFeatureAudit(native);
                    var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iStCAD_1");
                    Directory.CreateDirectory(logDir);
                    File.WriteAllLines(Path.Combine(logDir, "save-audit.log"), new[] { $"{DateTime.Now:O} {Path.GetFileName(path)} - references left pointing at objects that are not written (each is a file error in AutoCAD):" }.Concat(audit.Count == 0 ? new[] { "none" } : audit.ToArray()).Concat(mtextAudit));
                    if (audit.Count > 0)
                    {
                        sanitizeNotes ??= new List<string>();
                        sanitizeNotes.Add($"Save audit: {audit.Count} kind(s) of dangling reference, e.g. {audit[0]} (details: %LOCALAPPDATA%\\iStCAD_1\\save-audit.log).");
                        lock (SaveNotesGate) s_pendingSaveNotes = sanitizeNotes;
                    }
                }
                catch { }
                var writerWarnings = new List<string>();
                using (var writer = new DwgWriter(temporaryPath, native))
                {
                    writer.Configuration.WriteDynamicBlockData = false;
                    writer.OnNotification += (_, args) =>
                    {
                        if (args.NotificationType is NotificationType.Error or NotificationType.Warning && writerWarnings.Count < 20)
                            writerWarnings.Add($"{args.NotificationType}: {args.Message}");
                    };
                    writer.Write();
                }

                // Re-open the generated file before replacing the user's drawing. This catches
                // truncated or structurally unreadable output and prevents corrupting a good file.
                var readErrors = new List<string>();
                using var validationReader = new DwgReader(temporaryPath);
                validationReader.Configuration.KeepUnknownEntities = true;
                validationReader.OnNotification += (_, args) =>
                {
                    if (args.NotificationType == NotificationType.Error && readErrors.Count < 20 && !IsBenignObjectReadError(args.Message)) readErrors.Add(args.Message);
                };
                var validated = validationReader.Read();
                static int CountWritable(CadDocument document) =>
                    document.BlockRecords.Sum(block => block.Entities.Count(entity => entity is not UnknownEntity));
                var expectedEntityCount = CountWritable(native);
                var validatedEntityCount = CountWritable(validated);
                if (readErrors.Count > 0)
                    throw new InvalidDataException("DWG validation reported errors: " + string.Join(" | ", readErrors.Take(5)));
                if (validatedEntityCount != expectedEntityCount)
                    throw new InvalidDataException($"DWG validation found {validatedEntityCount} entities across block records; expected {expectedEntityCount}. Writer notes: {string.Join(" | ", writerWarnings.Take(3))}");
            }
            else if (extension.Equals(".dxf", StringComparison.OrdinalIgnoreCase))
            {
                using (var writer = new DxfWriter(temporaryPath, native)) writer.Write();
                using var validationReader = new DxfReader(temporaryPath);
                validationReader.Configuration.KeepUnknownEntities = true;
                var validated = validationReader.Read();
                var expectedEntityCount = native.BlockRecords.Sum(block => block.Entities.Count);
                var validatedEntityCount = validated.BlockRecords.Sum(block => block.Entities.Count);
                if (validatedEntityCount != expectedEntityCount)
                    throw new InvalidDataException($"DXF validation found {validatedEntityCount} entities across block records; expected {expectedEntityCount}.");
            }
            else throw new NotSupportedException($"Unsupported CAD file type: {extension}");

            // Keep the previous version next to the file so a bad save can always be recovered.
            if (File.Exists(path) && extension.Equals(".dwg", StringComparison.OrdinalIgnoreCase))
            {
                try { File.Copy(path, path + ".bak", overwrite: true); } catch { /* backup is best-effort */ }
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
        drawing.NativeContext = context;
    }

    /// <summary>Color, linetype, linetype scale and lineweight back to the native object - only where the model differs from what the native object already says,
    /// so untouched entities keep their original ACI colors and linetype references.</summary>
    private static void ApplyCommonProperties(CadDocument document, Entity native, CadEntity model)
    {
        try
        {
            CadColor? nativeExplicit = native.Color.IsByLayer || native.Color.IsByBlock ? null : new CadColor(native.Color.R, native.Color.G, native.Color.B);
            if (model.ColorByBlock != native.Color.IsByBlock || nativeExplicit != model.ExplicitColor)
                native.Color = model.ColorByBlock && model.ExplicitColor is null ? ACadSharp.Color.ByBlock : ToNativeColor(model.ExplicitColor);

            var nativeLineType = native.LineType is null || string.Equals(native.LineType.Name, "ByLayer", StringComparison.OrdinalIgnoreCase) ? null : native.LineType.Name;
            if (!string.Equals(nativeLineType, model.LineTypeName, StringComparison.OrdinalIgnoreCase))
            {
                var wanted = model.LineTypeName ?? "ByLayer";
                if (document.LineTypes.TryGetValue(wanted, out var lineType)) native.LineType = lineType;
            }

            var nativeWeight = (short)native.LineWeight >= 0 ? (short)native.LineWeight / 100.0 : (double?)null;
            if (nativeWeight != model.LineWeightMm)
            {
                if (model.LineWeightMm is { } mm && Enum.IsDefined(typeof(LineWeightType), (short)Math.Round(mm * 100))) native.LineWeight = (LineWeightType)(short)Math.Round(mm * 100);
                else if (model.LineWeightMm is null) native.LineWeight = LineWeightType.ByLayer;
            }

            var globalScale = Math.Max(1e-6, native.Document?.Header?.LineTypeScale ?? 1.0);
            if (Math.Abs(native.LineTypeScale * globalScale - model.LineTypeScale) > 1e-9 && model.LineTypeScale > 0)
                native.LineTypeScale = model.LineTypeScale / globalScale;
        }
        catch { /* property write-back is best effort; the geometry save must not fail because of it */ }
    }

    private static void ApplyLayer(CadDocument document, Entity native, string? layerName)
    {
        if (string.IsNullOrWhiteSpace(layerName)) layerName = Layer.DefaultName;
        if (!document.Layers.TryGetValue(layerName, out var layer))
        {
            layer = document.Layers.TryAdd(new Layer(layerName));
        }
        native.Layer = layer;
    }

    public void SetLayerColor(CadDrawing drawing, string layerName, CadColor color)
    {
        if (string.IsNullOrWhiteSpace(layerName)) return;
        if (drawing.NativeContext is not DocumentContext context) { drawing.LayerColors[layerName] = color; return; }
        lock (context.SyncRoot)
        {
            drawing.LayerColors[layerName] = color;
            var document = context.Document;
            if (!document.Layers.TryGetValue(layerName, out var layer)) layer = document.Layers.TryAdd(new Layer(layerName));
            layer.Color = new ACadSharp.Color(color.R, color.G, color.B);
        }
    }

    // AC1027 is the AutoCAD 2013 DWG format, which remains readable by a much wider
    // range of AutoCAD releases than the AC1032 (2018) default used by ACadSharp.
    // Existing files keep their source version; this applies only to newly-created documents.
    private static CadDocument CreateDefaultDocument() => new(ACadVersion.AC1027);

    /// <summary>Reads an AutoCAD dimension into the app's parametric form (linear horizontal/vertical, aligned, radius, diameter); null for other kinds.</summary>
    private static CadDimensionData? TryReadDimensionData(Dimension dim)
    {
        try
        {
            if (dim.Normal.Z < 0.999999) return null;
            var s = dim.GetActiveDimensionStyle() ?? dim.Style;
            if (s is null) return null;
            var style = new CadDimensionStyleDefinition(s.Name, s.TextHeight > 0 ? s.TextHeight : 2.5, s.ArrowSize > 0 ? s.ArrowSize : 2.5, s.ScaleFactor > 0 ? s.ScaleFactor : 1,
                s.ExtensionLineOffset, s.ExtensionLineExtension, s.DimensionLineGap, Math.Clamp((int)s.DecimalPlaces, 0, 8));
            var text = dim.Text;
            var over = string.IsNullOrWhiteSpace(text) || text == "<>" ? null : text;
            static Point2 P(XYZ v) => new(v.X, v.Y);
            switch (dim)
            {
                case DimensionDiameter diameter:
                {
                    var a = P(diameter.DefinitionPoint); var b = P(diameter.AngleVertex);
                    var center = new Point2((a.X + b.X) / 2, (a.Y + b.Y) / 2); var r = Point2.Distance(a, b) / 2;
                    if (r < 1e-9) return null;
                    var t = P(diameter.TextMiddlePoint);
                    return new CadDimensionData("DIAMETER", new[] { center, Point2.Distance(t, center) < 1e-9 ? a : t }, r, style, over);
                }
                case DimensionRadius radius:
                {
                    var c = P(radius.DefinitionPoint); var v = P(radius.AngleVertex); var r = Point2.Distance(c, v);
                    if (r < 1e-9) return null;
                    var t = P(radius.TextMiddlePoint);
                    return new CadDimensionData("RADIUS", new[] { c, Point2.Distance(t, c) < 1e-9 ? v : t }, r, style, over);
                }
                case DimensionLinear linear:
                {
                    var rot = Math.IEEERemainder(linear.Rotation, Math.PI); // -pi/2..pi/2
                    var p1 = P(linear.FirstPoint); var p2 = P(linear.SecondPoint); var def = P(linear.DefinitionPoint);
                    var mid = new Point2((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);
                    if (Math.Abs(rot) < 1e-6) return new CadDimensionData("LINEAR", new[] { p1, p2, new Point2(mid.X, def.Y) }, 0, style, over);
                    if (Math.Abs(Math.Abs(rot) - Math.PI / 2) < 1e-6) return new CadDimensionData("LINEAR", new[] { p1, p2, new Point2(def.X, mid.Y) }, 0, style, over);
                    return null;
                }
                case DimensionAligned aligned:
                    return new CadDimensionData("ALIGNED", new[] { P(aligned.FirstPoint), P(aligned.SecondPoint), P(aligned.DefinitionPoint) }, 0, style, over);
            }
        }
        catch { }
        return null;
    }

    private static void RemoveDimensionNatives(CadDocument document, DocumentContext context, List<Entity> natives)
    {
        foreach (var native in natives)
        {
            foreach (var record in context.SpaceRecords.Values) record.Entities.Remove(native);
            try { if (native is Dimension { Block: { } block }) document.BlockRecords.Remove(block.Name); } catch { }
        }
    }

    /// <summary>Writes one in-app dimension object as a real DIMENSION (with its anonymous block); falls back to native lines/polylines/MText.</summary>
    private static void SyncDimension(CadDocument document, DocumentContext context, CadDrawing drawing, ProxyCadEntity proxy, CadDimensionData data)
    {
        var space = context.SpaceRecords.GetValueOrDefault(drawing.GetSpaceName(proxy)) ?? document.ModelSpace;
        if (context.DimensionNatives.TryGetValue(proxy.Id, out var existing))
        {
            if (ReferenceEquals(existing.Data, data) || existing.Data.Equals(data))
            {
                foreach (var e in existing.Natives) { ApplyLayer(document, e, proxy.Layer); ApplyCommonProperties(document, e, proxy); }
                return;
            }
            RemoveDimensionNatives(document, context, existing.Natives);
            context.DimensionNatives.Remove(proxy.Id);
        }
        var natives = CreateDimensionNatives(document, space, proxy, data);
        foreach (var e in natives) { ApplyLayer(document, e, proxy.Layer); ApplyCommonProperties(document, e, proxy); }
        context.DimensionNatives[proxy.Id] = (data, natives);
    }

    private static List<Entity> CreateDimensionNatives(CadDocument document, BlockRecord space, ProxyCadEntity proxy, CadDimensionData data)
    {
        if (data.Kind != "ANGULAR")
        {
            Dimension? dim = null;
            try
            {
                dim = MakeNativeDimension(document, data);
                if (dim is not null)
                {
                    space.Entities.Add(dim);
                    dim.UpdateBlock();
                    if (dim.Block is { Entities.Count: > 0 }) return new List<Entity> { dim };
                    space.Entities.Remove(dim);
                }
            }
            catch
            {
                try { if (dim is not null) space.Entities.Remove(dim); } catch { }
            }
        }
        // Fallback: the dimension's drawing as plain native geometry (still one object inside iStCAD_1).
        var result = new List<Entity>();
        foreach (var path in proxy.ProxyGraphicsPaths ?? Array.Empty<ProxyPreviewPath>())
        {
            if (path.Points.Count < 2) continue;
            Entity part = path.Points.Count == 2 && !path.Closed
                ? new NativeLine(ToXyz(path.Points[0]), ToXyz(path.Points[1]))
                : CreatePolyline(new PolylineEntity(path.Points.Select(pt => new PolylineVertex(pt, 0)).ToArray(), path.Closed, Guid.NewGuid()));
            space.Entities.Add(part); result.Add(part);
        }
        foreach (var t in proxy.ProxyGraphicsTexts ?? Array.Empty<ProxyPreviewText>())
        {
            var part = CreateNativeMText(new CadMTextEntity(t.Text, t.Text, t.Position, t.Height, t.RotationRadians, t.Width, t.LineSpacing, t.AttachmentPoint, Guid.NewGuid()));
            space.Entities.Add(part); result.Add(part);
        }
        return result;
    }

    private static Dimension? MakeNativeDimension(CadDocument document, CadDimensionData d)
    {
        var p = d.Points;
        var style = d.Style;
        if (!document.DimensionStyles.TryGetValue(style.Name, out var dimStyle))
        {
            dimStyle = new DimensionStyle(style.Name)
            {
                TextHeight = style.TextHeight, ArrowSize = style.ArrowSize, ScaleFactor = style.Scale <= 0 ? 1 : style.Scale,
                ExtensionLineOffset = style.ExtensionOffset, ExtensionLineExtension = style.ExtensionExtend, DimensionLineGap = style.TextGap,
                DecimalPlaces = (short)Math.Clamp(style.Decimals, 0, 8)
            };
            document.DimensionStyles.Add(dimStyle);
        }
        Dimension? dim;
        switch (d.Kind)
        {
            case "LINEAR":
            {
                var mid = new Point2((p[0].X + p[1].X) / 2, (p[0].Y + p[1].Y) / 2);
                var horizontal = Math.Abs(p[2].Y - mid.Y) >= Math.Abs(p[2].X - mid.X);
                dim = new DimensionLinear
                {
                    FirstPoint = ToXyz(p[0]), SecondPoint = ToXyz(p[1]), Rotation = horizontal ? 0 : Math.PI / 2,
                    DefinitionPoint = horizontal ? new XYZ(p[1].X, p[2].Y, 0) : new XYZ(p[2].X, p[1].Y, 0),
                    TextMiddlePoint = horizontal ? new XYZ(mid.X, p[2].Y, 0) : new XYZ(p[2].X, mid.Y, 0)
                };
                break;
            }
            case "ALIGNED":
            {
                double dx = p[1].X - p[0].X, dy = p[1].Y - p[0].Y, len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-9) return null;
                double nx = -dy / len, ny = dx / len;
                var offset = (p[2].X - p[1].X) * nx + (p[2].Y - p[1].Y) * ny;
                var aligned = new DimensionAligned { FirstPoint = ToXyz(p[0]), SecondPoint = ToXyz(p[1]) };
                aligned.Offset = offset;
                aligned.TextMiddlePoint = new XYZ((p[0].X + p[1].X) / 2 + nx * offset, (p[0].Y + p[1].Y) / 2 + ny * offset, 0);
                dim = aligned;
                break;
            }
            case "RADIUS":
            case "DIAMETER":
            {
                double dx = p[1].X - p[0].X, dy = p[1].Y - p[0].Y, len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-9 || d.Radius <= 0) return null;
                var u = new XYZ(dx / len, dy / len, 0);
                var c = ToXyz(p[0]);
                if (d.Kind == "RADIUS") dim = new DimensionRadius { DefinitionPoint = c, AngleVertex = c + u * d.Radius, TextMiddlePoint = ToXyz(p[1]) };
                else dim = new DimensionDiameter { DefinitionPoint = c + u * d.Radius, AngleVertex = c - u * d.Radius, TextMiddlePoint = ToXyz(p[1]) };
                break;
            }
            default: return null;
        }
        dim.Style = dimStyle;
        if (!string.IsNullOrWhiteSpace(d.TextOverride)) dim.Text = d.TextOverride!;
        return dim;
    }

    private static Entity? CreateNative(CadEntity entity) => entity switch
    {
        LineEntity line => new NativeLine(ToXyz(line.Start), ToXyz(line.End)),
        CircleEntity circle => new NativeCircle { Center = ToXyz(circle.Center), Radius = circle.Radius },
        ArcEntity arc => new NativeArc
        {
            Center = ToXyz(arc.Center), Radius = arc.Radius,
            StartAngle = arc.StartAngleDegrees * Math.PI / 180.0,
            EndAngle = arc.EndAngleDegrees * Math.PI / 180.0
        },
        PolylineEntity polyline => CreatePolyline(polyline),
        CadTextEntity text => new NativeText(text.Value)
        {
            InsertPoint = ToXyz(text.Position), Height = text.Height, Rotation = text.RotationRadians,
            WidthFactor = text.WidthFactor,
            HorizontalAlignment = (TextHorizontalAlignment)Math.Clamp(text.HorizontalAlignment, 0, 5),
            VerticalAlignment = (TextVerticalAlignmentType)Math.Clamp(text.VerticalAlignment, 0, 3),
            Color = ToNativeColor(text.ExplicitColor)
        },
        CadMTextEntity text => CreateNativeMText(text),
        ProxyCadEntity { Hatch: { } hatchData } => CreateNativeHatch(hatchData),
        ProxyCadEntity { Ellipse: { } ellipseData } => CreateNativeEllipse(ellipseData),
        ProxyCadEntity { Spline: { } splineData } => CreateNativeSpline(splineData),
        ProxyCadEntity { Wipeout: { } wipeoutData } => CreateNativeWipeout(wipeoutData),
        _ => null
    };

    /// <summary>Builds a real native ELLIPSE from our compact CadEllipseData - a true parametric curve, not a tessellated
    /// polyline, so AutoCAD can edit it with its own ellipse grips and the shape stays exact at any zoom level.</summary>
    private static ACadSharp.Entities.Ellipse CreateNativeEllipse(CadEllipseData data)
    {
        var axis = new XY(data.MajorAxisEndpoint.X - data.Center.X, data.MajorAxisEndpoint.Y - data.Center.Y);
        return new ACadSharp.Entities.Ellipse
        {
            Center = ToXyz(data.Center), MajorAxisEndPoint = new XYZ(axis.X, axis.Y, 0),
            RadiusRatio = Math.Clamp(data.RadiusRatio, 1e-6, 1),
            StartParameter = data.StartParameter, EndParameter = data.EndParameter
        };
    }

    /// <summary>Builds a real native SPLINE from our compact CadSplineData (control points/knots/weights already
    /// computed once via ACadSharp's own Spline.UpdateFromFitPoints when the command was run) - a true cubic NURBS
    /// curve, not a tessellated polyline, so AutoCAD can edit it with its own fit-point/control-point grips.</summary>
    private static ACadSharp.Entities.Spline CreateNativeSpline(CadSplineData data)
    {
        var spline = new ACadSharp.Entities.Spline { Degree = data.Degree, IsClosed = data.Closed };
        foreach (var p in data.FitPoints) spline.FitPoints.Add(ToXyz(p));
        foreach (var p in data.ControlPoints) spline.ControlPoints.Add(ToXyz(p));
        foreach (var k in data.Knots) spline.Knots.Add(k);
        foreach (var w in data.Weights) spline.Weights.Add(w);
        return spline;
    }

    /// <summary>Builds a real native WIPEOUT from our compact CadWipeoutData. WIPEOUT shares its InsertPoint/U-vector/
    /// V-vector/Size "local pixel space" convention with RasterImage even though there is no actual image here; we keep
    /// U/V at exactly 1 world unit per "pixel" (axis-aligned, unscaled) so ClipBoundaryVertices are simply the world
    /// boundary points shifted by the bounding-box corner - simplest convention that still round-trips exactly.</summary>
    private static ACadSharp.Entities.Wipeout CreateNativeWipeout(CadWipeoutData data)
    {
        double minX = data.Boundary.Min(p => p.X), minY = data.Boundary.Min(p => p.Y);
        double maxX = data.Boundary.Max(p => p.X), maxY = data.Boundary.Max(p => p.Y);
        var wipeout = new ACadSharp.Entities.Wipeout
        {
            InsertPoint = new XYZ(minX, minY, 0),
            UVector = XYZ.AxisX,
            VVector = XYZ.AxisY,
            Size = new XY(Math.Max(maxX - minX, 1e-6), Math.Max(maxY - minY, 1e-6)),
            ClippingState = true
        };
        foreach (var p in data.Boundary) wipeout.ClipBoundaryVertices.Add(new XY(p.X - minX, p.Y - minY));
        return wipeout;
    }

    /// <summary>One in-app LEADER/MLEADER object, stored as a native LEADER (classic pre-2008 leader entity) plus a
    /// separate MTEXT at the landing. Mirrors SyncDimension: rebuilds from scratch only when the parametric data changed,
    /// otherwise just re-applies layer/color so an unrelated edit elsewhere doesn't touch it.</summary>
    private static void SyncLeader(CadDocument document, DocumentContext context, CadDrawing drawing, ProxyCadEntity proxy, CadLeaderData data)
    {
        var space = context.SpaceRecords.GetValueOrDefault(drawing.GetSpaceName(proxy)) ?? document.ModelSpace;
        if (context.LeaderNatives.TryGetValue(proxy.Id, out var existing))
        {
            if (ReferenceEquals(existing.Data, data) || existing.Data.Equals(data))
            {
                foreach (var e in existing.Natives) { ApplyLayer(document, e, proxy.Layer); ApplyCommonProperties(document, e, proxy); }
                return;
            }
            foreach (var n in existing.Natives) foreach (var record in context.SpaceRecords.Values) record.Entities.Remove(n);
            context.LeaderNatives.Remove(proxy.Id);
        }
        var natives = CreateLeaderNatives(space, data);
        foreach (var e in natives) { ApplyLayer(document, e, proxy.Layer); ApplyCommonProperties(document, e, proxy); }
        context.LeaderNatives[proxy.Id] = (data, natives);
    }

    /// <summary>Builds a real native LEADER (classic pre-2008 leader entity) + its (unlinked) annotation MTEXT from our
    /// compact CadLeaderData. Not hard-linked as Leader.AssociatedAnnotation, which this library only lets its own
    /// DWG/DXF reader set (internal setter) - AutoCAD still displays arrow + line + text correctly, just as two objects.</summary>
    private static List<Entity> CreateLeaderNatives(BlockRecord space, CadLeaderData data)
    {
        if (data.Points.Count < 2) return new List<Entity>();
        // Same landing-line geometry as MainWindow.Leaders.BuildLeaderParts, so the saved LEADER's vertices and the
        // text position match exactly what the app draws (arrow point ... landing-start, landing-end, then text).
        var points = data.Points;
        var last2D = points[^1]; var prev2D = points[^2];
        var goingRight = last2D.X >= prev2D.X;
        var landingLen = Math.Max(data.ArrowSize * 2.2, data.TextHeight * 1.2);
        var landingEnd2D = new Point2(last2D.X + (goingRight ? landingLen : -landingLen), last2D.Y);
        var vertices = points.Select(p => new XYZ(p.X, p.Y, 0)).Append(new XYZ(landingEnd2D.X, landingEnd2D.Y, 0)).ToList();
        var horizontal = new XYZ(goingRight ? 1 : -1, 0, 0);
        var leader = new Leader
        {
            Vertices = vertices, ArrowHeadEnabled = true, PathType = LeaderPathType.StraightLineSegments,
            CreationType = LeaderCreationType.CreatedWithoutAnnotation, HorizontalDirection = horizontal,
            HookLineDirection = HookLineDirection.Same, TextHeight = data.TextHeight, TextWidth = data.Text.Length * data.TextHeight * 0.6
        };
        var gap = data.TextHeight * 0.5;
        var textPos = new Point2(landingEnd2D.X + (goingRight ? gap : -gap), landingEnd2D.Y);
        var mtext = CreateNativeMText(new CadMTextEntity(data.Text, data.Text, textPos, data.TextHeight, 0, 0, 1, goingRight ? 4 : 6, Guid.NewGuid()));
        space.Entities.Add(leader); space.Entities.Add(mtext);
        return new List<Entity> { leader, mtext };
    }

    /// <summary>One in-app MLEADER object, stored as a true native MULTILEADER entity (CadLeaderData.IsMultiLeader == true).
    /// Mirrors SyncLeader: rebuilds from scratch only when the parametric data changed, otherwise just re-applies
    /// layer/color so an unrelated edit elsewhere doesn't touch it.</summary>
    private static void SyncMultiLeader(CadDocument document, DocumentContext context, CadDrawing drawing, ProxyCadEntity proxy, CadLeaderData data)
    {
        var space = context.SpaceRecords.GetValueOrDefault(drawing.GetSpaceName(proxy)) ?? document.ModelSpace;
        if (context.MultiLeaderNatives.TryGetValue(proxy.Id, out var existing))
        {
            if (ReferenceEquals(existing.Data, data) || existing.Data.Equals(data))
            {
                ApplyLayer(document, existing.Native, proxy.Layer); ApplyCommonProperties(document, existing.Native, proxy);
                return;
            }
            foreach (var record in context.SpaceRecords.Values) record.Entities.Remove(existing.Native);
            context.MultiLeaderNatives.Remove(proxy.Id);
        }
        var native = CreateNativeMultiLeader(data);
        if (native is null) return;
        space.Entities.Add(native);
        ApplyLayer(document, native, proxy.Layer); ApplyCommonProperties(document, native, proxy);
        context.MultiLeaderNatives[proxy.Id] = (data, native);
    }

    /// <summary>Builds a real native MULTILEADER (post-2008 object) from our compact CadLeaderData, for the MLEADER
    /// command (QLEADER keeps using the classic LEADER entity via CreateLeaderNatives/SyncLeader above). Same
    /// simplified 2-point input as the classic leader (arrow point, landing point; the short horizontal landing/
    /// dogleg segment and text position are auto-derived from TextHeight/ArrowSize, same geometry as BuildLeaderParts
    /// so what's drawn on screen matches exactly what gets saved) - but this is a genuinely correct native MULTILEADER
    /// object, not the classic LEADER. Style/LeaderLineType/TextStyle are left at the library's own safe defaults
    /// (MultiLeaderStyle.Default / LineType.ByLayer / TextStyle.Default); ACadSharp resolves them against the real
    /// document automatically once the entity is added to a space (AssignDocument), same as every other native
    /// entity in this file - no manual table lookup/registration needed.</summary>
    private static MultiLeader? CreateNativeMultiLeader(CadLeaderData data)
    {
        if (data.Points.Count < 2) return null;
        var points = data.Points;
        var p0 = points[0];
        var p1 = points[^1];
        var prev = points.Count > 2 ? points[^2] : points[0];
        var goingRight = p1.X >= prev.X;
        var landingLen = Math.Max(data.ArrowSize * 2.2, data.TextHeight * 1.2);
        var landingEnd = new Point2(p1.X + (goingRight ? landingLen : -landingLen), p1.Y);
        var gap = data.TextHeight * 0.5;
        var textPos = new Point2(landingEnd.X + (goingRight ? gap : -gap), landingEnd.Y);
        var direction = new XYZ(goingRight ? 1 : -1, 0, 0);
        var textAttach = goingRight ? TextAttachmentPointType.Left : TextAttachmentPointType.Right;
        var textAlign = goingRight ? TextAlignmentType.Left : TextAlignmentType.Right;

        var line = new MultiLeaderObjectContextData.LeaderLine();
        line.Points.Add(new XYZ(p0.X, p0.Y, 0));
        line.Points.Add(new XYZ(p1.X, p1.Y, 0));
        line.Points.Add(new XYZ(landingEnd.X, landingEnd.Y, 0));

        var root = new MultiLeaderObjectContextData.LeaderRoot
        {
            ConnectionPoint = new XYZ(landingEnd.X, landingEnd.Y, 0),
            Direction = direction,
            LandingDistance = landingLen,
            ContentValid = true,
        };
        root.Lines.Add(line);

        var mleader = new MultiLeader
        {
            ContentType = LeaderContentType.MText,
            EnableLanding = true,
            EnableDogleg = false, // the dogleg segment (p1 -> landingEnd) is already an explicit point in `line` above
            LandingDistance = landingLen,
            ArrowheadSize = data.ArrowSize,
            TextAttachmentPoint = textAttach,
            TextAlignment = textAlign,
        };
        mleader.ContextData.LeaderRoots.Add(root);
        mleader.ContextData.HasTextContents = true;
        mleader.ContextData.TextLabel = data.Text;
        mleader.ContextData.TextHeight = data.TextHeight;
        mleader.ContextData.TextLocation = new XYZ(textPos.X, textPos.Y, 0);
        mleader.ContextData.ContentBasePoint = new XYZ(landingEnd.X, landingEnd.Y, 0);
        mleader.ContextData.ArrowheadSize = data.ArrowSize;
        mleader.ContextData.LandingGap = gap;
        mleader.ContextData.ScaleFactor = 1.0;
        mleader.ContextData.TextAttachmentPoint = textAttach;
        mleader.ContextData.TextAlignment = textAlign;
        return mleader;
    }

    /// <summary>One in-app TABLE object, stored as a real native TableEntity. Mirrors SyncLeader/SyncMultiLeader:
    /// rebuilds from scratch only when the parametric data changed (comparing by the SAME CadTableData reference,
    /// same convention as SyncLeader - a cell-text edit always produces a new CellTexts array so this correctly
    /// detects a change), otherwise just re-applies layer/color.</summary>
    private static void SyncTable(CadDocument document, DocumentContext context, CadDrawing drawing, ProxyCadEntity proxy, CadTableData data)
    {
        var space = context.SpaceRecords.GetValueOrDefault(drawing.GetSpaceName(proxy)) ?? document.ModelSpace;
        if (context.TableNatives.TryGetValue(proxy.Id, out var existing))
        {
            if (ReferenceEquals(existing.Data, data) || existing.Data.Equals(data))
            {
                ApplyLayer(document, existing.Native, proxy.Layer); ApplyCommonProperties(document, existing.Native, proxy);
                return;
            }
            foreach (var record in context.SpaceRecords.Values) record.Entities.Remove(existing.Native);
            if (document.BlockRecords.Contains(existing.Block.Name)) document.BlockRecords.Remove(existing.Block.Name);
            context.TableNatives.Remove(proxy.Id);
        }
        var (native, block) = CreateNativeTable(data);
        if (native is null || block is null) return;
        document.BlockRecords.Add(block);
        space.Entities.Add(native);
        ApplyLayer(document, native, proxy.Layer); ApplyCommonProperties(document, native, proxy);
        context.TableNatives[proxy.Id] = (data, native, block);
    }

    /// <summary>Builds a real native TABLE (post-2008 AcDbTable/TABLECONTENT object) from our compact CadTableData, for
    /// the simplified TABLE command: a uniform Columns x Rows grid of plain-text cells. TableEntity : Insert, so every
    /// TableEntity must own a BlockRecord (its "cache" block, [confirmed by reading TableEntity.cs's constructor] -
    /// left EMPTY here, same low-risk choice already made for WIPEOUT's null Definition: the writer tolerates it and
    /// the real content lives in Content/Rows/Columns, not the cache block). TableStyle/TextStyle are left at the
    /// library's own safe defaults (TableStyle.Default / TextStyle.Default via field initializers), resolved against
    /// the real document automatically once added (same AssignDocument mechanism as every other native entity here).
    /// NOTE: confirmed by reading both writers - the DWG writer fully supports TableEntity, the DXF writer does NOT
    /// (isEntitySupported explicitly rejects TableEntity) - a table only survives a .dwg save, not .dxf.</summary>
    private static (TableEntity? Native, BlockRecord? Block) CreateNativeTable(CadTableData data)
    {
        if (data.Columns < 1 || data.Rows < 1) return (null, null);
        var block = new BlockRecord($"*T{Guid.NewGuid():N}"[..10]);
        var table = new TableEntity(block)
        {
            InsertPoint = new XYZ(data.Position.X, data.Position.Y, 0),
            HorizontalDirection = XYZ.AxisX,
        };
        for (var c = 0; c < data.Columns; c++) table.Columns.Add(new TableEntity.Column { Width = data.ColumnWidth });
        // Row 0 is the TOP row on screen (BuildTableCellTexts in MainWindow.Tables.cs uses the same top-to-bottom order),
        // but InsertPoint/Position is the BOTTOM-left world corner - AutoCAD tables are read/written top row first, so
        // the row order here must match the on-screen order, not the bottom-up world Y order.
        for (var r = 0; r < data.Rows; r++)
        {
            var row = new TableEntity.Row { Height = data.RowHeight };
            for (var c = 0; c < data.Columns; c++)
            {
                var index = r * data.Columns + c;
                var text = index < data.CellTexts.Count ? data.CellTexts[index] ?? string.Empty : string.Empty;
                var cell = new TableEntity.Cell { Type = TableEntity.CellType.Text };
                var content = new TableEntity.CellContent { ContentType = TableEntity.TableCellContentType.Value };
                content.CadValue.SetValue(text, CadValueType.String);
                content.CadValue.FormattedValue = text;
                content.Format.HasData = true;
                content.Format.TextHeight = data.TextHeight;
                cell.Contents.Add(content);
                row.Cells.Add(cell);
            }
            table.Rows.Add(row);
        }
        return (table, block);
    }

    private static void SyncViewport(CadDocument document, DocumentContext context, CadDrawing drawing, ProxyCadEntity proxy, CadViewportData data)
    {
        var spaceName = drawing.GetSpaceName(proxy);
        var space = context.SpaceRecords.GetValueOrDefault(spaceName) ?? document.ModelSpace;
        if (context.ViewportNatives.TryGetValue(proxy.Id, out var existing))
        {
            if (ReferenceEquals(existing.Data, data) || existing.Data.Equals(data))
            {
                ApplyLayer(document, existing.Native, proxy.Layer); ApplyCommonProperties(document, existing.Native, proxy);
                return;
            }
            foreach (var record in context.SpaceRecords.Values) record.Entities.Remove(existing.Native);
            context.ViewportNatives.Remove(proxy.Id);
        }
        // AutoCAD/ACadSharp reserve Viewport.Id 1 for an automatic viewport that just "represents the paper" (its Id
        // is computed from position among the Viewport entities already in this space's BlockRecord, not settable) -
        // without one already present, the FIRST real viewport we add here would itself become Id 1 and then be
        // silently treated as that paper placeholder (by AutoCAD itself and by this app's own import filter, both of
        // which require Id > 1 for a real content viewport) instead of the user's actual window onto the model.
        if (!space.Entities.OfType<Viewport>().Any())
        {
            var (paperW, paperH) = drawing.LayoutPapers.TryGetValue(spaceName, out var size) ? size : (420.0, 297.0);
            space.Entities.Add(new Viewport
            {
                Center = new XYZ(paperW / 2, paperH / 2, 0), Width = paperW, Height = paperH,
                ViewCenter = new XY(paperW / 2, paperH / 2), ViewHeight = paperH, ActiveStatus = 0
            });
        }
        var native = CreateNativeViewport(data);
        if (native is null) return;
        space.Entities.Add(native);
        ApplyLayer(document, native, proxy.Layer); ApplyCommonProperties(document, native, proxy);
        context.ViewportNatives[proxy.Id] = (data, native);
    }

    /// <summary>Builds a real native VIEWPORT from our compact CadViewportData, for the simplified VIEWPORT/MVIEW
    /// command: one paper-space rectangle (Center/Width/Height) plus the model-space window it looks through
    /// (ViewCenter/ViewHeight). Unlike TABLE/MULTILEADER, Viewport has no special constructor requirements and is
    /// [confirmed by reading both writers] fully supported by BOTH the DWG and the DXF writer, so there is no
    /// format-specific drop risk here. ActiveStatus = 1 ("on and active") matches the entity's own default and is
    /// what this app's own import filter (and the pre-existing PLOT-dialog extraction loop) already requires to
    /// treat a viewport as real content rather than bounds-only.</summary>
    private static Viewport? CreateNativeViewport(CadViewportData data)
    {
        if (data.Width <= 1e-9 || data.Height <= 1e-9) return null;
        return new Viewport
        {
            Center = new XYZ(data.Center.X, data.Center.Y, 0),
            Width = data.Width,
            Height = data.Height,
            ViewCenter = new XY(data.ViewCenter.X, data.ViewCenter.Y),
            ViewHeight = data.ViewHeight > 1e-9 ? data.ViewHeight : data.Height,
            ActiveStatus = 1
        };
    }

    /// <summary>Builds a real native HATCH (one pattern-line family + one polyline boundary per loop) from our compact CadHatchData,
    /// so a hatch created in this app survives save/reopen instead of being silently dropped. The pattern lines themselves are
    /// regenerated by AutoCAD/this app's own reader from Pattern+PatternAngle+PatternScale, matching what ProxyGraphicsPaths already shows.</summary>
    private static Entity? CreateNativeHatch(CadHatchData data)
    {
        if (data.Boundaries.Count == 0) return null;
        var spacing = data.Spacing > 1e-9 ? data.Spacing : 1.0;
        var pattern = new HatchPattern("_USER")
        {
            Lines = { new HatchPattern.Line { Angle = 0, BasePoint = XY.Zero, Offset = new XY(0, spacing) } }
        };
        var hatch = new Hatch { Pattern = pattern, PatternType = HatchPatternType.PatternFill, Style = HatchStyleType.Normal, IsAssociative = false };
        // Must run AFTER Pattern is assigned: both setters call Pattern.Update(...), which rotates/scales the pattern lines in place.
        hatch.PatternAngle = data.AngleDegrees * Math.PI / 180.0;
        hatch.PatternScale = 1.0;
        foreach (var ring in data.Boundaries)
        {
            if (ring.Count < 3) continue;
            var vertices = ring.Select(p => new XYZ(p.X, p.Y, 0)).ToList();
            var polyline = new Hatch.BoundaryPath.Polyline(vertices, isClosed: true);
            hatch.Paths.Add(new Hatch.BoundaryPath(new Hatch.BoundaryPath.Edge[] { polyline }) { Flags = BoundaryPathFlags.External | BoundaryPathFlags.Polyline });
        }
        return hatch.Paths.Count == 0 ? null : hatch;
    }

    /// <summary>Makes sure every block named by an INSERT among <paramref name="entities"/> that the app itself defined
    /// (via the BLOCK command, tracked in <see cref="CadDrawing.BlockDefinitions"/>) exists as a real ACadSharp BlockRecord
    /// before the main save loop runs - CreateInsertNative looks the block up by name and does nothing if it is missing, so
    /// a block defined in-session but never yet saved would otherwise silently vanish on save. Entities already read from
    /// a native BlockRecord (not a BlockDefinitions entry) are left untouched; AttributeDef members become AttributeDefinition
    /// members of the block, matching how AutoCAD itself stores an attributed block.</summary>
    private static void EnsureBlockRecordsExist(CadDocument document, CadDrawing drawing, IReadOnlyList<CadEntity> entities)
    {
        var names = entities.OfType<ProxyCadEntity>().Where(e => e.InsertData is not null).Select(e => e.InsertData!.BlockName).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (document.BlockRecords.Contains(name)) continue;
            if (!drawing.BlockDefinitions.TryGetValue(name, out var def)) continue;
            var record = new BlockRecord(name) { BlockEntity = { BasePoint = ToXyz(def.BasePoint) } };
            foreach (var member in def.Entities)
            {
                if (member is ProxyCadEntity { AttributeDef: { } attDef } attDefProxy)
                {
                    var position = attDefProxy.PreviewTextPosition ?? def.BasePoint;
                    record.Entities.Add(new AttributeDefinition
                    {
                        Tag = attDef.Tag, Prompt = attDef.Prompt ?? string.Empty, Value = attDef.DefaultValue,
                        InsertPoint = ToXyz(position), AlignmentPoint = ToXyz(position),
                        Height = attDef.Height > 0 ? attDef.Height : 2.5, Rotation = attDef.RotationDegrees * Math.PI / 180,
                        WidthFactor = attDef.WidthFactor > 0 ? attDef.WidthFactor : 1,
                        HorizontalAlignment = (TextHorizontalAlignment)Math.Clamp(attDef.HAlign, 0, 5),
                        VerticalAlignment = (TextVerticalAlignmentType)Math.Clamp(attDef.VAlign, 0, 3),
                        Flags = attDef.Invisible ? AttributeFlags.Hidden : AttributeFlags.None
                    });
                    continue;
                }
                if (CreateNative(member) is { } nativeMember) record.Entities.Add(nativeMember);
            }
            if (record.Entities.Count == 0) continue;
            document.BlockRecords.Add(record);
        }
    }

    private static Entity? CreateInsertNative(CadDocument document, CadInsertData data)
    {
        if (!document.BlockRecords.TryGetValue(data.BlockName, out var block)) return null;
        return new Insert(block)
        {
            InsertPoint = new XYZ(data.Position.X, data.Position.Y, 0), Rotation = data.Rotation, XScale = data.ScaleX, YScale = data.ScaleY,
            ZScale = Math.Abs(data.ScaleX) > 1e-12 ? data.ScaleX : 1
        };
    }

    private static readonly string[] AciNames = { "ByBlock", "Red", "Yellow", "Green", "Cyan", "Blue", "Magenta", "White" };

    private static CadInsertAttribute ReadInsertAttribute(AttributeBase a, BlockRecord? block)
    {
        string? prompt = null;
        try { prompt = block?.Entities.OfType<AttributeDefinition>().FirstOrDefault(d => string.Equals(d.Tag, a.Tag, StringComparison.OrdinalIgnoreCase))?.Prompt; } catch { }
        string color;
        try
        {
            color = a.Color.IsByLayer ? "ByLayer" : a.Color.IsByBlock ? "ByBlock"
                : a.Color.IsTrueColor ? $"#{a.Color.R:X2}{a.Color.G:X2}{a.Color.B:X2}" : a.Color.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch { color = "ByLayer"; }
        string weight;
        try
        {
            weight = a.LineWeight switch
            {
                LineWeightType.ByLayer => "ByLayer", LineWeightType.ByBlock => "ByBlock", LineWeightType.Default => "Default",
                var w when (short)w >= 0 => ((short)w / 100.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                _ => "ByLayer"
            };
        }
        catch { weight = "ByLayer"; }
        string? lineType = null;
        try { lineType = a.LineType?.Name; } catch { }
        string? layerName = null;
        try { layerName = a.Layer?.Name; } catch { }
        return new CadInsertAttribute(a.Tag ?? string.Empty, a.Value ?? string.Empty, prompt, a.Style?.Name,
            (int)a.HorizontalAlignment, (int)a.VerticalAlignment, a.Mirror.HasFlag(TextMirrorFlag.Backward), a.Mirror.HasFlag(TextMirrorFlag.UpsideDown),
            a.Height, a.WidthFactor, a.Rotation * 180.0 / Math.PI, a.ObliqueAngle * 180.0 / Math.PI,
            layerName, string.IsNullOrEmpty(lineType) ? "ByLayer" : lineType, color, weight, Detailed: true);
    }

    /// <summary>Writes the Enhanced-Attribute-Editor properties of one ATTRIB; only values that differ are touched.</summary>
    private static void ApplyAttributeProperties(AttributeBase a, CadInsertAttribute m, CadDocument? document)
    {
        const double eps = 1e-9;
        try
        {
            if (document is not null)
            {
                if (!string.IsNullOrWhiteSpace(m.TextStyle) && !string.Equals(a.Style?.Name, m.TextStyle, StringComparison.OrdinalIgnoreCase) && document.TextStyles.TryGetValue(m.TextStyle, out var style)) a.Style = style;
                if (!string.IsNullOrWhiteSpace(m.Layer) && !string.Equals(a.Layer?.Name, m.Layer, StringComparison.OrdinalIgnoreCase) && document.Layers.TryGetValue(m.Layer, out var layer)) a.Layer = layer;
                if (!string.IsNullOrWhiteSpace(m.LineType) && !string.Equals(a.LineType?.Name, m.LineType, StringComparison.OrdinalIgnoreCase) && document.LineTypes.TryGetValue(m.LineType, out var lineType)) a.LineType = lineType;
            }
            var horizontal = (TextHorizontalAlignment)Math.Clamp(m.HAlign, 0, 5);
            var vertical = (TextVerticalAlignmentType)Math.Clamp(m.VAlign, 0, 3);
            if (horizontal != a.HorizontalAlignment || vertical != a.VerticalAlignment)
            {
                // Keep the text anchored where it was shown: the anchor is the insertion point for Left/Baseline, otherwise the alignment point.
                var wasAligned = a.HorizontalAlignment != TextHorizontalAlignment.Left || a.VerticalAlignment != TextVerticalAlignmentType.Baseline;
                var anchor = wasAligned && (a.AlignmentPoint.X != 0 || a.AlignmentPoint.Y != 0) ? a.AlignmentPoint : a.InsertPoint;
                a.HorizontalAlignment = horizontal; a.VerticalAlignment = vertical;
                a.AlignmentPoint = anchor;
                if (horizontal == TextHorizontalAlignment.Left && vertical == TextVerticalAlignmentType.Baseline) a.InsertPoint = anchor;
            }
            var mirror = (m.Backwards ? TextMirrorFlag.Backward : TextMirrorFlag.None) | (m.UpsideDown ? TextMirrorFlag.UpsideDown : TextMirrorFlag.None);
            if (a.Mirror != mirror) a.Mirror = mirror;
            if (m.Height > 0 && Math.Abs(a.Height - m.Height) > eps) a.Height = m.Height;
            if (m.WidthFactor > 0 && Math.Abs(a.WidthFactor - m.WidthFactor) > eps) a.WidthFactor = m.WidthFactor;
            var rotation = m.RotationDegrees * Math.PI / 180.0;
            if (Math.Abs(a.Rotation - rotation) > 1e-7) a.Rotation = rotation;
            var oblique = m.ObliqueDegrees * Math.PI / 180.0;
            if (Math.Abs(a.ObliqueAngle - oblique) > 1e-7) a.ObliqueAngle = oblique;
            if (!string.IsNullOrWhiteSpace(m.Color))
            {
                var text = m.Color.Trim();
                ACadSharp.Color? parsed = null;
                if (text.Equals("ByLayer", StringComparison.OrdinalIgnoreCase)) parsed = ACadSharp.Color.ByLayer;
                else if (text.Equals("ByBlock", StringComparison.OrdinalIgnoreCase)) parsed = ACadSharp.Color.ByBlock;
                else if (text.StartsWith('#') && text.Length == 7 && int.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var rgb))
                    parsed = new ACadSharp.Color((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
                else if (short.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var index) && index is >= 1 and <= 255) parsed = new ACadSharp.Color(index);
                else
                {
                    var named = Array.FindIndex(AciNames, n => n.Equals(text, StringComparison.OrdinalIgnoreCase));
                    if (named > 0) parsed = new ACadSharp.Color((short)named);
                }
                if (parsed is { } value && !value.Equals(a.Color)) a.Color = value;
            }
            if (!string.IsNullOrWhiteSpace(m.LineWeight))
            {
                var text = m.LineWeight.Trim();
                LineWeightType? weight = text.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) ? LineWeightType.ByLayer
                    : text.Equals("ByBlock", StringComparison.OrdinalIgnoreCase) ? LineWeightType.ByBlock
                    : text.Equals("Default", StringComparison.OrdinalIgnoreCase) ? LineWeightType.Default
                    : double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mm) && Enum.IsDefined(typeof(LineWeightType), (short)Math.Round(mm * 100)) ? (LineWeightType)(short)Math.Round(mm * 100) : null;
                if (weight is { } w && a.LineWeight != w) a.LineWeight = w;
            }
        }
        catch
        {
            // One unsupported property must not block saving the drawing.
        }
    }

    /// <summary>Moves/rotates/scales a block reference and carries its attributes along.</summary>
    private static void UpdateInsert(Insert insert, CadInsertData model)
    {
        const double eps = 1e-9;
        if (model.Attributes is { Count: > 0 } edited && insert.Attributes.Count > 0)
        {
            var native = insert.Attributes.ToList();
            for (var i = 0; i < native.Count; i++)
            {
                var attribute = native[i];
                var match = edited.FirstOrDefault(a => string.Equals(a.Tag, attribute.Tag, StringComparison.OrdinalIgnoreCase)) ?? (i < edited.Count ? edited[i] : null);
                if (match is null) continue;
                if (match.Value != (attribute.Value ?? string.Empty))
                    try { attribute.Value = match.Value; if (attribute.MText is not null) attribute.MText.Value = match.Value; } catch { }
                if (match.Detailed) ApplyAttributeProperties(attribute, match, insert.Document);
            }
        }
        if (Math.Abs(insert.InsertPoint.X - model.Position.X) < eps && Math.Abs(insert.InsertPoint.Y - model.Position.Y) < eps &&
            Math.Abs(insert.Rotation - model.Rotation) < eps && Math.Abs(insert.XScale - model.ScaleX) < eps && Math.Abs(insert.YScale - model.ScaleY) < eps) return;
        double x0 = insert.InsertPoint.X, y0 = insert.InsertPoint.Y, r0 = insert.Rotation, sx0 = insert.XScale, sy0 = insert.YScale;
        if (insert.Attributes.Count > 0 && Math.Abs(sx0) > 1e-12 && Math.Abs(sy0) > 1e-12)
        {
            double c0 = Math.Cos(-r0), s0 = Math.Sin(-r0), c1 = Math.Cos(model.Rotation), s1 = Math.Sin(model.Rotation);
            XYZ Map(XYZ p)
            {
                double dx = p.X - x0, dy = p.Y - y0;
                double lx = (dx * c0 - dy * s0) / sx0 * model.ScaleX, ly = (dx * s0 + dy * c0) / sy0 * model.ScaleY;
                return new XYZ(model.Position.X + lx * c1 - ly * s1, model.Position.Y + lx * s1 + ly * c1, p.Z);
            }
            foreach (var attribute in insert.Attributes)
            {
                try
                {
                    attribute.InsertPoint = Map(attribute.InsertPoint);
                    attribute.AlignmentPoint = Map(attribute.AlignmentPoint);
                    attribute.Rotation += model.Rotation - r0;
                    attribute.Height *= Math.Abs(model.ScaleY / sy0);
                }
                catch { }
            }
        }
        insert.InsertPoint = new XYZ(model.Position.X, model.Position.Y, insert.InsertPoint.Z);
        insert.Rotation = model.Rotation; insert.XScale = model.ScaleX; insert.YScale = model.ScaleY;
    }

    private static void UpdateNative(Entity native, CadEntity entity)
    {
        switch (native, entity)
        {
            case (Insert insert, ProxyCadEntity { InsertData: { } insertModel }):
                UpdateInsert(insert, insertModel); break;
            case (NativeLine line, LineEntity model):
                line.StartPoint = ToXyz(model.Start); line.EndPoint = ToXyz(model.End); break;
            case (NativeCircle circle, CircleEntity model):
                circle.Center = ToXyz(model.Center); circle.Radius = model.Radius; break;
            case (NativeArc arc, ArcEntity model):
                arc.Center = ToXyz(model.Center); arc.Radius = model.Radius;
                arc.StartAngle = model.StartAngleDegrees * Math.PI / 180.0;
                arc.EndAngle = model.EndAngleDegrees * Math.PI / 180.0; break;
            case (ACadSharp.Entities.Ellipse ellipse, ProxyCadEntity { Ellipse: { } ellipseModel }):
                ellipse.Center = ToXyz(ellipseModel.Center);
                ellipse.MajorAxisEndPoint = new XYZ(ellipseModel.MajorAxisEndpoint.X - ellipseModel.Center.X, ellipseModel.MajorAxisEndpoint.Y - ellipseModel.Center.Y, 0);
                ellipse.RadiusRatio = Math.Clamp(ellipseModel.RadiusRatio, 1e-6, 1);
                ellipse.StartParameter = ellipseModel.StartParameter; ellipse.EndParameter = ellipseModel.EndParameter;
                break;
            case (ACadSharp.Entities.Spline spline, ProxyCadEntity { Spline: { } splineModel }):
                spline.Degree = splineModel.Degree; spline.IsClosed = splineModel.Closed;
                spline.FitPoints.Clear(); foreach (var p in splineModel.FitPoints) spline.FitPoints.Add(ToXyz(p));
                spline.ControlPoints.Clear(); foreach (var p in splineModel.ControlPoints) spline.ControlPoints.Add(ToXyz(p));
                spline.Knots.Clear(); foreach (var k in splineModel.Knots) spline.Knots.Add(k);
                spline.Weights.Clear(); foreach (var w in splineModel.Weights) spline.Weights.Add(w);
                break;
            case (ACadSharp.Entities.Wipeout wipeout, ProxyCadEntity { Wipeout: { } wipeoutModel }):
            {
                var rebuilt = CreateNativeWipeout(wipeoutModel);
                wipeout.InsertPoint = rebuilt.InsertPoint; wipeout.UVector = rebuilt.UVector; wipeout.VVector = rebuilt.VVector;
                wipeout.Size = rebuilt.Size; wipeout.ClippingState = rebuilt.ClippingState;
                wipeout.ClipBoundaryVertices.Clear();
                foreach (var v in rebuilt.ClipBoundaryVertices) wipeout.ClipBoundaryVertices.Add(v);
                break;
            }
            case (NativePolyline polyline, PolylineEntity model):
                polyline.Vertices.Clear();
                foreach (var vertex in model.Vertices)
                    polyline.Vertices.Add(new NativePolyline.Vertex(new XY(vertex.Point.X, vertex.Point.Y)) { Bulge = vertex.Bulge });
                polyline.IsClosed = model.Closed;
                break;
            case (NativeText text, CadTextEntity model):
                text.Value = model.Value; text.InsertPoint = ToXyz(model.Position); text.Height = model.Height;
                text.Rotation = model.RotationRadians; text.WidthFactor = model.WidthFactor;
                text.HorizontalAlignment = (TextHorizontalAlignment)Math.Clamp(model.HorizontalAlignment, 0, 5);
                text.VerticalAlignment = (TextVerticalAlignmentType)Math.Clamp(model.VerticalAlignment, 0, 3);
                text.Color = ToNativeColor(model.ExplicitColor);
                break;
            case (NativeMText text, CadMTextEntity model):
                text.Value = model.FormattedValue; text.InsertPoint = ToXyz(model.Position); text.Height = model.Height;
                text.AlignmentPoint = new XYZ(Math.Cos(model.RotationRadians), Math.Sin(model.RotationRadians), text.AlignmentPoint.Z);
                text.RectangleWidth = model.Width; text.LineSpacing = model.LineSpacing;
                text.AttachmentPoint = (AttachmentPointType)Math.Clamp(model.AttachmentPoint, 1, 9);
                text.Color = ToNativeColor(model.ExplicitColor);
                break;
            case (Hatch hatch, ProxyCadEntity { Hatch: { } hatchModel }):
                UpdateNativeHatch(hatch, hatchModel); break;
        }
    }

    /// <summary>Rebuilds an already-saved native HATCH's pattern + boundary to match the (possibly moved/rotated/scaled) model data.</summary>
    private static void UpdateNativeHatch(Hatch hatch, CadHatchData data)
    {
        var spacing = data.Spacing > 1e-9 ? data.Spacing : 1.0;
        hatch.Pattern = new HatchPattern("_USER") { Lines = { new HatchPattern.Line { Angle = 0, BasePoint = XY.Zero, Offset = new XY(0, spacing) } } };
        hatch.PatternAngle = data.AngleDegrees * Math.PI / 180.0;
        hatch.PatternScale = 1.0;
        hatch.Paths.Clear();
        foreach (var ring in data.Boundaries)
        {
            if (ring.Count < 3) continue;
            var vertices = ring.Select(p => new XYZ(p.X, p.Y, 0)).ToList();
            var polyline = new Hatch.BoundaryPath.Polyline(vertices, isClosed: true);
            hatch.Paths.Add(new Hatch.BoundaryPath(new Hatch.BoundaryPath.Edge[] { polyline }) { Flags = BoundaryPathFlags.External | BoundaryPathFlags.Polyline });
        }
    }

    private static NativePolyline CreatePolyline(PolylineEntity model)
    {
        var polyline = new NativePolyline { IsClosed = model.Closed };
        foreach (var vertex in model.Vertices)
            polyline.Vertices.Add(new NativePolyline.Vertex(new XY(vertex.Point.X, vertex.Point.Y)) { Bulge = vertex.Bulge });
        return polyline;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LineType, double[]> LineTypePatternCache = new();

    /// <summary>Signed alternating dash(+)/gap(-) list. Shape/text elements (zero advance) are dropped and same-sign runs merged.</summary>
    internal static double[] NormalizeLineTypePattern(LineType lineType)
    {
        var merged = new List<double>(8);
        foreach (var segment in lineType.Segments)
        {
            var length = segment.Length;
            if (!double.IsFinite(length)) continue;
            if (length == 0 && (segment.IsShape || segment.Flags != 0)) continue;
            if (merged.Count > 0 && length != 0 && Math.Sign(merged[^1]) == Math.Sign(length)) merged[^1] += length;
            else if (length == 0 && merged.Count > 0 && merged[^1] > 0) continue;
            else merged.Add(length);
        }
        if (merged.Count == 0) return Array.Empty<double>();
        return merged.ToArray();
    }

    private static CadEntity MapEntity(Entity entity, Guid id, out bool supported, out bool unresolvedDynamicInsertPreview,
        Dictionary<(BlockRecord Block, string LayerName), bool> nativeBlockPreviewCache,
        Dictionary<(BlockRecord Block, string LayerName), bool> unresolvedVisibilityCache,
        PreviewWorkBudget previewWorkBudget, PreviewWorkBudget topLevelPreviewBudget, CancellationToken cancellationToken)
    {
        unresolvedDynamicInsertPreview = false;
        var layer = entity.Layer?.Name ?? "0";
        var activeColor = entity.GetActiveColor();
        var displayColor = new CadColor(activeColor.R, activeColor.G, activeColor.B);
        CadColor? explicitColor = entity.Color.IsByLayer || entity.Color.IsByBlock ? null : new CadColor(entity.Color.R, entity.Color.G, entity.Color.B);
        var activeLineType = entity.GetActiveLineType();
        // One shared pattern array per native linetype (not one per entity): far less memory on import and
        // lets the renderer key pens/dash styles by reference instead of rebuilding strings per entity.
        var lineTypePattern = activeLineType is null ? null
            : LineTypePatternCache.GetValue(activeLineType, static lineType => NormalizeLineTypePattern(lineType));
        var globalLineTypeScale = entity.Document?.Header?.LineTypeScale ?? 1.0;
        var nativeTextStyle = entity switch { TextEntity text => text.Style, MText text => text.Style, _ => null };
        // Only TEXT/MTEXT have a style; the other ~95% of entities skip all font/path string work.
        var fontFamily = "Arial"; var fontBold = false; var fontItalic = false; var obliqueAngle = 0.0;
        if (nativeTextStyle is not null)
        {
            var fontName = nativeTextStyle.Filename ?? string.Empty;
            fontFamily = ResolveFontFamily(fontName);
            fontBold = nativeTextStyle.TrueType.HasFlag(FontFlags.Bold) || IsBoldFontFile(fontName);
            fontItalic = nativeTextStyle.TrueType.HasFlag(FontFlags.Italic) || IsItalicFontFile(fontName);
            obliqueAngle = nativeTextStyle.ObliqueAngle;
        }
        CadEntity Colorize(CadEntity mapped) => mapped with
        {
            NativeHandle = entity.Handle == 0 ? null : entity.Handle.ToString("X"),
            DisplayColor = displayColor,
            ExplicitColor = explicitColor,
            LineTypePattern = lineTypePattern,
            ColorByBlock = entity.Color.IsByBlock,
            LineTypeName = entity.LineType is null || string.Equals(entity.LineType.Name, "ByLayer", StringComparison.OrdinalIgnoreCase) ? null : entity.LineType.Name,
            LineWeightMm = (short)entity.LineWeight >= 0 ? (short)entity.LineWeight / 100.0 : null,
            LineTypeScale = Math.Max(1e-6, entity.LineTypeScale * Math.Max(1e-6, globalLineTypeScale)),
            TextFontFamily = fontFamily,
            TextBold = fontBold,
            TextItalic = fontItalic,
            TextObliqueAngleRadians = obliqueAngle,
            TextWidthFactor = nativeTextStyle is null ? 1 : Math.Clamp(nativeTextStyle.Width, 0.05, 10)
        };
        switch (entity)
        {
            case NativeLine line: supported = true; return Colorize(new LineEntity(ToPoint(line.StartPoint), ToPoint(line.EndPoint), id, layer));
            case NativeArc arc: supported = true; return Colorize(new ArcEntity(ToPoint(arc.Center), arc.Radius, arc.StartAngle * 180.0 / Math.PI, arc.EndAngle * 180.0 / Math.PI, id, layer));
            case NativeCircle circle: supported = true; return Colorize(new CircleEntity(ToPoint(circle.Center), circle.Radius, id, layer));
            case NativePolyline polyline:
                supported = true;
                return Colorize(new PolylineEntity(MapPolylineVertices(polyline, cancellationToken), polyline.IsClosed, id, layer));
            // Old-style "heavyweight" 2D POLYLINE (ACadSharp.Entities.Polyline2D - a different native entity type than
            // the modern LWPOLYLINE above, but very common in files from older AutoCAD versions or other CAD programs)
            // was previously unhandled here and fell through to the generic unsupported/bounds-only default branch
            // below, which is why a polyline with arc segments (bulge) drawn that way read in as a read-only outline
            // with no editable arcs. Mapped into the exact same PolylineEntity/Bulge model as LWPOLYLINE, so it is
            // fully editable (MOVE/ROTATE/SCALE/EXPLODE, arc segments included) immediately after opening the file.
            // On save it is written back out as a modern LWPOLYLINE (via the existing PolylineEntity -> NativePolyline
            // path) rather than the legacy entity type - same visual shape/arcs, just the newer of two equally-native
            // AutoCAD representations, like TEXT/ATTDEF simplifications already made elsewhere in this app.
            case ACadSharp.Entities.Polyline2D legacyPolyline:
                supported = true;
                return Colorize(new PolylineEntity(MapPolyline2DVertices(legacyPolyline, cancellationToken), legacyPolyline.IsClosed, id, layer));
            case NativeText text: supported = true; return Colorize(new CadTextEntity(text.Value, ToPoint(text.InsertPoint), text.Height, text.Rotation,
                text.WidthFactor, (int)text.HorizontalAlignment, (int)text.VerticalAlignment, id, layer));
            case NativeMText text:
                supported = true;
                var plainText = string.Join('\n', text.GetPlainTextLines());
                return Colorize(new CadMTextEntity(plainText, text.Value, ToPoint(text.InsertPoint), text.Height, text.Rotation,
                    text.RectangleWidth, text.LineSpacing, (int)text.AttachmentPoint, id, layer));
            default:
                supported = false;
                var nativeType = string.IsNullOrWhiteSpace(entity.ObjectName) ? entity.GetType().Name : entity.ObjectName;
                var previewVisible = IsNativeEntityVisible(entity);
                // Hidden/off/frozen INSERTs can contain thousands of nested primitives.
                // Do not build their bounds or tessellated preview at all; they will not
                // enter the visible spatial index and AutoCAD does not display them.
                var min = new Point2(0, 0);
                var max = new Point2(0, 0);
                var stageT0 = Stopwatch.GetTimestamp();
                var hasBounds = previewVisible && TryGetBounds(entity, out min, out max);
                if (previewVisible && !hasBounds && TryGetProxyExtents(entity, out min, out max, cancellationToken)) hasBounds = true;
                // INSERT preview walks immutable block definitions directly. It does not call
                // Insert.Explode(), which clones every child and becomes quadratic/alloc-heavy
                // for large or deeply nested blocks.
                if (entity is Insert && t_insertStageTicks is { } stageA) stageA[0] += Stopwatch.GetTimestamp() - stageT0;
                var previewBudgetAvailable = !previewWorkBudget.IsExhausted;
                // Dimensions and top-level proxy records are independent top-level entities;
                // they must not disappear merely because an earlier INSERT used the recursive
                // block-expansion budget. Each gets a bounded local preview budget.
                var entityPreviewBudget = entity is Dimension ? topLevelPreviewBudget : previewWorkBudget;
                // Dynamic block source definitions can contain entities from every visibility
                // state at once. Until the selected state is resolved, never walk that source
                // definition as if all of its entities were simultaneously visible. An
                // anonymous *U representation linked to a source block is already the
                // evaluated per-INSERT representation and remains safe to traverse.
                var visibilityScanBudget = 0;
                var hasUnresolvedDynamicVisibility = entity is Insert visibilityCandidate && previewVisible && previewBudgetAvailable &&
                    HasUnresolvedDynamicVisibilityTree(visibilityCandidate.Block,
                        new HashSet<BlockRecord>(ReferenceEqualityComparer.Instance), unresolvedVisibilityCache,
                        0, ref visibilityScanBudget, GetEffectiveLayer(visibilityCandidate, null), cancellationToken);
                unresolvedDynamicInsertPreview = hasUnresolvedDynamicVisibility;
                var stageT1 = Stopwatch.GetTimestamp();
                // Classify the definition before extracting any coordinates. In v1.0.49.18
                // extraction happened first, even for unsupported/custom blocks whose preview
                // was discarded a few lines later. That created large temporary point arrays.
                var nativeInsertPreviewAllowed = entity is Insert insertCandidate && previewVisible && previewBudgetAvailable &&
                    !hasUnresolvedDynamicVisibility &&
                    HasOnlyNativePreviewContent(insertCandidate, nativeBlockPreviewCache, cancellationToken);
                var stageT2 = Stopwatch.GetTimestamp();
                var insertContent = nativeInsertPreviewAllowed && entity is Insert rootInsert
                    ? ExtractInsertPreviewContent(rootInsert, previewWorkBudget, cancellationToken)
                    : null;
                var stageT3 = Stopwatch.GetTimestamp();
                if (entity is Insert && t_insertStageTicks is { } stageB) { stageB[1] += stageT1 - stageT0; stageB[2] += stageT2 - stageT1; stageB[3] += stageT3 - stageT2; }
                var preview = entity is Insert
                    ? insertContent is not null
                        ? (Paths: (IReadOnlyList<IReadOnlyList<Point2>>?)insertContent.Paths.Select(path => path.Points).ToArray(),
                           Closed: false, Text: (string?)null, TextHeight: 0d, TextRotation: 0d)
                        : (Paths: (IReadOnlyList<IReadOnlyList<Point2>>?)null, Closed: false, Text: (string?)null, TextHeight: 0d, TextRotation: 0d)
                    // A drawing-wide INSERT/proxy budget must not blank later top-level
                    // primitives. Only recursive Dimension expansion consumes that budget;
                    // top-level Hatch/Curve/Leader/Face previews remain independently bounded.
                    : previewVisible
                        ? ExtractPreview(entity, cancellationToken: cancellationToken, workBudget: entityPreviewBudget)
                        : (Paths: (IReadOnlyList<IReadOnlyList<Point2>>?)null, Closed: false, Text: (string?)null, TextHeight: 0d, TextRotation: 0d);

                // A native block preview and cached proxy graphics are alternative render
                // sources, never additive sources. Suppress proxy paths AND proxy texts when
                // native INSERT/DIMENSION content is available to avoid doubled circles.
                var insertHasNativeDefinition = entity is Insert && nativeInsertPreviewAllowed &&
                    insertContent?.WasTruncated != true;
                if (insertHasNativeDefinition && insertContent is { Paths.Count: 0, Texts.Count: 0 })
                    hasBounds = false; // empty/fully-hidden native block: do not draw its broad-phase box
                // Use exactly one source for an INSERT, including its text. Native traversal
                // is authoritative only when every visible child can be represented (or a
                // bounded preview was deliberately truncated); otherwise use proxy paths and
                // proxy texts together, never a native/proxy hybrid that draws duplicate data.
                var useNativeInsertPreview = entity is Insert &&
                    (insertHasNativeDefinition || insertContent?.WasTruncated == true);
                var preferNativePaths = useNativeInsertPreview ||
                    entity is Dimension && preview.Paths is { Count: > 0 };
                var preferNativeTexts = preferNativePaths;
                // When a Dynamic Block has Visibility States but ACadSharp has not resolved
                // the active per-INSERT state, its proxy graphics are NOT a safe fallback:
                // the proxy stream can contain the same geometry for several visibility
                // states (including circles/arcs that AutoCAD currently hides). Suppress both
                // native and proxy preview paths in this case and keep only the INSERT bounds.
                // The original native INSERT and BlockRecord remain intact/editable.
                // INSERT proxy graphics are not a trustworthy active-state representation.
                // They may contain stale cached geometry, inactive Dynamic Block visibility
                // states, or duplicated circle/arc paths even when AutoCAD displays the INSERT
                // correctly. Never tessellate INSERT.ProxyGeometries for viewport preview.
                // Use evaluated native block traversal when it is known-safe; otherwise the
                // existing INSERT extents fallback is safer than inventing visible geometry.
                // Top-level proxy entities receive an independent bounded preview budget.
                // A complex INSERT earlier in the file must not exhaust the shared recursive
                // budget and make later custom/proxy entities disappear from the viewport.
                (IReadOnlyList<ProxyPreviewPath> Paths, IReadOnlyList<ProxyPreviewText> Texts) proxyGraphics =
                    previewVisible && entity is not Insert &&
                    !preferNativePaths && !hasUnresolvedDynamicVisibility
                        ? ExtractProxyGraphics(entity, topLevelPreviewBudget, cancellationToken)
                        : (Array.Empty<ProxyPreviewPath>(), Array.Empty<ProxyPreviewText>());
                if (preferNativeTexts && proxyGraphics.Texts.Count > 0)
                    proxyGraphics = (proxyGraphics.Paths, Array.Empty<ProxyPreviewText>());
                var nestedTexts = entity is Insert
                    ? useNativeInsertPreview ? insertContent?.Texts ?? Array.Empty<ProxyPreviewText>() : Array.Empty<ProxyPreviewText>()
                    : previewVisible && entity is Dimension
                        ? ExtractNestedPreviewTexts(entity, entityPreviewBudget, cancellationToken)
                        : Array.Empty<ProxyPreviewText>();
                var proxyTexts = proxyGraphics.Texts.Concat(nestedTexts).Take(MaxInsertPreviewTexts).ToArray();
                var textPosition = GetPreviewTextPosition(entity);
                var mtextStyle = entity as MText;
                var textWidth = mtextStyle?.RectangleWidth ?? 0;
                var textLineSpacing = mtextStyle?.LineSpacing ?? 1;
                var textAttachment = mtextStyle is null ? 1 : (int)mtextStyle.AttachmentPoint;
                var needsPreviewBounds = !(entity is Insert && hasBounds);
                if (needsPreviewBounds && TryGetPreviewBounds(preview.Paths, proxyGraphics.Paths, proxyTexts, preview.Text, preview.TextHeight, preview.TextRotation,
                        textPosition, textWidth, textLineSpacing, textAttachment, out var previewMin, out var previewMax))
                {
                    min = hasBounds ? new Point2(Math.Min(min.X, previewMin.X), Math.Min(min.Y, previewMin.Y)) : previewMin;
                    max = hasBounds ? new Point2(Math.Max(max.X, previewMax.X), Math.Max(max.Y, previewMax.Y)) : previewMax;
                    hasBounds = true;
                }
                var nativeInsertPaths = useNativeInsertPreview ? insertContent?.Paths : null;
                var finalProxyPaths = nativeInsertPaths is { Count: > 0 } ? nativeInsertPaths : proxyGraphics.Paths;
                return Colorize(new ProxyCadEntity(nativeType, min, max, hasBounds, id, layer,
                    entity is Insert ? null : preview.Paths, preview.Closed, preview.Text, preview.TextHeight, preview.TextRotation,
                    finalProxyPaths, proxyTexts, textPosition, textWidth, textLineSpacing, textAttachment,
                    PreviewFilled: entity is Hatch filledHatch && (filledHatch.IsSolid || filledHatch.GradientColor?.Enabled == true),
                    InsertData: entity is Insert { IsMultiple: false } dataInsert && dataInsert.Normal.Z > 0.999999 && !string.IsNullOrEmpty(dataInsert.Block?.Name)
                        ? new CadInsertData(dataInsert.Block.Name, new Point2(dataInsert.InsertPoint.X, dataInsert.InsertPoint.Y), dataInsert.Rotation, dataInsert.XScale, dataInsert.YScale,
                            dataInsert.Attributes.Count > 0 ? dataInsert.Attributes.Select(a => ReadInsertAttribute(a, dataInsert.Block)).ToArray() : null,
                            dataInsert.Block.Entities.OfType<AttributeDefinition>().Count()) : null));
        }
    }

    private static Guid GetStableEntityId(Entity entity)
    {
        // Handles are unique in a DWG database and remain stable across opens. Use a
        // deterministic GUID for editor indexes while retaining the original handle explicitly.
        if (entity.Handle == 0) return Guid.NewGuid();
        // Identical to Guid.ParseExact(handle.ToString("X16") + 16 zeros, "N") without allocating/parsing strings per entity.
        var handle = entity.Handle;
        return new Guid((int)(handle >> 32), (short)(handle >> 16), (short)handle, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    private static string ResolveFontFamily(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return "Arial";
        var extension = Path.GetExtension(filename);
        var family = Path.GetFileNameWithoutExtension(filename).Trim();
        if (family.Length == 0 || extension.Equals(".shx", StringComparison.OrdinalIgnoreCase)) return "Arial";
        if (family.EndsWith("bold", StringComparison.OrdinalIgnoreCase)) family = family[..^4];
        else if (family.EndsWith("bd", StringComparison.OrdinalIgnoreCase) || family.EndsWith("bi", StringComparison.OrdinalIgnoreCase)) family = family[..^2];
        return family;
    }

    private static bool IsBoldFontFile(string filename)
    {
        var name = Path.GetFileNameWithoutExtension(filename);
        return name.Contains("bold", StringComparison.OrdinalIgnoreCase) || name.EndsWith("bd", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("romand", StringComparison.OrdinalIgnoreCase) || name.Equals("romant", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsItalicFontFile(string filename)
    {
        var name = Path.GetFileNameWithoutExtension(filename);
        return name.Contains("italic", StringComparison.OrdinalIgnoreCase) || name.Contains("oblique", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("bi", StringComparison.OrdinalIgnoreCase);
    }

    private static NativeMText CreateNativeMText(CadMTextEntity model) => new(model.FormattedValue)
    {
        InsertPoint = ToXyz(model.Position),
        AlignmentPoint = new XYZ(Math.Cos(model.RotationRadians), Math.Sin(model.RotationRadians), 0),
        Height = model.Height,
        RectangleWidth = model.Width,
        LineSpacing = model.LineSpacing,
        AttachmentPoint = (AttachmentPointType)Math.Clamp(model.AttachmentPoint, 1, 9),
        Color = ToNativeColor(model.ExplicitColor)
    };

    private static ACadSharp.Color ToNativeColor(CadColor? color) => color is { } value
        ? new ACadSharp.Color(value.R, value.G, value.B) : ACadSharp.Color.ByLayer;

    private static Point2? GetPreviewTextPosition(Entity entity) => entity switch
    {
        TextEntity text => ToPoint(text.InsertPoint),
        MText text => ToPoint(text.InsertPoint),
        _ => null
    };

    private static Layer? GetEffectiveLayer(Entity entity, Layer? inheritedInsertLayer)
    {
        var layer = entity.Layer;
        // Entities on layer 0 inside a block inherit the nearest owning INSERT layer.
        return inheritedInsertLayer is not null && layer is not null &&
            string.Equals(layer.Name, Layer.DefaultName, StringComparison.OrdinalIgnoreCase)
                ? inheritedInsertLayer
                : layer;
    }

    private static bool IsNativeEntityVisible(Entity entity, Layer? inheritedInsertLayer = null)
    {
        if (entity.IsInvisible) return false;
        var layer = GetEffectiveLayer(entity, inheritedInsertLayer);
        if (layer is null) return true;
        // True-color layers report Index == -1; that is not the legacy "negative index = layer off" marker.
        return layer.IsOn && (layer.Color.IsTrueColor || layer.Color.Index >= 0) && !layer.Flags.HasFlag(LayerFlags.Frozen);
    }

    private sealed class VisibilityResolution
    {
        public bool Resolvable = true;
        public HashSet<Entity>? Hidden;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BlockRecord, VisibilityResolution> VisibilityResolutions = new();

    /// <summary>
    /// Dynamic blocks with visibility states: resolve the authored (first) state so only its members are drawn instead
    /// of suppressing the whole block. Anonymous *U evaluated copies and ambiguous graphs stay fail-closed.
    /// </summary>
    private static VisibilityResolution ResolveVisibility(BlockRecord block)
    {
        if (VisibilityResolutions.TryGetValue(block, out var cached)) return cached;
        var result = new VisibilityResolution();
        static List<BlockVisibilityParameter> Parameters(BlockRecord? candidate) =>
            candidate?.EvaluationGraph is { } graph
                ? graph.Nodes.Select(node => node.Expression).OfType<BlockVisibilityParameter>().Where(p => p.States.Count > 0).ToList()
                : new List<BlockVisibilityParameter>();
        var own = Parameters(block);
        var fromSource = Parameters(block.Source);
        if (fromSource.Count > 0) result.Resolvable = false;
        else if (own.Count > 1) result.Resolvable = false;
        else if (own.Count == 1)
        {
            var parameter = own[0];
            var members = new HashSet<Entity>();
            foreach (var entity in parameter.Entities) members.Add(entity);
            foreach (var state in parameter.States.Values) foreach (var entity in state.Entities) members.Add(entity);
            var visible = new HashSet<Entity>(parameter.States.Values.First().Entities);
            var blockEntities = new HashSet<Entity>(block.Entities);
            var hidden = new HashSet<Entity>();
            foreach (var entity in members)
            {
                if (!blockEntities.Contains(entity)) continue; // reference not a child of this block: ignore
                if (!visible.Contains(entity)) hidden.Add(entity);
            }
            // If none of the referenced entities are children of this block, membership cannot be resolved.
            if (members.Count > 0 && !members.Any(blockEntities.Contains)) result.Resolvable = false;
            result.Hidden = hidden;
        }
        VisibilityResolutions.AddOrUpdate(block, result);
        return result;
    }

    private static bool HasUnresolvedDynamicVisibilityStates(BlockRecord block) => !ResolveVisibility(block).Resolvable;

    private static bool IsHiddenByVisibility(BlockRecord block, Entity child)
    {
        var resolution = ResolveVisibility(block);
        return resolution.Hidden is { Count: > 0 } hidden && hidden.Contains(child);
    }

    private static bool HasUnresolvedDynamicVisibilityTree(BlockRecord? block, HashSet<BlockRecord> active,
        Dictionary<(BlockRecord Block, string LayerName), bool> cache, int depth, ref int scannedEntities,
        Layer? inheritedLayer, CancellationToken cancellationToken)
    {
        if (block is null) return false;
        if (cancellationToken.IsCancellationRequested) return true;
        // Incomplete scans must fail closed: show a bounds-only INSERT rather than risk
        // drawing entities from inactive Dynamic Block visibility states.
        if (depth > MaxInsertPreviewDepth || scannedEntities > 50_000) return true;
        var cacheKey = (Block: block, LayerName: inheritedLayer?.Name ?? string.Empty);
        if (cache.TryGetValue(cacheKey, out var cached)) return cached;
        if (!active.Add(block)) return true; // circular block reference: conservative fallback
        try
        {
            var unresolved = HasUnresolvedDynamicVisibilityStates(block);
            if (!unresolved)
            {
                foreach (var child in block.Entities)
                {
                    if (cancellationToken.IsCancellationRequested || ++scannedEntities > 50_000)
                    {
                        unresolved = true;
                        break;
                    }
                    if (!IsNativeEntityVisible(child, inheritedLayer)) continue;
                    var childLayer = GetEffectiveLayer(child, inheritedLayer);
                    if (child is Insert nested && HasUnresolvedDynamicVisibilityTree(nested.Block, active,
                            cache, depth + 1, ref scannedEntities, childLayer, cancellationToken))
                    {
                        unresolved = true;
                        break;
                    }
                    if (child is Dimension { Block: not null } dimension &&
                        HasUnresolvedDynamicVisibilityTree(dimension.Block, active, cache,
                            depth + 1, ref scannedEntities, childLayer, cancellationToken))
                    {
                        unresolved = true;
                        break;
                    }
                }
            }
            if (!cancellationToken.IsCancellationRequested) cache[cacheKey] = unresolved;
            return unresolved;
        }
        finally { active.Remove(block); }
    }

    // True only when a non-empty block is composed of entity types that the native
    // preview walker can represent. Nested block references are inspected recursively;
    // cycles and unknown/custom entity types are rejected and may use proxy fallback.
    private static bool HasOnlyNativePreviewContent(Insert insert, Dictionary<(BlockRecord Block, string LayerName), bool> cache, CancellationToken cancellationToken)
    {
        if (insert.Block is null) return false;
        var budget = 0;
        return IsBlockNativePreviewable(insert.Block, cache,
            new HashSet<BlockRecord>(ReferenceEqualityComparer.Instance), 0, ref budget, GetEffectiveLayer(insert, null), cancellationToken);
    }

    private static bool IsBlockNativePreviewable(BlockRecord block, Dictionary<(BlockRecord Block, string LayerName), bool> cache,
        HashSet<BlockRecord> active, int depth, ref int budget, Layer? inheritedLayer, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || depth >= MaxInsertPreviewDepth || !active.Add(block)) return false;
        try
        {
            // BlockVisibilityParameter stores entity membership for multiple named states.
            // ACadSharp exposes the state definitions, but this renderer does not yet resolve
            // the active per-INSERT state. Treat the dynamic source as proxy-only to prevent
            // every state's circles/arcs being drawn together. Anonymous *U blocks with a
            // source link are the evaluated state-specific representation and are allowed.
            if (HasUnresolvedDynamicVisibilityStates(block)) return false;

            var cacheKey = (Block: block, LayerName: inheritedLayer?.Name ?? string.Empty);
            if (cache.TryGetValue(cacheKey, out var cached)) return cached;
            if (block.Entities.Count == 0 || block.Entities.Count > MaxInsertPreviewEntities)
            {
                cache[cacheKey] = false;
                return false;
            }
            foreach (var entity in block.Entities)
            {
                if (cancellationToken.IsCancellationRequested || ++budget > MaxInsertPreviewEntities) return false;
                // Invisible/off/frozen children do not need a proxy fallback: AutoCAD
                // does not display them, and the native walker intentionally skips them.
                if (!IsNativeEntityVisible(entity, inheritedLayer) || IsHiddenByVisibility(block, entity)) continue;
                var effectiveLayer = GetEffectiveLayer(entity, inheritedLayer);
                if (entity is Insert nested)
                {
                    if (nested.Block is null || !IsBlockNativePreviewable(nested.Block, cache, active, depth + 1, ref budget, effectiveLayer, cancellationToken))
                    {
                        if (!cancellationToken.IsCancellationRequested) cache[cacheKey] = false;
                        return false;
                    }
                    continue;
                }
                if (entity is Dimension { Block: not null } dimension)
                {
                    if (!IsBlockNativePreviewable(dimension.Block, cache, active, depth + 1, ref budget, effectiveLayer, cancellationToken))
                    {
                        if (!cancellationToken.IsCancellationRequested) cache[cacheKey] = false;
                        return false;
                    }
                    continue;
                }
                // An unsupported child (image, wipeout, multileader, OLE...) used to disable the WHOLE block, so the
                // block vanished. Skip just that child and draw everything else.
                if (!IsNativePreviewContentType(entity)) continue;
            }
            cache[cacheKey] = true;
            return true;
        }
        finally { active.Remove(block); }
    }

    private static bool IsNativePreviewContentType(Entity entity) => entity is
        NativeLine or NativeArc or NativeCircle or Ellipse or Spline or IPolyline or Leader or
        Face3D or Solid or MLine or Point or NativeText or NativeMText or AttributeEntity or
        AttributeDefinition or Hatch;

    private static IReadOnlyList<PolylineVertex> MapPolylineVertices(NativePolyline polyline, CancellationToken cancellationToken)
    {
        var vertices = new List<PolylineVertex>(polyline.Vertices.Count);
        for (var index = 0; index < polyline.Vertices.Count; index++)
        {
            if ((index & 255) == 0 && cancellationToken.IsCancellationRequested) return Array.Empty<PolylineVertex>();
            var vertex = polyline.Vertices[index];
            vertices.Add(new PolylineVertex(new Point2(vertex.Location.X, vertex.Location.Y), vertex.Bulge));
        }
        return vertices;
    }

    /// <summary>Same mapping as <see cref="MapPolylineVertices"/>, for the old-style heavyweight 2D POLYLINE entity
    /// (<see cref="ACadSharp.Entities.Polyline2D"/>) - its Vertices collection is a different native type
    /// (SeqendCollection&lt;Vertex2D&gt;, not an indexable list like LWPOLYLINE's), so it is walked with foreach
    /// instead of an indexer, but each vertex has the exact same Location/Bulge shape.</summary>
    private static IReadOnlyList<PolylineVertex> MapPolyline2DVertices(ACadSharp.Entities.Polyline2D polyline, CancellationToken cancellationToken)
    {
        var vertices = new List<PolylineVertex>();
        var index = 0;
        foreach (var vertex in polyline.Vertices)
        {
            if ((index++ & 255) == 0 && cancellationToken.IsCancellationRequested) return Array.Empty<PolylineVertex>();
            vertices.Add(new PolylineVertex(new Point2(vertex.Location.X, vertex.Location.Y), vertex.Bulge));
        }
        return vertices;
    }

    private static InsertPreviewContent ExtractInsertPreviewContent(Insert root, PreviewWorkBudget workBudget, CancellationToken cancellationToken)
    {
        var paths = new List<ProxyPreviewPath>();
        var texts = new List<ProxyPreviewText>();
        var activeBlocks = new HashSet<BlockRecord>(ReferenceEqualityComparer.Instance);
        var visitedEntities = 0;
        var pointCount = 0;
        var truncated = false;
        if (root.Block is null) return new InsertPreviewContent(paths, texts, false);

        var rootLayer = GetEffectiveLayer(root, null);
        var rootMatrix = root.GetTransform().Matrix;
        // Repeated INSERTs of the same block (e.g. 568 x KCS_SNODE) used to re-walk and re-tessellate the whole definition each time.
        // Walk it once in block coordinates, cache that, and just transform the cached points per INSERT.
        var localCache = t_localInsertCache;
        var localKey = (root.Block, rootLayer?.Name ?? string.Empty);
        if (localCache is not null && localCache.TryGetValue(localKey, out var cachedLocal))
            ApplyLocalPreview(cachedLocal, rootMatrix, chargeBudget: true);
        else
        {
            WalkBlock(root.Block, Matrix4.Identity, rootLayer, 0);
            var local = new LocalInsertPreview(paths.ToArray(), texts.ToArray());
            var complete = !truncated && !cancellationToken.IsCancellationRequested && !workBudget.IsExhausted;
            paths.Clear(); texts.Clear(); pointCount = 0;
            if (localCache is not null && complete) localCache[localKey] = local;
            ApplyLocalPreview(local, rootMatrix, chargeBudget: false);
        }
        // ATTRIB instances are stored on the INSERT and are already in the owning
        // insert's coordinate system. For a top-level INSERT no further transform is needed.
        foreach (var attribute in root.Attributes)
        {
            if (cancellationToken.IsCancellationRequested || texts.Count >= MaxInsertPreviewTexts) { truncated = true; break; }
            if (attribute.Flags.HasFlag(AttributeFlags.Hidden) || !IsNativeEntityVisible(attribute, rootLayer)) continue; // invisible ATTRIBs are not displayed
            AddText(attribute, Matrix4.Identity);
        }
        return new InsertPreviewContent(paths, texts, truncated);

        void ApplyLocalPreview(LocalInsertPreview local, Matrix4 matrix, bool chargeBudget)
        {
            foreach (var path in local.Paths)
            {
                if (cancellationToken.IsCancellationRequested) { truncated = true; return; }
                var moved = new List<Point2>(path.Points.Count);
                foreach (var point in path.Points)
                {
                    var world = matrix * new XYZ(point.X, point.Y, 0);
                    if (double.IsFinite(world.X) && double.IsFinite(world.Y)) moved.Add(ToPoint(world));
                }
                if (moved.Count < 2) continue;
                if (chargeBudget && !workBudget.TryAddGeometryPath(moved.Count)) { truncated = true; return; }
                paths.Add(new ProxyPreviewPath(moved, path.Closed));
                pointCount += moved.Count;
            }
            foreach (var text in local.Texts)
            {
                var origin = new XYZ(text.Position.X, text.Position.Y, 0);
                var position = matrix * origin;
                var directionEnd = matrix * (origin + new XYZ(Math.Cos(text.RotationRadians), Math.Sin(text.RotationRadians), 0));
                var heightEnd = matrix * (origin + new XYZ(0, Math.Max(Math.Abs(text.Height), 1e-9), 0));
                var height = Math.Sqrt(Math.Pow(heightEnd.X - position.X, 2) + Math.Pow(heightEnd.Y - position.Y, 2));
                var rotation = Math.Atan2(directionEnd.Y - position.Y, directionEnd.X - position.X);
                var widthScale = Math.Sqrt(Math.Pow(directionEnd.X - position.X, 2) + Math.Pow(directionEnd.Y - position.Y, 2));
                var width = text.Width > 0 && widthScale > 0 ? text.Width * widthScale : 0;
                if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(height) || height <= 1e-12) continue;
                if (chargeBudget && !workBudget.TryAddText()) { truncated = true; return; }
                texts.Add(text with { Position = ToPoint(position), Height = height, RotationRadians = rotation, Width = width });
            }
        }

        void WalkBlock(BlockRecord block, Matrix4 toWorld, Layer? inheritedLayer, int depth)
        {
            if (cancellationToken.IsCancellationRequested || workBudget.IsExhausted || depth > MaxInsertPreviewDepth ||
                visitedEntities >= MaxInsertPreviewEntities || pointCount >= MaxInsertPreviewPoints ||
                paths.Count >= MaxInsertPreviewPaths)
            {
                truncated = true;
                return;
            }
            if (!activeBlocks.Add(block))
            {
                truncated = true; // circular block reference; skip only this recursion branch
                return;
            }
            try
            {
                foreach (var child in block.Entities)
                {
                    if (cancellationToken.IsCancellationRequested || workBudget.IsExhausted) { truncated = true; return; }
                    if (++visitedEntities > MaxInsertPreviewEntities || !workBudget.TryVisitEntity() ||
                        pointCount >= MaxInsertPreviewPoints || paths.Count >= MaxInsertPreviewPaths)
                    {
                        truncated = true;
                        return;
                    }
                    if (!IsNativeEntityVisible(child, inheritedLayer) || IsHiddenByVisibility(block, child)) continue;
                    var childLayer = GetEffectiveLayer(child, inheritedLayer);
                    if (child is Insert nested)
                    {
                        if (nested.Block is null || depth >= MaxInsertPreviewDepth)
                        {
                            truncated = true;
                            continue;
                        }
                        // ATTRIB records attached to this nested INSERT live in the parent
                        // block coordinates; transform them with the parent's matrix.
                        foreach (var attribute in nested.Attributes)
                        {
                            if (cancellationToken.IsCancellationRequested || texts.Count >= MaxInsertPreviewTexts) { truncated = true; break; }
                            if (!attribute.Flags.HasFlag(AttributeFlags.Hidden) && IsNativeEntityVisible(attribute, childLayer)) AddText(attribute, toWorld);
                        }
                        WalkBlock(nested.Block, toWorld * nested.GetTransform().Matrix, childLayer, depth + 1);
                        continue;
                    }
                    if (child is Dimension { Block: not null } dimension)
                    {
                        // Dimension graphics are already represented in their anonymous block.
                        WalkBlock(dimension.Block, toWorld, childLayer, depth + 1);
                        continue;
                    }
                    if (child is AttributeDefinition) continue;
                    if (child is NativeText or NativeMText)
                    {
                        AddText(child, toWorld);
                        continue;
                    }
                    if (child is Hatch hatch)
                    {
                        // Build bounded boundary loops directly from native edge records, then
                        // apply the owning INSERT transform. The Hatch remains a single native
                        // entity; no boundary entities are exploded into the drawing database.
                        var hatchPaths = ExtractHatchBoundaryPreview(hatch,
                            Math.Max(2, MaxInsertPreviewPoints - pointCount), cancellationToken);
                        workBudget.RecordHatchPreview();
                        foreach (var hatchPath in hatchPaths)
                        {
                            if (cancellationToken.IsCancellationRequested || paths.Count >= MaxInsertPreviewPaths ||
                                pointCount >= MaxInsertPreviewPoints) { truncated = true; break; }
                            var transformed = new List<Point2>(Math.Min(hatchPath.Count, MaxInsertPreviewPoints - pointCount));
                            foreach (var point in hatchPath)
                            {
                                if (transformed.Count >= MaxInsertPreviewPoints - pointCount) break;
                                var world = toWorld * new XYZ(point.X, point.Y, 0);
                                if (double.IsFinite(world.X) && double.IsFinite(world.Y)) transformed.Add(ToPoint(world));
                            }
                            if (transformed.Count >= 2 && workBudget.TryAddGeometryPath(transformed.Count))
                            {
                                paths.Add(new ProxyPreviewPath(transformed, Closed: true));
                                pointCount += transformed.Count;
                            }
                            else if (transformed.Count >= 2) truncated = true;
                        }
                        continue;
                    }
                    AddEntityPath(child, toWorld);
                }
            }
            finally { activeBlocks.Remove(block); }
        }

        void AddEntityPath(Entity entity, Matrix4 transform)
        {
            if (workBudget.IsExhausted || paths.Count >= MaxInsertPreviewPaths || pointCount >= MaxInsertPreviewPoints) { truncated = true; return; }
            IReadOnlyList<XYZ>? rawPoints = entity switch
            {
                NativeLine line => [line.StartPoint, line.EndPoint],
                NativeArc arc => TessellateArc(arc, 64, cancellationToken),
                NativeCircle circle when double.IsFinite(circle.Radius) && Math.Abs(circle.Radius) > 1e-12 =>
                    circle.PolygonalVertexes(64).Take(65).ToArray(),
                Ellipse ellipse => ellipse.PolygonalVertexes(96).Take(256).ToArray(),
                Spline spline when spline.TryPolygonalVertexes(96, out var vertices) => vertices.Take(256).ToArray(),
                IPolyline polyline => GetPolylinePreviewPoints(polyline, 4096, cancellationToken),
                Leader leader => leader.Vertices.Take(1024).ToArray(),
                Face3D face => DistinctConsecutive([face.FirstCorner, face.SecondCorner, face.ThirdCorner, face.FourthCorner]),
                Solid solid => DistinctConsecutive([solid.FirstCorner, solid.SecondCorner, solid.ThirdCorner, solid.FourthCorner]),
                MLine mline => mline.Vertices.Take(1024).Select(vertex => vertex.Position).ToArray(),
                // POINT objects inside blocks are not drawn as a cross by default (PDMODE 0 = a dot); the
                // synthetic 1-unit cross produced stray lines around every INSERT, so they are skipped.
                Point => null,
                _ => null
            };
            if (rawPoints is null || rawPoints.Count < 2) return;
            var points = new List<Point2>(Math.Min(rawPoints.Count, MaxInsertPreviewPoints - pointCount));
            for (var index = 0; index < rawPoints.Count; index++)
            {
                if ((index & 255) == 0 && cancellationToken.IsCancellationRequested) { truncated = true; return; }
                if (pointCount + points.Count >= MaxInsertPreviewPoints) { truncated = true; break; }
                var transformed = transform * rawPoints[index];
                if (double.IsFinite(transformed.X) && double.IsFinite(transformed.Y))
                    points.Add(ToPoint(transformed));
            }
            if (points.Count < 2) return;
            var closed = entity switch
            {
                NativeCircle => true,
                Ellipse ellipse => ellipse.IsFullEllipse,
                Spline spline => spline.IsClosed,
                IPolyline polyline => polyline.IsClosed,
                Face3D or Solid => true,
                _ => false
            };
            if (!workBudget.TryAddGeometryPath(points.Count)) { truncated = true; return; }
            paths.Add(new ProxyPreviewPath(points, closed));
            pointCount += points.Count;
        }

        void AddText(Entity entity, Matrix4 transform)
        {
            if (texts.Count >= MaxInsertPreviewTexts || workBudget.IsExhausted || cancellationToken.IsCancellationRequested)
            { truncated = texts.Count >= MaxInsertPreviewTexts || workBudget.IsExhausted; return; }
            string value;
            XYZ insertPoint;
            double requestedHeight;
            double requestedRotation;
            double requestedWidth = 0;
            double lineSpacing = 1;
            int attachment = 1;
            if (entity is MText mtext)
            {
                value = string.Join('\n', mtext.GetPlainTextLines());
                insertPoint = mtext.InsertPoint;
                requestedHeight = mtext.Height;
                requestedRotation = mtext.Rotation;
                requestedWidth = mtext.RectangleWidth;
                lineSpacing = mtext.LineSpacing;
                attachment = (int)mtext.AttachmentPoint;
            }
            else if (entity is TextEntity text)
            {
                value = text.Value;
                var aligned = text.HorizontalAlignment != TextHorizontalAlignment.Left || text.VerticalAlignment != TextVerticalAlignmentType.Baseline;
                insertPoint = aligned && (text.AlignmentPoint.X != 0 || text.AlignmentPoint.Y != 0) ? text.AlignmentPoint : text.InsertPoint;
                requestedHeight = text.Height;
                requestedRotation = text.Rotation;
                attachment = TextAlignmentToAttachment((int)text.HorizontalAlignment, (int)text.VerticalAlignment);
            }
            else return;
            if (string.IsNullOrWhiteSpace(value)) return;
            var position = transform * insertPoint;
            var direction = new XYZ(Math.Cos(requestedRotation), Math.Sin(requestedRotation), 0);
            var directionEnd = transform * (insertPoint + direction);
            var heightEnd = transform * (insertPoint + new XYZ(0, Math.Max(Math.Abs(requestedHeight), 1e-9), 0));
            var height = Math.Sqrt(Math.Pow(heightEnd.X - position.X, 2) + Math.Pow(heightEnd.Y - position.Y, 2));
            var rotation = Math.Atan2(directionEnd.Y - position.Y, directionEnd.X - position.X);
            var widthScale = Math.Sqrt(Math.Pow(directionEnd.X - position.X, 2) + Math.Pow(directionEnd.Y - position.Y, 2));
            var width = requestedWidth > 0 && widthScale > 0 ? requestedWidth * widthScale : 0;
            if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(height) || height <= 1e-12) return;
            if (!workBudget.TryAddText()) { truncated = true; return; }
            // Real style of the block text / ATTRIB: font, width factor, oblique, justification (the old Arial/top-left default put attribute text in the wrong place).
            string family = "Arial"; bool bold = false, italic = false; double oblique = 0, widthFactor = 1; string? formatted = null; var hAlign = 0; var vAlign = 0; string? tag = null;
            if (entity is MText nativeMText)
            {
                var st = ResolveTextStyleFont(nativeMText.Style);
                (family, bold, italic, oblique, widthFactor) = st; formatted = nativeMText.Value;
            }
            else if (entity is TextEntity nativeText)
            {
                var st = ResolveTextStyleFont(nativeText.Style);
                (family, bold, italic, oblique, widthFactor) = st;
                if (nativeText.ObliqueAngle != 0) oblique = nativeText.ObliqueAngle;
                widthFactor = Math.Clamp(nativeText.WidthFactor * st.Width, 0.05, 10);
                hAlign = (int)nativeText.HorizontalAlignment; vAlign = (int)nativeText.VerticalAlignment;
                if (entity is AttributeBase attributeBase) tag = attributeBase.Tag;
            }
            texts.Add(new ProxyPreviewText(value.Length > 4096 ? value[..4096] : value,
                ToPoint(position), height, rotation, width, lineSpacing, attachment, family, bold, italic, oblique, widthFactor,
                entity is MText, formatted, hAlign, vAlign, tag));
        }
    }

    /// <summary>Preview points for a polyline living INSIDE A BLOCK (used to build the cached geometry an INSERT's
    /// proxy draws - a block's own entities are never expanded into the app's own editable PolylineEntity, so they
    /// never go through PolylineTessellator). Just taking vertex.Location and connecting them with straight lines,
    /// as this used to, silently flattened every bulge (arc) segment into a chord - the same "arc shows as a
    /// straight line" bug already fixed for top-level entities and for the draft rasterizer, here a third time, for
    /// polylines nested in a block (e.g. a door-swing arc inside a door block). Tessellates with the exact same
    /// bulge math as PolylineTessellator.Tessellate, just operating directly on ACadSharp's native IPolyline/IVertex
    /// (Location/Bulge) instead of the app's own PolylineVertex, since a block's entities are still native objects
    /// here, never converted to the app's model.</summary>
    private static IReadOnlyList<XYZ> GetPolylinePreviewPoints(IPolyline polyline, int maximumPoints, CancellationToken cancellationToken)
    {
        var raw = new List<(XYZ Location, double Bulge)>(Math.Min(maximumPoints, 1024));
        var readCount = 0;
        foreach (var vertex in polyline.Vertices)
        {
            if ((readCount & 255) == 0 && cancellationToken.IsCancellationRequested) return Array.Empty<XYZ>();
            if (readCount++ >= maximumPoints) break;
            var z = vertex.Location.Dimension > 2 ? vertex.Location[2] : polyline.Elevation;
            raw.Add((new XYZ(vertex.Location[0], vertex.Location[1], z), vertex.Bulge));
        }
        if (raw.Count == 0) return Array.Empty<XYZ>();
        var points = new List<XYZ>(Math.Min(maximumPoints, 1024)) { raw[0].Location };
        var segmentCount = polyline.IsClosed ? raw.Count : raw.Count - 1;
        for (var i = 0; i < segmentCount && points.Count < maximumPoints; i++)
        {
            if ((i & 255) == 0 && cancellationToken.IsCancellationRequested) return Array.Empty<XYZ>();
            var current = raw[i]; var next = raw[(i + 1) % raw.Count];
            var bulge = current.Bulge;
            var dx = next.Location.X - current.Location.X; var dy = next.Location.Y - current.Location.Y;
            var chord = Math.Sqrt(dx * dx + dy * dy);
            if (Math.Abs(bulge) < 1e-10 || chord < 1e-10 || !double.IsFinite(bulge)) { points.Add(next.Location); continue; }
            var theta = 4 * Math.Atan(bulge);
            var offset = chord / (2 * Math.Tan(theta / 2));
            var cx = (current.Location.X + next.Location.X) / 2 - dy / chord * offset;
            var cy = (current.Location.Y + next.Location.Y) / 2 + dx / chord * offset;
            var startAngle = Math.Atan2(current.Location.Y - cy, current.Location.X - cx);
            var radius = Math.Sqrt(Math.Pow(current.Location.X - cx, 2) + Math.Pow(current.Location.Y - cy, 2));
            if (!double.IsFinite(radius)) { points.Add(next.Location); continue; }
            var steps = Math.Clamp((int)Math.Ceiling(Math.Abs(theta) / (Math.PI / 18)), 2, 72);
            for (var step = 1; step <= steps && points.Count < maximumPoints; step++)
            {
                var angle = startAngle + theta * step / steps;
                points.Add(new XYZ(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle), current.Location.Z));
            }
        }
        return points;
    }

    private static IReadOnlyList<XYZ> DistinctConsecutive(IReadOnlyList<XYZ> points)
    {
        var result = new List<XYZ>(points.Count);
        foreach (var point in points)
        {
            if (result.Count == 0) { result.Add(point); continue; }
            var previous = result[^1];
            var dx = previous.X - point.X; var dy = previous.Y - point.Y; var dz = previous.Z - point.Z;
            if (dx * dx + dy * dy + dz * dz > 1e-24) result.Add(point);
        }
        return result;
    }

    private static IReadOnlyList<ProxyPreviewText> ExtractNestedPreviewTexts(Entity root, PreviewWorkBudget workBudget, CancellationToken cancellationToken)
    {
        var result = new List<ProxyPreviewText>();
        var active = new HashSet<BlockRecord>(ReferenceEqualityComparer.Instance);
        var count = 0;
        if (root is Dimension { Block: not null } dimension)
            WalkBlock(dimension.Block, Matrix4.Identity, GetEffectiveLayer(root, null), 0);
        return result;

        void WalkBlock(BlockRecord block, Matrix4 transform, Layer? inheritedLayer, int depth)
        {
            if (depth > MaxInsertPreviewDepth || workBudget.IsExhausted || !active.Add(block) || result.Count >= MaxInsertPreviewTexts) return;
            try
            {
            foreach (var entity in block.Entities)
            {
                if (cancellationToken.IsCancellationRequested || workBudget.IsExhausted || ++count > MaxInsertPreviewEntities ||
                    !workBudget.TryVisitEntity() || result.Count >= MaxInsertPreviewTexts) return;
                if (!IsNativeEntityVisible(entity, inheritedLayer) || entity is AttributeDefinition) continue;
                var effectiveLayer = GetEffectiveLayer(entity, inheritedLayer);
                if (entity is MText mtext && !string.IsNullOrWhiteSpace(mtext.PlainText))
                {
                    var position = transform * mtext.InsertPoint;
                    var value = string.Join('\n', mtext.GetPlainTextLines());
                    var (family, bold, italic, oblique, styleWidth) = ResolveTextStyleFont(mtext.Style);
                    var (dRot, dScale) = TransformRotationScale(transform);
                    var raw = mtext.Value ?? value;
                    if (workBudget.TryAddText())
                        result.Add(new ProxyPreviewText(value.Length > 4096 ? value[..4096] : value, ToPoint(position),
                            mtext.Height * dScale, mtext.Rotation + dRot, mtext.RectangleWidth * dScale, mtext.LineSpacing, (int)mtext.AttachmentPoint,
                            family, bold, italic, oblique, styleWidth, true, raw.Length > 4096 ? raw[..4096] : raw));
                    else return;
                }
                else if (entity is TextEntity text && !string.IsNullOrWhiteSpace(text.Value))
                {
                    var aligned = text.HorizontalAlignment != TextHorizontalAlignment.Left || text.VerticalAlignment != TextVerticalAlignmentType.Baseline;
                    var position = transform * (aligned && text.AlignmentPoint is { } ap && (ap.X != 0 || ap.Y != 0) ? ap : text.InsertPoint);
                    var (family, bold, italic, oblique, styleWidth) = ResolveTextStyleFont(text.Style);
                    var (dRot, dScale) = TransformRotationScale(transform);
                    if (workBudget.TryAddText())
                        result.Add(new ProxyPreviewText(text.Value.Length > 1024 ? text.Value[..1024] : text.Value, ToPoint(position), text.Height * dScale, text.Rotation + dRot,
                            0, 1, TextAlignmentToAttachment((int)text.HorizontalAlignment, (int)text.VerticalAlignment), family, bold, italic, text.ObliqueAngle != 0 ? text.ObliqueAngle : oblique, Math.Clamp(text.WidthFactor * styleWidth, 0.05, 10),
                            false, null, (int)text.HorizontalAlignment, (int)text.VerticalAlignment));
                    else return;
                }
                else if (entity is Insert nested && nested.Block is not null)
                {
                    WalkBlock(nested.Block, transform * nested.GetTransform().Matrix, effectiveLayer, depth + 1);
                }
                else if (entity is Dimension { Block: not null } nestedDimension)
                {
                    WalkBlock(nestedDimension.Block, transform, effectiveLayer, depth + 1);
                }
            }
            }
            finally { active.Remove(block); }
        }
    }

    private static (string Family, bool Bold, bool Italic, double Oblique, double Width) ResolveTextStyleFont(TextStyle? style)
    {
        if (style is null) return ("Arial", false, false, 0, 1);
        var fontName = style.Filename ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fontName)) fontName = style.BigFontFilename ?? string.Empty;
        return (ResolveFontFamily(fontName), style.TrueType.HasFlag(FontFlags.Bold) || IsBoldFontFile(fontName),
            style.TrueType.HasFlag(FontFlags.Italic) || IsItalicFontFile(fontName), style.ObliqueAngle, Math.Clamp(style.Width, 0.05, 10));
    }

    private static (double Rotation, double Scale) TransformRotationScale(Matrix4 transform)
    {
        var o = transform * XYZ.Zero; var x = transform * XYZ.AxisX;
        var dx = x.X - o.X; var dy = x.Y - o.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        return len < 1e-12 ? (0, 1) : (Math.Atan2(dy, dx), len);
    }

    /// <summary>MTEXT-style attachment (1-9, row-major from top-left) equivalent of a TEXT justification, so pick boxes and bounds sit where the glyphs are drawn.</summary>
    private static int TextAlignmentToAttachment(int horizontal, int vertical)
    {
        var column = horizontal is 1 or 4 ? 1 : horizontal == 2 ? 2 : 0;
        var row = horizontal == 4 ? 1 : vertical switch { 3 => 0, 2 => 1, _ => 2 };
        return row * 3 + column + 1;
    }

    private static Point2 ToPoint(XYZ point) => new(point.X, point.Y);
    private static XYZ ToXyz(Point2 point) => new(point.X, point.Y, 0);

    private static (IReadOnlyList<ProxyPreviewPath> Paths, IReadOnlyList<ProxyPreviewText> Texts) ExtractProxyGraphics(
        Entity entity, PreviewWorkBudget workBudget, CancellationToken cancellationToken)
    {
        var paths = new List<ProxyPreviewPath>();
        var texts = new List<ProxyPreviewText>();
        var transforms = new Stack<Transform>();
        var pointCount = 0;
        XYZ Apply(XYZ point)
        {
            foreach (var transform in transforms.Reverse()) point = transform.ApplyTransform(point);
            return point;
        }
        void Add(IEnumerable<XYZ> source, bool closed = false)
        {
            if (paths.Count >= MaxProxyPreviewPaths || workBudget.IsExhausted || cancellationToken.IsCancellationRequested) return;
            var points = new List<Point2>(Math.Min(256, MaxProxyPreviewPoints - pointCount));
            var index = 0;
            foreach (var sourcePoint in source)
            {
                if ((index++ & 255) == 0 && cancellationToken.IsCancellationRequested) return;
                if (pointCount + points.Count >= MaxProxyPreviewPoints) break;
                var point = Apply(sourcePoint);
                if (double.IsFinite(point.X) && double.IsFinite(point.Y)) points.Add(ToPoint(point));
            }
            if (points.Count < 2) return;
            if (!workBudget.TryAddGeometryPath(points.Count)) return;
            paths.Add(new ProxyPreviewPath(points, closed));
            pointCount += points.Count;
        }

        try
        {
            var itemCount = 0;
            foreach (var item in entity.ProxyGeometries)
            {
                if (cancellationToken.IsCancellationRequested || workBudget.IsExhausted || ++itemCount > 20_000 || !workBudget.TryVisitProxyItem() || pointCount >= MaxProxyPreviewPoints ||
                    paths.Count >= MaxProxyPreviewPaths || texts.Count >= MaxInsertPreviewTexts) break;
                switch (item)
                {
                    case ProxyPushModelTransform push:
                        transforms.Push(new Transform(push.TransformationMatrix));
                        break;
                    case ProxyPushModelTransform2 push:
                        transforms.Push(new Transform(push.TransformationMatrix));
                        break;
                    case ProxyPopModelTransform when transforms.Count > 0:
                        transforms.Pop();
                        break;
                    case ProxyText text:
                        AddProxyText(text.Text, text.StartPoint, text.TextDirection, text.Height);
                        break;
                    case ProxyUnicodeText text:
                        AddProxyText(text.Text, text.StartPoint, text.TextDirection, text.Height);
                        break;
                    case ProxyText2 text:
                        AddProxyText(text.Text, text.StartPoint, text.TextDirection, text.Height);
                        break;
                    case ProxyUnicodeText2 text:
                        AddProxyText(text.Text, text.StartPoint, text.TextDirection, text.Height);
                        break;
                    case ProxyPolyline polyline:
                        Add(polyline.Points);
                        break;
                    case ProxyPolygon polygon when polygon.Points is not null:
                        Add(polygon.Points, closed: true);
                        break;
                    case ProxyCircle circle when double.IsFinite(circle.Radius) && circle.Radius > 1e-12:
                        Add(Enumerable.Range(0, 65).Select(index => new XYZ(circle.Center.X + circle.Radius * Math.Cos(index * Math.PI * 2 / 64), circle.Center.Y + circle.Radius * Math.Sin(index * Math.PI * 2 / 64), circle.Center.Z)), closed: true);
                        break;
                    case ProxyCircularArc arc when double.IsFinite(arc.Radius) && arc.Radius > 1e-12 &&
                        double.IsFinite(arc.SweepAngle) && Math.Abs(arc.SweepAngle) > 1e-10:
                        Add(TessellateProxyArc(arc, cancellationToken), closed: Math.Abs(Math.Abs(arc.SweepAngle) - Math.PI * 2) < 1e-8);
                        break;
                    case ProxyXLine:
                    case ProxyRay:
                        // Infinite construction geometry has no finite viewport representation yet.
                        break;
                    case ProxyMeshBase mesh when mesh.Vertices.Count >= 2:
                        if (mesh is ProxyShell shell && shell.Faces.Count > 0)
                        {
                            var visitedFaces = 0;
                            foreach (var face in shell.Faces)
                            {
                                if (cancellationToken.IsCancellationRequested || workBudget.IsExhausted ||
                                    ++visitedFaces > 20_000 || paths.Count >= MaxProxyPreviewPaths ||
                                    !workBudget.TryVisitProxyItem() || pointCount >= MaxProxyPreviewPoints) break;
                                var indices = new List<int>(64);
                                var faceIndex = 0;
                                foreach (var index in face)
                                {
                                    if ((faceIndex++ & 255) == 0 && cancellationToken.IsCancellationRequested) break;
                                    if (faceIndex > 4_096 || indices.Count >= 1_024) break;
                                    if (index >= 0 && index < mesh.Vertices.Count) indices.Add(index);
                                }
                                if (indices.Count >= 2) Add(indices.Select(index => mesh.Vertices[index]), closed: true);
                            }
                        }
                        else Add(mesh.Vertices);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch { }
        return (paths, texts);

        void AddProxyText(string? text, XYZ start, XYZ direction, double height)
        {
            if (string.IsNullOrWhiteSpace(text) || texts.Count >= MaxInsertPreviewTexts || workBudget.IsExhausted || cancellationToken.IsCancellationRequested) return;
            var position = Apply(start);
            var directionEnd = Apply(start + direction);
            var angle = Math.Atan2(directionEnd.Y - position.Y, directionEnd.X - position.X);
            if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(height) || height <= 0) return;
            if (!workBudget.TryAddText()) return;
            texts.Add(new ProxyPreviewText(text.Length > 1024 ? text[..1024] : text, ToPoint(position), height, angle));
        }
    }

    private static IEnumerable<XYZ> TessellateProxyArc(ProxyCircularArc arc, CancellationToken cancellationToken)
    {
        var normalLength = Math.Sqrt(arc.Normal.X * arc.Normal.X + arc.Normal.Y * arc.Normal.Y + arc.Normal.Z * arc.Normal.Z);
        if (!double.IsFinite(normalLength) || normalLength < 1e-12 || !double.IsFinite(arc.Radius) || arc.Radius <= 1e-12 ||
            !double.IsFinite(arc.SweepAngle) || Math.Abs(arc.SweepAngle) <= 1e-10) yield break;
        var nx = arc.Normal.X / normalLength; var ny = arc.Normal.Y / normalLength; var nz = arc.Normal.Z / normalLength;
        var sx = arc.StartVectorDirection.X; var sy = arc.StartVectorDirection.Y; var sz = arc.StartVectorDirection.Z;
        var sl = Math.Sqrt(sx * sx + sy * sy + sz * sz);
        if (!double.IsFinite(sl) || sl < 1e-12) yield break;
        sx /= sl; sy /= sl; sz /= sl;
        var ux = ny * sz - nz * sy; var uy = nz * sx - nx * sz; var uz = nx * sy - ny * sx;
        var count = Math.Clamp((int)Math.Ceiling(64 * Math.Abs(arc.SweepAngle) / (Math.PI * 2)), 4, 256);
        for (var index = 0; index <= count; index++)
        {
            if ((index & 31) == 0 && cancellationToken.IsCancellationRequested) yield break;
            var angle = arc.SweepAngle * index / count;
            var x = sx * Math.Cos(angle) + ux * Math.Sin(angle);
            var y = sy * Math.Cos(angle) + uy * Math.Sin(angle);
            var z = sz * Math.Cos(angle) + uz * Math.Sin(angle);
            yield return new XYZ(arc.Center.X + arc.Radius * x, arc.Center.Y + arc.Radius * y, arc.Center.Z + arc.Radius * z);
        }
    }

    private static (IReadOnlyList<IReadOnlyList<Point2>>? Paths, bool Closed, string? Text, double TextHeight, double TextRotation) ExtractPreview(
        Entity entity, int depth = 0, Layer? inheritedInsertLayer = null, CancellationToken cancellationToken = default,
        PreviewWorkBudget? workBudget = null)
    {
        const int maxPoints = 4096;
        try
        {
            if (cancellationToken.IsCancellationRequested || !IsNativeEntityVisible(entity, inheritedInsertLayer))
                return (null, false, null, 0, 0);
            var effectiveLayer = GetEffectiveLayer(entity, inheritedInsertLayer);
            if (entity is Insert nestedInsert)
            {
                var nestedContent = ExtractInsertPreviewContent(nestedInsert, workBudget ?? new PreviewWorkBudget(), cancellationToken);
                return (nestedContent.Paths.Select(path => (IReadOnlyList<Point2>)path.Points).ToArray(), false, null, 0, 0);
            }
            if (entity is Dimension dimension && dimension.Block is not null)
                return (ExtractEntitiesPreview(dimension.Block.Entities, depth + 1, maxPoints, effectiveLayer, cancellationToken, workBudget), false, null, 0, 0);
            if (entity is Hatch hatch)
            {
                // Render boundary loops directly from ACadSharp edge data. Do not explode
                // the Hatch or materialize one temporary CAD entity per boundary edge.
                var hatchPaths = ExtractHatchBoundaryPreview(hatch, maxPoints, cancellationToken);
                workBudget?.RecordHatchPreview();
                return (hatchPaths, true, null, 0, 0);
            }
            IReadOnlyList<Point2>? points = entity switch
            {
                NativeLine line => [ToPoint(line.StartPoint), ToPoint(line.EndPoint)],
                NativeArc arc => TessellateArc(arc, 64, cancellationToken).Select(ToPoint).ToArray(),
                NativeCircle circle when double.IsFinite(circle.Radius) && Math.Abs(circle.Radius) > 1e-12 => Enumerable.Range(0, 65).Select(index => new Point2(circle.Center.X + circle.Radius * Math.Cos(index * Math.PI * 2 / 64), circle.Center.Y + circle.Radius * Math.Sin(index * Math.PI * 2 / 64))).ToArray(),
                Ellipse ellipse => ellipse.PolygonalVertexes(96).Take(maxPoints).Select(ToPoint).ToArray(),
                Spline spline when spline.TryPolygonalVertexes(96, out var vertices) => vertices.Take(maxPoints).Select(ToPoint).ToArray(),
                IPolyline polyline => GetPolylinePreviewPoints(polyline, maxPoints, cancellationToken).Select(ToPoint).ToArray(),
                Leader leader => leader.Vertices.Take(maxPoints).Select(ToPoint).ToArray(),
                Face3D face => DistinctConsecutive([face.FirstCorner, face.SecondCorner, face.ThirdCorner, face.FourthCorner]).Select(ToPoint).ToArray(),
                Solid solid => DistinctConsecutive([solid.FirstCorner, solid.SecondCorner, solid.ThirdCorner, solid.FourthCorner]).Select(ToPoint).ToArray(),
                MLine mline => mline.Vertices.Take(maxPoints).Select(vertex => ToPoint(vertex.Position)).ToArray(),
                Point => null,
                TextEntity => null,
                MText => null,
                _ => null
            };
            string? label = entity switch
            {
                TextEntity text => text.Value,
                MText mtext => string.Join('\n', mtext.GetPlainTextLines()),
                _ => null
            };
            var labelHeight = entity switch { TextEntity text => text.Height, MText mtext => mtext.Height, _ => 0 };
            var labelRotation = entity switch { TextEntity text => text.Rotation, MText mtext => mtext.Rotation, _ => 0 };
            IReadOnlyList<IReadOnlyList<Point2>>? paths = entity is Point pointEntity
                ? new IReadOnlyList<Point2>[]
                {
                    new[] { new Point2(pointEntity.Location.X - 0.5, pointEntity.Location.Y), new Point2(pointEntity.Location.X + 0.5, pointEntity.Location.Y) },
                    new[] { new Point2(pointEntity.Location.X, pointEntity.Location.Y - 0.5), new Point2(pointEntity.Location.X, pointEntity.Location.Y + 0.5) }
                }
                : points is { Count: >= 2 } ? new IReadOnlyList<Point2>[] { points } : null;
            var closed = (entity is NativeCircle) || (entity switch { Ellipse ellipse => ellipse.IsFullEllipse, Spline spline => spline.IsClosed, IPolyline polyline => polyline.IsClosed, Face3D or Solid => true, _ => false });
            return (paths, closed, string.IsNullOrWhiteSpace(label) ? null : label, labelHeight, labelRotation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (null, false, null, 0, 0);
        }
        catch
        {
            return (null, false, null, 0, 0);
        }
    }

    private static IReadOnlyList<IReadOnlyList<Point2>> ExtractHatchBoundaryPreview(
        Hatch hatch, int maxPoints, CancellationToken cancellationToken)
    {
        var result = new List<IReadOnlyList<Point2>>(Math.Min(hatch.Paths.Count, 64));
        var ocsToWorld = Matrix3.ArbitraryAxis(hatch.Normal);
        var remaining = Math.Max(2, maxPoints);
        foreach (var boundary in hatch.Paths)
        {
            if (cancellationToken.IsCancellationRequested || remaining < 2) break;
            var points = new List<Point2>(Math.Min(128, remaining));
            void Add(double x, double y)
            {
                if (points.Count >= remaining || !double.IsFinite(x) || !double.IsFinite(y)) return;
                var world = ocsToWorld * new XYZ(x, y, hatch.Elevation);
                if (double.IsFinite(world.X) && double.IsFinite(world.Y)) points.Add(ToPoint(world));
            }
            const double chainTolerance = 1e-4;
            foreach (var edge in boundary.Edges)
            {
                if (cancellationToken.IsCancellationRequested || points.Count >= remaining) break;
                var before = points.Count;
                switch (edge)
                {
                    case Hatch.BoundaryPath.Line line:
                        Add(line.Start.X, line.Start.Y);
                        Add(line.End.X, line.End.Y);
                        break;
                    case Hatch.BoundaryPath.Arc arc:
                    {
                        var sweep = arc.CounterClockWise ? arc.EndAngle - arc.StartAngle : arc.StartAngle - arc.EndAngle;
                        sweep %= Math.PI * 2;
                        if (sweep < 0) sweep += Math.PI * 2;
                        if (sweep <= 1e-10) sweep = Math.PI * 2;
                        var count = Math.Clamp((int)Math.Ceiling(96 * sweep / (Math.PI * 2)), 2, 96);
                        for (var i = 0; i <= count && points.Count < remaining; i++)
                        {
                            if ((i & 31) == 0 && cancellationToken.IsCancellationRequested) break;
                            var angle = arc.StartAngle + (arc.CounterClockWise ? 1 : -1) * sweep * i / count;
                            Add(arc.Center.X + arc.Radius * Math.Cos(angle), arc.Center.Y + arc.Radius * Math.Sin(angle));
                        }
                        break;
                    }
                    case Hatch.BoundaryPath.Ellipse ellipse:
                    {
                        var sweep = ellipse.CounterClockWise ? ellipse.EndAngle - ellipse.StartAngle : ellipse.StartAngle - ellipse.EndAngle;
                        sweep %= Math.PI * 2;
                        if (sweep < 0) sweep += Math.PI * 2;
                        if (sweep <= 1e-10) sweep = Math.PI * 2;
                        var count = Math.Clamp((int)Math.Ceiling(96 * sweep / (Math.PI * 2)), 2, 96);
                        var majorX = ellipse.MajorAxisEndPoint.X;
                        var majorY = ellipse.MajorAxisEndPoint.Y;
                        var minorX = -majorY * ellipse.RadiusRatio;
                        var minorY = majorX * ellipse.RadiusRatio;
                        for (var i = 0; i <= count && points.Count < remaining; i++)
                        {
                            if ((i & 31) == 0 && cancellationToken.IsCancellationRequested) break;
                            var angle = ellipse.StartAngle + (ellipse.CounterClockWise ? 1 : -1) * sweep * i / count;
                            var c = Math.Cos(angle); var sn = Math.Sin(angle);
                            Add(ellipse.Center.X + majorX * c + minorX * sn, ellipse.Center.Y + majorY * c + minorY * sn);
                        }
                        break;
                    }
                    case Hatch.BoundaryPath.Polyline polyline:
                    {
                        var vertices = polyline.Vertices;
                        if (vertices.Count < 2) break;
                        Add(vertices[0].X, vertices[0].Y);
                        var segmentCount = polyline.IsClosed ? vertices.Count : vertices.Count - 1;
                        for (var i = 0; i < segmentCount && points.Count < remaining; i++)
                        {
                            var a = vertices[i]; var b = vertices[(i + 1) % vertices.Count];
                            AppendBulge(a.X, a.Y, b.X, b.Y, a.Z, Add, remaining - points.Count, cancellationToken);
                        }
                        break;
                    }
                    case Hatch.BoundaryPath.Spline spline:
                    {
                        // ACadSharp's spline sampler is bounded and keeps the source Hatch intact.
                        foreach (var vertex in spline.PolygonalVertexes(96))
                        {
                            if (cancellationToken.IsCancellationRequested || points.Count >= remaining) break;
                            Add(vertex.X, vertex.Y);
                        }
                        break;
                    }
                }
                // Edges must chain; a jump starts a new loop instead of drawing a long chord across the drawing.
                if (before > 0 && points.Count > before)
                {
                    var previous = points[before - 1];
                    var first = points[before]; var last = points[^1];
                    var gapStart = Math.Abs(first.X - previous.X) + Math.Abs(first.Y - previous.Y);
                    if (gapStart > chainTolerance)
                    {
                        var gapEnd = Math.Abs(last.X - previous.X) + Math.Abs(last.Y - previous.Y);
                        if (gapEnd <= chainTolerance) points.Reverse(before, points.Count - before);
                        else
                        {
                            if (before >= 3) result.Add(points.GetRange(0, before));
                            points.RemoveRange(0, before);
                        }
                    }
                }
            }
            // Some producers store source boundary entities rather than edge records.
            if (points.Count < 2 && boundary.Entities.Count > 0)
            {
                foreach (var sourceEntity in boundary.Entities)
                {
                    if (cancellationToken.IsCancellationRequested || points.Count >= remaining) break;
                    var sourcePreview = ExtractPreview(sourceEntity, cancellationToken: cancellationToken);
                    if (sourcePreview.Paths is null) continue;
                    foreach (var sourcePath in sourcePreview.Paths)
                    foreach (var point in sourcePath)
                    {
                        if (points.Count >= remaining) break;
                        points.Add(point);
                    }
                }
            }
            if (points.Count >= 3 || (points.Count >= 2 && boundary.Edges.Count == 0))
            {
                result.Add(points);
                remaining -= points.Count;
            }
        }
        return result;
    }

    private static void AppendBulge(double x1, double y1, double x2, double y2, double bulge,
        Action<double, double> add, int maxPoints, CancellationToken cancellationToken)
    {
        if (maxPoints <= 1 || !double.IsFinite(bulge) || Math.Abs(bulge) <= 1e-12)
        {
            if (maxPoints > 0) add(x2, y2);
            return;
        }
        var dx = x2 - x1; var dy = y2 - y1;
        var chord = Math.Sqrt(dx * dx + dy * dy);
        if (!double.IsFinite(chord) || chord <= 1e-12) return;
        var theta = 4 * Math.Atan(bulge);
        var offset = chord * (1 - bulge * bulge) / (4 * bulge);
        var cx = (x1 + x2) * 0.5 - dy / chord * offset;
        var cy = (y1 + y2) * 0.5 + dx / chord * offset;
        var radius = chord * (1 + bulge * bulge) / (4 * Math.Abs(bulge));
        var start = Math.Atan2(y1 - cy, x1 - cx);
        var count = Math.Clamp((int)Math.Ceiling(48 * Math.Abs(theta) / (Math.PI * 2)), 2, Math.Min(48, maxPoints));
        for (var i = 1; i <= count; i++)
        {
            if ((i & 15) == 0 && cancellationToken.IsCancellationRequested) return;
            var angle = start + theta * i / count;
            add(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle));
        }
    }

    private static IReadOnlyList<XYZ> TessellateArc(NativeArc arc, int segments, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(arc.Radius) || Math.Abs(arc.Radius) <= 1e-12 ||
            !double.IsFinite(arc.StartAngle) || !double.IsFinite(arc.EndAngle)) return Array.Empty<XYZ>();
        var rawSweep = arc.EndAngle - arc.StartAngle;
        // A degenerate ARC with identical (or equivalent full-turn) endpoints is not
        // silently converted into a complete circle. CIRCLE has its own native entity.
        if (!double.IsFinite(rawSweep) || Math.Abs(rawSweep) <= 1e-10) return Array.Empty<XYZ>();
        var fullTurn = Math.PI * 2;
        var sweep = rawSweep % fullTurn;
        if (sweep < 0) sweep += fullTurn;
        if (sweep <= 1e-10) return Array.Empty<XYZ>();
        var count = Math.Clamp((int)Math.Ceiling(segments * sweep / (Math.PI * 2)), 2, Math.Max(2, segments));
        var points = new XYZ[count + 1];
        for (var index = 0; index <= count; index++)
        {
            if ((index & 31) == 0 && cancellationToken.IsCancellationRequested) return Array.Empty<XYZ>();
            var angle = arc.StartAngle + sweep * index / count;
            points[index] = arc.PolarCoordinateRelativeToCenter(angle);
        }
        return points;
    }

    private static IReadOnlyList<IReadOnlyList<Point2>> ExtractEntitiesPreview(
        IEnumerable<Entity> entities, int depth, int maxPoints, Layer? inheritedInsertLayer = null,
        CancellationToken cancellationToken = default, PreviewWorkBudget? workBudget = null)
    {
        if (depth >= MaxInsertPreviewDepth || cancellationToken.IsCancellationRequested) return Array.Empty<IReadOnlyList<Point2>>();
        var paths = new List<IReadOnlyList<Point2>>();
        var pointCount = 0;
        var visited = 0;
        foreach (var child in entities)
        {
            if (cancellationToken.IsCancellationRequested || workBudget?.IsExhausted == true || ++visited > 20_000 ||
                (workBudget is not null && !workBudget.TryVisitEntity()) || pointCount >= maxPoints) break;
            if (!IsNativeEntityVisible(child, inheritedInsertLayer)) continue;
            var preview = ExtractPreview(child, depth + 1, inheritedInsertLayer, cancellationToken, workBudget);
            if (preview.Paths is null) continue;
            foreach (var path in preview.Paths)
            {
                if (cancellationToken.IsCancellationRequested || path.Count < 2) break;
                var remaining = maxPoints - pointCount;
                if (remaining <= 0) return paths;
                var clipped = path.Take(remaining).ToArray();
                if (clipped.Length >= 2)
                {
                    // Insert/Dimension/Hatch children account for their own recursive
                    // preview paths. Count primitive child paths here so aggregate
                    // hatch/dimension results also respect the drawing-wide budget.
                    if (workBudget is not null && child is not (Insert or Dimension or Hatch) &&
                        !workBudget.TryAddGeometryPath(clipped.Length)) return paths;
                    paths.Add(clipped);
                }
                pointCount += clipped.Length;
            }
        }
        return paths;
    }

    private static bool TryGetPreviewBounds(IReadOnlyList<IReadOnlyList<Point2>>? previewPaths, IReadOnlyList<ProxyPreviewPath> proxyPaths,
        IReadOnlyList<ProxyPreviewText> proxyTexts, string? previewText, double previewTextHeight, double previewTextRotation,
        Point2? previewTextPosition, double previewTextWidth, double previewTextLineSpacing, int previewTextAttachment, out Point2 min, out Point2 max)
    {
        var points = (previewPaths ?? Array.Empty<IReadOnlyList<Point2>>()).SelectMany(path => path)
            .Concat(proxyPaths.SelectMany(path => path.Points))
            .Concat(proxyTexts.SelectMany(GetTextBounds))
            .Concat(previewText is not null && previewTextPosition is Point2 position
                ? GetTextBounds(new ProxyPreviewText(previewText, position, previewTextHeight, previewTextRotation,
                    previewTextWidth, previewTextLineSpacing, previewTextAttachment))
                : Array.Empty<Point2>())
            .Where(point => double.IsFinite(point.X) && double.IsFinite(point.Y)).ToArray();
        if (points.Length == 0)
        {
            min = max = new Point2(0, 0);
            return false;
        }
        min = new Point2(points.Min(point => point.X), points.Min(point => point.Y));
        max = new Point2(points.Max(point => point.X), points.Max(point => point.Y));
        return true;

        static IEnumerable<Point2> GetTextBounds(ProxyPreviewText text)
        {
            var height = double.IsFinite(text.Height) && text.Height > 0 ? text.Height : 1;
            var lines = text.Text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
            var maxLineLength = lines.Select(line => line.Length).DefaultIfEmpty(1).Max();
            var estimatedWidth = Math.Max(height * 0.4, Math.Min(height * maxLineLength * 0.62, 100_000));
            var width = double.IsFinite(text.Width) && text.Width > 0 ? Math.Max(estimatedWidth, text.Width) : estimatedWidth;
            var lineCount = Math.Max(1, text.Text.Count(character => character == '\n') + 1);
            var lineHeight = height * 1.5 * Math.Clamp(double.IsFinite(text.LineSpacing) ? text.LineSpacing : 1, 0.25, 4);
            var attachment = Math.Clamp(text.AttachmentPoint, 1, 9);
            var horizontalOffset = (attachment - 1) % 3 * 0.5;
            var verticalOffset = (attachment - 1) / 3 * 0.5;
            var left = -width * horizontalOffset;
            var totalHeight = lineHeight * lineCount;
            var top = totalHeight * verticalOffset;
            var bottom = top - totalHeight;
            var angle = double.IsFinite(text.RotationRadians) ? text.RotationRadians : 0;
            var cosine = Math.Cos(angle); var sine = Math.Sin(angle);
            var anchor = text.Position;
            foreach (var (x, y) in new[] { (left, bottom), (left + width, bottom), (left + width, top), (left, top) })
                yield return new Point2(anchor.X + x * cosine - y * sine, anchor.Y + x * sine + y * cosine);
        }
    }

    private static IReadOnlyList<Point2> DistinctConsecutive(IReadOnlyList<Point2> points)
    {
        var result = new List<Point2>(points.Count);
        foreach (var point in points)
            if (result.Count == 0 || Point2.Distance(result[^1], point) > 1e-9) result.Add(point);
        return result;
    }

    [ThreadStatic] private static HashSet<BlockRecord>? t_measuring;

    /// <summary>
    /// Bounding box of a block definition in block coordinates, memoized at EVERY nesting level. The vendor
    /// BlockRecord.GetBoundingBox() re-measures nested INSERT blocks recursively on each call, which cost ~3 s over 1,569 INSERTs.
    /// </summary>
    private static BoundingBox MeasureBlockBounds(BlockRecord block)
    {
        if (BlockBoundsCache.TryGetValue(block, out var cached)) return cached.Box;
        var measuring = t_measuring ??= new HashSet<BlockRecord>(ReferenceEqualityComparer.Instance);
        if (!measuring.Add(block)) return BoundingBox.Null; // circular reference
        var box = BoundingBox.Null;
        try
        {
            foreach (var item in block.Entities)
            {
                BoundingBox itemBox;
                try
                {
                    if (item is Insert child && child.Block is { } childBlock)
                    {
                        var inner = MeasureBlockBounds(childBlock);
                        if (inner.Extent is not (BoundingBoxExtent.Finite or BoundingBoxExtent.Point)) continue;
                        var transform = child.GetTransform();
                        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
                        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
                        for (var corner = 0; corner < 8; corner++)
                        {
                            var c = transform.ApplyTransform(new XYZ((corner & 1) == 0 ? inner.Min.X : inner.Max.X, (corner & 2) == 0 ? inner.Min.Y : inner.Max.Y, (corner & 4) == 0 ? inner.Min.Z : inner.Max.Z));
                            minX = Math.Min(minX, c.X); maxX = Math.Max(maxX, c.X); minY = Math.Min(minY, c.Y); maxY = Math.Max(maxY, c.Y);
                            minZ = Math.Min(minZ, c.Z); maxZ = Math.Max(maxZ, c.Z);
                        }
                        if (!double.IsFinite(minX + minY + maxX + maxY + minZ + maxZ)) continue;
                        itemBox = new BoundingBox(new XYZ(minX, minY, minZ), new XYZ(maxX, maxY, maxZ));
                    }
                    else itemBox = item.GetBoundingBox();
                }
                catch { continue; }
                if (itemBox.Extent == BoundingBoxExtent.Infinite || itemBox.Extent == BoundingBoxExtent.Null) continue;
                box = box.Merge(itemBox);
            }
        }
        finally { measuring.Remove(block); }
        BlockBoundsCache.AddOrUpdate(block, new BlockBounds(box));
        return box;
    }

    private sealed class BlockBounds { public readonly BoundingBox Box; public BlockBounds(BoundingBox box) { Box = box; } }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BlockRecord, BlockBounds> BlockBoundsCache = new();

    private static bool TryGetBounds(Entity entity, out Point2 min, out Point2 max)
    {
        try
        {
            if (entity is Insert insertEntity && insertEntity.Block is { } insertBlock)
            {
                // Insert.GetBoundingBox() re-measures EVERY entity of the block (recursively) on each call, so 568 inserts of one
                // block measured it 568 times. Measure the block once and transform its 4 corners per INSERT.
                var blockBox = MeasureBlockBounds(insertBlock);
                var blockOk = blockBox.Extent is BoundingBoxExtent.Finite or BoundingBoxExtent.Point
                    && double.IsFinite(blockBox.Min.X) && double.IsFinite(blockBox.Min.Y) && double.IsFinite(blockBox.Max.X) && double.IsFinite(blockBox.Max.Y);
                if (!blockOk) { min = max = new Point2(0, 0); return false; }
                var transform = insertEntity.GetTransform();
                double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
                for (var corner = 0; corner < 4; corner++)
                {
                    var c = transform.ApplyTransform(new XYZ((corner & 1) == 0 ? blockBox.Min.X : blockBox.Max.X, (corner & 2) == 0 ? blockBox.Min.Y : blockBox.Max.Y, blockBox.Min.Z));
                    minX = Math.Min(minX, c.X); maxX = Math.Max(maxX, c.X); minY = Math.Min(minY, c.Y); maxY = Math.Max(maxY, c.Y);
                }
                var finite = double.IsFinite(minX) && double.IsFinite(minY) && double.IsFinite(maxX) && double.IsFinite(maxY);
                min = finite ? new Point2(minX, minY) : new Point2(0, 0);
                max = finite ? new Point2(maxX, maxY) : new Point2(0, 0);
                return finite;
            }
            var bounds = entity.GetBoundingBox();
            var valid = bounds.Extent is BoundingBoxExtent.Finite or BoundingBoxExtent.Point
                && double.IsFinite(bounds.Min.X) && double.IsFinite(bounds.Min.Y)
                && double.IsFinite(bounds.Max.X) && double.IsFinite(bounds.Max.Y);
            min = valid ? new Point2(bounds.Min.X, bounds.Min.Y) : new Point2(0, 0);
            max = valid ? new Point2(bounds.Max.X, bounds.Max.Y) : new Point2(0, 0);
            return valid;
        }
        catch
        {
            min = max = new Point2(0, 0);
            return false;
        }
    }

    private static bool TryGetProxyExtents(Entity entity, out Point2 min, out Point2 max, CancellationToken cancellationToken)
    {
        foreach (var extents in entity.ProxyGeometries.OfType<ProxyExtents>())
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!double.IsFinite(extents.Min.X) || !double.IsFinite(extents.Min.Y)
                || !double.IsFinite(extents.Max.X) || !double.IsFinite(extents.Max.Y)) continue;
            min = new Point2(Math.Min(extents.Min.X, extents.Max.X), Math.Min(extents.Min.Y, extents.Max.Y));
            max = new Point2(Math.Max(extents.Min.X, extents.Max.X), Math.Max(extents.Min.Y, extents.Max.Y));
            return true;
        }
        min = max = new Point2(0, 0);
        return false;
    }

    private sealed class DocumentContext(CadDocument document)
    {
        public CadDocument Document { get; } = document;
        public object SyncRoot { get; } = new();
        public Dictionary<Guid, Entity> MappedEntities { get; } = new();
        /// <summary>Parametric data read from DIMENSIONs of the opened file; if it is unchanged the original native dimension is kept untouched.</summary>
        public Dictionary<Guid, CadDimensionData> ImportedDimensions { get; } = new();
        /// <summary>One in-app DIMENSION object that is stored as a native dimension (or, as fallback, several native entities).</summary>
        public Dictionary<Guid, (CadDimensionData Data, List<Entity> Natives)> DimensionNatives { get; } = new();
        /// <summary>One in-app QLEADER object (CadLeaderData.IsMultiLeader == false), stored as a native LEADER entity plus its (unlinked) annotation MTEXT.</summary>
        public Dictionary<Guid, (CadLeaderData Data, List<Entity> Natives)> LeaderNatives { get; } = new();
        /// <summary>One in-app MLEADER object (CadLeaderData.IsMultiLeader == true), stored as a true native MULTILEADER entity.
        /// Import of an existing native MULTILEADER (from this app's own earlier save, or from real AutoCAD) is not
        /// implemented: it is kept in the file untouched (never corrupted/downgraded on save) but shows as a generic,
        /// non-parametric placeholder object in this app for that session - same disclosed limitation as TABLE.</summary>
        public Dictionary<Guid, (CadLeaderData Data, MultiLeader Native)> MultiLeaderNatives { get; } = new();
        /// <summary>One in-app TABLE object, stored as a real native TableEntity (post-2008 AcDbTable) plus the small
        /// anonymous BlockRecord every TableEntity must own (TableEntity : Insert). DWG-only - the DXF writer drops it.</summary>
        public Dictionary<Guid, (CadTableData Data, TableEntity Native, BlockRecord Block)> TableNatives { get; } = new();
        public Dictionary<Guid, (CadViewportData Data, Viewport Native)> ViewportNatives { get; } = new();
        public Dictionary<string, BlockRecord> SpaceRecords { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
