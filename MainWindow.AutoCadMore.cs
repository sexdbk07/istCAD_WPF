using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1;

/// <summary>More AutoCAD commands: POLYGON, ELLIPSE, DONUT, LENGTHEN, SELECTSIMILAR, PEDIT (close/open/reverse/join), REVERSE, COPYCLIP/CUTCLIP/PASTECLIP.</summary>
public partial class MainWindow
{
    private readonly List<CadEntity> _clipboardEntities = new();

    private bool TryHandleMoreAutoCad(string[] tokens)
    {
        switch (tokens[0])
        {
            // POLYGON/POL must be the real AutoCAD regular polygon (sides -> center -> Edge/Inscribed/Circumscribed -> radius),
            // not the free-click closed-region sketch tool this used to open (that was a different, custom tool mistakenly
            // given the same name as the real AutoCAD command - fixed in v1.0.219). RPOLYGON/RPOL kept as the same command
            // under its old name too, so nothing that already relied on it breaks.
            case "POLYGON": case "POL": case "RPOLYGON": case "RPOL": RegularPolygonCommand(); return true;
            case "ELLIPSE": case "EL": EllipseCommand(); return true;
            case "DONUT": case "DO": DonutCommand(); return true;
            case "LENGTHEN": case "LEN": LengthenCommand(); return true;
            case "SELECTSIMILAR": case "SSIM": SelectSimilarCommand(); return true;
            case "PEDIT": case "PE": PeditCommand(tokens.Skip(1).FirstOrDefault()); return true;
            case "REVERSE": PeditCommand("R"); return true;
            case "COPYCLIP": case "COPYBASE": ClipboardCopy(false); return true;
            case "CUTCLIP": ClipboardCopy(true); return true;
            case "PASTECLIP": case "PASTE": ClipboardPaste(); return true;
        }
        return false;
    }

    private void AddNewEntities(string command, IReadOnlyList<CadEntity> items, string message, bool restyle = true)
    {
        if (restyle) items = items.Select(item => _editor.StyleNew(item)).ToList();
        _editor.ApplyEntityChanges(Array.Empty<CadEntity>(), items);
        _selectedEntityIds.Clear(); foreach (var e in items) _selectedEntityIds.Add(e.Id);
        RefreshPropertiesPalette(); RenderSelectionOverlay(); RenderDrawing(false);
        AppendCommandHistory($"{command}: {message} U to undo.");
    }





    private void SelectSimilarCommand()
    {
        var selected = SelectedGeometry();
        if (selected.Length == 0) { AppendCommandHistory("SELECTSIMILAR: select an object first."); return; }
        var kinds = selected.Select(e => (e.GetType(), e.Layer.ToUpperInvariant())).ToHashSet();
        var matches = _editor.Drawing.Entities.Where(e => kinds.Contains((e.GetType(), e.Layer.ToUpperInvariant()))).ToList();
        _selectedEntityIds.Clear(); foreach (var e in matches) _selectedEntityIds.Add(e.Id);
        RefreshPropertiesPalette(); RenderSelectionOverlay();
        AppendCommandHistory($"SELECTSIMILAR: {matches.Count:N0} object(s) of the same type and layer selected.");
    }

    private static PolylineEntity ReversePolyline(PolylineEntity pl)
    {
        var v = pl.Vertices; var n = v.Count; var result = new List<PolylineVertex>(n);
        for (var j = 0; j < n; j++)
        {
            var m = n - 1 - j; // original index
            var bulge = m >= 1 ? -v[m - 1].Bulge : (pl.Closed ? -v[n - 1].Bulge : 0);
            result.Add(new PolylineVertex(v[m].Point, bulge));
        }
        return pl with { Vertices = result };
    }


    private void PeditApply(CadEntity[] selected, string option, bool autoChain)
    {
        if (string.IsNullOrWhiteSpace(option)) return;
        switch (option.Trim().ToUpperInvariant()[0])
        {
            case 'C': case 'O': case 'R':
                var close = char.ToUpperInvariant(option.Trim()[0]);
                var removed = new List<CadEntity>(); var added = new List<CadEntity>();
                foreach (var pl in selected.OfType<PolylineEntity>())
                {
                    PolylineEntity changed = close == 'R' ? ReversePolyline(pl) : pl with { Closed = close == 'C' };
                    if (close != 'R' && pl.Closed == changed.Closed) continue;
                    removed.Add(pl); added.Add(changed);
                }
                // REVERSE (and PEDIT R) is also documented to work on a plain LINE (AutoCAD's REVERSE command
                // accepts line/polyline/spline/helix) - Close/Open have no meaning for a LINE, so only handle it here.
                if (close == 'R')
                    foreach (var line in selected.OfType<LineEntity>())
                    { removed.Add(line); added.Add(line with { Start = line.End, End = line.Start }); }
                if (added.Count == 0) { AppendCommandHistory(close == 'R' ? "REVERSE: select a line or polyline first." : "PEDIT: no polyline changed."); return; }
                ApplySelectedReplacement(close == 'R' ? "REVERSE" : "PEDIT", removed.ToArray(), added); return;
            case 'J':
                var first0 = selected.OfType<LineEntity>().FirstOrDefault();
                if (first0 is null) { AppendCommandHistory("PEDIT J: select a LINE (connected lines are joined automatically)."); return; }
                var lines = autoChain
                    ? _editor.Drawing.Entities.OfType<LineEntity>().Where(l => !_editor.Drawing.HiddenEntityIds.Contains(l.Id)).OrderBy(l => l.Id == first0.Id ? 0 : 1).ToList()
                    : selected.OfType<LineEntity>().ToList();
                if (lines.Count < 2) { AppendCommandHistory("PEDIT J: select two or more connected LINE objects."); return; }
                const double tol = 1e-6;
                var chain = new List<Point2> { lines[0].Start, lines[0].End }; var used = new HashSet<LineEntity> { lines[0] };
                bool progress = true;
                while (progress)
                {
                    progress = false;
                    foreach (var l in lines.Where(x => !used.Contains(x)))
                    {
                        if (Point2.Distance(chain[^1], l.Start) < tol) { chain.Add(l.End); }
                        else if (Point2.Distance(chain[^1], l.End) < tol) { chain.Add(l.Start); }
                        else if (Point2.Distance(chain[0], l.End) < tol) { chain.Insert(0, l.Start); }
                        else if (Point2.Distance(chain[0], l.Start) < tol) { chain.Insert(0, l.End); }
                        else continue;
                        used.Add(l); progress = true; break;
                    }
                }
                if (used.Count < 2) { AppendCommandHistory("PEDIT J: the line does not touch another line end to end."); return; }
                var closed = chain.Count > 3 && Point2.Distance(chain[0], chain[^1]) < tol;
                if (closed) chain.RemoveAt(chain.Count - 1);
                var first = lines[0];
                var joined = CopyLook(new PolylineEntity(chain.Select(pt => new PolylineVertex(pt)).ToList(), closed, Guid.NewGuid(), first.Layer), first);
                ApplySelectedReplacement("PEDIT", used.Cast<CadEntity>().ToArray(), new CadEntity[] { joined });
                if (!autoChain && used.Count < lines.Count) AppendCommandHistory($"PEDIT J: {lines.Count - used.Count} line(s) were not connected and stay unchanged.");
                return;
            default: AppendCommandHistory("PEDIT: use C (close), O (open), R (reverse) or J (join)."); return;
        }
    }

    private static (double MinX, double MinY) ClipboardOrigin(IEnumerable<CadEntity> items)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        void Take(double x, double y) { if (x < minX) minX = x; if (y < minY) minY = y; }
        foreach (var e in items)
            switch (e)
            {
                case LineEntity l: Take(l.Start.X, l.Start.Y); Take(l.End.X, l.End.Y); break;
                case CircleEntity c: Take(c.Center.X - c.Radius, c.Center.Y - c.Radius); break;
                case ArcEntity a: Take(a.Center.X - a.Radius, a.Center.Y - a.Radius); break;
                case PolylineEntity pl: foreach (var v in pl.Vertices) Take(v.Point.X, v.Point.Y); break;
                case CadTextEntity t: Take(t.Position.X, t.Position.Y); break;
                case CadMTextEntity m: Take(m.Position.X, m.Position.Y); break;
            }
        return minX == double.MaxValue ? (0, 0) : (minX, minY);
    }

    private void ClipboardCopy(bool cut)
    {
        var selected = SelectedGeometry().Where(e => e is LineEntity or CircleEntity or ArcEntity or PolylineEntity or CadTextEntity or CadMTextEntity).ToArray();
        if (selected.Length == 0) { AppendCommandHistory((cut ? "CUTCLIP" : "COPYCLIP") + ": select lines, circles, arcs, polylines or text first."); return; }
        _clipboardEntities.Clear(); _clipboardEntities.AddRange(selected);
        if (cut) { _editor.ApplyEntityChanges(selected, Array.Empty<CadEntity>()); _selectedEntityIds.Clear(); RefreshPropertiesPalette(); RenderSelectionOverlay(); RenderDrawing(false); }
        AppendCommandHistory($"{(cut ? "CUTCLIP" : "COPYCLIP")}: {selected.Length:N0} object(s) in the clipboard. PASTECLIP to place them.");
    }

    private void ClipboardPaste()
    {
        if (_clipboardEntities.Count == 0) { AppendCommandHistory("PASTECLIP: the clipboard is empty - use COPYCLIP first."); return; }
        var origin = ClipboardOrigin(_clipboardEntities);
        _selectedEntityIds.Clear();
        _stretchActive = false; _transformCopy = true; _transformEntities = _clipboardEntities.ToArray();
        _transformActive = true; _transformBasePoint = new Point2(origin.MinX, origin.MinY);
        DynamicPromptText.Text = "Specify insertion point:";
        DynamicValueTextBox.Clear();
        UpdateDynamicInputMode(); PositionDynamicInput(_lastPointer); FocusDynamicInput();
        AppendCommandHistory($"PASTECLIP: {_clipboardEntities.Count:N0} object(s). Specify insertion point:");
    }
}
