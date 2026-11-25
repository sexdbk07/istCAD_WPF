using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using iStCAD_1.Application.Input;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1;

/// <summary>
/// AutoCAD command-line behaviour for the everyday commands: LINE [Close/Undo], PLINE [Arc/Line/Close/Undo],
/// COPY (multiple), ROTATE [Copy/Reference], SCALE [Copy/Reference], MIRROR (erase source objects?).
/// </summary>
public partial class MainWindow
{
    // ---- LINE: Close / Undo ----------------------------------------------------------------------

    /// <summary>Points of the LINE command in progress (first point, then the end of every segment).</summary>
    private readonly List<Point2> _lineChain = new();

    private string LinePrompt() => _lineChain.Count >= 3 ? "Specify next point or [Close/Undo]:" : _lineChain.Count == 2 ? "Specify next point or [Undo]:" : "Specify next point:";

    /// <summary>Follows the LINE command (called every frame): remembers its points and shows the options that apply.</summary>
    private void TrackLineChain()
    {
        if (!_editor.IsLineActive) { if (_lineChain.Count > 0) _lineChain.Clear(); return; }
        if (_editor.LineStart is not Point2 start) return;
        if (_lineChain.Count > 0 && _lineChain[^1] == start) return;
        _lineChain.Add(start);
        var prompt = LinePrompt();
        if (DynamicPromptText.Text.StartsWith("Specify next point", StringComparison.Ordinal) && DynamicPromptText.Text != prompt) DynamicPromptText.Text = prompt;
    }

    // ---- ROTATE: Copy / Reference; COPY: multiple -----------------------------------------------

    private bool _rotateCopy;
    private int _rotateReferenceStage;      // 0 = none, 1 = waiting for the reference angle, 2 = waiting for the new angle
    private double _rotateReferenceAngle;   // radians
    private int _copyCount;                 // copies placed so far by the running COPY command

    private static bool TryParseDegrees(string text, out double degrees)
    {
        var t = text.TrimEnd('d', 'D', '°');
        return (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out degrees) || double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out degrees)) && double.IsFinite(degrees);
    }

    /// <summary>A click (or typed point) while ROTATE waits for an angle: handles the two Reference steps, otherwise rotates.</summary>
    private void RotateAnglePicked(double angleRadians)
    {
        if (_rotateReferenceStage == 1)
        {
            _rotateReferenceAngle = angleRadians; _rotateReferenceStage = 2;
            DynamicPromptText.Text = "Specify the new angle:"; DynamicValueTextBox.Clear();
            AppendCommandHistory($"Reference angle = {(angleRadians * 180 / Math.PI).ToString("0.###", CultureInfo.CurrentCulture)}. Specify the new angle:");
            return;
        }
        CompleteRotate(_rotateReferenceStage == 2 ? angleRadians - _rotateReferenceAngle : angleRadians);
    }

    /// <summary>
    /// Options typed while a classic command is running (before the text is read as a point or number).
    /// LINE: C = close, U = undo last segment. ROTATE: C = copy, R = reference angle.
    /// </summary>
    private bool TryHandleLegacyOption(string text)
    {
        var key = text.Trim().ToUpperInvariant();
        if (TryHandleDisplacementOption(text, key)) return true; // MOVE / COPY / STRETCH [Displacement]
        if (_editor.IsLineActive && key.Length > 0)
        {
            TrackLineChain();
            if (key is "C" or "CLOSE")
            {
                DynamicValueTextBox.Clear();
                if (_lineChain.Count < 3) { AppendCommandHistory("LINE: Close needs at least two segments."); return true; }
                _editor.AddLinePoint(_lineChain[0]);
                EndLineCommand();
                AppendCommandHistory("LINE: closed.");
                DrawingCanvas.Focus(); RenderDrawing(false);
                return true;
            }
            if (key is "U" or "UNDO")
            {
                DynamicValueTextBox.Clear();
                if (_lineChain.Count == 0) return true;
                if (_lineChain.Count == 1)
                {
                    _lineChain.Clear(); _editor.BeginLine();
                    DynamicPromptText.Text = "Specify first point:"; RenderPreview();
                    return true;
                }
                _editor.Undo(); // the last segment
                _lineChain.RemoveAt(_lineChain.Count - 1);
                _editor.BeginLine(); _editor.AddLinePoint(_lineChain[^1]);
                _editor.MovePointer(ConstrainPointer(ScreenToWorld(_lastPointer)));
                DynamicPromptText.Text = LinePrompt();
                AppendCommandHistory("LINE: last segment undone.");
                RenderPreview(); RenderDrawing(false);
                return true;
            }
            return false;
        }
        if (_transformActive && _transformCopy && _copyCount > 0 && key is "E" or "EXIT" or "X")
        {
            // COPY [Exit]
            DynamicValueTextBox.Clear(); EndLineCommand(); AppendCommandHistory("COPY: done."); DrawingCanvas.Focus();
            return true;
        }
        if (_transformActive && _transformCopy && _copyCount > 0 && key is "U" or "UNDO")
        {
            // COPY [Undo]: removes the copy placed last, keeps placing further copies from the same base point.
            DynamicValueTextBox.Clear();
            var last = _copyPlacedStack[^1]; _copyPlacedStack.RemoveAt(_copyPlacedStack.Count - 1);
            _editor.ApplyEntityChanges(last, Array.Empty<CadEntity>());
            _copyCount--;
            RefreshPropertiesPalette(); RenderDrawing(false);
            DynamicPromptText.Text = "Specify second point or [Exit/Undo] <Exit>:";
            AppendCommandHistory("COPY: last copy undone. Specify second point or [Exit/Undo] <Exit>:");
            return true;
        }
        if (_rotateActive && _rotateBasePoint is Point2 rotateBase)
        {
            if (_rotateReferenceStage == 0)
            {
                if (key is "C" or "COPY")
                {
                    _rotateCopy = true; DynamicValueTextBox.Clear();
                    DynamicPromptText.Text = "Specify rotation angle or [Reference]:";
                    AppendCommandHistory("Rotating a copy of the selected objects. Specify rotation angle or [Reference]:");
                    return true;
                }
                if (key is "R" or "REFERENCE")
                {
                    _rotateReferenceStage = 1; DynamicValueTextBox.Clear();
                    DynamicPromptText.Text = "Specify the reference angle <0>:";
                    AppendCommandHistory("Specify the reference angle <0> (type it, or pick a point: the angle from the base point):");
                    return true;
                }
                return false;
            }
            // Reference steps: Enter = 0 for the reference angle; a number is an angle; a point gives the angle from the base point.
            if (key.Length == 0)
            {
                if (_rotateReferenceStage == 1) { RotateAnglePicked(0); return true; }
                return false;
            }
            if (TryParseDegrees(key, out var degrees)) { RotateAnglePicked(degrees * Math.PI / 180); return true; }
            if (DynamicInputParser.TryParsePoint(text, rotateBase, ScreenToWorld(_lastPointer), out var picked) && Point2.Distance(picked, rotateBase) > 1e-9)
            { RotateAnglePicked(Math.Atan2(picked.Y - rotateBase.Y, picked.X - rotateBase.X)); return true; }
            DynamicPromptText.Text = "Enter an angle in degrees or a point:"; DynamicValueTextBox.SelectAll();
            return true;
        }
        return false;
    }

    // ---- PLINE: Arc / Line / Close / Undo ------------------------------------------------------

    private sealed class PlineState
    {
        public List<PolylineVertex> Vertices = new();
        public bool ArcMode;
        public bool Done;
    }

    /// <summary>Direction (radians) in which the polyline leaves its last vertex: the tangent at the end of the last segment.</summary>
    private static double PlineEndTangent(List<PolylineVertex> v)
    {
        if (v.Count < 2) return 0;
        var a = v[^2]; var b = v[^1];
        return Math.Atan2(b.Point.Y - a.Point.Y, b.Point.X - a.Point.X) + 2 * Math.Atan(a.Bulge);
    }

    /// <summary>Bulge of an arc that leaves <paramref name="from"/> tangent to the given direction and ends at <paramref name="to"/> (AutoCAD's default PLINE arc).</summary>
    private static double TangentArcBulge(Point2 from, double tangent, Point2 to)
    {
        var alpha = (Math.Atan2(to.Y - from.Y, to.X - from.X) - tangent) % (2 * Math.PI);
        if (alpha > Math.PI) alpha -= 2 * Math.PI; else if (alpha < -Math.PI) alpha += 2 * Math.PI;
        alpha = Math.Clamp(alpha, -Math.PI + 1e-3, Math.PI - 1e-3); // a point straight behind would be a full circle
        return Math.Tan(alpha / 2);
    }

    private void PlineCommand()
    {
        EndLineCommand();
        var state = new PlineState();
        string Prompt() => state.ArcMode
            ? (state.Vertices.Count >= 2 ? "Specify endpoint of arc or [Line/Close/Undo]:" : "Specify endpoint of arc or [Line/Undo]:")
            : (state.Vertices.Count >= 3 ? "Specify next point or [Arc/Close/Undo]:" : state.Vertices.Count >= 2 ? "Specify next point or [Arc/Undo]:" : "Specify next point or [Arc]:");
        // Vertices so far plus the segment to a new point (line or tangent arc).
        List<PolylineVertex> With(Wizard x, Point2 next)
        {
            var v = state.Vertices.Count > 0 ? new List<PolylineVertex>(state.Vertices) : new List<PolylineVertex> { new(x.P(0)) };
            if (Point2.Distance(v[^1].Point, next) < 1e-10) return v;
            if (state.ArcMode) v[^1] = v[^1] with { Bulge = TangentArcBulge(v[^1].Point, PlineEndTangent(v), next) };
            v.Add(new PolylineVertex(next));
            return v;
        }
        void Create(List<PolylineVertex> vertices, bool closed)
        {
            state.Done = true;
            if (vertices.Count < 2) { AppendCommandHistory("PLINE: at least two points are required."); return; }
            if (closed && Point2.Distance(vertices[0].Point, vertices[^1].Point) < 1e-10) vertices.RemoveAt(vertices.Count - 1);
            var entity = _editor.StyleNew(new PolylineEntity(vertices.ToArray(), closed && vertices.Count > 1, Guid.NewGuid()));
            _editor.ApplyEntityChanges(Array.Empty<CadEntity>(), new[] { entity });
            AppendCommandHistory($"Polyline created: {vertices.Count} vertices{(closed ? ", closed" : string.Empty)}.");
            RenderDrawing(false);
        }
        var wizard = new Wizard
        {
            Name = "PLINE",
            Data = state,
            RepeatFrom = 1,
            EndOnEsc = true, // Esc keeps what was drawn, as in AutoCAD
            Steps = new[]
            {
                new WizStep { Kind = WizKind.Point, Prompt = "Specify start point:" },
                new WizStep
                {
                    Kind = WizKind.Point, Ref = 0, Prompt = "Specify next point or [Arc]:",
                    Keyword = (x, text) =>
                    {
                        switch (text)
                        {
                            case "A": case "ARC": state.ArcMode = true; break;
                            case "L": case "LINE": state.ArcMode = false; break;
                            case "U": case "UNDO":
                                if (state.Vertices.Count >= 2)
                                {
                                    state.Vertices.RemoveAt(state.Vertices.Count - 1);
                                    state.Vertices[^1] = state.Vertices[^1] with { Bulge = 0 };
                                    x.Values[0] = state.Vertices[^1].Point;
                                    if (state.Vertices.Count == 1) state.Vertices.Clear(); // back to just the start point
                                }
                                else AppendCommandHistory("PLINE: all segments already undone.");
                                break;
                            case "C": case "CLOSE":
                            {
                                var needed = state.ArcMode ? 2 : 3;
                                if (state.Vertices.Count < needed) { AppendCommandHistory("PLINE: not enough segments to close."); return true; }
                                var v = new List<PolylineVertex>(state.Vertices);
                                if (state.ArcMode) v[^1] = v[^1] with { Bulge = TangentArcBulge(v[^1].Point, PlineEndTangent(v), v[0].Point) };
                                CancelPickTool(silent: true);
                                Create(v, true);
                                return true;
                            }
                            default: return false;
                        }
                        x.Steps[1].Prompt = Prompt();
                        WizardGoto(1);
                        return true;
                    }
                }
            },
            Preview = x =>
            {
                if (x.Index != 1 || !x.Has(0) || !x.Has(1)) return null;
                var v = With(x, x.P(1));
                return v.Count < 2 ? null : new List<CadEntity> { new PolylineEntity(v.ToArray(), false, Guid.NewGuid(), _editor.Drawing.CurrentLayer) };
            },
            Commit = x =>
            {
                var v = With(x, x.P(1));
                if (v.Count == state.Vertices.Count || (state.Vertices.Count == 0 && v.Count < 2)) { AppendCommandHistory("PLINE: the point must differ from the previous one."); return true; }
                state.Vertices = v;
                x.Values[0] = v[^1].Point; // the next segment starts here (rubber band, @ input, ortho)
                x.Steps[1].Prompt = Prompt();
                return true;
            },
            OnEnd = _ => { if (!state.Done) Create(new List<PolylineVertex>(state.Vertices), false); }
        };
        StartWizard(wizard);
    }

    // ---- SCALE: factor, Copy, Reference ---------------------------------------------------------

    private void ScaleCommand()
    {
        if (!RebuildSelectedProxies("SCALE")) return;
        RequireSelection("SCALE", () =>
        {
            if (!RebuildSelectedProxies("SCALE")) return;
            var originals = SelectedGeometry();
            if (originals.Length == 0) { AppendCommandHistory("SCALE: select objects first."); return; }
            // x.Flag = copy; x.Data = "R" while the Reference steps are used.
            double Factor(Wizard x) => (x.Data as string) == "R" ? (x.Has(2) && x.Has(3) && x.D(2) > 1e-12 ? x.D(3) / x.D(2) : double.NaN) : x.Has(1) ? x.D(1) : double.NaN;
            StartWizard(new Wizard
            {
                Name = "SCALE",
                Steps = new[]
                {
                    new WizStep { Kind = WizKind.Point, Prompt = "Specify base point:" },
                    new WizStep
                    {
                        Kind = WizKind.Dist, Ref = 0, Prompt = "Specify scale factor or [Copy/Reference]", Next = (_, _) => 99,
                        Keyword = (x, text) =>
                        {
                            if (text is "C" or "COPY") { x.Flag = true; AppendCommandHistory("Scaling a copy of the selected objects."); WizardGoto(1); return true; }
                            if (text is "R" or "REFERENCE") { x.Data = "R"; WizardGoto(2); return true; }
                            return false;
                        }
                    },
                    new WizStep { Kind = WizKind.Dist, Ref = 0, Default = 1.0, Prompt = "Specify reference length (type it, or pick a point: distance from the base point)" },
                    new WizStep { Kind = WizKind.Dist, Ref = 0, Prompt = "Specify new length" }
                },
                Preview = x =>
                {
                    if (!x.Has(0) || originals.Length > 400 || x.Index is 0 or 2) return null;
                    var f = Factor(x); if (!(f > 0) || !double.IsFinite(f)) return null;
                    return originals.Select(o => ScaleEntity(o, x.P(0), f)).OfType<CadEntity>().ToList();
                },
                Commit = x =>
                {
                    var f = Factor(x);
                    if (!(f > 0) || !double.IsFinite(f)) { AppendCommandHistory("SCALE: the factor must be positive."); return false; }
                    var from = new List<CadEntity>(); var to = new List<CadEntity>();
                    foreach (var o in originals)
                        if (ScaleEntity(o, x.P(0), f) is { } scaled) { from.Add(o); to.Add(x.Flag ? scaled with { Id = Guid.NewGuid(), NativeHandle = null } : scaled); }
                    if (x.Flag)
                    {
                        if (to.Count == 0) { AppendCommandHistory("SCALE: no supported objects."); return true; }
                        _editor.ApplyEntityChanges(Array.Empty<CadEntity>(), to);
                        _selectedEntityIds.Clear(); foreach (var e in to) _selectedEntityIds.Add(e.Id);
                        RefreshPropertiesPalette(); RenderDrawing(false);
                        AppendCommandHistory($"SCALE: {to.Count} scaled copies created (factor {f.ToString("0.####", CultureInfo.CurrentCulture)}).");
                    }
                    else ApplySelectedReplacement("SCALE", from.ToArray(), to);
                    return true;
                }
            });
        });
    }

    // ---- MIRROR: mirror line by two points, then "Erase source objects?" ------------------------

    private static CadEntity? MirrorEntity(CadEntity entity, Point2 a, Point2 b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, norm = dx * dx + dy * dy;
        if (norm < 1e-18) return null;
        Point2 P(Point2 p) { var t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / norm; return new Point2(2 * (a.X + t * dx) - p.X, 2 * (a.Y + t * dy) - p.Y); }
        var axis = Math.Atan2(dy, dx) * 180 / Math.PI;
        double Angle(double degrees) => ((2 * axis - degrees) % 360 + 360) % 360; // direction reflected in the mirror line
        return entity switch
        {
            LineEntity l => l with { Start = P(l.Start), End = P(l.End) },
            CircleEntity c => c with { Center = P(c.Center) },
            // A mirrored counter-clockwise arc runs clockwise, so its ends swap.
            ArcEntity arc => arc with { Center = P(arc.Center), StartAngleDegrees = Angle(arc.EndAngleDegrees), EndAngleDegrees = Angle(arc.StartAngleDegrees) },
            // Same vertex order; every bulge changes sign.
            PolylineEntity poly => poly with { Vertices = poly.Vertices.Select(v => v with { Point = P(v.Point), Bulge = -v.Bulge }).ToArray() },
            CadTextEntity t => t with { Position = P(t.Position) },
            CadMTextEntity t => t with { Position = P(t.Position) },
            // The pattern lines (ProxyGraphicsPaths) are correctly mirrored point-by-point; only the stored pattern angle
            // (used to rebuild a native HATCH on save) is approximated here rather than reflected exactly - a minor save-only detail.
            ProxyCadEntity { Hatch: not null } hp => TransformProxy(hp, P, 0, 1),
            // Same simplification as HATCH above: the drawn leader/arrow/text mirror correctly; the saved LEADER's
            // HorizontalDirection (used only for the hookline) is approximated rather than reflected exactly.
            ProxyCadEntity { Leader: not null } lp => TransformProxy(lp, P, 0, 1),
            ProxyCadEntity { AttributeDef: not null } ap => TransformProxy(ap, P, 0, 1),
            ProxyCadEntity { Ellipse: not null } ep => TransformProxy(ep, P, 0, 1),
            ProxyCadEntity { Spline: not null } sp => TransformProxy(sp, P, 0, 1),
            ProxyCadEntity { Wipeout: not null } wo => TransformProxy(wo, P, 0, 1),
            _ => null
        };
    }

    private void MirrorCommand()
    {
        RequireSelection("MIRROR", () =>
        {
            if (SelectedGeometry().Length == 0) { AppendCommandHistory("MIRROR: select objects first."); return; }
            StartWizard(new Wizard
            {
                Name = "MIRROR",
                Steps = new[]
                {
                    new WizStep { Kind = WizKind.Point, Prompt = "Specify first point of mirror line:" },
                    new WizStep { Kind = WizKind.Point, Ref = 0, Prompt = "Specify second point of mirror line:" },
                    new WizStep { Kind = WizKind.Text, Default = "N", Prompt = "Erase source objects? [Yes/No]" }
                },
                Preview = x =>
                {
                    if (x.Index != 1 || !x.Has(0) || !x.Has(1)) return null;
                    var selected = SelectedGeometry(); if (selected.Length > 400) return null;
                    return selected.Select(e => MirrorEntity(e, x.P(0), x.P(1))).OfType<CadEntity>().Select(e => e with { Id = Guid.NewGuid() }).ToList();
                },
                Commit = x =>
                {
                    if (Point2.Distance(x.P(0), x.P(1)) < 1e-9) { AppendCommandHistory("MIRROR: the mirror line needs two different points."); return false; }
                    var erase = x.S(2).Trim().ToUpperInvariant().StartsWith("Y");
                    // Blocks and other imported objects are turned into basic geometry first so they can be mirrored.
                    if (!RebuildSelectedProxies("MIRROR", keepInserts: false)) return true;
                    var sources = new List<CadEntity>(); var output = new List<CadEntity>();
                    foreach (var entity in SelectedGeometry())
                    {
                        CadEntity? mirrored = entity is ProxyCadEntity { Dimension: { } dim } proxy
                            ? RecomposeDimension(proxy, dim with { Points = dim.Points.Select(p => MirrorPoint(p, x.P(0), x.P(1))).ToArray() })
                            : MirrorEntity(entity, x.P(0), x.P(1));
                        if (mirrored is null) continue;
                        sources.Add(entity); output.Add(mirrored with { Id = Guid.NewGuid(), NativeHandle = null });
                    }
                    if (output.Count == 0) { AppendCommandHistory("MIRROR: no supported objects in the selection."); return true; }
                    _editor.ApplyEntityChanges(erase ? sources : new List<CadEntity>(), output);
                    _selectedEntityIds.Clear(); foreach (var e in output) _selectedEntityIds.Add(e.Id);
                    RefreshPropertiesPalette(); RenderDrawing(false);
                    AppendCommandHistory($"MIRROR: {output.Count} mirrored object(s) created; source objects {(erase ? "erased" : "kept")}.");
                    return true;
                }
            });
        });
    }

    private static Point2 MirrorPoint(Point2 p, Point2 a, Point2 b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, norm = dx * dx + dy * dy;
        if (norm < 1e-18) return p;
        var t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / norm;
        return new Point2(2 * (a.X + t * dx) - p.X, 2 * (a.Y + t * dy) - p.Y);
    }
}
