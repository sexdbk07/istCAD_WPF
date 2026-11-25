using iStCAD_1.Core.Geometry;

namespace iStCAD_1.Core.Model;

public sealed class CadDrawing
{
    private readonly Dictionary<string, List<CadEntity>> _spaces = new(StringComparer.OrdinalIgnoreCase) { ["Model"] = [] };
    private readonly Dictionary<Guid, string> _entitySpaces = [];
    private string _activeSpace = "Model";
    public string Name { get; set; } = "Drawing1";
    public object? NativeContext { get; set; }
    public int UnsupportedEntityCount { get; set; }
    public int NativeEntityCount { get; set; }
    public int DocumentEntityCount { get; set; }
    public CadOpenMetrics? OpenMetrics { get; set; }
    /// <summary>Entity type counts for Model Space only (not block definitions or Paper Space).</summary>
    public Dictionary<string, int> NativeEntityTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Top-level entity type counts for each layout/space, keyed by its displayed name.</summary>
    public Dictionary<string, Dictionary<string, int>> NativeEntityTypesBySpace { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Top-level Model Space entity counts grouped by layer.</summary>
    public Dictionary<string, int> NativeModelSpaceLayerCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public CadDatabaseSummary? DatabaseSummary { get; set; }
    public Dictionary<string, CadColor> LayerColors { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, CadTextStyleDefinition> TextStyles { get; } = new(StringComparer.OrdinalIgnoreCase) { ["Standard"] = new("Standard", "Arial", 2.5, 1) };
    public Dictionary<string, CadDimensionStyleDefinition> DimensionStyles { get; } = new(StringComparer.OrdinalIgnoreCase) { ["ISO-25"] = new("ISO-25", 2.5, 2.5, 1) };
    public Dictionary<string, CadLineStyleDefinition> LineStyles { get; } = new(StringComparer.OrdinalIgnoreCase) { ["Continuous"] = new("Continuous", Array.Empty<double>()), ["Dashed"] = new("Dashed", new double[]{6,3}), ["Dotted"] = new("Dotted", new double[]{1,2}), ["Center"] = new("Center", new double[]{9,3,2,3}) };
    /// <summary>Layer table (state per layer name).</summary>
    public Dictionary<string, CadLayerState> LayerStates { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Paper size in mm per layout (after rotation).</summary>
    public Dictionary<string, (double Width, double Height)> LayoutPapers { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Model viewports per layout.</summary>
    public Dictionary<string, List<CadLayoutViewport>> LayoutViewports { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string CurrentLayer { get; set; } = "0";
    public string CurrentTextStyle { get; set; } = "Standard";
    public string CurrentDimensionStyle { get; set; } = "ISO-25";
    /// <summary>Global linetype scale (AutoCAD's LTSCALE): defaults to 100 so dashed/dotted lines read correctly at the standard 1:100 plot scale.
    /// Applied to every newly styled object (see CadEditor.ApplyCurrentStyle); the LTSCALE command also re-applies it to existing objects.</summary>
    public double LtScale { get; set; } = 100;
    public string CurrentColor { get; set; } = "ByLayer";
    public string CurrentLineStyle { get; set; } = "ByLayer";
    /// <summary>In-session named block definitions created via BLOCK (name -> base point + member entities, in block-local
    /// coordinates relative to that base point). Consulted by INSERT to offer the drawing's own blocks, and by the native
    /// save path to create a matching ACadSharp BlockRecord (with AttributeDefinition members) the first time it is referenced.</summary>
    public Dictionary<string, CadBlockDefinition> BlockDefinitions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ReadDiagnostics { get; } = [];
    public HashSet<Guid> HiddenEntityIds { get; } = [];
    public string ActiveSpaceName => _activeSpace;
    public IReadOnlyList<CadEntity> Entities => _spaces[_activeSpace];
    public IReadOnlyList<CadEntity> AllSpaceEntities => _spaces.Values.SelectMany(entities => entities).ToArray();
    public IReadOnlyList<string> SpaceNames => _spaces.Keys.ToArray();
    /// <summary>Entities of any space by name (empty when the space does not exist).</summary>
    public IReadOnlyList<CadEntity> GetSpaceEntities(string name) => _spaces.TryGetValue(name, out var list) ? list : Array.Empty<CadEntity>();
    /// <summary>Plot page setups per space name (Model or layout), kept for the session and saved beside the drawing.</summary>
    public Dictionary<string, PlotSetup> PlotSetups { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int GetSpaceEntityCount(string name) => _spaces.GetValueOrDefault(name)?.Count ?? 0;
    public void Add(CadEntity entity) { _spaces[_activeSpace].Add(entity); _entitySpaces[entity.Id] = _activeSpace; }
    public void SetSpaceEntities(string name, IEnumerable<CadEntity> entities)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Space name cannot be empty.", nameof(name));
        if (_spaces.TryGetValue(name, out var previous)) foreach (var entity in previous) _entitySpaces.Remove(entity.Id);
        _spaces[name] = entities.ToList();
        foreach (var entity in _spaces[name]) _entitySpaces[entity.Id] = name;
    }
    public bool ActivateSpace(string name)
    {
        if (!_spaces.ContainsKey(name)) _spaces[name] = [];
        _activeSpace = name;
        return true;
    }
    public string GetSpaceName(CadEntity entity) => _entitySpaces.GetValueOrDefault(entity.Id, _activeSpace);
    public void ReplaceAll(IEnumerable<CadEntity> entities)
    {
        foreach (var entity in _spaces[_activeSpace]) _entitySpaces.Remove(entity.Id);
        _spaces[_activeSpace].Clear();
        _spaces[_activeSpace].AddRange(entities);
        foreach (var entity in _spaces[_activeSpace]) _entitySpaces[entity.Id] = _activeSpace;
    }
    /// <summary>Removes many entities in one O(n) pass (the one-by-one Remove is O(n) per entity with structural record equality).</summary>
    public int RemoveMany(IEnumerable<CadEntity> entities)
    {
        var ids = new HashSet<Guid>();
        foreach (var entity in entities) ids.Add(entity.Id);
        if (ids.Count == 0) return 0;
        var list = _spaces[_activeSpace];
        var removed = list.RemoveAll(candidate => ids.Contains(candidate.Id));
        foreach (var id in ids) _entitySpaces.Remove(id);
        return removed;
    }
    public bool Remove(CadEntity entity)
    {
        var removed = _spaces[_activeSpace].Remove(entity);
        if (removed) _entitySpaces.Remove(entity.Id);
        return removed;
    }
    public CadDrawing Snapshot()
    {
        var copy = new CadDrawing { Name = Name, NativeContext = NativeContext, UnsupportedEntityCount = UnsupportedEntityCount, NativeEntityCount = NativeEntityCount, DocumentEntityCount = DocumentEntityCount, DatabaseSummary = DatabaseSummary, OpenMetrics = OpenMetrics };
        foreach (var pair in NativeEntityTypes) copy.NativeEntityTypes[pair.Key] = pair.Value;
        foreach (var space in NativeEntityTypesBySpace)
            copy.NativeEntityTypesBySpace[space.Key] = new Dictionary<string, int>(space.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in NativeModelSpaceLayerCounts) copy.NativeModelSpaceLayerCounts[pair.Key] = pair.Value;
        foreach (var pair in LayerColors) copy.LayerColors[pair.Key] = pair.Value;
        foreach(var item in TextStyles) copy.TextStyles[item.Key] = item.Value;
        foreach(var item in DimensionStyles) copy.DimensionStyles[item.Key] = item.Value;
        foreach(var item in LineStyles) copy.LineStyles[item.Key] = item.Value;
        copy.CurrentLayer = CurrentLayer; copy.CurrentTextStyle = CurrentTextStyle;
        copy.CurrentDimensionStyle = CurrentDimensionStyle; copy.CurrentColor = CurrentColor;
        copy.CurrentLineStyle = CurrentLineStyle;
        foreach (var item in PlotSetups) copy.PlotSetups[item.Key] = item.Value;
        foreach (var item in LayerStates) copy.LayerStates[item.Key] = item.Value.Clone();
        foreach (var item in LayoutPapers) copy.LayoutPapers[item.Key] = item.Value;
        foreach (var item in LayoutViewports) copy.LayoutViewports[item.Key] = item.Value.ToList();
        foreach (var item in BlockDefinitions) copy.BlockDefinitions[item.Key] = item.Value;
        copy.ReadDiagnostics.AddRange(ReadDiagnostics);
        copy.HiddenEntityIds.UnionWith(HiddenEntityIds);
        foreach (var space in _spaces) copy.SetSpaceEntities(space.Key, space.Value);
        copy.ActivateSpace(_activeSpace);
        return copy;
    }
}

public sealed record CadDatabaseSummary(int ReachableObjectCount, int LayerCount, int BlockRecordCount, int LayoutCount,
    int LineTypeCount, int TextStyleCount, int DimensionStyleCount, int UcsCount, int ViewCount, int ViewportCount,
    int RegisteredApplicationCount, int XDataOwnerCount, int XDataApplicationCount, int XDataRecordCount,
    int XRecordCount, int ExtensionDictionaryCount);

public sealed record CadOpenMetrics(TimeSpan ReadDuration, TimeSpan MappingDuration, int NativeObjectCount, int MappedEntityCount);

public sealed record CadTextStyleDefinition(string Name, string FontFamily, double Height, double WidthFactor);
public sealed record CadDimensionStyleDefinition(string Name, double TextHeight, double ArrowSize, double Scale,
    double ExtensionOffset = 0.625, double ExtensionExtend = 1.25, double TextGap = 0.625, int Decimals = 2);
public sealed record CadLineStyleDefinition(string Name, IReadOnlyList<double> Pattern);
/// <summary>A named block defined in-session via BLOCK: member entities stored in block-local coordinates (i.e. already
/// offset so that BasePoint maps to the origin), exactly as AutoCAD stores a BlockRecord's own entity list.</summary>
public sealed record CadBlockDefinition(string Name, Point2 BasePoint, IReadOnlyList<CadEntity> Entities);
