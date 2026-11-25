using System.Globalization;
using System.Text.Json;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1;

/// <summary>FILLET / CHAMFER like AutoCAD: pick-first or pick-after, pick or window, radius typed on the command line (R), latest value remembered.</summary>
public partial class MainWindow
{
    // ---- Remembered inputs (survive restarts) ----
    private static Dictionary<string, string>? s_lastInputs;
    private static string LastInputsPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iStCAD_1", "last-inputs.json");

    private static Dictionary<string, string> LastInputs()
    {
        if (s_lastInputs is not null) return s_lastInputs;
        try { s_lastInputs = JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(LastInputsPath)); } catch { }
        return s_lastInputs ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static string? GetLastInput(string key) => LastInputs().TryGetValue(key, out var value) ? value : null;

    private static void SetLastInput(string key, string value)
    {
        var inputs = LastInputs();
        if (inputs.TryGetValue(key, out var existing) && existing == value) return;
        inputs[key] = value;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LastInputsPath)!);
            System.IO.File.WriteAllText(LastInputsPath, JsonSerializer.Serialize(inputs));
        }
        catch { }
    }

    // ---- State ----
    private bool _cornerFillet = true;
    private bool _cornerAwaitingValue;
    private bool _cornerTrim = true;
    private CadEntity[] _cornerUndoBefore = Array.Empty<CadEntity>();
    private CadEntity[] _cornerUndoAfter = Array.Empty<CadEntity>();
    private readonly Dictionary<Guid, Point2> _cornerPicks = new();

    private string CornerKey => _cornerFillet ? "fillet.radius" : "chamfer.distance";
    private string CornerLabel => _cornerFillet ? "Radius" : "Distance";
    private double CornerValue => double.TryParse(GetLastInput(CornerKey), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : (_cornerFillet ? 0 : 25);
    private static string Fmt(double v) => v.ToString("0.####", CultureInfo.CurrentCulture);

    private LineEntity[] SelectedLinesOnly() =>
        _selectedEntityIds.Select(id => _spatialIndex.TryGetEntity(id, out var e) ? e : null).OfType<LineEntity>().ToArray();

    private void BeginCorner(bool fillet)
    {
        _cornerFillet = fillet; _cornerAwaitingValue = false; _cornerTrim = true; _cornerPicks.Clear();
        _cornerUndoBefore = Array.Empty<CadEntity>(); _cornerUndoAfter = Array.Empty<CadEntity>();
        var name = fillet ? "FILLET" : "CHAMFER";
        AppendCommandHistory($"{name}: current {CornerLabel.ToLowerInvariant()} = {Fmt(CornerValue)}. Type {(fillet ? "R" : "D")} to change it, T for Trim/No trim, U to Undo the last one.");
        var preselected = SelectedLinesOnly();
        if (preselected.Length == 2 && _selectedEntityIds.Count == 2) { ApplyCorner(preselected[0], preselected[1]); return; } // objects selected first
        if (_selectedEntityIds.Count == 1 && SelectedGeometry().OfType<PolylineEntity>().FirstOrDefault() is { } preselectedPolyline) { TryCornerPolyline(preselectedPolyline); return; } // one polyline: all its corners
        if (preselected.Length != 1 || _selectedEntityIds.Count != 1) _selectedEntityIds.Clear();
        _pendingSelectionAction = SelectionAction.Corner;
        AppendCommandHistory($"{name}: select the first object or [{CornerLabel}]: pick two LINEs (or sweep a window/crossing over them), or pick one POLYLINE to treat all its corners (Enter = done, Esc = cancel).");
        DrawingCanvas.Focus(); RefreshPropertiesPalette(); RenderSelectionOverlay();
    }

    /// <summary>Called after every pick/sweep while a selection-driven command is pending.</summary>
    private void AfterPendingSelectionChanged(CadEntity? picked)
    {
        if (_pendingSelectionAction == SelectionAction.Corner)
        {
            if (picked is PolylineEntity pickedPolyline && _selectedEntityIds.Count == 1)
            {
                // AutoCAD's Polyline option: one polyline picked = fillet / chamfer every corner of it.
                if (TryCornerPolyline(pickedPolyline)) _pendingSelectionAction = SelectionAction.None;
                else { _selectedEntityIds.Clear(); RenderSelectionOverlay(); }
                return;
            }
            if (picked is not null) _cornerPicks[picked.Id] = ScreenToWorld(_lastPointer);
            var lines = SelectedLinesOnly();
            if (_cornerFillet && _selectedEntityIds.Count == 2 && lines.Length < 2 && SelectedGeometry() is { Length: 2 } pair &&
                pair.All(e => e is LineEntity or ArcEntity) && _cornerPicks.TryGetValue(pair[0].Id, out var pickA) && _cornerPicks.TryGetValue(pair[1].Id, out var pickB))
            {
                // FILLET with an arc: tangent fillet arc, both objects trimmed or extended to it.
                _pendingSelectionAction = SelectionAction.None;
                if (!FilletCurves(pair[0], pickA, pair[1], pickB)) { _selectedEntityIds.Clear(); RenderSelectionOverlay(); }
                return;
            }
            if (_selectedEntityIds.Count == 2 && lines.Length == 2) { _pendingSelectionAction = SelectionAction.None; ApplyCorner(lines[0], lines[1]); }
            else if (_selectedEntityIds.Count == 1) AppendCommandHistory("Select second object:");
        }
        else if (_pendingSelectionAction == SelectionAction.Hatch && picked is not null)
        {
            _pendingSelectionAction = SelectionAction.None; HatchSelected();
        }
    }

    private void ApplyCornerFromSelection()
    {
        var lines = SelectedLinesOnly();
        if (lines.Length != 2 || _selectedEntityIds.Count != 2) { AppendCommandHistory($"{(_cornerFillet ? "FILLET" : "CHAMFER")}: select exactly two LINE objects."); return; }
        ApplyCorner(lines[0], lines[1]);
    }

    /// <summary>Command-line input while FILLET/CHAMFER waits for objects: R/D then a number, "R5", or just a number.</summary>
    private bool TryHandleCornerInput(string command)
    {
        if (_pendingSelectionAction != SelectionAction.Corner) { _cornerAwaitingValue = false; return false; }
        var text = command.Trim();
        if (_cornerAwaitingValue)
        {
            _cornerAwaitingValue = false;
            if (text.Length == 0) { AppendCommandHistory($"{CornerLabel} = {Fmt(CornerValue)}"); return true; }
            SetCornerValue(text); return true;
        }
        if (text.Length == 0) return false; // Enter = finish selection
        var upper = text.ToUpperInvariant();
        if (upper is "T" or "TRIM" or "NOTRIM")
        {
            _cornerTrim = !_cornerTrim;
            AppendCommandHistory(_cornerTrim
                ? $"Trim mode = ON (the two lines are shortened to the corner)."
                : $"Trim mode = OFF (the two lines stay as they are; only the {(_cornerFillet ? "arc" : "chamfer line")} is added).");
            return true;
        }
        if (upper is "U" or "UNDO")
        {
            UndoLastCorner();
            return true;
        }
        var key = _cornerFillet ? "R" : "D";
        if (upper == key || upper == "RADIUS" || upper == "DISTANCE" || upper == "BK")
        {
            _cornerAwaitingValue = true;
            AppendCommandHistory($"Specify {(_cornerFillet ? "fillet radius" : "chamfer distance")} <{Fmt(CornerValue)}>:");
            return true;
        }
        if (upper.StartsWith(key, StringComparison.Ordinal) && upper.Length > 1 && ParsePropertyNumber(upper[1..], out _)) { SetCornerValue(upper[1..]); return true; }
        if (ParsePropertyNumber(text, out _)) { SetCornerValue(text); return true; }
        _pendingSelectionAction = SelectionAction.None; // another command was typed: it replaces FILLET/CHAMFER
        return false;
    }

    private void SetCornerValue(string text)
    {
        if (!ParsePropertyNumber(text, out var value) || value < 0) { AppendCommandHistory($"{CornerLabel} must be a number >= 0."); return; }
        SetLastInput(CornerKey, value.ToString("R", CultureInfo.InvariantCulture));
        AppendCommandHistory($"{CornerLabel} = {Fmt(value)}  (remembered). Now select the objects.");
    }

    private void ApplyCorner(LineEntity a, LineEntity b)
    {
        var name = _cornerFillet ? "FILLET" : "CHAMFER";
        var value = CornerValue;
        var r1 = new Point2(a.End.X - a.Start.X, a.End.Y - a.Start.Y); var r2 = new Point2(b.End.X - b.Start.X, b.End.Y - b.Start.Y);
        var denom = r1.X * r2.Y - r1.Y * r2.X;
        if (Math.Abs(denom) < 1e-10 * Math.Max(1, Math.Abs(r1.X * r2.Y))) { AppendCommandHistory($"{name}: the lines are parallel; no corner can be built."); return; }
        var sx = b.Start.X - a.Start.X; var sy = b.Start.Y - a.Start.Y;
        var t = (sx * r2.Y - sy * r2.X) / denom;
        var corner = new Point2(a.Start.X + t * r1.X, a.Start.Y + t * r1.Y);

        // The part of each line that is kept: the side where it was picked, else the longer side.
        Point2 Far(LineEntity line)
        {
            if (_cornerPicks.TryGetValue(line.Id, out var pick))
            {
                var ds = (pick.X - corner.X) * (line.Start.X - corner.X) + (pick.Y - corner.Y) * (line.Start.Y - corner.Y);
                var de = (pick.X - corner.X) * (line.End.X - corner.X) + (pick.Y - corner.Y) * (line.End.Y - corner.Y);
                return ds >= de ? line.Start : line.End;
            }
            return Point2.Distance(line.Start, corner) >= Point2.Distance(line.End, corner) ? line.Start : line.End;
        }
        var farA = Far(a); var farB = Far(b);
        var lenA = Point2.Distance(farA, corner); var lenB = Point2.Distance(farB, corner);
        if (lenA < 1e-9 || lenB < 1e-9) { AppendCommandHistory($"{name}: a line ends exactly at the corner."); return; }
        var dA = new Point2((farA.X - corner.X) / lenA, (farA.Y - corner.Y) / lenA);
        var dB = new Point2((farB.X - corner.X) / lenB, (farB.Y - corner.Y) / lenB);
        var cross = dA.X * dB.Y - dA.Y * dB.X; var dot = dA.X * dB.X + dA.Y * dB.Y;
        var theta = Math.Atan2(Math.Abs(cross), dot); // angle between the kept parts, 0..pi
        var cut = value <= 1e-12 ? 0 : _cornerFillet ? value / Math.Tan(theta / 2) : value;
        if (cut >= lenA - 1e-9 || cut >= lenB - 1e-9) { AppendCommandHistory($"{name}: {CornerLabel.ToLowerInvariant()} {Fmt(value)} is too large for these lines."); return; }
        var ta = new Point2(corner.X + dA.X * cut, corner.Y + dA.Y * cut);
        var tb = new Point2(corner.X + dB.X * cut, corner.Y + dB.Y * cut);
        var outA = farA == a.Start ? a with { End = ta } : a with { Start = ta };
        var outB = farB == b.Start ? b with { End = tb } : b with { Start = tb };
        var added = new List<CadEntity>();
        if (value > 1e-12)
        {
            if (_cornerFillet)
            {
                var bx = dA.X + dB.X; var by = dA.Y + dB.Y; var bl = Math.Sqrt(bx * bx + by * by);
                var centerDistance = value / Math.Sin(theta / 2);
                var center = new Point2(corner.X + bx / bl * centerDistance, corner.Y + by / bl * centerDistance);
                double Angle(Point2 p) => (Math.Atan2(p.Y - center.Y, p.X - center.X) * 180 / Math.PI + 360) % 360;
                var counterClockwise = (ta.X - center.X) * (tb.Y - center.Y) - (ta.Y - center.Y) * (tb.X - center.X) > 0;
                var arc = new ArcEntity(center, value, counterClockwise ? Angle(ta) : Angle(tb), counterClockwise ? Angle(tb) : Angle(ta), Guid.NewGuid(), a.Layer)
                { ExplicitColor = a.ExplicitColor, DisplayColor = a.DisplayColor, LineTypePattern = a.LineTypePattern, LineTypeScale = a.LineTypeScale };
                added.Add(arc);
            }
            else if (Point2.Distance(ta, tb) > 1e-9)
                added.Add(new LineEntity(ta, tb, Guid.NewGuid(), a.Layer) { ExplicitColor = a.ExplicitColor, DisplayColor = a.DisplayColor, LineTypePattern = a.LineTypePattern, LineTypeScale = a.LineTypeScale });
        }
        // Trim mode (default, like AutoCAD's default): the two lines are shortened to the corner. No trim: the lines stay full-length, only the blend is added.
        var before = _cornerTrim ? new CadEntity[] { a, b } : Array.Empty<CadEntity>();
        var after = _cornerTrim ? new List<CadEntity> { outA, outB } : new List<CadEntity>();
        after.AddRange(added);
        _editor.ApplyEntityChanges(before, after);
        _cornerUndoBefore = before; _cornerUndoAfter = after.ToArray();
        _selectedEntityIds.Clear(); foreach (var e in after) _selectedEntityIds.Add(e.Id);
        _cornerPicks.Clear();
        RefreshPropertiesPalette(); RenderSelectionOverlay();
        AppendCommandHistory($"{name}: {CornerLabel.ToLowerInvariant()} {Fmt(value)} applied{(value <= 1e-12 ? " (lines joined at the corner)" : "")}. Type U to undo just this one.");
        DrawingCanvas.Focus();
    }

    /// <summary>Undoes the most recent FILLET/CHAMFER applied in this command session (type U while selecting the next pair).</summary>
    private void UndoLastCorner()
    {
        if (_cornerUndoBefore.Length == 0 && _cornerUndoAfter.Length == 0) { AppendCommandHistory("FILLET/CHAMFER: nothing to undo yet."); return; }
        _editor.ApplyEntityChanges(_cornerUndoAfter, _cornerUndoBefore);
        _selectedEntityIds.Clear(); foreach (var e in _cornerUndoBefore) _selectedEntityIds.Add(e.Id);
        _cornerUndoBefore = Array.Empty<CadEntity>(); _cornerUndoAfter = Array.Empty<CadEntity>();
        RefreshPropertiesPalette(); RenderSelectionOverlay();
        AppendCommandHistory("FILLET/CHAMFER: last one undone.");
    }
}
