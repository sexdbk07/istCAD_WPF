using System;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1;

public partial class MainWindow
{
    private void DrawingCanvasBorder_RightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_polylineActive) FinishPolyline();
        else if (_editor.IsLineActive || _editor.IsCircleActive || _transformActive || _rotateActive || _mirrorPointActive || _distancePointActive || _rectangleActive || _arcActive || _pendingSelectionAction != SelectionAction.None)
            CancelActiveCommand();
        else if (!string.IsNullOrWhiteSpace(_lastCommand)) ProcessCommand(_lastCommand);
        else return;
        DrawingCanvas.Focus(); e.Handled = true;
    }

    private CadEntity[] SelectedGeometry() => _editor.Drawing.Entities.Where(item => _selectedEntityIds.Contains(item.Id)).ToArray();

    private void BeginDistancePointMeasurement()
    {
        _distancePointActive = true; _distanceStart = null;
        _editor.MovePointer(ConstrainPointer(ScreenToWorld(_lastPointer)));
        DynamicPromptText.Text = "DIST: Pick first point:";
        UpdateDynamicInputMode(); RenderPreview(); DrawingCanvas.Focus();
        AppendCommandHistory("DIST: Pick first point and second point with OSNAP; tracking line follows cursor.");
    }

    private void CompleteDistanceByPickedPoints(Point2 first, Point2 second)
    {
        var dx=second.X-first.X; var dy=second.Y-first.Y;
        AppendCommandHistory($"DIST: Distance={Point2.Distance(first, second):0.###}; ΔX={dx:0.###}; ΔY={dy:0.###}; Angle={Math.Atan2(dy,dx)*180/Math.PI:0.###}°");
        _distancePointActive=false; _distanceStart=null; DynamicPromptText.Text=string.Empty;
        UpdateDynamicInputMode(); RenderPreview(); DrawingCanvas.Focus();
    }

    private void MeasureSelectedEntities(string command)
    {
        var selected = SelectedGeometry();
        if (selected.Length == 0) { AppendCommandHistory($"{command}: Select objects first."); return; }
        double totalLength = 0, totalArea = 0;
        int measured = 0;
        foreach (var entity in selected)
        {
            switch (entity)
            {
                case LineEntity line:
                    totalLength += Point2.Distance(line.Start, line.End); measured++; break;
                case CircleEntity circle:
                    totalLength += 2 * Math.PI * circle.Radius;
                    totalArea += Math.PI * circle.Radius * circle.Radius; measured++; break;
                case ProxyCadEntity { Ellipse: { } el } when el.EndParameter - el.StartParameter >= Math.Tau - 1e-6:
                {
                    var axisVec = el.MajorAxisEndpoint - el.Center;
                    var a = Math.Sqrt(axisVec.X * axisVec.X + axisVec.Y * axisVec.Y);
                    var b = a * el.RadiusRatio;
                    totalArea += Math.PI * a * b;
                    totalLength += Math.PI * (3 * (a + b) - Math.Sqrt((3 * a + b) * (a + 3 * b))); // Ramanujan's approximation
                    measured++; break;
                }
                case PolylineEntity poly when poly.Vertices.Count >= 2:
                    for (int i = 1; i < poly.Vertices.Count; i++) totalLength += Point2.Distance(poly.Vertices[i-1].Point, poly.Vertices[i].Point);
                    if (poly.Closed)
                    {
                        totalLength += Point2.Distance(poly.Vertices[^1].Point, poly.Vertices[0].Point);
                        double twiceArea = 0;
                        for (int i = 0; i < poly.Vertices.Count; i++)
                        {
                            var a = poly.Vertices[i].Point; var b = poly.Vertices[(i+1)%poly.Vertices.Count].Point;
                            twiceArea += a.X*b.Y-b.X*a.Y;
                        }
                        totalArea += Math.Abs(twiceArea)/2;
                    }
                    measured++; break;
            }
        }
        if (command is "AREA" or "AA") AppendCommandHistory($"AREA: {totalArea:0.###} drawing units² across {measured} supported entities (straight polyline edges only).");
        else AppendCommandHistory($"DIST (selected entity lengths): {totalLength:0.###} drawing units across {measured} supported entities.");
    }

    private void MatchSelectedProperties()
    {
        var selected = SelectedGeometry();
        if (selected.Length < 2) { AppendCommandHistory("MATCHPROP: Select source first, then one or more targets."); return; }
        var source = selected[0];
        var updated = new List<CadEntity>();
        foreach (var target in selected.Skip(1))
            updated.Add(target with { Layer = source.Layer, ExplicitColor = source.ExplicitColor, DisplayColor = source.DisplayColor,
                LineTypePattern = source.LineTypePattern, LineTypeScale = source.LineTypeScale,
                ColorByBlock = source.ColorByBlock, LineTypeName = source.LineTypeName, LineWeightMm = source.LineWeightMm });
        _editor.ApplyEntityChanges(selected.Skip(1), updated);
        _selectedEntityIds.Clear(); foreach (var entity in updated) _selectedEntityIds.Add(entity.Id);
        RefreshPropertiesPalette(); AppendCommandHistory($"MATCHPROP: Applied source properties to {updated.Count} objects.");
    }

    private void RemoveDuplicateSelectedGeometry()
    {
        var selected = SelectedGeometry();
        if (selected.Length == 0) { AppendCommandHistory("OVERKILL: Select objects first."); return; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var remove = new List<CadEntity>();
        foreach (var entity in selected)
        {
            string? geometry = entity switch
            {
                LineEntity line => $"L:{line.Start.X:R},{line.Start.Y:R};{line.End.X:R},{line.End.Y:R}",
                CircleEntity circle => $"C:{circle.Center.X:R},{circle.Center.Y:R};{circle.Radius:R}",
                ArcEntity arc => $"A:{arc.Center.X:R},{arc.Center.Y:R};{arc.Radius:R};{arc.StartAngleDegrees:R};{arc.EndAngleDegrees:R}",
                _ => null
            };
            if (geometry is null) continue;
            var key = $"{entity.Layer}|{entity.ExplicitColor}|{geometry}";
            if (!seen.Add(key)) remove.Add(entity);
        }
        if (remove.Count > 0)
        {
            var confirm = MessageBox.Show(this, $"Delete {remove.Count} exact duplicate selected objects?", "OVERKILL", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
            _editor.ApplyEntityChanges(remove, Array.Empty<CadEntity>());
            foreach (var item in remove) _selectedEntityIds.Remove(item.Id);
            RefreshPropertiesPalette();
        }
        AppendCommandHistory($"OVERKILL: Removed {remove.Count} exact duplicates from selected lines/circles/arcs.");
    }
}
