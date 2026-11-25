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
/// Guided commands (AutoCAD style): every input is a prompt that is answered by clicking in the drawing (with OSNAP and live preview)
/// or by typing a value / coordinates in the dynamic input box. Enter accepts the &lt;default&gt;; Esc ends the command.
/// </summary>
public partial class MainWindow
{
    private enum WizKind { Point, Dist, Angle, Number, Text, Entity }

    private sealed class WizStep
    {
        public WizKind Kind;
        public string Prompt = "";
        public object? Default;
        /// <summary>Index of the Point step that Dist / Angle steps measure from.</summary>
        public int Ref = -1;
        public Func<CadEntity, bool>? Accept;
        /// <summary>Enter on this step finishes the command (after running Commit) instead of demanding a value.</summary>
        public bool Optional;
        /// <summary>Index of the next step (>= step count ends the command). Receives the values and which of them were typed.</summary>
        public Func<object?[], bool[], int>? Next;
        /// <summary>Command options typed instead of a value (AutoCAD style [Center/2P/...]): receives the upper-case text, returns true when it handled it.</summary>
        public Func<Wizard, string, bool>? Keyword;
    }

    private sealed class Wizard
    {
        public string Name = "";
        public WizStep[] Steps = Array.Empty<WizStep>();
        public int Index;
        /// <summary>When set, the command goes back to this step after each result (until Enter / Esc).</summary>
        public int RepeatFrom = -1;
        public object?[] Values = Array.Empty<object?>();
        public Point2?[] Clicks = Array.Empty<Point2?>();
        public bool[] Typed = Array.Empty<bool>();
        public bool Flag;
        public object? Data;
        public Func<Wizard, List<CadEntity>?>? Preview;
        public Func<Wizard, bool> Commit = _ => true;
        /// <summary>Run when the command is ended with Enter at its repeat step (and with Esc when <see cref="EndOnEsc"/> is set).</summary>
        public Action<Wizard>? OnEnd;
        public bool EndOnEsc;
        public bool Has(int i) => i >= 0 && i < Values.Length && Values[i] is not null;
        public Point2 P(int i) => (Point2)Values[i]!;
        public double D(int i) => Convert.ToDouble(Values[i], CultureInfo.InvariantCulture);
        public CadEntity E(int i) => (CadEntity)Values[i]!;
        public string S(int i) => Values[i] as string ?? string.Empty;
    }

    private Wizard? _wiz;
    private Action? _selectionCallback;

    private string WizardKey(Wizard w, int i) => $"wiz:{w.Name}:{i}";

    private void StartWizard(Wizard w, int startIndex = 0)
    {
        w.Values = w.Steps.Select(s => s.Default).ToArray();
        w.Clicks = new Point2?[w.Steps.Length];
        w.Typed = new bool[w.Steps.Length];
        for (var i = 0; i < w.Steps.Length; i++)
            if (w.Steps[i].Kind is WizKind.Number or WizKind.Dist or WizKind.Angle && GetLastInput(WizardKey(w, i)) is { } remembered &&
                double.TryParse(remembered, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && w.Steps[i].Default is not null)
                w.Values[i] = number;
        w.Index = startIndex;
        BeginPickTool("WIZ", WizardPrompt(w));
        _wiz = w;
        WizardShowPrompt(false);
    }

    private static string WizardPrompt(Wizard w)
    {
        var s = w.Steps[w.Index];
        var text = $"{w.Name}: {s.Prompt}";
        if (s.Default is not null && s.Kind is not (WizKind.Point or WizKind.Entity))
        {
            var v = w.Values[w.Index] ?? s.Default;
            text += v is double dv ? $" <{dv.ToString("0.####", CultureInfo.CurrentCulture)}>" : $" <{v}>";
        }
        return text;
    }

    private void WizardShowPrompt(bool log = true)
    {
        if (_wiz is not { } w) return;
        var text = WizardPrompt(w);
        DynamicPromptText.Text = text;
        if (log) AppendCommandHistory(text);
        DynamicValueTextBox.Clear();
        UpdateDynamicInputMode();
        RenderPreview();
    }

    private bool WizardWantsSpace => _pickTool == "WIZ" && _wiz is { } w && w.Index < w.Steps.Length && w.Steps[w.Index].Kind == WizKind.Text;

    private void WizardSet(object value, Point2? click, bool typed)
    {
        if (_wiz is not { } w) return;
        var s = w.Steps[w.Index];
        w.Values[w.Index] = value; w.Clicks[w.Index] = click; w.Typed[w.Index] = typed;
        if (value is double number && s.Kind is WizKind.Number or WizKind.Dist or WizKind.Angle && typed)
            SetLastInput(WizardKey(w, w.Index), number.ToString("R", CultureInfo.InvariantCulture));
        WizardAdvance();
    }

    private void WizardAdvance()
    {
        if (_wiz is not { } w) return;
        var step = w.Steps[w.Index];
        var next = step.Next?.Invoke(w.Values, w.Typed) ?? w.Index + 1;
        if (next >= w.Steps.Length)
        {
            var ok = false;
            try { ok = w.Commit(w); } catch (Exception ex) { AppendCommandHistory($"{w.Name}: {ex.Message}"); }
            if (ok && w.RepeatFrom >= 0 && w.RepeatFrom < w.Steps.Length)
            {
                w.Index = w.RepeatFrom;
                for (var i = w.RepeatFrom; i < w.Steps.Length; i++) { w.Values[i] = w.Steps[i].Default; w.Clicks[i] = null; w.Typed[i] = false; }
                WizardShowPrompt();
                return;
            }
            CancelPickTool(silent: true);
            DrawingCanvas.Focus();
            return;
        }
        w.Index = next;
        WizardShowPrompt();
    }

    /// <summary>Jumps to another step (used by keyword options such as ARC [Center] or CIRCLE [2P/3P]).</summary>
    private void WizardGoto(int index)
    {
        if (_wiz is not { } w || index < 0 || index >= w.Steps.Length) return;
        w.Index = index;
        WizardShowPrompt();
    }

    private void WizardClick(Point screen)
    {
        if (_wiz is not { } w) return;
        var s = w.Steps[w.Index];
        switch (s.Kind)
        {
            case WizKind.Point:
            {
                var p = PickWorldPoint(screen); WizardSet(p, p, false); break;
            }
            case WizKind.Dist:
            case WizKind.Angle:
            {
                if (s.Ref < 0 || !w.Has(s.Ref)) { AppendCommandHistory("Type a value."); break; }
                var b = w.P(s.Ref); var p = PickWorldPoint(screen);
                if (s.Kind == WizKind.Dist)
                {
                    var d = Point2.Distance(b, p);
                    if (d < 1e-9) { AppendCommandHistory("The point must differ from the base point."); break; }
                    WizardSet(d, p, false);
                }
                else WizardSet(Math.Atan2(p.Y - b.Y, p.X - b.X) * 180 / Math.PI, p, false);
                break;
            }
            case WizKind.Entity:
            {
                var hit = FindEntityAt(screen, recordSample: false);
                if (hit is null) { AppendCommandHistory($"{w.Name}: nothing under the cursor."); break; }
                if (s.Accept is not null && !s.Accept(hit)) { AppendCommandHistory($"{w.Name}: that object type is not supported here."); break; }
                WizardSet(hit, ScreenToWorld(screen), false);
                break;
            }
            default:
                AppendCommandHistory("Type a value and press Enter.");
                break;
        }
    }

    /// <summary>Typed answer (or Enter with an empty string).</summary>
    private void WizardTyped(string text)
    {
        if (_wiz is not { } w) return;
        var s = w.Steps[w.Index];
        text = text.Trim();
        DynamicValueTextBox.Clear();
        if (text.Length == 0)
        {
            if (s.Default is not null) { WizardSet(w.Values[w.Index] ?? s.Default, null, true); return; }
            if (s.Optional)
            {
                try { w.Commit(w); } catch (Exception ex) { AppendCommandHistory($"{w.Name}: {ex.Message}"); }
                CancelPickTool(silent: true); DrawingCanvas.Focus(); return;
            }
            if (w.RepeatFrom >= 0 && w.Index == w.RepeatFrom)
            {
                var onEnd = w.OnEnd;
                CancelPickTool(silent: onEnd is not null); DrawingCanvas.Focus();
                try { onEnd?.Invoke(w); } catch (Exception ex) { AppendCommandHistory($"{w.Name}: {ex.Message}"); }
                return;
            }
            AppendCommandHistory($"{w.Name}: a value is required.");
            return;
        }
        if (s.Keyword is not null)
        {
            var handled = false;
            try { handled = s.Keyword(w, text.ToUpperInvariant()); } catch (Exception ex) { AppendCommandHistory($"{w.Name}: {ex.Message}"); handled = true; }
            if (handled) return;
        }
        switch (s.Kind)
        {
            case WizKind.Point:
            {
                Point2 reference = ScreenToWorld(_lastPointer);
                for (var i = w.Index - 1; i >= 0; i--) if (w.Values[i] is Point2 earlier) { reference = earlier; break; }
                if (DynamicInputParser.TryParsePoint(text, reference, ScreenToWorld(_lastPointer), out var point)) WizardSet(point, point, true);
                else AppendCommandHistory("Enter a point as X,Y or @dX,dY, or click in the drawing.");
                break;
            }
            case WizKind.Dist:
            case WizKind.Number:
            case WizKind.Angle:
            {
                var t = text.TrimEnd('d', 'D');
                if (s.Kind == WizKind.Dist && (t.EndsWith('c') || t.EndsWith('C'))) { w.Flag = true; t = t[..^1]; }
                else if (s.Kind == WizKind.Dist) w.Flag = false;
                if (!double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) { AppendCommandHistory("Enter a number."); break; }
                if (s.Kind == WizKind.Dist && number <= 0) { AppendCommandHistory("The value must be positive."); break; }
                WizardSet(number, null, true);
                break;
            }
            case WizKind.Text:
                WizardSet(text, null, true);
                break;
            default:
                AppendCommandHistory("Click an object in the drawing.");
                break;
        }
    }

    /// <summary>Live preview for the current step at the pointer (the same builder as the final result).</summary>
    private List<CadEntity>? WizardPreview(Point2 cursor)
    {
        if (_wiz is not { } w || w.Preview is null || w.Index >= w.Steps.Length) return null;
        var i = w.Index; var s = w.Steps[i];
        var (oldValue, oldClick, oldTyped) = (w.Values[i], w.Clicks[i], w.Typed[i]);
        try
        {
            switch (s.Kind)
            {
                case WizKind.Point: w.Values[i] = cursor; break;
                case WizKind.Dist when s.Ref >= 0 && w.Has(s.Ref): { var d = Point2.Distance(w.P(s.Ref), cursor); if (d < 1e-9) return null; w.Values[i] = d; break; }
                case WizKind.Angle when s.Ref >= 0 && w.Has(s.Ref): { var b = w.P(s.Ref); w.Values[i] = Math.Atan2(cursor.Y - b.Y, cursor.X - b.X) * 180 / Math.PI; break; }
                case WizKind.Dist or WizKind.Angle: return null;
            }
            w.Clicks[i] = cursor; w.Typed[i] = false;
            return w.Preview(w);
        }
        finally { w.Values[i] = oldValue; w.Clicks[i] = oldClick; w.Typed[i] = oldTyped; }
    }

    private void RequireSelection(string command, Action next)
    {
        if (_selectedEntityIds.Count > 0) { next(); return; }
        _selectionCallback = next;
        _pendingSelectionAction = SelectionAction.Callback;
        AppendCommandHistory($"{command}: Select objects, then press Enter:");
        DrawingCanvas.Focus();
    }

    private PolylineEntity NewPoly(IEnumerable<Point2> points, bool closed) =>
        new(points.Select(p => new PolylineVertex(p)).ToList(), closed, Guid.NewGuid(), _editor.Drawing.CurrentLayer);

    private static CadEntity CopyLook(CadEntity target, CadEntity source) => target with
    {
        ExplicitColor = source.ExplicitColor, DisplayColor = source.DisplayColor, ColorByBlock = source.ColorByBlock,
        LineTypePattern = source.LineTypePattern, LineTypeName = source.LineTypeName, LineWeightMm = source.LineWeightMm
    };

    // ---- RPOLYGON -------------------------------------------------------------------

    private void RegularPolygonCommand()
    {
        List<CadEntity>? Build(Wizard x)
        {
            if (!x.Has(0) || !x.Has(1) || !x.Has(2)) return null;
            var sides = (int)Math.Round(x.D(0)); if (sides < 3 || sides > 1024) return null;
            var c = x.P(1); var r = x.D(2); if (r <= 0) return null;
            var baseAngle = x.Clicks[2] is { } k ? Math.Atan2(k.Y - c.Y, k.X - c.X) : Math.PI / 2;
            var radius = x.Flag ? r / Math.Cos(Math.PI / sides) : r;
            var start = x.Flag && x.Clicks[2] is not null ? baseAngle - Math.PI / sides : baseAngle;
            var pts = Enumerable.Range(0, sides).Select(i => new Point2(c.X + radius * Math.Cos(start + Math.Tau * i / sides), c.Y + radius * Math.Sin(start + Math.Tau * i / sides)));
            return new List<CadEntity> { NewPoly(pts, true) };
        }
        StartWizard(new Wizard
        {
            Name = "RPOLYGON",
            Steps = new[]
            {
                new WizStep { Kind = WizKind.Number, Prompt = "Enter number of sides", Default = 6.0 },
                new WizStep { Kind = WizKind.Point, Prompt = "Specify center of polygon:" },
                new WizStep { Kind = WizKind.Dist, Ref = 1, Prompt = "Specify radius (click = a corner; type a number, add C for circumscribed):" }
            },
            Preview = Build,
            Commit = x => { var items = Build(x); if (items is null) { AppendCommandHistory("RPOLYGON: invalid sides or radius."); return false; } AddNewEntities("RPOLYGON", items, "regular polygon created."); return true; }
        });
    }

    // ---- ELLIPSE ----------------------------------------------------------------------

    /// <summary>Samples a CadEllipseData into world points for display/hit-testing (ProxyGraphicsPaths) - the STORED shape
    /// stays the exact parametric data; this is only how it is drawn and picked, same as HATCH/LEADER already do.</summary>
    private static Point2[] SampleEllipse(CadEllipseData e, int segments = 120)
    {
        var axis = e.MajorAxisEndpoint - e.Center;
        var a = Math.Sqrt(axis.X * axis.X + axis.Y * axis.Y);
        var rot = Math.Atan2(axis.Y, axis.X);
        double cos = Math.Cos(rot), sin = Math.Sin(rot);
        var b = a * e.RadiusRatio;
        var full = e.EndParameter - e.StartParameter >= Math.Tau - 1e-6;
        var steps = full ? segments : Math.Max(8, (int)(segments * (e.EndParameter - e.StartParameter) / Math.Tau));
        return Enumerable.Range(0, steps + (full ? 0 : 1)).Select(i =>
        {
            var t = e.StartParameter + (e.EndParameter - e.StartParameter) * i / steps;
            double px = a * Math.Cos(t), py = b * Math.Sin(t);
            return new Point2(e.Center.X + px * cos - py * sin, e.Center.Y + px * sin + py * cos);
        }).ToArray();
    }

    private void EllipseCommand()
    {
        ProxyCadEntity? Build(Wizard x)
        {
            if (!x.Has(0) || !x.Has(1)) return null;
            var c = x.P(0); var axis = x.P(1);
            var a = Point2.Distance(c, axis); if (a < 1e-9) return null;
            var b = x.Has(2) ? x.D(2) : a / 2;
            var ratio = Math.Clamp(a < 1e-9 ? 1 : b / a, 1e-6, 1);
            var data = new CadEllipseData(c, axis, ratio);
            var pts = SampleEllipse(data);
            double minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y), maxX = pts.Max(p => p.X), maxY = pts.Max(p => p.Y);
            return (ProxyCadEntity)_editor.StyleNew(new ProxyCadEntity("ELLIPSE", new Point2(minX, minY), new Point2(maxX, maxY), true, Guid.NewGuid(), _editor.Drawing.CurrentLayer,
                ProxyGraphicsPaths: new[] { new ProxyPreviewPath(pts, true) }, Ellipse: data));
        }
        StartWizard(new Wizard
        {
            Name = "ELLIPSE",
            Steps = new[]
            {
                new WizStep { Kind = WizKind.Point, Prompt = "Specify center of ellipse:" },
                new WizStep { Kind = WizKind.Point, Prompt = "Specify endpoint of the first axis:" },
                new WizStep { Kind = WizKind.Dist, Ref = 0, Prompt = "Specify the other semi-axis length:" }
            },
            Preview = x => Build(x) is { } e ? new List<CadEntity> { e } : null,
            Commit = x =>
            {
                var item = Build(x);
                if (item is null) return false;
                AddNewEntities("ELLIPSE", new List<CadEntity> { item }, "ellipse created (real native ELLIPSE, not a polyline approximation).");
                return true;
            }
        });
    }

    // ---- DONUT ------------------------------------------------------------------------

    private void DonutCommand()
    {
        List<CadEntity>? Build(Wizard x)
        {
            if (!x.Has(0) || !x.Has(1) || !x.Has(2)) return null;
            double inner = x.D(0), outer = x.D(1); if (inner < 0 || outer <= inner) return null;
            var c = x.P(2);
            var items = new List<CadEntity> { new CircleEntity(c, outer / 2, Guid.NewGuid(), _editor.Drawing.CurrentLayer) };
            if (inner > 0) items.Add(new CircleEntity(c, inner / 2, Guid.NewGuid(), _editor.Drawing.CurrentLayer));
            return items;
        }
        StartWizard(new Wizard
        {
            Name = "DONUT",
            Steps = new[]
            {
                new WizStep { Kind = WizKind.Number, Prompt = "Specify inside diameter", Default = 20.0 },
                new WizStep { Kind = WizKind.Number, Prompt = "Specify outside diameter", Default = 40.0 },
                new WizStep { Kind = WizKind.Point, Prompt = "Specify center of donut (Enter or Esc to end):" }
            },
            RepeatFrom = 2,
            Preview = Build,
            Commit = x =>
            {
                var items = Build(x);
                if (items is null) { AppendCommandHistory("DONUT: the outside diameter must be larger than the inside diameter."); return false; }
                AddNewEntities("DONUT", items, "donut outline created."); return true;
            }
        });
    }

    // ---- LENGTHEN ---------------------------------------------------------------------

    private void LengthenCommand()
    {
        static string Mode(string text) { var t = text.Trim().ToUpperInvariant(); return t.StartsWith("DY") ? "DY" : t.StartsWith("P") ? "P" : t.StartsWith("T") ? "T" : "DE"; }
        StartWizard(new Wizard
        {
            Name = "LENGTHEN",
            Steps = new[]
            {
                new WizStep { Kind = WizKind.Text, Default = GetLastInput("lengthen.mode") ?? "DE", Prompt = "Enter option [DElta/Percent/Total/DYnamic]",
                    Next = (v, _) => Mode(v[0] as string ?? "DE") switch { "P" => 2, "T" => 3, "DY" => 4, _ => 1 } },
                new WizStep { Kind = WizKind.Number, Default = 50.0, Prompt = "Enter delta length (negative shortens)", Next = (_, _) => 4 },
                new WizStep { Kind = WizKind.Number, Default = 150.0, Prompt = "Enter percentage length (100 = unchanged)", Next = (_, _) => 4 },
                new WizStep { Kind = WizKind.Number, Default = 100.0, Prompt = "Specify total length", Next = (_, _) => 4 },
                new WizStep { Kind = WizKind.Entity, Prompt = "Select an object to change, near the end to move (Enter or Esc to end):",
                    Accept = e => e is LineEntity or ArcEntity || e is PolylineEntity { Closed: false },
                    Next = (v, _) => Mode(v[0] as string ?? "DE") == "DY" ? 5 : 99 },
                new WizStep { Kind = WizKind.Point, Prompt = "Specify new end point:", Next = (_, _) => 99 }
            },
            RepeatFrom = 4,
            Commit = x =>
            {
                var mode = Mode(x.S(0)); SetLastInput("lengthen.mode", mode);
                var target = x.E(4);
                if (PathOf(target) is not { } path || path.Closed) { AppendCommandHistory("LENGTHEN: select an open line, arc or polyline."); return true; }
                var total = path.Length;
                var click = x.Clicks[4] ?? path.Prims[^1].B;
                var atEnd = NearestParameter(path, click) >= path.Prims.Count / 2.0;
                var prim = atEnd ? path.Prims[^1] : path.Prims[0];
                var length = prim.Length;
                if (mode == "DY")
                {
                    if (!x.Has(5)) { AppendCommandHistory("LENGTHEN: specify a new end point."); return true; }
                    var t = prim.Param(x.P(5));
                    Prim dynPrim;
                    if (atEnd) { if (t <= 1e-9) { AppendCommandHistory("LENGTHEN: that end segment would become zero or negative length."); return true; } dynPrim = prim.Sub(0, t); }
                    else { if (t >= 1 - 1e-9) { AppendCommandHistory("LENGTHEN: that end segment would become zero or negative length."); return true; } dynPrim = prim.Sub(t, 1); }
                    if (dynPrim.IsArc && Math.Abs(dynPrim.Sweep) >= 2 * Math.PI - 1e-9) { AppendCommandHistory("LENGTHEN: the arc would become a full circle."); return true; }
                    var dynPrims = new List<Prim>(path.Prims);
                    if (atEnd) dynPrims[^1] = dynPrim; else dynPrims[0] = dynPrim;
                    if (EntityFromPrims(dynPrims, target) is not { } dynChanged) return true;
                    _editor.ApplyEntityChanges(new[] { target }, new[] { dynChanged });
                    _selectedEntityIds.Clear(); _selectedEntityIds.Add(dynChanged.Id);
                    RefreshPropertiesPalette(); RenderDrawing(false);
                    var dynTotal = total - length + dynPrim.Length;
                    AppendCommandHistory($"LENGTHEN: length {total.ToString("0.###", CultureInfo.CurrentCulture)} → {dynTotal.ToString("0.###", CultureInfo.CurrentCulture)} (Dynamic).");
                    return true;
                }
                var delta = mode switch { "P" => total * x.D(2) / 100 - total, "T" => x.D(3) - total, _ => x.D(1) };
                if ((mode == "P" && x.D(2) <= 0) || (mode == "T" && x.D(3) <= 0)) { AppendCommandHistory("LENGTHEN: the value must be positive."); return true; }
                if (length < 1e-12 || length + delta <= 1e-9) { AppendCommandHistory("LENGTHEN: that end segment would become zero or negative length."); return true; }
                var factor = (length + delta) / length;
                if (prim.IsArc && Math.Abs(prim.Sweep) * factor >= 2 * Math.PI - 1e-9) { AppendCommandHistory("LENGTHEN: the arc would become a full circle."); return true; }
                var prims = new List<Prim>(path.Prims);
                if (atEnd) prims[^1] = prim.Sub(0, factor); else prims[0] = prim.Sub(1 - factor, 1);
                if (EntityFromPrims(prims, target) is not { } changed) return true;
                _editor.ApplyEntityChanges(new[] { target }, new[] { changed });
                _selectedEntityIds.Clear(); _selectedEntityIds.Add(changed.Id);
                RefreshPropertiesPalette(); RenderDrawing(false);
                AppendCommandHistory($"LENGTHEN: length {total.ToString("0.###", CultureInfo.CurrentCulture)} → {(total + delta).ToString("0.###", CultureInfo.CurrentCulture)}.");
                return true;
            }
        });
    }

    // ---- OFFSET -----------------------------------------------------------------------

    private CadEntity? OffsetResult(CadEntity source, Point2 side, double distance)
    {
        double Cross(Point2 a, Point2 b, Point2 p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
        switch (source)
        {
            case LineEntity l when Point2.Distance(l.Start, l.End) > 1e-10:
            {
                var d = Cross(l.Start, l.End, side) >= 0 ? distance : -distance;
                var len = Point2.Distance(l.Start, l.End); double ox = -(l.End.Y - l.Start.Y) / len * d, oy = (l.End.X - l.Start.X) / len * d;
                return l with { Start = new Point2(l.Start.X + ox, l.Start.Y + oy), End = new Point2(l.End.X + ox, l.End.Y + oy), Id = Guid.NewGuid(), NativeHandle = null };
            }
            case CircleEntity c:
            {
                var r = Point2.Distance(side, c.Center) > c.Radius ? c.Radius + distance : c.Radius - distance;
                return r > 1e-9 ? c with { Radius = r, Id = Guid.NewGuid(), NativeHandle = null } : null;
            }
            case ArcEntity a:
            {
                var r = Point2.Distance(side, a.Center) > a.Radius ? a.Radius + distance : a.Radius - distance;
                return r > 1e-9 ? a with { Radius = r, Id = Guid.NewGuid(), NativeHandle = null } : null;
            }
            case PolylineEntity curved when curved.Vertices.Count >= 2 && curved.Vertices.Any(v => Math.Abs(v.Bulge) >= 1e-12):
                return OffsetCurvedPolyline(curved, side, distance);
            case PolylineEntity pl when pl.Vertices.Count >= 2:
            {
                var pts = pl.Vertices.Select(v => v.Point).ToList();
                var count = pl.Closed ? pts.Count : pts.Count - 1;
                double best = double.MaxValue; var sign = 1.0;
                for (var i = 0; i < count; i++)
                {
                    var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                    double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy; if (len2 < 1e-18) continue;
                    var t = Math.Clamp(((side.X - a.X) * dx + (side.Y - a.Y) * dy) / len2, 0, 1);
                    var dist = Point2.Distance(side, new Point2(a.X + dx * t, a.Y + dy * t));
                    if (dist < best) { best = dist; sign = Cross(a, b, side) >= 0 ? 1 : -1; }
                }
                var result = OffsetPolyline(pl, distance * sign);
                return result is null ? null : result with { Id = Guid.NewGuid(), NativeHandle = null };
            }
        }
        return null;
    }

    /// <summary>Settings that persist for the whole OFFSET command run (Erase/Layer/Multiple toggles, undo stack).</summary>
    private sealed class OffsetSettings
    {
        public bool Erase;
        public bool LayerCurrent;
        public bool Multiple;
        public readonly List<CadEntity> Created = new();
    }

    private void OffsetSelectedGeometry()
    {
        // x.Flag = Through mode: the distance is measured from the picked point to the object.
        double? Distance(Wizard x) => x.Flag ? (x.Has(1) && x.Has(2) ? DistanceToCurve(x.E(1), x.P(2)) : null) : x.Has(0) ? (double?)x.D(0) : null;
        CadEntity? Result(Wizard x) => x.Has(1) && x.Has(2) && Distance(x) is { } d && d > 1e-9 ? OffsetResult(x.E(1), x.P(2), d) : null;
        StartWizard(new Wizard
        {
            Name = "OFFSET",
            Steps = new[]
            {
                new WizStep
                {
                    Kind = WizKind.Number, Prompt = "Specify offset distance or [Through/Erase/Layer]", Default = 100.0,
                    Keyword = (x, text) =>
                    {
                        var settings = x.Data as OffsetSettings ?? (OffsetSettings)(x.Data = new OffsetSettings());
                        switch (text)
                        {
                            case "T" or "THROUGH":
                                x.Flag = true; x.Steps[2].Prompt = "Specify through point:";
                                WizardGoto(1); return true;
                            case "E" or "ERASE":
                                settings.Erase = !settings.Erase;
                                AppendCommandHistory($"OFFSET: Erase source objects = {(settings.Erase ? "Yes" : "No")}.");
                                WizardShowPrompt(false); return true;
                            case "L" or "LAYER":
                                settings.LayerCurrent = !settings.LayerCurrent;
                                AppendCommandHistory($"OFFSET: new objects go to [{(settings.LayerCurrent ? "Current" : "Source")}] layer.");
                                WizardShowPrompt(false); return true;
                            default: return false;
                        }
                    }
                },
                new WizStep { Kind = WizKind.Entity, Prompt = "Select object to offset (Enter or Esc to end):", Accept = e => e is LineEntity or CircleEntity or ArcEntity or PolylineEntity },
                new WizStep
                {
                    Kind = WizKind.Point, Prompt = "Specify point on side to offset or [Exit/Multiple/Undo]",
                    Keyword = (x, text) =>
                    {
                        var settings = x.Data as OffsetSettings ?? (OffsetSettings)(x.Data = new OffsetSettings());
                        switch (text)
                        {
                            case "M" or "MULTIPLE":
                                settings.Multiple = true;
                                AppendCommandHistory("OFFSET: Multiple mode on - keeps offsetting the last result.");
                                WizardShowPrompt(false); return true;
                            case "U" or "UNDO":
                                if (settings.Created.Count == 0) { AppendCommandHistory("OFFSET: nothing to undo."); return true; }
                                var last = settings.Created[^1]; settings.Created.RemoveAt(settings.Created.Count - 1);
                                _editor.ApplyEntityChanges(new[] { last }, Array.Empty<CadEntity>());
                                RefreshPropertiesPalette(); RenderDrawing(false);
                                AppendCommandHistory("OFFSET: offset undone.");
                                x.Values[1] = null; x.Typed[1] = false; x.Clicks[1] = null;
                                WizardGoto(1); return true;
                            default: return false;
                        }
                    }
                }
            },
            RepeatFrom = 1,
            Preview = x => Result(x) is { } r ? new List<CadEntity> { r } : null,
            Commit = x =>
            {
                var settings = x.Data as OffsetSettings ?? (OffsetSettings)(x.Data = new OffsetSettings());
                if (!x.Flag && x.D(0) <= 0) { AppendCommandHistory("OFFSET: the distance must be positive."); return false; }
                var result = Result(x);
                if (result is null) { AppendCommandHistory("OFFSET: cannot offset this object by that distance."); return true; }
                var sourceEntity = x.E(1);
                var created = CopyLook(result, sourceEntity) with { Layer = settings.LayerCurrent ? _editor.Drawing.CurrentLayer : sourceEntity.Layer };
                AddNewEntities("OFFSET", new List<CadEntity> { created }, "offset object created.", restyle: false);
                settings.Created.Add(created);
                if (settings.Erase) _editor.ApplyEntityChanges(new[] { sourceEntity }, Array.Empty<CadEntity>());
                if (settings.Multiple) { x.Values[1] = created; x.Typed[1] = false; x.Clicks[1] = null; x.RepeatFrom = 2; }
                else x.RepeatFrom = 1;
                return true;
            }
        });
    }

    // ---- SCALE ------------------------------------------------------------------------

    private CadEntity? ScaleEntity(CadEntity entity, Point2 origin, double f)
    {
        Point2 P(Point2 x) => new(origin.X + (x.X - origin.X) * f, origin.Y + (x.Y - origin.Y) * f);
        return entity switch
        {
            LineEntity l => l with { Start = P(l.Start), End = P(l.End) },
            CircleEntity c => c with { Center = P(c.Center), Radius = c.Radius * f },
            ArcEntity a => a with { Center = P(a.Center), Radius = a.Radius * f },
            PolylineEntity poly => poly with { Vertices = poly.Vertices.Select(v => v with { Point = P(v.Point) }).ToArray() },
            CadTextEntity t => t with { Position = P(t.Position), Height = t.Height * f },
            CadMTextEntity t => t with { Position = P(t.Position), Height = t.Height * f, Width = t.Width * f },
            ProxyCadEntity { Dimension: { } dim } px => RecomposeDimension(px, dim with { Points = dim.Points.Select(P).ToArray(), Radius = dim.Radius * f }),
            ProxyCadEntity { InsertData: not null } ip => TransformProxy(ip, P, 0, f),
            ProxyCadEntity { Hatch: not null } hp => TransformProxy(hp, P, 0, f),
            ProxyCadEntity { Leader: not null } lp => TransformProxy(lp, P, 0, f),
            ProxyCadEntity { AttributeDef: not null } ap => TransformProxy(ap, P, 0, f),
            ProxyCadEntity { Ellipse: not null } ep => TransformProxy(ep, P, 0, f),
            ProxyCadEntity { Spline: not null } sp => TransformProxy(sp, P, 0, f),
            ProxyCadEntity { Wipeout: not null } wo => TransformProxy(wo, P, 0, f),
            // Uniform scale (this app's SCALE is always a single factor f) keeps TABLE's axis-aligned model exact.
            ProxyCadEntity { Table: { } tableScaleData } tp => TransformProxy(tp, P, 0, f) with
            {
                Table = tableScaleData with { Position = P(tableScaleData.Position), ColumnWidth = tableScaleData.ColumnWidth * f, RowHeight = tableScaleData.RowHeight * f }
            },
            // Scaling the paper-space rectangle keeps the model-space ViewHeight/Height ratio constant (same zoom
            // factor) - growing the window reveals more of the model, shrinking it reveals less, matching how
            // resizing a real AutoCAD viewport border behaves. ViewCenter (a MODEL point) is left unchanged.
            ProxyCadEntity { Viewport: { } viewportScaleData } vp => TransformProxy(vp, P, 0, f) with
            {
                Viewport = viewportScaleData with { Center = P(viewportScaleData.Center), Width = viewportScaleData.Width * f, Height = viewportScaleData.Height * f, ViewHeight = viewportScaleData.ViewHeight * f }
            },
            _ => null
        };
    }

    private void TransformSelectedByScale()
    {
        if (!RebuildSelectedProxies("SCALE")) return;
        RequireSelection("SCALE", () =>
        {
            if (!RebuildSelectedProxies("SCALE")) return;
            var originals = SelectedGeometry();
            if (originals.Length == 0) { AppendCommandHistory("SCALE: select objects first."); return; }
            double Factor(Wizard x) => x.Typed[1] ? x.D(1) : x.D(2) / x.D(1);
            StartWizard(new Wizard
            {
                Name = "SCALE",
                Steps = new[]
                {
                    new WizStep { Kind = WizKind.Point, Prompt = "Specify base point:" },
                    new WizStep { Kind = WizKind.Dist, Ref = 0, Prompt = "Specify scale factor (type it), or click a reference length:", Next = (_, typed) => typed[1] ? 3 : 2 },
                    new WizStep { Kind = WizKind.Dist, Ref = 0, Prompt = "Click the new length:" }
                },
                Preview = x =>
                {
                    if (x.Index != 2 || !x.Has(0) || !x.Has(1) || !x.Has(2)) return null;
                    var f = Factor(x); if (f <= 0 || originals.Length > 400) return null;
                    return originals.Select(o => ScaleEntity(o, x.P(0), f)).OfType<CadEntity>().ToList();
                },
                Commit = x =>
                {
                    var f = Factor(x);
                    if (!(f > 0) || !double.IsFinite(f)) { AppendCommandHistory("SCALE: the factor must be positive."); return false; }
                    var from = new List<CadEntity>(); var to = new List<CadEntity>();
                    foreach (var o in originals) if (ScaleEntity(o, x.P(0), f) is { } scaled) { from.Add(o); to.Add(scaled); }
                    ApplySelectedReplacement("SCALE", from.ToArray(), to);
                    return true;
                }
            });
        });
    }

    // ---- TEXT / MTEXT -----------------------------------------------------------------

    private void CreateSimpleCadText(bool multiline)
    {
        var d = _editor.Drawing;
        var styleHeight = d.TextStyles.TryGetValue(d.CurrentTextStyle, out var ts) && ts.Height > 0 ? ts.Height : 250.0;
        List<CadEntity>? Build(Wizard x)
        {
            if (!x.Has(0)) return null;
            var height = x.Has(1) ? x.D(1) : styleHeight;
            var value = x.Has(3) && x.S(3).Length > 0 ? x.S(3) : "Text";
            return new List<CadEntity> { new CadMTextEntity(value, value, x.P(0), height, (x.Has(2) ? x.D(2) : 0) * Math.PI / 180, 0, 1, 1, Guid.NewGuid(), _editor.Drawing.CurrentLayer) };
        }
        StartWizard(new Wizard
        {
            Name = multiline ? "MTEXT" : "TEXT",
            Steps = new[]
            {
                new WizStep { Kind = WizKind.Point, Prompt = "Specify start point of text:" },
                new WizStep { Kind = WizKind.Dist, Ref = 0, Default = styleHeight, Prompt = "Specify height (click = distance from the start point):" },
                new WizStep { Kind = WizKind.Angle, Ref = 0, Default = 0.0, Prompt = "Specify rotation angle (click a direction):" },
                new WizStep { Kind = WizKind.Text, Prompt = multiline ? "Enter text (use \\P for a new line):" : "Enter text:" }
            },
            Preview = Build,
            Commit = x =>
            {
                var value = x.S(3); if (string.IsNullOrWhiteSpace(value)) { AppendCommandHistory("TEXT: empty text."); return false; }
                var height = x.D(1); if (height <= 0) return false;
                var rotation = x.D(2) * Math.PI / 180;
                CadEntity entity = multiline
                    ? new CadMTextEntity(value, value, x.P(0), height, rotation, 0, 1, 1, Guid.NewGuid(), _editor.Drawing.CurrentLayer)
                    : new CadTextEntity(value, x.P(0), height, rotation, 1, 0, 0, Guid.NewGuid(), _editor.Drawing.CurrentLayer);
                entity = _editor.StyleNew(entity);
                _editor.ApplyEntityChanges(Array.Empty<CadEntity>(), new[] { entity });
                _selectedEntityIds.Clear(); _selectedEntityIds.Add(entity.Id);
                RefreshPropertiesPalette(); RenderDrawing(false);
                AppendCommandHistory($"{(multiline ? "MTEXT" : "TEXT")}: text added.");
                return true;
            }
        });
    }

    // ---- HATCH (angle / spacing) --------------------------------------------------------

    private void HatchSelected()
    {
        var boundaries = SelectedHatchBoundaries();
        if (boundaries.Length == 0) { AppendCommandHistory("HATCH: select a closed polyline, rectangle or circle."); return; }
        var suggestedSpacing = SuggestedHatchSpacing(boundaries);
        StartWizard(new Wizard
        {
            Name = "HATCH",
            Steps = new[]
            {
                new WizStep { Kind = WizKind.Angle, Ref = -1, Default = 45.0, Prompt = "Specify pattern angle (degrees)" },
                new WizStep { Kind = WizKind.Number, Default = suggestedSpacing, Prompt = "Specify line spacing (smaller = denser)" }
            },
            Commit = x => { HatchApply(boundaries, x.D(0), x.D(1)); return true; }
        });
    }

    // ---- ARRAY ------------------------------------------------------------------------

    private List<CadEntity> ArrayCopies(CadEntity[] entities, int cols, int rows, double stepX, double stepY, out int unsupported)
    {
        var additions = new List<CadEntity>(); unsupported = 0;
        for (var row = 0; row < rows; row++)
            for (var col = 0; col < cols; col++)
            {
                if (row == 0 && col == 0) continue;
                double dx = col * stepX, dy = row * stepY; Point2 P(Point2 v) => new(v.X + dx, v.Y + dy);
                foreach (var e in entities)
                {
                    CadEntity? moved = e switch
                    {
                        LineEntity l => l with { Start = P(l.Start), End = P(l.End) },
                        CircleEntity c => c with { Center = P(c.Center) },
                        ArcEntity a => a with { Center = P(a.Center) },
                        PolylineEntity pl => pl with { Vertices = pl.Vertices.Select(v => v with { Point = P(v.Point) }).ToArray() },
                        CadTextEntity t => t with { Position = P(t.Position) },
                        CadMTextEntity t => t with { Position = P(t.Position) },
                        ProxyCadEntity { Dimension: not null } or ProxyCadEntity { InsertData: not null } or ProxyCadEntity { Hatch: not null }
                            or ProxyCadEntity { Ellipse: not null } or ProxyCadEntity { Spline: not null } => TranslateEntity(e, new Point2(dx, dy), true),
                        _ => null
                    };
                    if (moved is null) { unsupported++; continue; }
                    additions.Add(moved with { Id = Guid.NewGuid(), NativeHandle = null });
                }
            }
        return additions;
    }

    /// <summary>ARRAY: asks for the type like AutoCAD, then runs the rectangular or polar array.</summary>
    private void ArrayCommand()
    {
        RequireSelection("ARRAY", () =>
        {
            if (SelectedGeometry().Length == 0) { AppendCommandHistory("ARRAY: select objects first."); return; }
            StartWizard(new Wizard
            {
                Name = "ARRAY",
                Steps = new[] { new WizStep { Kind = WizKind.Text, Default = GetLastInput("array.type") ?? "R", Prompt = "Enter array type [Rectangular/POlar/PAth]" } },
                Commit = x =>
                {
                    var kind = x.S(0).Trim().ToUpperInvariant();
                    var polar = kind.StartsWith("PO");
                    var path = !polar && kind.StartsWith("PA");
                    SetLastInput("array.type", polar ? "PO" : path ? "PA" : "R");
                    // Start the follow-up command after this one has closed.
                    Dispatcher.BeginInvoke(new Action(() => { if (polar) ArrayPolarCommand(); else if (path) ArrayPathCommand(); else ArraySelectedGeometry(); }));
                    return true;
                }
            });
        });
    }

    private static Point2 ArrayReferencePoint(CadEntity e) => e switch
    {
        LineEntity l => new Point2((l.Start.X + l.End.X) / 2, (l.Start.Y + l.End.Y) / 2),
        CircleEntity c => c.Center,
        ArcEntity a => a.Center,
        PolylineEntity pl when pl.Vertices.Count > 0 => pl.Vertices[0].Point,
        CadTextEntity t => t.Position,
        CadMTextEntity t => t.Position,
        ProxyCadEntity { InsertData: { } ins } => ins.Position,
        ProxyCadEntity p => new Point2((p.BoundsMin.X + p.BoundsMax.X) / 2, (p.BoundsMin.Y + p.BoundsMax.Y) / 2),
        _ => default
    };

    /// <summary>Copies for a polar array: <paramref name="count"/> items in total over <paramref name="fillDegrees"/> (360 = full circle).</summary>
    private List<CadEntity> PolarCopies(CadEntity[] entities, Point2 center, int count, double fillDegrees, bool rotateItems, out int unsupported)
    {
        var additions = new List<CadEntity>(); unsupported = 0;
        var full = Math.Abs(Math.Abs(fillDegrees) - 360) < 1e-9;
        var step = fillDegrees / (full ? count : count - 1) * Math.PI / 180;
        for (var i = 1; i < count; i++)
        {
            var angle = step * i; double cos = Math.Cos(angle), sin = Math.Sin(angle);
            foreach (var e in entities)
            {
                if (e is not (LineEntity or CircleEntity or ArcEntity or PolylineEntity or CadTextEntity or CadMTextEntity) && e is not ProxyCadEntity { Dimension: not null } && e is not ProxyCadEntity { InsertData: not null }
                    && e is not ProxyCadEntity { Hatch: not null } && e is not ProxyCadEntity { Ellipse: not null } && e is not ProxyCadEntity { Spline: not null })
                { if (i == 1) unsupported++; continue; }
                CadEntity copy;
                if (rotateItems) copy = RotateEntity(e, center, angle);
                else
                {
                    var r = ArrayReferencePoint(e);
                    double dx = r.X - center.X, dy = r.Y - center.Y;
                    copy = TranslateEntity(e, new Point2(center.X + dx * cos - dy * sin - r.X, center.Y + dx * sin + dy * cos - r.Y), true);
                }
                additions.Add(copy with { Id = Guid.NewGuid(), NativeHandle = null });
            }
        }
        return additions;
    }

    private void ArrayPolarCommand()
    {
        RequireSelection("ARRAY", () =>
        {
            var entities = SelectedGeometry();
            if (entities.Length == 0) { AppendCommandHistory("ARRAY: select objects first."); return; }
            bool Valid(Wizard x) { var n = (int)Math.Round(x.D(1)); return n >= 2 && n <= 1000 && (long)n * entities.Length <= 25000 && Math.Abs(x.D(2)) > 1e-9 && Math.Abs(x.D(2)) <= 360; }
            bool Rotate(Wizard x) => !x.S(3).Trim().ToUpperInvariant().StartsWith("N");
            StartWizard(new Wizard
            {
                Name = "ARRAY",
                Steps = new[]
                {
                    new WizStep { Kind = WizKind.Point, Prompt = "Specify center point of array:" },
                    new WizStep { Kind = WizKind.Number, Default = 6.0, Prompt = "Enter number of items" },
                    new WizStep { Kind = WizKind.Number, Default = 360.0, Prompt = "Specify the angle to fill (+ = counter-clockwise, - = clockwise)" },
                    new WizStep { Kind = WizKind.Text, Default = "Y", Prompt = "Rotate arrayed objects? [Yes/No]" }
                },
                Preview = x =>
                {
                    if (!x.Has(0) || !Valid(x) || (int)Math.Round(x.D(1)) * entities.Length > 600) return null;
                    return PolarCopies(entities, x.P(0), (int)Math.Round(x.D(1)), x.D(2), Rotate(x), out _);
                },
                Commit = x =>
                {
                    if (!Valid(x)) { AppendCommandHistory("ARRAY: use 2..1000 items, an angle within ±360 (not 0), at most 25000 new objects."); return false; }
                    var items = PolarCopies(entities, x.P(0), (int)Math.Round(x.D(1)), x.D(2), Rotate(x), out var skipped);
                    if (items.Count == 0) { AppendCommandHistory("ARRAY: no supported objects."); return false; }
                    _editor.ApplyEntityChanges(Array.Empty<CadEntity>(), items);
                    _selectedEntityIds.Clear(); foreach (var e in items) _selectedEntityIds.Add(e.Id);
                    RefreshPropertiesPalette(); RenderDrawing(false);
                    AppendCommandHistory($"ARRAY: polar array created {items.Count} objects{(skipped > 0 ? $"; {skipped} unsupported object(s) skipped" : "")}. U to undo.");
                    return true;
                }
            });
        });
    }

    private void ArraySelectedGeometry()
    {
        RequireSelection("ARRAY", () =>
        {
            var entities = SelectedGeometry();
            if (entities.Length == 0) { AppendCommandHistory("ARRAY: select objects first."); return; }
            bool Valid(Wizard x) { var c = (int)Math.Round(x.D(0)); var r = (int)Math.Round(x.D(1)); return c >= 1 && r >= 1 && c <= 100 && r <= 100 && c * r > 1 && (long)c * r * entities.Length <= 25000; }
            StartWizard(new Wizard
            {
                Name = "ARRAY",
                Steps = new[]
                {
                    new WizStep { Kind = WizKind.Number, Default = 3.0, Prompt = "Enter number of columns" },
                    new WizStep { Kind = WizKind.Number, Default = 2.0, Prompt = "Enter number of rows" },
                    new WizStep { Kind = WizKind.Point, Prompt = "Specify first corner of the unit cell:" },
                    new WizStep { Kind = WizKind.Point, Prompt = "Specify opposite corner of the unit cell (its width/height are the spacing):" }
                },
                Preview = x =>
                {
                    if (!x.Has(2) || !x.Has(3) || !Valid(x) || (int)Math.Round(x.D(0)) * (int)Math.Round(x.D(1)) * entities.Length > 600) return null;
                    return ArrayCopies(entities, (int)Math.Round(x.D(0)), (int)Math.Round(x.D(1)), x.P(3).X - x.P(2).X, x.P(3).Y - x.P(2).Y, out _);
                },
                Commit = x =>
                {
                    if (!Valid(x)) { AppendCommandHistory("ARRAY: use 1..100 rows/columns, more than one cell, at most 25000 new objects."); return false; }
                    var items = ArrayCopies(entities, (int)Math.Round(x.D(0)), (int)Math.Round(x.D(1)), x.P(3).X - x.P(2).X, x.P(3).Y - x.P(2).Y, out var skipped);
                    if (items.Count == 0) { AppendCommandHistory("ARRAY: no supported objects."); return false; }
                    _editor.ApplyEntityChanges(Array.Empty<CadEntity>(), items);
                    _selectedEntityIds.Clear(); foreach (var e in items) _selectedEntityIds.Add(e.Id);
                    RefreshPropertiesPalette(); RenderDrawing(false);
                    AppendCommandHistory($"ARRAY: created {items.Count} objects; {skipped} unsupported copies skipped.");
                    return true;
                }
            });
        });
    }

    // ---- ARRAY Path ---------------------------------------------------------------------

    /// <summary>Samples any supported path entity into "count" evenly-spaced-by-arc-length stations (plus the local
    /// tangent direction at each one, for optional alignment) - a closed path (CIRCLE, closed POLYLINE, full ELLIPSE,
    /// closed SPLINE) wraps the items around with no duplicate at the seam; an open path spreads them from its start
    /// to its end. Returns null for an unsupported path entity or a degenerate (near-zero-length) one.</summary>
    private static (List<Point2> Points, List<double> Angles)? PathStations(CadEntity path, int count)
    {
        List<Point2> raw; bool closed;
        switch (path)
        {
            case LineEntity l: raw = new List<Point2> { l.Start, l.End }; closed = false; break;
            case CircleEntity c:
                raw = Enumerable.Range(0, 128).Select(i => new Point2(c.Center.X + c.Radius * Math.Cos(2 * Math.PI * i / 128), c.Center.Y + c.Radius * Math.Sin(2 * Math.PI * i / 128))).ToList();
                closed = true; break;
            case ArcEntity a:
            {
                var sweep = a.EndAngleDegrees - a.StartAngleDegrees; while (sweep <= 0) sweep += 360;
                var steps = Math.Clamp((int)(sweep / 3), 16, 180);
                raw = Enumerable.Range(0, steps + 1).Select(i =>
                {
                    var ang = (a.StartAngleDegrees + sweep * i / steps) * Math.PI / 180;
                    return new Point2(a.Center.X + a.Radius * Math.Cos(ang), a.Center.Y + a.Radius * Math.Sin(ang));
                }).ToList();
                closed = false; break;
            }
            case PolylineEntity p:
                raw = PolylineTessellator.Tessellate(p, 64).ToList();
                closed = p.Closed; break;
            case ProxyCadEntity { Ellipse: { } el }:
                raw = SampleEllipse(el).ToList();
                closed = el.EndParameter - el.StartParameter >= Math.Tau - 1e-6; break;
            case ProxyCadEntity { Spline: { } sp }:
                raw = SampleSpline(sp).ToList();
                closed = sp.Closed; break;
            default: return null;
        }
        if (raw.Count < 2) return null;
        if (closed && Point2.Distance(raw[0], raw[^1]) > 1e-9) raw.Add(raw[0]);
        var cum = new double[raw.Count];
        for (var i = 1; i < raw.Count; i++) cum[i] = cum[i - 1] + Point2.Distance(raw[i - 1], raw[i]);
        var total = cum[^1];
        if (total < 1e-9) return null;
        var points = new List<Point2>(count); var angles = new List<double>(count);
        for (var i = 0; i < count; i++)
        {
            var target = closed ? total * i / count : total * i / (count - 1);
            var seg = 1; while (seg < cum.Length - 1 && cum[seg] < target) seg++;
            var segStart = cum[seg - 1]; var segLen = cum[seg] - segStart;
            var t = segLen > 1e-12 ? (target - segStart) / segLen : 0;
            var a0 = raw[seg - 1]; var a1 = raw[seg];
            points.Add(new Point2(a0.X + (a1.X - a0.X) * t, a0.Y + (a1.Y - a0.Y) * t));
            angles.Add(Math.Atan2(a1.Y - a0.Y, a1.X - a0.X));
        }
        return (points, angles);
    }

    /// <summary>Copies for a path array: items 2..count placed at the stations returned by <see cref="PathStations"/>,
    /// each simply translated from item 1's position to its station (and, if <paramref name="alignItems"/>, additionally
    /// rotated by the change in path tangent direction since station 0) - item 1 (the original selection) is left
    /// exactly where it already is, same convention as the existing rectangular/polar array commands.</summary>
    private List<CadEntity> PathCopies(CadEntity[] entities, List<Point2> stations, List<double> angles, bool alignItems, out int unsupported)
    {
        var additions = new List<CadEntity>(); unsupported = 0;
        var basePt = stations[0]; var baseAngle = angles[0];
        for (var i = 1; i < stations.Count; i++)
        {
            var offset = new Point2(stations[i].X - basePt.X, stations[i].Y - basePt.Y);
            var dAngle = alignItems ? angles[i] - baseAngle : 0;
            foreach (var e in entities)
            {
                if (e is not (LineEntity or CircleEntity or ArcEntity or PolylineEntity or CadTextEntity or CadMTextEntity) && e is not ProxyCadEntity { Dimension: not null } && e is not ProxyCadEntity { InsertData: not null } && e is not ProxyCadEntity { Hatch: not null } && e is not ProxyCadEntity { Ellipse: not null } && e is not ProxyCadEntity { Spline: not null })
                { if (i == 1) unsupported++; continue; }
                var copy = TranslateEntity(e, offset, true);
                if (Math.Abs(dAngle) > 1e-12) { var r = ArrayReferencePoint(e); copy = RotateEntity(copy, new Point2(r.X + offset.X, r.Y + offset.Y), dAngle); }
                additions.Add(copy with { Id = Guid.NewGuid(), NativeHandle = null });
            }
        }
        return additions;
    }

    private void ArrayPathCommand()
    {
        RequireSelection("ARRAY", () =>
        {
            var entities = SelectedGeometry();
            if (entities.Length == 0) { AppendCommandHistory("ARRAY: select objects first."); return; }
            bool Valid(Wizard x)
            {
                if (!x.Has(0) || x.E(0) is not { } path) return false;
                var n = (int)Math.Round(x.D(1));
                return n >= 2 && n <= 1000 && (long)n * entities.Length <= 25000 && PathStations(path, n) is not null;
            }
            bool Align(Wizard x) => !x.S(2).Trim().ToUpperInvariant().StartsWith("N");
            StartWizard(new Wizard
            {
                Name = "ARRAY",
                Steps = new[]
                {
                    new WizStep { Kind = WizKind.Entity, Prompt = "Select path curve (line/arc/circle/polyline/ellipse/spline):",
                        Accept = e => e is LineEntity or CircleEntity or ArcEntity or PolylineEntity or ProxyCadEntity { Ellipse: not null } or ProxyCadEntity { Spline: not null } },
                    new WizStep { Kind = WizKind.Number, Default = 4.0, Prompt = "Enter number of items along the path" },
                    new WizStep { Kind = WizKind.Text, Default = "Y", Prompt = "Align items to the path direction? [Yes/No]" }
                },
                Preview = x =>
                {
                    if (!Valid(x) || (int)Math.Round(x.D(1)) * entities.Length > 600) return null;
                    var (pts, angs) = PathStations(x.E(0)!, (int)Math.Round(x.D(1)))!.Value;
                    return PathCopies(entities, pts, angs, Align(x), out _);
                },
                Commit = x =>
                {
                    if (!Valid(x)) { AppendCommandHistory("ARRAY: pick a supported path curve and 2..1000 items, at most 25000 new objects."); return false; }
                    var (pts, angs) = PathStations(x.E(0)!, (int)Math.Round(x.D(1)))!.Value;
                    var items = PathCopies(entities, pts, angs, Align(x), out var skipped);
                    if (items.Count == 0) { AppendCommandHistory("ARRAY: no supported objects."); return false; }
                    _editor.ApplyEntityChanges(Array.Empty<CadEntity>(), items);
                    _selectedEntityIds.Clear(); foreach (var e in items) _selectedEntityIds.Add(e.Id);
                    RefreshPropertiesPalette(); RenderDrawing(false);
                    AppendCommandHistory($"ARRAY: path array created {items.Count} objects{(skipped > 0 ? $"; {skipped} unsupported object(s) skipped" : "")}.");
                    return true;
                }
            });
        });
    }

    // ---- BREAK ------------------------------------------------------------------------

    private void BreakCommand()
    {
        StartWizard(new Wizard
        {
            Name = "BREAK",
            Steps = new[]
            {
                new WizStep { Kind = WizKind.Entity, Prompt = "Select object (the pick is the first break point):", Accept = e => e is LineEntity or ArcEntity or CircleEntity or PolylineEntity },
                new WizStep
                {
                    Kind = WizKind.Point, Prompt = "Specify second break point or [First point] (@ = break at the first point):", Next = (_, _) => 99,
                    Keyword = (x, text) =>
                    {
                        if (text is "F" or "FIRST") { WizardGoto(2); return true; }
                        if (text == "@" && x.Clicks[0] is { } same) { WizardSet(same, same, true); return true; }
                        return false;
                    }
                },
                new WizStep { Kind = WizKind.Point, Prompt = "Specify first break point:" },
                new WizStep
                {
                    Kind = WizKind.Point, Prompt = "Specify second break point (@ = same point):",
                    Keyword = (x, text) => { if (text == "@" && x.Has(2)) { var same = x.P(2); WizardSet(same, same, true); return true; } return false; }
                }
            },
            Commit = x =>
            {
                var first = x.Has(2) ? x.P(2) : x.Clicks[0] ?? default;
                var second = x.Has(3) ? x.P(3) : x.P(1);
                return BreakEntity(x.E(0), first, second);
            }
        });
    }

    // ---- PEDIT ------------------------------------------------------------------------

    private void PeditCommand(string? option)
    {
        var preselected = SelectedGeometry().Where(e => e is PolylineEntity or LineEntity).ToArray();
        var fixedOption = option is { Length: > 0 } ? option.Trim().ToUpperInvariant()[0].ToString() : null;
        var steps = new List<WizStep>
        {
            new WizStep { Kind = WizKind.Entity, Prompt = "Select a polyline (or a line, to join connected lines):", Accept = e => e is PolylineEntity or LineEntity },
        };
        if (fixedOption is null) steps.Add(new WizStep { Kind = WizKind.Text, Default = "C", Prompt = "Enter option: Close, Open, Reverse, Join" });
        StartWizard(new Wizard
        {
            Name = fixedOption == "R" ? "REVERSE" : "PEDIT",
            Steps = steps.ToArray(),
            Commit = x =>
            {
                var opt = fixedOption ?? x.S(1);
                var targets = preselected.Length > 0 ? preselected : new[] { x.E(0) };
                PeditApply(targets, opt, autoChain: preselected.Length == 0);
                return true;
            }
        }, preselected.Length > 0 && fixedOption is null ? 1 : 0);
        if (preselected.Length > 0 && fixedOption is not null && _wiz is { } w) { w.Values[0] = preselected[0]; WizardAdvance(); }
    }
}
