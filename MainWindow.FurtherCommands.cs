using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1;

public partial class MainWindow
{
    private string? PromptCadParameter(string title, string description, string initial)
    {
        initial = GetLastInput("param:" + title) ?? initial; // the latest value typed for this command is the new default
        var dialog = CreateCadChildWindow(title, 390, 178);
        dialog.ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new Thickness(15) };
        panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var field = new TextBox { Text = initial, Height = 28, Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(field);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", Width = 76, Height = 29, IsDefault = true, Margin = new Thickness(0,0,8,0) };
        var cancel = new Button { Content = "Cancel", Width = 76, Height = 29, IsCancel = true };
        ok.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(ok); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        dialog.Content = panel; ConfigureEditorDialog(dialog);
        if (dialog.ShowDialog() != true) return null;
        SetLastInput("param:" + title, field.Text.Trim());
        return field.Text.Trim();
    }

    private static bool ReadParameters(string input, int count, out double[] values)
    {
        values = Array.Empty<double>();
        var parts = input.Split(new[] { ';', ' ' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != count) return false;
        var result = new double[count];
        for (var i = 0; i < count; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]) || !double.IsFinite(result[i])) return false;
        values = result; return true;
    }

    private void ApplySelectedReplacement(string command, CadEntity[] originals, IReadOnlyList<CadEntity> replacements)
    {
        if (replacements.Count == 0) { AppendCommandHistory($"{command}: No supported geometry changed."); return; }
        _editor.ApplyEntityChanges(originals, replacements);
        _selectedEntityIds.Clear(); foreach (var entity in replacements) _selectedEntityIds.Add(entity.Id);
        RefreshPropertiesPalette(); AppendCommandHistory($"{command}: Updated {originals.Length} objects, created {replacements.Count} objects.");
    }


    private void BeginMirrorPointAxis()
    {
        var selected = SelectedGeometry();
        if (selected.Length == 0) { AppendCommandHistory("MIRROR: Select objects first, then run MI."); return; }
        if (selected.All(x => x is not (LineEntity or CircleEntity or ArcEntity or PolylineEntity)))
        { AppendCommandHistory("MIRROR supports Line/Circle/Arc/Polyline."); return; }
        _mirrorPointActive = true; _mirrorAxisStart = null;
        _editor.MovePointer(ConstrainPointer(ScreenToWorld(_lastPointer)));
        DynamicPromptText.Text = "MIRROR: Pick axis first point:";
        UpdateDynamicInputMode(); RenderPreview(); DrawingCanvas.Focus();
        AppendCommandHistory("MIRROR: Pick two axis points on drawing (OSNAP enabled). Right-click or Esc to cancel.");
    }

    private void CompleteMirrorByPickedAxis(Point2 a, Point2 b)
    {
        if (!RebuildSelectedProxies("MIRROR", keepInserts: false)) return;
        var selected = SelectedGeometry();
        var dx=b.X-a.X;var dy=b.Y-a.Y;var norm=dx*dx+dy*dy;
        if(norm<1e-18) { AppendCommandHistory("MIRROR: Axis needs two distinct points."); return; }
        Point2 P(Point2 x) { var t=((x.X-a.X)*dx+(x.Y-a.Y)*dy)/norm; return new Point2(2*(a.X+t*dx)-x.X,2*(a.Y+t*dy)-x.Y); }
        double Angle(double degrees) { var radians=degrees*Math.PI/180;var u=P(new Point2(Math.Cos(radians),Math.Sin(radians)));var zero=P(new Point2(0,0));return (Math.Atan2(u.Y-zero.Y,u.X-zero.X)*180/Math.PI+360)%360; }
        var output=new List<CadEntity>();
        foreach(var entity in selected)
        {
            CadEntity? item=entity switch
            {
                LineEntity l=>l with { Start=P(l.Start),End=P(l.End) },
                CircleEntity c=>c with { Center=P(c.Center) },
                ArcEntity arc=>arc with { Center=P(arc.Center),StartAngleDegrees=Angle(arc.EndAngleDegrees),EndAngleDegrees=Angle(arc.StartAngleDegrees) },
                PolylineEntity poly=>poly with { Vertices=poly.Vertices.Reverse().Select(v=>v with {Point=P(v.Point),Bulge=-v.Bulge}).ToArray() },
                CadTextEntity t=>t with { Position=P(t.Position) },
                CadMTextEntity t=>t with { Position=P(t.Position) },
                ProxyCadEntity { Dimension: { } dim } px=>RecomposeDimension(px, dim with { Points=dim.Points.Select(P).ToArray() }),
                _=>null
            };
            if(item is not null) output.Add(item with { Id=Guid.NewGuid(), NativeHandle=null });
        }
        if(output.Count==0) { AppendCommandHistory("MIRROR: No supported objects (LINE/CIRCLE/ARC/POLYLINE)."); return; }
        _editor.ApplyEntityChanges(Array.Empty<CadEntity>(),output);
        _selectedEntityIds.Clear();foreach(var item in output)_selectedEntityIds.Add(item.Id);
        _mirrorPointActive = false; _mirrorAxisStart = null;
        UpdateDynamicInputMode(); RenderPreview();
        RefreshPropertiesPalette();AppendCommandHistory($"MIRROR: Created {output.Count} mirrored objects; originals retained.");
    }

    /// <summary>Offset of a straight-segment polyline to the left of its direction (negative = right), mitered at corners.</summary>
    private static PolylineEntity? OffsetPolyline(PolylineEntity pl, double d)
    {
        var pts = pl.Vertices.Select(v => v.Point).ToList();
        for (var i = pts.Count - 1; i > 0; i--) if (Point2.Distance(pts[i], pts[i - 1]) < 1e-10) pts.RemoveAt(i);
        if (pl.Closed && pts.Count > 1 && Point2.Distance(pts[0], pts[^1]) < 1e-10) pts.RemoveAt(pts.Count - 1);
        if (pts.Count < 2) return null;
        var segs = pl.Closed ? pts.Count : pts.Count - 1;
        var lines = new (Point2 A, Point2 B)[segs];
        for (var i = 0; i < segs; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % pts.Count];
            var dx = b.X - a.X; var dy = b.Y - a.Y; var len = Math.Sqrt(dx * dx + dy * dy);
            double ox = -dy / len * d, oy = dx / len * d;
            lines[i] = (new Point2(a.X + ox, a.Y + oy), new Point2(b.X + ox, b.Y + oy));
        }
        Point2 Join(int s0, int s1, Point2 fallback, Point2 original)
        {
            var (p, q) = (lines[s0].A, lines[s0].B); var (r, t) = (lines[s1].A, lines[s1].B);
            double d1x = q.X - p.X, d1y = q.Y - p.Y, d2x = t.X - r.X, d2y = t.Y - r.Y;
            var den = d1x * d2y - d1y * d2x;
            if (Math.Abs(den) < 1e-12) return fallback;
            var u = ((r.X - p.X) * d2y - (r.Y - p.Y) * d2x) / den;
            var hit = new Point2(p.X + d1x * u, p.Y + d1y * u);
            return Point2.Distance(hit, original) > Math.Abs(d) * 10 ? fallback : hit;
        }
        var result = new List<PolylineVertex>();
        for (var i = 0; i < pts.Count; i++)
        {
            if (!pl.Closed && i == 0) result.Add(new PolylineVertex(lines[0].A));
            else if (!pl.Closed && i == pts.Count - 1) result.Add(new PolylineVertex(lines[segs - 1].B));
            else
            {
                var prev = (i - 1 + segs) % segs; var next = i % segs;
                result.Add(new PolylineVertex(Join(prev, next, lines[next].A, pts[i])));
            }
        }
        return pl with { Vertices = result };
    }


    private void ExplodeSelectedPolylines()
    {
        var polylines=SelectedGeometry().OfType<PolylineEntity>().ToArray();
        var additions=new List<CadEntity>();var removals=new List<CadEntity>();
        foreach(var imported in _editor.Drawing.Entities.Where(e=>_selectedEntityIds.Contains(e.Id)).OfType<ProxyCadEntity>().Where(e=>e.Dimension is null))
        {
            var rebuilt=RebuildProxy(imported);
            if(rebuilt is null) continue;
            additions.AddRange(rebuilt); removals.Add(imported);
        }
        foreach(var dimension in _editor.Drawing.Entities.Where(e=>_selectedEntityIds.Contains(e.Id)).OfType<ProxyCadEntity>().Where(e=>e.Dimension is not null))
        {
            var parts=BuildDimensionParts(dimension.Dimension!);
            if(parts is null) continue;
            additions.AddRange(parts.Select(part=>(CadEntity)(part with { Layer=dimension.Layer })));
            removals.Add(dimension);
        }
        foreach(var poly in polylines)
        {
            // Straight segments become LINEs, bulge segments become ARCs (as in AutoCAD).
            if(PathOf(poly) is not { } path) continue;
            additions.AddRange(EntitiesFromPrims(path.Prims, poly));
            removals.Add(poly);
        }
        if(additions.Count==0){AppendCommandHistory("EXPLODE: select polylines, blocks or dimensions to explode.");return;}
        ApplySelectedReplacement("EXPLODE",removals.ToArray(),additions);
    }

    private void JoinSelectedLines()
    {
        var lines=SelectedGeometry().OfType<LineEntity>().ToArray();
        if(lines.Length<2){AppendCommandHistory("JOIN: Select two or more connected lines.");return;}
        var ordered=new List<Point2>{lines[0].Start,lines[0].End};var pool=lines.Skip(1).ToList();const double tolerance=1e-7;
        while(pool.Count>0)
        {
            var index=pool.FindIndex(l=>Point2.Distance(l.Start,ordered[^1])<=tolerance||Point2.Distance(l.End,ordered[^1])<=tolerance||Point2.Distance(l.Start,ordered[0])<=tolerance||Point2.Distance(l.End,ordered[0])<=tolerance);
            if(index<0)break;
            var line=pool[index];pool.RemoveAt(index);
            if(Point2.Distance(line.Start,ordered[^1])<=tolerance)ordered.Add(line.End);
            else if(Point2.Distance(line.End,ordered[^1])<=tolerance)ordered.Add(line.Start);
            else if(Point2.Distance(line.Start,ordered[0])<=tolerance)ordered.Insert(0,line.End);
            else ordered.Insert(0,line.Start);
        }
        if(pool.Count>0){AppendCommandHistory("JOIN: Selection is disconnected or branches; select one continuous chain.");return;}
        var closed=ordered.Count>2 && Point2.Distance(ordered[0],ordered[^1])<=tolerance;
        if(closed)ordered.RemoveAt(ordered.Count-1);
        var first=lines[0];var poly=new PolylineEntity(ordered.Select(p=>new PolylineVertex(p)).ToArray(),closed,Guid.NewGuid(),first.Layer){ExplicitColor=first.ExplicitColor,DisplayColor=first.DisplayColor,LineTypePattern=first.LineTypePattern,LineTypeScale=first.LineTypeScale};
        ApplySelectedReplacement("JOIN",lines,new CadEntity[]{poly});
    }

    private void PurgeUnusedCadDefinitions()
    {
        var d=_editor.Drawing;
        var layers=new HashSet<string>(d.AllSpaceEntities.Select(x=>x.Layer),StringComparer.OrdinalIgnoreCase){"0",d.CurrentLayer};
        var styles=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"Continuous","ByLayer",d.CurrentLineStyle};
        var unusedLayers=d.LayerColors.Keys.Where(k=>!layers.Contains(k)).ToArray();
        var unusedLines=d.LineStyles.Keys.Where(k=>!styles.Contains(k) && k!="Dashed" && k!="Dotted" && k!="Center").ToArray();
        if(unusedLayers.Length+unusedLines.Length==0){AppendCommandHistory("PURGE: No unused custom layer colors or line styles.");return;}
        if(MessageBox.Show(this,$"Remove {unusedLayers.Length} unused layer definitions and {unusedLines.Length} unused custom line styles?", "PURGE",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        foreach(var name in unusedLayers)d.LayerColors.Remove(name);
        foreach(var name in unusedLines)d.LineStyles.Remove(name);
        RefreshCadStyles();AppendCommandHistory($"PURGE: Removed {unusedLayers.Length+unusedLines.Length} unused definitions.");
    }

}
