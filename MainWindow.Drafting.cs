using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IOPath = System.IO.Path;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1;

public partial class MainWindow
{
    private double _pickboxSize = 10, _gripSize = 7, _gridWorldStep;
    private System.Windows.Shapes.Path? _draftingCursor, _draftingCursorX, _draftingCursorY;
    private System.Windows.Shapes.Path? _trackingGuidePath;
    private System.Windows.Shapes.Path? _polarGuidePath;
    private readonly List<Point2> _trackingAnchors = new(16);
    private readonly TranslateTransform _draftingCursorTransform = new();
    private sealed record DraftingSettings(double PickboxSize, double GripSize, double GridSize);
    private static string DraftingSettingsPath => IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iStCAD_1", "drafting.json");

    private void InitializeDraftingCursor()
    {
        _draftingCursor = new System.Windows.Shapes.Path { Stroke = Brushes.White, StrokeThickness = 1,
            IsHitTestVisible = false, Visibility = Visibility.Collapsed, RenderTransform = _draftingCursorTransform };
        Panel.SetZIndex(_draftingCursor, 25);
        DrawingCanvas.Children.Add(_draftingCursor);
        // AutoCAD-style axis tint: the X (horizontal) arm is reddish, the Y (vertical) arm greenish.
        _draftingCursorX = new System.Windows.Shapes.Path { Stroke = new SolidColorBrush(Color.FromRgb(255, 110, 110)), StrokeThickness = 1,
            IsHitTestVisible = false, Visibility = Visibility.Collapsed, RenderTransform = _draftingCursorTransform };
        _draftingCursorY = new System.Windows.Shapes.Path { Stroke = new SolidColorBrush(Color.FromRgb(110, 235, 120)), StrokeThickness = 1,
            IsHitTestVisible = false, Visibility = Visibility.Collapsed, RenderTransform = _draftingCursorTransform };
        Panel.SetZIndex(_draftingCursorX, 25); Panel.SetZIndex(_draftingCursorY, 25);
        DrawingCanvas.Children.Add(_draftingCursorX); DrawingCanvas.Children.Add(_draftingCursorY);
        _trackingGuidePath = new System.Windows.Shapes.Path
        {
            Stroke = new SolidColorBrush(Color.FromArgb(220, 72, 174, 235)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 3 },
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        Panel.SetZIndex(_trackingGuidePath, 24);
        DrawingCanvas.Children.Add(_trackingGuidePath);
        _polarGuidePath = new System.Windows.Shapes.Path
        {
            Stroke = new SolidColorBrush(Color.FromArgb(210, 225, 190, 90)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 2, 3 },
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        Panel.SetZIndex(_polarGuidePath, 23);
        DrawingCanvas.Children.Add(_polarGuidePath);
        DrawingCanvas.SizeChanged += (_, _) => RebuildDraftingCursor();
        RebuildDraftingCursor();
        AppSettingsButton.Click += (_, _) => ShowDraftingSettings();
        PreferencesButton.Click += (_, _) => ShowDraftingSettings();
        Loaded += async (_, _) =>
        {
            try
            {
                if (!File.Exists(DraftingSettingsPath)) return;
                var settings = JsonSerializer.Deserialize<DraftingSettings>(await File.ReadAllTextAsync(DraftingSettingsPath));
                if (settings is null) return;
                ApplyDraftingSettings(settings);
            }
            catch (Exception ex) { AppendCommandHistory($"Could not load drafting settings: {ex.Message}"); }
        };
    }

    private enum DraftCursor { Idle, Point, Object }
    private DraftCursor _draftCursorShown = DraftCursor.Idle;
    private Geometry? _cursorGapX, _cursorGapY, _cursorFullX, _cursorFullY;
    private bool _selectHintActive;
    private Point _selectHintPointer;

    /// <summary>AutoCAD cursor: crosshair only while a point is requested, pickbox only while objects are requested, both when idle.</summary>
    private DraftCursor GetDraftCursor()
    {
        if (_pendingSelectionAction != SelectionAction.None) return DraftCursor.Object;
        if (_pickTool == "WIZ" && _wiz is { } w)
            return w.Index < w.Steps.Length && w.Steps[w.Index].Kind == WizKind.Entity ? DraftCursor.Object : DraftCursor.Point;
        if ((_pickTool is "DIMRADIUS" or "DIMDIAMETER" && _dimEntityA is null) || (_pickTool == "DIMANGULAR" && _dimEntityB is null)) return DraftCursor.Object;
        return IsDynamicCommandActive ? DraftCursor.Point : DraftCursor.Idle;
    }

    /// <summary>"Select objects:" label that follows the pointer while a command waits for a selection (point prompts already have theirs).</summary>
    private void UpdateSelectionHint()
    {
        var selecting = _dynamicInputEnabled && _pendingSelectionAction != SelectionAction.None && !IsDynamicCommandActive;
        if (selecting != _selectHintActive)
        {
            _selectHintActive = selecting;
            if (selecting) DynamicPromptText.Text = "Select objects:";
            else if (DynamicPromptText.Text == "Select objects:") DynamicPromptText.Text = string.Empty;
            UpdateDynamicInputMode();
        }
        if (selecting && DynamicHintPanel.Visibility == Visibility.Visible && _lastPointer != _selectHintPointer)
        {
            _selectHintPointer = _lastPointer;
            PositionDynamicInput(_lastPointer);
        }
    }

    private void RebuildDraftingCursor()
    {
        if (_draftingCursor is null) return;
        var half = _pickboxSize / 2;
        var reachX = Math.Max(1, DrawingCanvas.ActualWidth) + 2;
        var reachY = Math.Max(1, DrawingCanvas.ActualHeight) + 2;
        var box = new RectangleGeometry(new Rect(-half, -half, _pickboxSize, _pickboxSize)); box.Freeze();
        _draftingCursor.Data = box;
        // AutoCAD-style full-length crosshair, split around the pickbox.
        var gx = new GeometryGroup();
        gx.Children.Add(new LineGeometry(new Point(-reachX, 0), new Point(-half, 0)));
        gx.Children.Add(new LineGeometry(new Point(half, 0), new Point(reachX, 0)));
        gx.Freeze();
        var gy = new GeometryGroup();
        gy.Children.Add(new LineGeometry(new Point(0, -reachY), new Point(0, -half)));
        gy.Children.Add(new LineGeometry(new Point(0, half), new Point(0, reachY)));
        gy.Freeze();
        var fx = new LineGeometry(new Point(-reachX, 0), new Point(reachX, 0)); fx.Freeze();
        var fy = new LineGeometry(new Point(0, -reachY), new Point(0, reachY)); fy.Freeze();
        _cursorGapX = gx; _cursorGapY = gy; _cursorFullX = fx; _cursorFullY = fy;
        if (_draftingCursorX is not null) _draftingCursorX.Data = _draftCursorShown == DraftCursor.Point ? fx : gx;
        if (_draftingCursorY is not null) _draftingCursorY.Data = _draftCursorShown == DraftCursor.Point ? fy : gy;
    }

    private void UpdateDraftingCursor()
    {
        if (_draftingCursor is null) return;
        var overDynamicEditor = DynamicInputPanel?.IsMouseOver == true;
        var show = !_isPanning && DrawingCanvas.IsMouseOver && !overDynamicEditor;
        UpdateSelectionHint();
        TrackLineChain();
        var kind = GetDraftCursor();
        if (kind != _draftCursorShown)
        {
            _draftCursorShown = kind;
            if (_draftingCursorX is not null && _cursorFullX is not null && _cursorGapX is not null) _draftingCursorX.Data = kind == DraftCursor.Point ? _cursorFullX : _cursorGapX;
            if (_draftingCursorY is not null && _cursorFullY is not null && _cursorGapY is not null) _draftingCursorY.Data = kind == DraftCursor.Point ? _cursorFullY : _cursorGapY;
        }
        // Point request: crosshair only. Object request: pickbox only. Idle: both.
        _draftingCursor.Visibility = show && kind != DraftCursor.Point ? Visibility.Visible : Visibility.Collapsed;
        var armsVisible = show && kind != DraftCursor.Object ? Visibility.Visible : Visibility.Collapsed;
        if (_draftingCursorX is not null) _draftingCursorX.Visibility = armsVisible;
        if (_draftingCursorY is not null) _draftingCursorY.Visibility = armsVisible;
        if (!show) { if (!_isPanning && DrawingCanvas.Cursor == Cursors.None) DrawingCanvas.Cursor = Cursors.Cross; return; }
        DrawingCanvas.Cursor = Cursors.None;
        var x = Math.Round(_lastPointer.X) + .5; var y = Math.Round(_lastPointer.Y) + .5;
        if (_draftingCursorTransform.X != x) _draftingCursorTransform.X = x;
        if (_draftingCursorTransform.Y != y) _draftingCursorTransform.Y = y;
    }

    private Point2? GetDraftingReferencePoint()
    {
        if (_pickTool == "WIZ" && _wiz is { } wizard && wizard.Index < wizard.Steps.Length)
        {
            // Ortho / polar work from the step's reference point, else from the last point already given to the command.
            var step = wizard.Steps[wizard.Index];
            if (step.Ref >= 0 && wizard.Values[step.Ref] is Point2 stepReference) return stepReference;
            for (var k = wizard.Index - 1; k >= 0; k--)
                if (wizard.Steps[k].Kind == WizKind.Point && wizard.Values[k] is Point2 previous) return previous;
            return null;
        }
        if (_editor.LineStart is Point2 lineStart) return lineStart;
        if (_polylineActive && _polylineVertices.Count > 0) return _polylineVertices[^1].Point;
        if (_transformBasePoint is Point2 transformBase) return transformBase;
        if (_rotateBasePoint is Point2 rotateBase) return rotateBase;
        if (_rectangleFirstCorner is Point2 rectangleBase) return rectangleBase;
        if (_arcStartPoint is Point2 arcStart) return arcStart;
        if (_arcCenter is Point2 arcCenter) return arcCenter;
        return null;
    }

    private void UpdatePolarGuide(Point2? origin, Point2? target)
    {
        if (_polarGuidePath is null) return;
        if (StatusPolarToggle.IsChecked != true || origin is not Point2 start ||
            target is not Point2 end || DrawingCanvas.ActualWidth <= 0 || DrawingCanvas.ActualHeight <= 0)
        {
            _polarGuidePath.Visibility = Visibility.Collapsed;
            return;
        }

        var a = WorldToScreen(start);
        var b = WorldToScreen(end);
        var dx = b.X - a.X; var dy = b.Y - a.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-6) { _polarGuidePath.Visibility = Visibility.Collapsed; return; }
        var extension = Math.Max(DrawingCanvas.ActualWidth, DrawingCanvas.ActualHeight);
        var tip = new Point(b.X + dx / length * extension, b.Y + dy / length * extension);
        var geometry = new LineGeometry(a, tip);
        geometry.Freeze();
        _polarGuidePath.Data = geometry;
        _polarGuidePath.Visibility = Visibility.Visible;
    }

    private Point2 ApplyObjectTracking(Point2 candidate)
    {
        if (!IsDynamicCommandActive || StatusOtrackToggle.IsChecked != true || _trackingAnchors.Count == 0)
        {
            UpdateTrackingGuide(null, null);
            return candidate;
        }

        var tolerance = 8.0 / Math.Max(_viewScale, 1e-9);
        double? trackedX = null, trackedY = null;
        var bestX = tolerance; var bestY = tolerance;
        foreach (var anchor in _trackingAnchors)
        {
            var dx = Math.Abs(candidate.X - anchor.X);
            var dy = Math.Abs(candidate.Y - anchor.Y);
            if (dx < bestX) { bestX = dx; trackedX = anchor.X; }
            if (dy < bestY) { bestY = dy; trackedY = anchor.Y; }
        }

        UpdateTrackingGuide(trackedX, trackedY);
        return new Point2(trackedX ?? candidate.X, trackedY ?? candidate.Y);
    }

    private void UpdateTrackingGuide(double? x, double? y)
    {
        if (_trackingGuidePath is null) return;
        if (StatusOtrackToggle.IsChecked != true || (x is null && y is null) ||
            DrawingCanvas.ActualWidth <= 0 || DrawingCanvas.ActualHeight <= 0)
        {
            _trackingGuidePath.Visibility = Visibility.Collapsed;
            return;
        }

        var geometry = new GeometryGroup();
        if (x is double worldX)
        {
            var screenX = WorldToScreen(new Point2(worldX, _viewCenter.Y)).X;
            geometry.Children.Add(new LineGeometry(new Point(screenX, 0), new Point(screenX, DrawingCanvas.ActualHeight)));
        }
        if (y is double worldY)
        {
            var screenY = WorldToScreen(new Point2(_viewCenter.X, worldY)).Y;
            geometry.Children.Add(new LineGeometry(new Point(0, screenY), new Point(DrawingCanvas.ActualWidth, screenY)));
        }
        geometry.Freeze();
        _trackingGuidePath.Data = geometry;
        _trackingGuidePath.Visibility = Visibility.Visible;
    }

    private void ApplyDraftingSettings(DraftingSettings settings)
    {
        _pickboxSize = double.IsFinite(settings.PickboxSize) ? Math.Clamp(settings.PickboxSize, 4, 30) : 10;
        _gripSize = double.IsFinite(settings.GripSize) ? Math.Clamp(settings.GripSize, 3, 20) : 7;
        _gridWorldStep = double.IsFinite(settings.GridSize) && settings.GridSize > 0 ? Math.Clamp(settings.GridSize, 1e-9, 1e12) : 0;
        RebuildDraftingCursor();
        _lastGridImageKey = null; _lastSelectionOverlayKey = null;
        RenderDrawingGrid(); RenderSelectionOverlay();
    }

    private void ShowDraftingSettings()
    {
        var body = new StackPanel { Margin = new Thickness(18) };
        var pick = AddNumericEditor(body, "Pickbox size (screen DIPs, 4–30)", _pickboxSize);
        var grip = AddNumericEditor(body, "Grip size (screen DIPs, 3–20)", _gripSize);
        var grid = AddNumericEditor(body, "Grid spacing (drawing units; 0 = adaptive)", _gridWorldStep);
        body.Children.Add(new TextBlock { Text = "Grip markers show defining points. Dense grids are thinned while zooming out.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var apply = new Button { Content = "Apply", MinWidth = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
        var dialog = new Window { Title = "Drafting Settings", Owner = this, Width = 410, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = body };
        ConfigureEditorDialog(dialog);
        apply.Click += async (_, _) =>
        {
            if (!TryReadFinite(pick.Text, out var p) || p < 4 || p > 30 || !TryReadFinite(grip.Text, out var g) || g < 3 || g > 20 || !TryReadFinite(grid.Text, out var step) || step < 0 || (step > 0 && (step < 1e-9 || step > 1e12)))
            { MessageBox.Show(dialog, "Enter valid sizes in the displayed ranges and grid spacing 0 or 1e-9 to 1e12.", "Drafting Settings"); return; }
            var settings = new DraftingSettings(p, g, step);
            ApplyDraftingSettings(settings); dialog.DialogResult = true;
            try { Directory.CreateDirectory(IOPath.GetDirectoryName(DraftingSettingsPath)!); await File.WriteAllTextAsync(DraftingSettingsPath, JsonSerializer.Serialize(settings)); }
            catch (Exception ex) { AppendCommandHistory($"Could not save drafting settings: {ex.Message}"); }
        };
        buttons.Children.Add(apply); buttons.Children.Add(cancel); body.Children.Add(buttons);
        dialog.ShowDialog();
    }

    private void DrawSelectionGrips(DrawingContext context, IReadOnlyList<CadEntity> entities)
    {
        var half = _gripSize / 2;
        var pen = new Pen(Brushes.LightSkyBlue, 1); pen.Freeze();
        var remaining = 2048; // Bound marker drawing work for very large selections.
        foreach (var entity in entities)
            foreach (var point in GripPoints(entity))
            {
                var screen = WorldToScreen(point);
                if (!double.IsFinite(screen.X) || !double.IsFinite(screen.Y) || screen.X < -half || screen.Y < -half || screen.X > DrawingCanvas.ActualWidth + half || screen.Y > DrawingCanvas.ActualHeight + half) continue;
                context.DrawRectangle(Brushes.DodgerBlue, pen, new Rect(screen.X - half, screen.Y - half, _gripSize, _gripSize));
                if (--remaining == 0) return;
            }
    }

    private static IEnumerable<Point2> GripPoints(CadEntity entity) => EnumerateGrips(entity).Select(grip => grip.Point);
}
