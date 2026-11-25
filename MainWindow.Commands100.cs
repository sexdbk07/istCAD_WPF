using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using iStCAD_1.Application.Input;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1;

/// <summary>
/// AutoCAD command set, batch 1 of the "100 common commands" work (see docs/AUTOCAD_COMMANDS.md):
/// layer tools (LAYOFF, LAYON, LAYFRZ, LAYTHW, LAYLCK, LAYULK, LAYISO, LAYUNISO, LAYMCUR, LAYCUR), OOPS, status toggles typed as commands,
/// AutoCAD command names that were missing as aliases, CIRCLE Ttr, FILLET with arcs, TRIM / EXTEND fence, MOVE / COPY displacement.
/// </summary>
public partial class MainWindow
{
    private CadEntity[] _lastErased = Array.Empty<CadEntity>();
    private Dictionary<string, (bool On, bool Frozen, bool Locked)>? _layerIsolateSaved;
    private Point2? _fenceStart;
    private bool _transformDisplacement;

    /// <summary>Commands added for AutoCAD parity. Returns true when the command was handled here.</summary>
    private bool TryHandleAutoCadCommand(string name)
    {
        switch (name)
        {
            // ---- layer tools ---------------------------------------------------------------------------------------------
            case "LAYOFF":
                LayerPickCommand("LAYOFF", "Select an object on the layer to be turned off:", repeat: true, layer =>
                {
                    if (IsCurrentLayer(layer)) return $"Layer \"{layer}\" is the current layer and stays on (make another layer current first).";
                    SetLayerStates(states => LayerState(states, layer).On = false);
                    return $"Layer \"{layer}\" has been turned off.";
                });
                return true;
            case "LAYFRZ":
                LayerPickCommand("LAYFRZ", "Select an object on the layer to be frozen:", repeat: true, layer =>
                {
                    if (IsCurrentLayer(layer)) return $"Layer \"{layer}\" is the current layer and cannot be frozen.";
                    SetLayerStates(states => LayerState(states, layer).Frozen = true);
                    return $"Layer \"{layer}\" has been frozen.";
                });
                return true;
            case "LAYLCK":
                LayerPickCommand("LAYLCK", "Select an object on the layer to be locked:", repeat: false, layer =>
                {
                    SetLayerStates(states => LayerState(states, layer).Locked = true);
                    return $"Layer \"{layer}\" has been locked.";
                });
                return true;
            case "LAYULK":
                LayerPickCommand("LAYULK", "Select an object on the layer to be unlocked:", repeat: false, layer =>
                {
                    SetLayerStates(states => LayerState(states, layer).Locked = false);
                    return $"Layer \"{layer}\" has been unlocked.";
                }, includeLocked: true);
                return true;
            case "LAYMCUR":
                LayerPickCommand("LAYMCUR", "Select object whose layer will become current:", repeat: false, layer =>
                {
                    _editor.Drawing.CurrentLayer = layer;
                    RefreshCadStyles(); RefreshPropertiesPalette();
                    return $"{layer} is now the current layer.";
                }, includeLocked: true);
                return true;
            case "LAYON":
                SetLayerStates(states => { foreach (var s in states.Values) s.On = true; });
                AppendCommandHistory("All layers have been turned on.");
                return true;
            case "LAYTHW":
                SetLayerStates(states => { foreach (var s in states.Values) s.Frozen = false; });
                AppendCommandHistory("All layers have been thawed.");
                return true;
            case "LAYISO": LayerIsolateCommand(); return true;
            case "LAYUNISO": LayerUnisolateCommand(); return true;
            case "LAYCUR": LayerToCurrentCommand(); return true;

            // ---- OOPS: bring back what the last ERASE removed ----------------------------------------------------------
            case "OOPS":
                if (_lastErased.Length == 0) { AppendCommandHistory("OOPS: nothing to restore (no objects were erased)."); return true; }
                var existing = _editor.Drawing.Entities.Select(e => e.Id).ToHashSet();
                var restore = _lastErased.Where(e => !existing.Contains(e.Id)).ToArray();
                _lastErased = Array.Empty<CadEntity>();
                if (restore.Length == 0) { AppendCommandHistory("OOPS: the erased objects are already back."); return true; }
                _editor.ApplyEntityChanges(Array.Empty<CadEntity>(), restore);
                RenderDrawing(false);
                AppendCommandHistory($"OOPS: {restore.Length} erased object(s) restored.");
                return true;

            // ---- drafting settings typed as commands -------------------------------------------------------------------
            case "ORTHO": ToggleStatus(StatusOrthoToggle); return true;
            case "OSNAP": case "OS": case "-OSNAP": ToggleStatus(StatusOsnapToggle); return true;
            case "GRID":
                GridToggleButton.IsChecked = GridToggleButton.IsChecked != true;
                GridToggle_Click(GridToggleButton, new RoutedEventArgs());
                return true;

            // ---- AutoCAD names for commands that already exist ----------------------------------------------------------
            case "QSAVE": ProcessCommand("SAVE"); return true;
            case "REDRAW": case "REDRAWALL": case "RE": case "REGENALL": case "REA": case "R": ProcessCommand("REGEN"); return true;
            case "RECTANG": ProcessCommand("RECTANGLE"); return true;
            case "DTEXT": ProcessCommand("TEXT"); return true;
            case "MT": ProcessCommand("MTEXT"); return true;
            case "BHATCH": case "BH": ProcessCommand("HATCH"); return true;
            case "CP": ProcessCommand("COPY"); return true;
            case "ME": ProcessCommand("MEASURE"); return true;
            case "LS": ProcessCommand("LIST"); return true;
            case "CH": case "MO": case "PROPS": case "DDMODIFY": ProcessCommand("PROPERTIES"); return true;
            case "DDIM": ProcessCommand("DIMSTYLE"); return true;
            case "DCE": case "DIMCEN": DimCenterCommand(); return true;
            case "SELECTALL": case "AI_SELALL": SelectAllVisible(); return true;
            case "COLOR": case "COL": ColorCommand(); return true;
            case "LTSCALE": case "LTS": LtScaleCommand(); return true;
        }
        return false;
    }

    /// <summary>LTSCALE (LTS): the drawing's global linetype scale (default 100, for the standard 1:100 plot). Updates every
    /// existing object too, so dashed/dotted lines read correctly right away - new objects already pick it up automatically.</summary>
    private void LtScaleCommand()
    {
        var d = _editor.Drawing;
        StartWizard(new Wizard
        {
            Name = "LTSCALE",
            Steps = new[] { new WizStep { Kind = WizKind.Number, Default = d.LtScale, Prompt = "Enter new linetype scale factor (100 = standard 1:100)" } },
            Commit = x =>
            {
                var value = x.D(0);
                if (value <= 0) { AppendCommandHistory("LTSCALE: the value must be positive."); return false; }
                d.LtScale = value;
                var from = d.AllSpaceEntities.Where(e => Math.Abs(e.LineTypeScale - value) > 1e-9).ToArray();
                if (from.Length > 0)
                {
                    var to = from.Select(e => e with { LineTypeScale = value }).ToArray();
                    _editor.ApplyEntityChanges(from, to);
                }
                RefreshCadStyles(); RefreshPropertiesPalette(); RenderDrawing(false);
                AppendCommandHistory($"LTSCALE: now {Fmt(value)} ({from.Length} existing object(s) updated to match).");
                return true;
            }
        });
    }

    /// <summary>DIMCENTER (DCE): draws a small cross at the center of a selected arc or circle.</summary>
    private void DimCenterCommand()
    {
        StartWizard(new Wizard
        {
            Name = "DIMCENTER",
            Steps = new[] { new WizStep { Kind = WizKind.Entity, Prompt = "Select arc or circle (Enter or Esc to end):", Accept = e => e is ArcEntity or CircleEntity } },
            RepeatFrom = 0,
            Commit = x =>
            {
                var (center, radius) = x.E(0) switch
                {
                    ArcEntity a => (a.Center, a.Radius),
                    CircleEntity c => (c.Center, c.Radius),
                    _ => (default(Point2), 0.0)
                };
                if (radius <= 1e-9) { AppendCommandHistory("DIMCENTER: select an arc or circle."); return true; }
                var size = Math.Max(radius * 0.08, 1.0);
                var layer = _editor.Drawing.CurrentLayer;
                var items = new List<CadEntity>
                {
                    new LineEntity(new Point2(center.X - size, center.Y), new Point2(center.X + size, center.Y), Guid.NewGuid(), layer),
                    new LineEntity(new Point2(center.X, center.Y - size), new Point2(center.X, center.Y + size), Guid.NewGuid(), layer)
                };
                AddNewEntities("DIMCENTER", items, "center mark added.");
                return true;
            }
        });
    }

    /// <summary>COLOR (COL): sets the current color by name/number/RGB, typed on the command line (no color-picker dialog).</summary>
    private void ColorCommand()
    {
        StartWizard(new Wizard
        {
            Name = "COLOR",
            Steps = new[]
            {
                new WizStep
                {
                    Kind = WizKind.Text, Default = _editor.Drawing.CurrentColor,
                    Prompt = "Enter a color name or number (ByLayer, Red, Yellow, Green, Cyan, Blue, White, or R,G,B)"
                }
            },
            Commit = x =>
            {
                var text = x.S(0).Trim();
                string? resolved = text.ToUpperInvariant() switch
                {
                    "" or "BYLAYER" => "ByLayer",
                    "1" or "RED" => "Red",
                    "2" or "YELLOW" => "Yellow",
                    "3" or "GREEN" => "Green",
                    "4" or "CYAN" => "Cyan",
                    "5" or "BLUE" => "Blue",
                    "7" or "WHITE" or "BLACK" => "White",
                    _ => null
                };
                if (resolved is null && TryResolveCadColorValue(text, out _, out _)) resolved = text;
                if (resolved is null) { AppendCommandHistory($"COLOR: unknown color \"{text}\". Use ByLayer, a color name, or R,G,B."); return false; }
                _editor.Drawing.CurrentColor = resolved;
                RefreshCadStyles(); RefreshPropertiesPalette();
                AppendCommandHistory($"COLOR: current color is now {resolved}.");
                return true;
            }
        });
    }

    private void ToggleStatus(System.Windows.Controls.Primitives.ToggleButton toggle)
    {
        toggle.IsChecked = toggle.IsChecked != true;
        DraftingToggle_Click(toggle, new RoutedEventArgs());
    }

    private void SelectAllVisible()
    {
        var d = _editor.Drawing;
        _selectedEntityIds.Clear();
        foreach (var e in d.Entities)
            if (!d.HiddenEntityIds.Contains(e.Id) && !IsLayerLocked(e.Layer)) _selectedEntityIds.Add(e.Id);
        RefreshPropertiesPalette(); RenderSelectionOverlay();
        AppendCommandHistory($"{_selectedEntityIds.Count} object(s) selected.");
    }

    // ---- layers ---------------------------------------------------------------------------------------------------------

    private bool IsCurrentLayer(string layer) => string.Equals(layer, _editor.Drawing.CurrentLayer, StringComparison.OrdinalIgnoreCase);

    private static CadLayerState LayerState(Dictionary<string, CadLayerState> states, string layer)
    {
        if (!states.TryGetValue(layer, out var state)) states[layer] = state = new CadLayerState { Name = layer };
        return state;
    }

    /// <summary>Changes layer on/freeze/lock states and refreshes what is visible (not an undo step, as in the Layer Manager).</summary>
    private void SetLayerStates(Action<Dictionary<string, CadLayerState>> change)
    {
        var d = _editor.Drawing;
        var hiddenBefore = d.LayerStates.Values.Where(s => !s.Visible).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        change(d.LayerStates);
        ApplyLayerVisibility(hiddenBefore);
        RefreshCadStyles(); RefreshPropertiesPalette();
        _activeSession.IsDirty = true; _activeSession.Revision++; RefreshDrawingTabs();
    }

    /// <summary>"Select an object on the layer to be ...": acts on the layer of each picked object.</summary>
    private void LayerPickCommand(string name, string prompt, bool repeat, Func<string, string> apply, bool includeLocked = false)
    {
        StartWizard(new Wizard
        {
            Name = name,
            Steps = new[] { new WizStep { Kind = WizKind.Entity, Prompt = prompt } },
            RepeatFrom = repeat ? 0 : -1,
            Commit = x =>
            {
                var layer = x.E(0).Layer;
                AppendCommandHistory(apply(layer));
                return true;
            }
        });
    }

    /// <summary>LAYISO: every layer except those of the selected objects is turned off; LAYUNISO puts the states back.</summary>
    private void LayerIsolateCommand()
    {
        RequireSelection("LAYISO", () =>
        {
            var keep = SelectedGeometry().Select(e => e.Layer).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (keep.Count == 0) { AppendCommandHistory("LAYISO: select objects on the layer(s) to be isolated."); return; }
            var d = _editor.Drawing;
            // The first isolation keeps the states to restore; isolating again narrows it without losing them.
            _layerIsolateSaved ??= d.LayerStates.ToDictionary(p => p.Key, p => (p.Value.On, p.Value.Frozen, p.Value.Locked), StringComparer.OrdinalIgnoreCase);
            var layerNames = d.AllSpaceEntities.Select(e => e.Layer).Concat(d.LayerStates.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var turnedOff = 0;
            SetLayerStates(states =>
            {
                foreach (var layer in layerNames)
                {
                    if (keep.Contains(layer)) continue;
                    var state = LayerState(states, layer);
                    if (state.On) { state.On = false; turnedOff++; }
                }
            });
            if (!keep.Contains(d.CurrentLayer)) { d.CurrentLayer = keep.First(); RefreshCadStyles(); }
            _selectedEntityIds.Clear(); RenderSelectionOverlay();
            AppendCommandHistory($"Layer {string.Join(", ", keep.Take(5))}{(keep.Count > 5 ? ", ..." : "")} isolated: {turnedOff} other layer(s) turned off. LAYUNISO restores them.");
        });
    }

    private void LayerUnisolateCommand()
    {
        if (_layerIsolateSaved is not { } saved) { AppendCommandHistory("LAYUNISO: no layers to restore; use LAYISO first."); return; }
        _layerIsolateSaved = null;
        SetLayerStates(states =>
        {
            foreach (var (layer, was) in saved)
            {
                var state = LayerState(states, layer);
                state.On = was.On; state.Frozen = was.Frozen; state.Locked = was.Locked;
            }
            // Layers that had no table row before the isolation were on.
            foreach (var state in states.Values) if (!saved.ContainsKey(state.Name)) state.On = true;
        });
        AppendCommandHistory("Layers isolated by the LAYISO command have been restored.");
    }

    /// <summary>LAYCUR: moves the selected objects to the current layer.</summary>
    private void LayerToCurrentCommand()
    {
        RequireSelection("LAYCUR", () =>
        {
            var current = _editor.Drawing.CurrentLayer;
            var sources = SelectedGeometry().Where(e => !string.Equals(e.Layer, current, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (sources.Length == 0) { AppendCommandHistory($"LAYCUR: the selected objects are already on layer \"{current}\"."); return; }
            var changed = sources.Select(e => e with { Layer = current }).ToArray();
            _editor.ApplyEntityChanges(sources, changed);
            RefreshPropertiesPalette(); RenderDrawing(false);
            AppendCommandHistory($"{sources.Length} object(s) changed to layer \"{current}\" (the current layer).");
        });
    }

    // ---- MOVE / COPY / STRETCH: Displacement ---------------------------------------------------------------------------

    /// <summary>"Specify base point or [Displacement]": D asks for a displacement vector instead of two points.</summary>
    private bool TryHandleDisplacementOption(string text, string key)
    {
        if (!_transformActive) { _transformDisplacement = false; return false; }
        if (_transformBasePoint is null && !_transformDisplacement && key is "D" or "DISPLACEMENT")
        {
            _transformDisplacement = true; DynamicValueTextBox.Clear();
            DynamicPromptText.Text = "Specify displacement <0,0>:";
            AppendCommandHistory("Specify displacement <0,0>:");
            return true;
        }
        if (!_transformDisplacement || _transformBasePoint is not null) return false;
        var origin = new Point2(0, 0);
        if (key.Length == 0) { _transformDisplacement = false; EndLineCommand(); AppendCommandHistory("Displacement 0,0: nothing moved."); return true; }
        if (!DynamicInputParser.TryParsePoint(text, origin, origin, out var vector)) { DynamicPromptText.Text = "Enter the displacement as X,Y:"; DynamicValueTextBox.SelectAll(); return true; }
        _transformDisplacement = false;
        _transformBasePoint = origin;
        DynamicValueTextBox.Clear();
        CompleteMove(vector);
        if (_transformCopy) { EndLineCommand(); DrawingCanvas.Focus(); } // one displaced copy, as in AutoCAD
        return true;
    }

    // ---- TRIM / EXTEND: fence ------------------------------------------------------------------------------------------

    /// <summary>Two picks in empty space define a fence: every object it crosses is trimmed (or extended) where the fence crosses it.</summary>
    private void HandleTrimFenceClick(Point2 world, bool trim)
    {
        if (_fenceStart is not { } start)
        {
            _fenceStart = world;
            AppendCommandHistory($"{_pickTool}: fence - specify the next fence point (every object the fence crosses is {(trim ? "trimmed" : "extended")}).");
            return;
        }
        _fenceStart = null;
        if (Point2.Distance(start, world) < 1e-9) return;
        var fence = Prim.Line(start, world); var hits = new List<Point2>();
        var d = _editor.Drawing; var targets = new List<(Guid Id, Point2 At)>();
        foreach (var entity in d.Entities)
        {
            if (d.HiddenEntityIds.Contains(entity.Id) || IsLayerLocked(entity.Layer) || PathOf(entity) is not { } path) continue;
            foreach (var prim in path.Prims)
            {
                hits.Clear(); IntersectCarriers(prim, fence, hits);
                var crossing = hits.Where(h => WithinPrim(fence, h) && WithinPrim(prim, h)).ToList();
                if (crossing.Count == 0) continue;
                targets.Add((entity.Id, crossing[0])); break;
            }
        }
        if (targets.Count == 0) { AppendCommandHistory($"{_pickTool}: the fence crosses no LINE, ARC, CIRCLE or POLYLINE."); return; }
        foreach (var (id, at) in targets)
        {
            // Earlier trims replace objects, so look each one up again by id.
            var entity = _editor.Drawing.Entities.FirstOrDefault(e => e.Id == id);
            if (entity is null) continue;
            if (trim) TrimEntity(entity, at); else ExtendEntity(entity, at);
        }
        AppendCommandHistory($"{_pickTool}: fence applied to {targets.Count} object(s).");
    }

    // ---- CIRCLE Ttr and FILLET with arcs: circles tangent to two objects ----------------------------------------------

    /// <summary>The line or arc of an object nearest to a pick (LINE, ARC, CIRCLE, or one segment of a POLYLINE).</summary>
    private static Prim? TangentPrim(CadEntity e, Point2 pick)
    {
        if (PathOf(e) is not { } path) return null;
        var u = NearestParameter(path, pick);
        return path.Prims[Math.Clamp((int)Math.Floor(u), 0, path.Prims.Count - 1)];
    }

    /// <summary>Where a circle about <paramref name="center"/> touches the carrier (infinite line / full circle) of a primitive.</summary>
    private static Point2 TangentPoint(in Prim p, Point2 center)
    {
        if (!p.IsArc) return p.At(p.Param(center));
        double dx = center.X - p.C.X, dy = center.Y - p.C.Y; var len = Math.Sqrt(dx * dx + dy * dy);
        return len < 1e-12 ? p.A : new Point2(p.C.X + dx / len * p.R, p.C.Y + dy / len * p.R);
    }

    /// <summary>Carriers at distance r from a primitive: two parallel lines, or the circles of radius R+r and |R-r|.</summary>
    private static List<Prim> OffsetCarriers(in Prim p, double r)
    {
        var result = new List<Prim>(2);
        if (!p.IsArc)
        {
            var len = p.Length; if (len < 1e-12) return result;
            double nx = -(p.B.Y - p.A.Y) / len * r, ny = (p.B.X - p.A.X) / len * r;
            result.Add(Prim.Line(new Point2(p.A.X + nx, p.A.Y + ny), new Point2(p.B.X + nx, p.B.Y + ny)));
            result.Add(Prim.Line(new Point2(p.A.X - nx, p.A.Y - ny), new Point2(p.B.X - nx, p.B.Y - ny)));
        }
        else
        {
            result.Add(Prim.Arc(p.C, p.R + r, 0, 2 * Math.PI));
            if (Math.Abs(p.R - r) > 1e-9) result.Add(Prim.Arc(p.C, Math.Abs(p.R - r), 0, 2 * Math.PI));
        }
        return result;
    }

    /// <summary>
    /// Centre of the circle of radius r tangent to both primitives whose tangent points are nearest the two picks (AutoCAD's rule for TTR and FILLET).
    /// </summary>
    private static (Point2 Center, Point2 T1, Point2 T2)? TangentCircle(in Prim p1, Point2 pick1, in Prim p2, Point2 pick2, double r)
    {
        var hits = new List<Point2>(); (Point2, Point2, Point2)? best = null; var bestScore = double.MaxValue;
        foreach (var a in OffsetCarriers(p1, r))
            foreach (var b in OffsetCarriers(p2, r))
            {
                hits.Clear(); IntersectCarriers(a, b, hits);
                foreach (var center in hits)
                {
                    var t1 = TangentPoint(p1, center); var t2 = TangentPoint(p2, center);
                    var score = Point2.Distance(t1, pick1) + Point2.Distance(t2, pick2);
                    if (score < bestScore) { bestScore = score; best = (center, t1, t2); }
                }
            }
        return best;
    }

    /// <summary>CIRCLE Ttr: circle of the given radius tangent to the two picked objects.</summary>
    private CircleEntity? TangentTangentRadiusCircle(CadEntity e1, Point2 pick1, CadEntity e2, Point2 pick2, double radius)
    {
        if (radius <= 1e-9 || TangentPrim(e1, pick1) is not { } p1 || TangentPrim(e2, pick2) is not { } p2) return null;
        return TangentCircle(p1, pick1, p2, pick2, radius) is { } found ? new CircleEntity(found.Center, radius, Guid.NewGuid(), _editor.Drawing.CurrentLayer) : null;
    }

    /// <summary>
    /// FILLET between a line and an arc, or two arcs (radius &gt; 0): both objects are trimmed or extended to the tangent points, keeping the picked parts,
    /// and the fillet arc is added. Returns false with a message when it cannot be done.
    /// </summary>
    private bool FilletCurves(CadEntity e1, Point2 pick1, CadEntity e2, Point2 pick2)
    {
        var radius = CornerValue;
        if (radius <= 1e-12) { AppendCommandHistory("FILLET: with an arc the radius must be greater than 0 (type R first)."); return false; }
        if (PathOf(e1) is not { Closed: false, Prims.Count: 1 } path1 || PathOf(e2) is not { Closed: false, Prims.Count: 1 } path2)
        { AppendCommandHistory("FILLET: pick LINE or ARC objects (for a polyline pick the polyline alone)."); return false; }
        var p1 = path1.Prims[0]; var p2 = path2.Prims[0];
        if (TangentCircle(p1, pick1, p2, pick2, radius) is not { } found) { AppendCommandHistory($"FILLET: no fillet of radius {Fmt(radius)} fits between these objects."); return false; }

        // Trim or extend an object to its tangent point, keeping the side that was picked.
        static Prim? Kept(Prim p, Point2 tangent, Point2 pick)
        {
            var t = p.Param(tangent);
            if (p.IsArc && t > 1 && p.Turn - t < t - 1) t -= p.Turn; // nearer to the start than to the end: measure backwards
            var picked = p.Nearest(pick);
            var kept = picked < t ? p.Sub(0, t) : p.Sub(t, 1);
            if (kept.Length < 1e-9 || (kept.IsArc && Math.Abs(kept.Sweep) >= 2 * Math.PI - 1e-9)) return null;
            return kept;
        }
        if (Kept(p1, found.T1, pick1) is not { } k1 || Kept(p2, found.T2, pick2) is not { } k2) { AppendCommandHistory("FILLET: the radius is too large for these objects."); return false; }
        double a1 = Math.Atan2(found.T1.Y - found.Center.Y, found.T1.X - found.Center.X), a2 = Math.Atan2(found.T2.Y - found.Center.Y, found.T2.X - found.Center.X);
        var sweep = (a2 - a1) % (2 * Math.PI);
        if (sweep > Math.PI) sweep -= 2 * Math.PI; else if (sweep < -Math.PI) sweep += 2 * Math.PI;
        if (Math.Abs(sweep) < 1e-9) { AppendCommandHistory("FILLET: the objects are already tangent there."); return false; }
        var results = new List<CadEntity>();
        if (EntityFromPrims(new List<Prim> { k1 }, e1) is { } out1) results.Add(out1);
        if (EntityFromPrims(new List<Prim> { k2 }, e2) is { } out2) results.Add(out2);
        results.Add(WithLookOf(ArcFromPrim(Prim.Arc(found.Center, radius, a1, sweep), e1.Layer), e1));
        _editor.ApplyEntityChanges(new[] { e1, e2 }, results);
        _selectedEntityIds.Clear(); _cornerPicks.Clear();
        RefreshPropertiesPalette(); RenderSelectionOverlay(); RenderDrawing(false);
        AppendCommandHistory($"FILLET: radius {Fmt(radius)} fillet created.");
        DrawingCanvas.Focus();
        return true;
    }
}
