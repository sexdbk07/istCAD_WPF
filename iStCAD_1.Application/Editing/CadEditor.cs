using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1.Application.Editing;

public sealed class CadEditor
{
    private const int MaxHistorySnapshots = 20;
    private const int MaxHistoryEntityReferences = 1_500_000;
    private readonly Stack<CadDrawing> _undo = new();
    private readonly Stack<CadDrawing> _redo = new();
    public CadDrawing Drawing { get; private set; } = new();
    public bool IsLineActive { get; private set; }
    public bool IsCircleActive { get; private set; }
    public Point2? CircleCenter { get; private set; }
    public Point2? LineStart { get; private set; }
    public Point2? PreviewPoint { get; private set; }
    public event EventHandler? DrawingChanged;
    private (CadEntity[] Removed, CadEntity[] Added)? _pendingDelta;
    /// <summary>Entity delta of the change being signalled (null when the whole drawing changed: undo, open, new).</summary>
    public (CadEntity[] Removed, CadEntity[] Added)? LastEntityDelta { get; private set; }
    private void RaiseChanged()
    {
        LastEntityDelta = _pendingDelta; _pendingDelta = null;
        DrawingChanged?.Invoke(this, EventArgs.Empty);
    }
    private static CadColor? ResolveExplicitColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "ByLayer", StringComparison.OrdinalIgnoreCase)) return null;
        return value.Trim() switch
        {
            "Red" => new CadColor(255, 90, 90),
            "Yellow" => new CadColor(255, 220, 80),
            "Green" => new CadColor(95, 220, 125),
            "Cyan" => new CadColor(85, 215, 225),
            "Blue" => new CadColor(110, 140, 255),
            "White" => new CadColor(235, 235, 235),
            _ => TryParseRgb(value, out var rgb) ? rgb : null
        };
    }

    private static bool TryParseRgb(string value, out CadColor color)
    {
        color = default;
        var t = value.Trim();
        if (t.StartsWith("#") && t.Length == 7)
        {
            try
            {
                var r = Convert.ToByte(t.Substring(1, 2), 16);
                var g = Convert.ToByte(t.Substring(3, 2), 16);
                var b = Convert.ToByte(t.Substring(5, 2), 16);
                color = new CadColor(r, g, b);
                return true;
            }
            catch { return false; }
        }
        var parts = t.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && byte.TryParse(parts[0], out var r2) && byte.TryParse(parts[1], out var g2) && byte.TryParse(parts[2], out var b2))
        {
            color = new CadColor(r2, g2, b2);
            return true;
        }
        return false;
    }

    /// <summary>Gives a newly created object the current layer, color, linetype and text style (ByLayer values stay ByLayer and are resolved from the layer).</summary>
    public CadEntity StyleNew(CadEntity entity) => ApplyCurrentStyle(entity);

    private CadEntity ApplyCurrentStyle(CadEntity entity)
    {
        var layer = Drawing.CurrentLayer;
        var byBlock = string.Equals(Drawing.CurrentColor, "ByBlock", StringComparison.OrdinalIgnoreCase);
        var explicitColor = byBlock ? null : ResolveExplicitColor(Drawing.CurrentColor);
        if (explicitColor is null && !byBlock && Drawing.CurrentColor.Contains(','))
        {
            var parts = Drawing.CurrentColor.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 3 && byte.TryParse(parts[0], out var r) && byte.TryParse(parts[1], out var g) && byte.TryParse(parts[2], out var b))
                explicitColor = new CadColor(r, g, b);
        }
        var byLayerLine = string.IsNullOrWhiteSpace(Drawing.CurrentLineStyle) || Drawing.CurrentLineStyle.Equals("ByLayer", StringComparison.OrdinalIgnoreCase);
        var styleName = byLayerLine
            ? (Drawing.LayerStates.TryGetValue(layer, out var layerState) ? layerState.LineType : "Continuous")
            : Drawing.CurrentLineStyle;
        var pattern = Drawing.LineStyles.TryGetValue(styleName ?? "Continuous", out var definition) && definition.Pattern.Count > 0 ? definition.Pattern.ToArray() : null;
        return entity with { Layer = layer, ExplicitColor = explicitColor, ColorByBlock = byBlock, LineTypePattern = pattern,
            LineTypeName = byLayerLine ? null : Drawing.CurrentLineStyle,
            LineTypeScale = Drawing.LtScale > 0 ? Drawing.LtScale : 1,
            TextFontFamily = Drawing.TextStyles.TryGetValue(Drawing.CurrentTextStyle, out var text) ? text.FontFamily : "Arial" };
    }

    public void NewDrawing(string name = "Drawing1")
    {
        Drawing = new CadDrawing { Name = name };
        _undo.Clear(); _redo.Clear(); IsLineActive = false; IsCircleActive = false; LineStart = null; CircleCenter = null; PreviewPoint = null;
        RaiseChanged();
    }

    public void SetDrawing(CadDrawing drawing)
    {
        Drawing = drawing; _undo.Clear(); _redo.Clear(); IsLineActive = false; IsCircleActive = false; LineStart = null; CircleCenter = null; PreviewPoint = null;
        RaiseChanged();
    }

    public void BeginLine() { IsCircleActive = false; CircleCenter = null; IsLineActive = true; LineStart = null; PreviewPoint = null; }
    public void BeginCircle() { IsLineActive = false; LineStart = null; IsCircleActive = true; CircleCenter = null; PreviewPoint = null; }
    public void AddCircleCenter(Point2 center) { if (IsCircleActive) { CircleCenter = center; PreviewPoint = center; } }
    public bool AddCircleRadius(double radius)
    {
        if (!IsCircleActive || CircleCenter is null || radius <= 0) return false;
        SaveUndo();
        var newCircle = ApplyCurrentStyle(new CircleEntity(CircleCenter.Value, radius, Guid.NewGuid()));
        Drawing.Add(newCircle); _pendingDelta = (Array.Empty<CadEntity>(), new CadEntity[] { newCircle });
        IsCircleActive = false; CircleCenter = null; PreviewPoint = null;
        RaiseChanged();
        return true;
    }
    public void MovePointer(Point2 point) { PreviewPoint = point; }
    public bool AddLinePoint(Point2 point)
    {
        if (!IsLineActive) return false;
        if (LineStart is null) { LineStart = point; PreviewPoint = point; return false; }
        SaveUndo();
        var newLine = ApplyCurrentStyle(new LineEntity(LineStart.Value, point, Guid.NewGuid()));
        Drawing.Add(newLine); _pendingDelta = (Array.Empty<CadEntity>(), new CadEntity[] { newLine });
        LineStart = point; PreviewPoint = point;
        RaiseChanged();
        return true;
    }
    public void EndCommand() { IsLineActive = false; IsCircleActive = false; LineStart = null; CircleCenter = null; PreviewPoint = null; }
    public void ApplyEntityChanges(IEnumerable<CadEntity> remove, IEnumerable<CadEntity> add)
    {
        var removeItems = remove.ToArray();
        var addItems = add.ToArray();
        if (removeItems.Length == 0 && addItems.Length == 0) return;
        SaveUndo();
        Drawing.RemoveMany(removeItems);
        foreach (var entity in addItems) Drawing.Add(entity);
        _pendingDelta = (removeItems, addItems);
        RaiseChanged();
    }
    public void Undo()
    {
        if (_undo.Count == 0) return;
        PushBounded(_redo, Drawing.Snapshot());
        Drawing = _undo.Pop(); RaiseChanged();
    }
    public void Redo()
    {
        if (_redo.Count == 0) return;
        PushBounded(_undo, Drawing.Snapshot());
        Drawing = _redo.Pop(); RaiseChanged();
    }
    private void SaveUndo() { PushBounded(_undo, Drawing.Snapshot()); _redo.Clear(); }

    private static void PushBounded(Stack<CadDrawing> history, CadDrawing snapshot)
    {
        history.Push(snapshot);
        var snapshotLimit = Math.Clamp(MaxHistoryEntityReferences / Math.Max(1, snapshot.Entities.Count), 1, MaxHistorySnapshots);
        if (history.Count <= snapshotLimit) return;
        var newest = history.Take(snapshotLimit).ToArray();
        history.Clear();
        for (var index = newest.Length - 1; index >= 0; index--) history.Push(newest[index]);
    }
}
