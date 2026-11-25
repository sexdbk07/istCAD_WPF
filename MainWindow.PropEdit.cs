using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1;

/// <summary>Editable Properties palette: click a value, type the new one, Enter applies (undoable), Esc cancels.</summary>
public partial class MainWindow
{
    private static bool ParsePropertyNumber(string text, out double value)
    {
        text = text.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value)) return true;
        return double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private static Point2 LineEndFrom(LineEntity line, double? length, double? angleDegrees)
    {
        var dx = line.End.X - line.Start.X; var dy = line.End.Y - line.Start.Y;
        var currentLength = Math.Sqrt(dx * dx + dy * dy);
        var angle = angleDegrees is { } degrees ? degrees * Math.PI / 180 : currentLength > 1e-12 ? Math.Atan2(dy, dx) : 0;
        var distance = length ?? currentLength;
        return new Point2(line.Start.X + distance * Math.Cos(angle), line.Start.Y + distance * Math.Sin(angle));
    }

    /// <summary>Applies one numeric property edit to a single entity. <paramref name="change"/> returns null when the value is not acceptable.</summary>
    private string? EditNumber<T>(T entity, string input, Func<T, double, CadEntity?> change) where T : CadEntity
    {
        if (!ParsePropertyNumber(input, out var value)) return $"\"{input}\" is not a valid number.";
        var updated = change(entity, value);
        return updated is null ? $"{value.ToString(CultureInfo.CurrentCulture)} is not an acceptable value here." : ApplyPropertyEdit(entity, updated);
    }

    private string? ApplyPropertyEdit(CadEntity original, CadEntity updated) => ApplyPropertyEdits([original], _ => updated);

    /// <summary>One undoable edit for every target that is not on a locked layer. Returns an error text, or null on success.</summary>
    private string? ApplyPropertyEdits(IReadOnlyList<CadEntity> targets, Func<CadEntity, CadEntity> change)
    {
        var remove = new List<CadEntity>(); var add = new List<CadEntity>(); var locked = 0;
        foreach (var target in targets)
        {
            if (IsLayerLocked(target.Layer)) { locked++; continue; }
            var updated = change(target);
            if (updated.Equals(target)) continue;
            remove.Add(target); add.Add(updated);
        }
        if (remove.Count == 0) return locked > 0 ? "the object's layer is locked." : null; // same value: nothing to do
        _editor.ApplyEntityChanges(remove, add);
        RefreshPropertiesPalette();
        RenderSelectionOverlay();
        AppendCommandHistory($"Properties: updated {remove.Count:N0} object(s){(locked > 0 ? $" ({locked:N0} on locked layers skipped)" : "")}. U to undo.");
        return null;
    }

    private string? EditLayer(IReadOnlyList<CadEntity> targets, string input)
    {
        var name = _editor.Drawing.LayerColors.Keys.Concat(_editor.Drawing.LayerStates.Keys)
            .FirstOrDefault(layer => layer.Equals(input.Trim(), StringComparison.OrdinalIgnoreCase));
        if (name is null) return $"layer \"{input.Trim()}\" does not exist. Create it first (LAYER).";
        var layerColor = _editor.Drawing.LayerColors.TryGetValue(name, out var color) ? color : (CadColor?)null;
        return ApplyPropertyEdits(targets, e => e with
        {
            Layer = name,
            DisplayColor = e.ExplicitColor ?? layerColor ?? e.DisplayColor,
            LineTypePattern = e.LineTypeName is null ? LineTypePatternFor(null, name) : e.LineTypePattern
        });
    }

    private string? EditColor(IReadOnlyList<CadEntity> targets, string input)
    {
        if (input.Trim().Equals("ByLayer", StringComparison.OrdinalIgnoreCase))
            return ApplyPropertyEdits(targets, e =>
                e with { ExplicitColor = null, ColorByBlock = false, DisplayColor = _editor.Drawing.LayerColors.TryGetValue(e.Layer, out var layerColor) ? layerColor : e.DisplayColor });
        if (input.Trim().Equals("ByBlock", StringComparison.OrdinalIgnoreCase))
            return ApplyPropertyEdits(targets, e => e with { ExplicitColor = null, ColorByBlock = true, DisplayColor = new CadColor(255, 255, 255) });
        if (!TryResolveCadColorValue(input, out var chosen, out _)) return "color must be ByLayer, a name (Red, Yellow, Green, Cyan, Blue, White), #RRGGBB or R,G,B.";
        return ApplyPropertyEdits(targets, e => e with { ExplicitColor = chosen, ColorByBlock = false, DisplayColor = chosen });
    }

    /// <summary>Dash pattern for an entity linetype name (null = ByLayer, resolved through the layer like AutoCAD).</summary>
    private IReadOnlyList<double>? LineTypePatternFor(string? lineTypeName, string layer)
    {
        var d = _editor.Drawing;
        var name = lineTypeName;
        if (name is null) name = d.LayerStates.TryGetValue(layer, out var state) ? state.LineType : "Continuous";
        if (string.IsNullOrWhiteSpace(name) || name.Equals("Continuous", StringComparison.OrdinalIgnoreCase) || name.Equals("ByBlock", StringComparison.OrdinalIgnoreCase)) return null;
        return d.LineStyles.TryGetValue(name, out var def) && def.Pattern.Count > 0 ? def.Pattern : null;
    }

    private string? EditLineType(IReadOnlyList<CadEntity> targets, string input)
    {
        var d = _editor.Drawing;
        var typed = input.Trim();
        var name = new[] { "ByLayer", "ByBlock", "Continuous" }.Concat(d.LineStyles.Keys).FirstOrDefault(n => n.Equals(typed, StringComparison.OrdinalIgnoreCase));
        if (name is null) return $"linetype \"{typed}\" is not loaded. Use ByLayer, ByBlock, Continuous or one of: {string.Join(", ", d.LineStyles.Keys.Take(12))}.";
        var stored = name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) ? null : name;
        return ApplyPropertyEdits(targets, e => e with { LineTypeName = stored, LineTypePattern = LineTypePatternFor(stored, e.Layer) });
    }

    private string? EditLineWeight(IReadOnlyList<CadEntity> targets, string input)
    {
        var typed = input.Trim().Replace("mm", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        if (typed.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) || typed.Equals("Default", StringComparison.OrdinalIgnoreCase))
            return ApplyPropertyEdits(targets, e => e with { LineWeightMm = null });
        if (!ParsePropertyNumber(typed, out var mm) || mm < 0 || mm > 2.11) return "lineweight must be ByLayer or a number of mm from 0 to 2.11 (0.25, 0.35, 0.5 ...).";
        var standard = new[] { 0, 0.05, 0.09, 0.13, 0.15, 0.18, 0.2, 0.25, 0.3, 0.35, 0.4, 0.5, 0.53, 0.6, 0.7, 0.8, 0.9, 1.0, 1.06, 1.2, 1.4, 1.58, 2.0, 2.11 };
        var nearest = standard.OrderBy(v => Math.Abs(v - mm)).First();
        return ApplyPropertyEdits(targets, e => e with { LineWeightMm = nearest });
    }

    private string? EditLineTypeScale(IReadOnlyList<CadEntity> targets, string input)
    {
        if (!ParsePropertyNumber(input, out var scale) || scale <= 0) return "linetype scale must be a positive number.";
        return ApplyPropertyEdits(targets, e => e with { LineTypeScale = scale });
    }

    /// <summary>Turns a property row into an in-place editor.</summary>
    private void MakeEditable(System.Windows.Controls.Grid row, string label, string value, Func<string, string?> commit)
    {
        if (row.Children.Count < 2 || row.Children[1] is not System.Windows.Controls.StackPanel valuePanel || valuePanel.Children.Count == 0 ||
            valuePanel.Children[^1] is not System.Windows.Controls.TextBlock block) return;
        var hover = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        block.Foreground = new SolidColorBrush(Color.FromRgb(240, 243, 247));
        row.Cursor = Cursors.IBeam;
        row.ToolTip = $"{label}: {value}  (click to edit, Enter to apply, Esc to cancel)";
        row.MouseEnter += (_, _) => row.Background = hover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (valuePanel.Children[^1] is System.Windows.Controls.TextBox) return;
            e.Handled = true;
            var box = new System.Windows.Controls.TextBox
            {
                Text = value, MinWidth = 90, FontSize = 12, Padding = new Thickness(3, 0, 3, 0), VerticalContentAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromRgb(26, 31, 38)), Foreground = Brushes.White, CaretBrush = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0, 213, 159)), BorderThickness = new Thickness(1)
            };
            valuePanel.Children.Remove(block); valuePanel.Children.Add(box);
            var finished = false;
            void Finish(bool apply)
            {
                if (finished) return;
                finished = true;
                var text = box.Text.Trim();
                string? error = null;
                if (apply && text.Length > 0 && text != value)
                {
                    try { error = commit(text); } catch (Exception ex) { error = ex.Message; }
                    if (error is not null) AppendCommandHistory("Properties: " + error);
                }
                if (valuePanel.Children.Contains(box)) { valuePanel.Children.Remove(box); valuePanel.Children.Add(block); }
            }
            box.KeyDown += (_, args) =>
            {
                if (args.Key == Key.Enter) { args.Handled = true; Finish(true); DrawingCanvas.Focus(); }
                else if (args.Key == Key.Escape) { args.Handled = true; Finish(false); DrawingCanvas.Focus(); }
            };
            box.LostKeyboardFocus += (_, _) => Finish(true);
            box.Dispatcher.BeginInvoke(new Action(() => { box.Focus(); Keyboard.Focus(box); box.SelectAll(); }), System.Windows.Threading.DispatcherPriority.Input);
        };
    }
}
