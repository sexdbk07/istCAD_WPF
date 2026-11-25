using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using iStCAD_1.Application.Spatial;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;
using iStCAD_1.Rendering;
using Brush = System.Windows.Media.Brush;
using TextBox = System.Windows.Controls.TextBox;

namespace iStCAD_1;

/// <summary>AutoCAD-style Layer Properties Manager (LAYER / LA).</summary>
public partial class MainWindow
{
    private static Action<LayerRow, string>? s_layerChanged;

    private sealed class LayerRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        private string _colorText = "#FFFFFF";
        private bool _isCurrent;
        public string Name { get; set; } = "0";
        public bool IsNew { get; set; }
        private bool _on = true, _freeze, _lock, _plot = true;
        private string _lineType = "Continuous", _lineweight = "Default";
        private int _transparency;
        public bool On { get => _on; set { if (_on == value) return; _on = value; Raise(nameof(On)); s_layerChanged?.Invoke(this, nameof(On)); } }
        public bool Freeze { get => _freeze; set { if (_freeze == value) return; _freeze = value; Raise(nameof(Freeze)); s_layerChanged?.Invoke(this, nameof(Freeze)); } }
        public bool Lock { get => _lock; set { if (_lock == value) return; _lock = value; Raise(nameof(Lock)); s_layerChanged?.Invoke(this, nameof(Lock)); } }
        public bool Plot { get => _plot; set { if (_plot == value) return; _plot = value; Raise(nameof(Plot)); s_layerChanged?.Invoke(this, nameof(Plot)); } }
        public bool NewVpFreeze { get; set; }
        public string LineType { get => _lineType; set { if (_lineType == value) return; _lineType = value; Raise(nameof(LineType)); s_layerChanged?.Invoke(this, nameof(LineType)); } }
        public string Lineweight { get => _lineweight; set { if (_lineweight == value) return; _lineweight = value; Raise(nameof(Lineweight)); s_layerChanged?.Invoke(this, nameof(Lineweight)); } }
        public int Transparency { get => _transparency; set { if (_transparency == value) return; _transparency = value; Raise(nameof(Transparency)); s_layerChanged?.Invoke(this, nameof(Transparency)); } }
        /// <summary>Copies one edited property from another (selected) row, so a change made on one of several selected layers applies to all of them.</summary>
        public void CopyFrom(LayerRow source, string property)
        {
            switch (property)
            {
                case nameof(On): On = source.On; break;
                case nameof(Freeze): Freeze = source.Freeze; break;
                case nameof(Lock): Lock = source.Lock; break;
                case nameof(Plot): Plot = source.Plot; break;
                case nameof(LineType): LineType = source.LineType; break;
                case nameof(Lineweight): Lineweight = source.Lineweight; break;
                case nameof(Transparency): Transparency = source.Transparency; break;
                case nameof(ColorText): ColorText = source.ColorText; break;
            }
        }
        public string Description { get; set; } = string.Empty;
        public int ObjectCount { get; set; }
        public bool IsCurrent { get => _isCurrent; set { _isCurrent = value; Raise(nameof(IsCurrent)); Raise(nameof(StatusText)); } }
        public string StatusText => IsCurrent ? "✔" : (ObjectCount > 0 ? "▰" : "▱");
        public string ColorText
        {
            get => _colorText;
            set
            {
                if (!TryParseLayerColor(value, out var c)) return;
                _colorText = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                Raise(nameof(ColorText)); Raise(nameof(ColorBrush)); Raise(nameof(ColorName));
                s_layerChanged?.Invoke(this, nameof(ColorText));
            }
        }
        public Brush ColorBrush { get { TryParseLayerColor(_colorText, out var c); return new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)); } }
        public string ColorName
        {
            get
            {
                TryParseLayerColor(_colorText, out var c);
                return LayerColorName(c);
            }
        }
    }

    private static readonly Dictionary<string, CadColor> LayerPalette = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = new(255, 0, 0), ["yellow"] = new(255, 255, 0), ["green"] = new(0, 255, 0), ["cyan"] = new(0, 255, 255),
        ["blue"] = new(0, 0, 255), ["magenta"] = new(255, 0, 255), ["white"] = new(255, 255, 255), ["8"] = new(128, 128, 128), ["9"] = new(192, 192, 192)
    };

    private static readonly (string Name, CadColor Color)[] LayerNamedColors =
    {
        ("Red", new(255, 0, 0)), ("Yellow", new(255, 255, 0)), ("Green", new(0, 255, 0)), ("Cyan", new(0, 255, 255)),
        ("Blue", new(0, 0, 255)), ("Magenta", new(255, 0, 255)), ("White", new(255, 255, 255)), ("Black", new(0, 0, 0)),
        ("Gray", new(128, 128, 128)), ("Light Gray", new(192, 192, 192)), ("Orange", new(255, 127, 0)), ("Brown", new(165, 42, 42))
    };

    /// <summary>Named colour when the RGB matches exactly, otherwise "R,G,B".</summary>
    private static string LayerColorName(CadColor c)
    {
        foreach (var (name, color) in LayerNamedColors) if (color == c) return name;
        return $"{c.R},{c.G},{c.B}";
    }

    private static readonly string[] LayerLineweights =
    {
        "Default", "0.00", "0.05", "0.09", "0.13", "0.15", "0.18", "0.20", "0.25", "0.30", "0.35", "0.40", "0.50", "0.53", "0.60", "0.70", "0.80", "0.90", "1.00", "1.06", "1.20", "1.40", "1.58", "2.00", "2.11"
    };

    private static bool TryParseLayerColor(string? text, out CadColor color)
    {
        color = new CadColor(255, 255, 255);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (LayerPalette.TryGetValue(t, out color)) return true;
        foreach (var (name, named) in LayerNamedColors) if (name.Equals(t, StringComparison.OrdinalIgnoreCase)) { color = named; return true; }
        if (t.StartsWith('#') && t.Length == 7 && int.TryParse(t[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        { color = new CadColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb); return true; }
        var parts = t.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 3 && byte.TryParse(parts[0], out var r) && byte.TryParse(parts[1], out var g) && byte.TryParse(parts[2], out var b))
        { color = new CadColor(r, g, b); return true; }
        return false;
    }

    private bool IsLayerLocked(string layer) => _editor.Drawing.LayerStates.TryGetValue(layer, out var state) && state.Locked;

    private void ShowLayerManager()
    {
        var d = _editor.Drawing;
        var counts = d.AllSpaceEntities.GroupBy(e => e.Layer, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var names = new[] { "0" }.Concat(d.LayerStates.Keys).Concat(d.LayerColors.Keys).Concat(counts.Keys)
            .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var rows = new System.Collections.ObjectModel.ObservableCollection<LayerRow>();
        foreach (var name in names)
        {
            d.LayerStates.TryGetValue(name, out var st);
            var color = d.LayerColors.TryGetValue(name, out var lc) ? lc : new CadColor(255, 255, 255);
            rows.Add(new LayerRow
            {
                Name = name, On = st?.On ?? true, Freeze = st?.Frozen ?? false, Lock = st?.Locked ?? false, Plot = st?.Plot ?? !name.Equals("Defpoints", StringComparison.OrdinalIgnoreCase),
                NewVpFreeze = st?.NewViewportFreeze ?? false, LineType = st?.LineType ?? "Continuous",
                Lineweight = st?.LineWeightMm is { } w ? w.ToString("0.00", CultureInfo.InvariantCulture) : "Default",
                Transparency = st?.Transparency ?? 0, Description = st?.Description ?? string.Empty,
                ObjectCount = counts.GetValueOrDefault(name), IsCurrent = name.Equals(d.CurrentLayer, StringComparison.OrdinalIgnoreCase),
                ColorText = $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            });
        }
        var deleted = new List<string>();
        var originalVisibility = d.LayerStates.Values.Where(s => !s.Visible).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var dialog = CreateCadChildWindow($"Layer Properties Manager — {d.Name}", 1060, 620);
        dialog.ResizeMode = ResizeMode.CanResize;
        var root = new DockPanel { Margin = new Thickness(10) };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        Button Btn(string t, double w = 90, bool def = false, bool cancel = false) => new() { Content = t, MinWidth = w, Height = 28, Margin = new Thickness(6, 0, 0, 0), IsDefault = def, IsCancel = cancel };
        var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), Foreground = Brushes.Gray };
        var okBtn = Btn("OK", def: true); var cancelBtn = Btn("Cancel", cancel: true); var applyBtn = Btn("Apply");
        footer.Children.Add(status); footer.Children.Add(okBtn); footer.Children.Add(cancelBtn); footer.Children.Add(applyBtn);

        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        var newBtn = Btn("＋ New layer", 100); var newVpBtn = Btn("＋ New (VP frozen)", 130); var delBtn = Btn("✕ Delete", 80); var curBtn = Btn("✔ Set current", 110);
        var search = new TextBox { Width = 200, Height = 26, Margin = new Thickness(12, 0, 0, 0), ToolTip = "Search layers by name", VerticalContentAlignment = VerticalAlignment.Center };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        newBtn.Margin = new Thickness(0);
        left.Children.Add(newBtn); left.Children.Add(newVpBtn); left.Children.Add(delBtn); left.Children.Add(curBtn);
        var searchPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        searchPanel.Children.Add(new TextBlock { Text = "Search:", Margin = new Thickness(0, 0, 6, 0) }); searchPanel.Children.Add(search);
        DockPanel.SetDock(searchPanel, Dock.Right);
        toolbar.Children.Add(searchPanel); toolbar.Children.Add(left);

        var grid = new DataGrid
        {
            ItemsSource = rows, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Extended,
            HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.None, RowHeight = 24,
            Background = Brushes.White, RowBackground = Brushes.White,
            AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(244, 246, 249)), Foreground = Brushes.Black,
            BorderBrush = new SolidColorBrush(Color.FromRgb(198, 205, 214)), CanUserResizeRows = false, EnableRowVirtualization = true
        };
        var headerStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(236, 239, 244))));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
        headerStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(198, 205, 214))));
        headerStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
        headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4)));
        grid.ColumnHeaderStyle = headerStyle;
        var cellStyle = new Style(typeof(DataGridCell));
        cellStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        cellStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
        var selTrigger = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        selTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(204, 226, 250))));
        selTrigger.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
        cellStyle.Triggers.Add(selTrigger);
        grid.CellStyle = cellStyle;

        DataGridTextColumn Text(string header, string path, double width, bool readOnly = false) => new()
        {
            Header = header, Binding = new Binding(path) { Mode = readOnly ? BindingMode.OneWay : BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, Width = width, IsReadOnly = readOnly
        };
        DataGridCheckBoxColumn Check(string header, string path, double width) => new()
        {
            Header = header, Binding = new Binding(path) { Mode = BindingMode.TwoWay }, Width = width
        };
        grid.Columns.Add(Text("Status", "StatusText", 52, readOnly: true));
        var nameColumn = new DataGridTextColumn { Header = "Name", Binding = new Binding("Name") { Mode = BindingMode.TwoWay }, Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 160 };
        grid.Columns.Add(nameColumn);
        // Controls live directly in the cells, so one click edits a cell without changing the row selection:
        // with several rows selected, the change is applied to all of them (see s_layerChanged).
        const string ns0 = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
        DataGridTemplateColumn TemplateCheck(string header, string path, double width) => new()
        {
            Header = header, Width = width,
            CellTemplate = (DataTemplate)XamlReader.Parse($"<DataTemplate {ns0}><CheckBox IsChecked=\"{{Binding {path}, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}}\" HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\"/></DataTemplate>")
        };
        DataGridTemplateColumn TemplateCombo(string header, string path, IEnumerable<string> items, double width) => new()
        {
            Header = header, Width = width,
            CellTemplate = (DataTemplate)XamlReader.Parse($"<DataTemplate {ns0}><ComboBox Margin=\"2,1\" SelectedValuePath=\"Content\" SelectedValue=\"{{Binding {path}, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}}\">{string.Concat(items.Select(i => "<ComboBoxItem>" + System.Security.SecurityElement.Escape(i) + "</ComboBoxItem>"))}</ComboBox></DataTemplate>")
        };
        grid.Columns.Add(TemplateCheck("On", "On", 40)); grid.Columns.Add(TemplateCheck("Freeze", "Freeze", 54)); grid.Columns.Add(TemplateCheck("Lock", "Lock", 44));
        const string ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
        var colorColumn = new DataGridTemplateColumn
        {
            Header = "Color", Width = 130,
            CellTemplate = (DataTemplate)XamlReader.Parse($"<DataTemplate {ns}><Button Tag=\"color\" Margin=\"2,1\" Padding=\"4,0\" HorizontalContentAlignment=\"Left\" Background=\"Transparent\" BorderThickness=\"0\" Cursor=\"Hand\"><StackPanel Orientation=\"Horizontal\"><Border Width=\"14\" Height=\"14\" BorderBrush=\"#808890\" BorderThickness=\"1\" Margin=\"0,0,6,0\" Background=\"{{Binding ColorBrush}}\"/><TextBlock Text=\"{{Binding ColorName}}\" Foreground=\"Black\"/></StackPanel></Button></DataTemplate>")
        };
        grid.Columns.Add(colorColumn);
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Linetype", Width = 140,
            CellTemplate = (DataTemplate)XamlReader.Parse($"<DataTemplate {ns}><Button Tag=\"ltype\" Margin=\"2,1\" Padding=\"4,0\" HorizontalContentAlignment=\"Left\" Background=\"Transparent\" BorderThickness=\"0\" Cursor=\"Hand\"><TextBlock Text=\"{{Binding LineType}}\" Foreground=\"Black\"/></Button></DataTemplate>")
        });
        grid.Columns.Add(TemplateCombo("Lineweight (mm)", "Lineweight", LayerLineweights, 120));
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Transparency", Width = 90,
            CellTemplate = (DataTemplate)XamlReader.Parse($"<DataTemplate {ns0}><TextBox Margin=\"2,1\" BorderThickness=\"0\" Background=\"Transparent\" VerticalContentAlignment=\"Center\" Text=\"{{Binding Transparency, Mode=TwoWay, UpdateSourceTrigger=LostFocus}}\"/></DataTemplate>")
        });
        grid.Columns.Add(TemplateCheck("Plot", "Plot", 44));
        grid.BeginningEdit += (_, e) =>
        {
            // Existing layers cannot be renamed here (native references are by name); new layers can.
            if (e.Column == nameColumn && e.Row.Item is LayerRow r && !r.IsNew) e.Cancel = true;
        };

        // Several selected layers are edited together: a change made in any cell is copied to all selected rows.
        var propagating = false;
        s_layerChanged = (source, property) =>
        {
            if (propagating || grid.SelectedItems.Count < 2 || !grid.SelectedItems.Contains(source)) return;
            propagating = true;
            try { foreach (var other in grid.SelectedItems.OfType<LayerRow>().ToArray()) if (!ReferenceEquals(other, source)) other.CopyFrom(source, property); }
            finally { propagating = false; }
        };
        dialog.Closed += (_, _) => s_layerChanged = null;

        root.Children.Add(grid);

        var view = CollectionViewSource.GetDefaultView(rows);
        search.TextChanged += (_, _) =>
        {
            var q = search.Text.Trim();
            view.Filter = string.IsNullOrEmpty(q) ? null : o => o is LayerRow r && r.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
        };

        string NewName()
        {
            var i = 1; while (rows.Any(r => r.Name.Equals($"Layer{i}", StringComparison.OrdinalIgnoreCase))) i++; return $"Layer{i}";
        }
        void AddLayer(bool vpFrozen)
        {
            var template = grid.SelectedItem as LayerRow;
            var row = new LayerRow { Name = NewName(), IsNew = true, NewVpFreeze = vpFrozen, ColorText = template?.ColorText ?? "#FFFFFF", LineType = template?.LineType ?? "Continuous", Lineweight = template?.Lineweight ?? "Default", Plot = true };
            rows.Add(row); grid.SelectedItem = row; grid.ScrollIntoView(row);
            grid.CurrentCell = new DataGridCellInfo(row, nameColumn); grid.BeginEdit();
            status.Text = "Type the new layer name.";
        }
        newBtn.Click += (_, _) => AddLayer(false);
        newVpBtn.Click += (_, _) => AddLayer(true);
        void SetCurrent()
        {
            if (grid.SelectedItem is not LayerRow target) { status.Text = "Select a layer first."; return; }
            if (!target.On || target.Freeze) { status.Text = "A frozen or off layer cannot be current."; return; }
            foreach (var r in rows) r.IsCurrent = ReferenceEquals(r, target);
            status.Text = $"Current layer: {target.Name}";
        }
        curBtn.Click += (_, _) => SetCurrent();
        grid.MouseDoubleClick += (_, e) =>
        {
            for (var o = e.OriginalSource as DependencyObject; o is not null && o is not DataGrid; o = o is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o))
            {
                if (o is TextBox or ComboBox or CheckBox or Button or System.Windows.Controls.Primitives.DataGridColumnHeader) return;
                if (o is DataGridRow row) { grid.SelectedItem = row.Item; SetCurrent(); e.Handled = true; return; }
            }
        };
        grid.AddHandler(Button.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is not Button { Tag: string tag, DataContext: LayerRow row }) return;
            if (tag == "color")
            {
                if (PickLayerColor(dialog, row.ColorText) is { } picked) row.ColorText = $"#{picked.R:X2}{picked.G:X2}{picked.B:X2}";
            }
            else if (tag == "ltype")
            {
                if (PickLayerLineType(dialog, d, row.LineType) is { } name) row.LineType = name;
            }
        }));
        delBtn.Click += (_, _) =>
        {
            var targets = grid.SelectedItems.OfType<LayerRow>().ToList();
            if (targets.Count == 0) { status.Text = "Select layers to delete."; return; }
            var skipped = new List<string>();
            foreach (var t in targets)
            {
                if (t.Name == "0" || t.Name.Equals("Defpoints", StringComparison.OrdinalIgnoreCase) || t.IsCurrent || t.ObjectCount > 0) { skipped.Add(t.Name); continue; }
                rows.Remove(t); if (!t.IsNew) deleted.Add(t.Name);
            }
            status.Text = skipped.Count == 0 ? $"Deleted {targets.Count} layer(s)." : $"Kept (layer 0, Defpoints, current or in use): {string.Join(", ", skipped.Take(6))}";
        };

        bool Apply()
        {
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            var invalid = rows.Where(r => string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 255 || r.Name.IndexOfAny(new[] { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', '=', '`' }) >= 0).ToList();
            if (invalid.Count > 0) { status.Text = $"Invalid layer name: {invalid[0].Name}"; return false; }
            var dup = rows.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (dup is not null) { status.Text = $"Duplicate layer name: {dup.Key}"; return false; }
            foreach (var row in rows)
            {
                var state = new CadLayerState
                {
                    Name = row.Name, On = row.On, Frozen = row.Freeze, Locked = row.Lock, Plot = row.Plot, NewViewportFreeze = row.NewVpFreeze,
                    LineType = row.LineType, Description = row.Description ?? string.Empty,
                    Transparency = Math.Clamp(row.Transparency, 0, 90),
                    LineWeightMm = double.TryParse(row.Lineweight, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) ? mm : null
                };
                d.LayerStates[row.Name] = state;
                TryParseLayerColor(row.ColorText, out var layerColor);
                _cadFiles.SetLayerColor(d, row.Name, layerColor);
            }
            foreach (var name in deleted) { d.LayerStates.Remove(name); d.LayerColors.Remove(name); }
            var current = rows.FirstOrDefault(r => r.IsCurrent)?.Name ?? "0";
            d.CurrentLayer = current;
            ApplyLayerVisibility(originalVisibility);
            originalVisibility.Clear();
            foreach (var s in d.LayerStates.Values.Where(x => !x.Visible)) originalVisibility.Add(s.Name);
            deleted.Clear();
            RefreshCadStyles(); RefreshPropertiesPalette();
            _activeSession.IsDirty = true; _activeSession.Revision++; RefreshDrawingTabs();
            AppendCommandHistory($"Layers updated ({rows.Count} layers; current {current}).");
            status.Text = "Applied.";
            return true;
        }
        applyBtn.Click += (_, _) => Apply();
        okBtn.Click += (_, _) => { if (Apply()) dialog.DialogResult = true; };

        dialog.Content = root;
        ConfigureEditorDialog(dialog);
        dialog.ShowDialog();
    }

    /// <summary>Colour picker: named colours, a generated palette and R,G,B entry.</summary>
    private CadColor? PickLayerColor(Window owner, string currentText)
    {
        TryParseLayerColor(currentText, out var current);
        CadColor? result = null;
        var dlg = CreateCadChildWindow("Select Color", 430, 420);
        dlg.Owner = owner;
        var panel = new StackPanel { Margin = new Thickness(12) };
        var preview = new Border { Width = 28, Height = 22, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
        var nameText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var rgbBox = new TextBox { Width = 110, Height = 24, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), ToolTip = "R,G,B (e.g. 80,45,25), #RRGGBB or colour name" };
        void Show(CadColor c)
        {
            current = c;
            preview.Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
            nameText.Text = LayerColorName(c);
            rgbBox.Text = $"{c.R},{c.G},{c.B}";
        }
        Button Swatch(CadColor c, string tip, double size)
        {
            var b = new Button { Width = size, Height = size, Margin = new Thickness(1), Padding = new Thickness(0), ToolTip = tip, Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), BorderBrush = Brushes.Gray };
            b.Click += (_, _) => Show(c);
            b.MouseDoubleClick += (_, _) => { result = c; dlg.DialogResult = true; };
            return b;
        }
        panel.Children.Add(new TextBlock { Text = "Standard colors", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        var named = new WrapPanel();
        foreach (var (n, c) in LayerNamedColors) named.Children.Add(Swatch(c, n, 28));
        panel.Children.Add(named);
        panel.Children.Add(new TextBlock { Text = "Color palette", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
        var pal = new UniformGrid { Columns = 24, Width = 24 * 16 };
        static CadColor Hsl(double h, double s, double l)
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            double F(double t) { t = (t % 1 + 1) % 1; return t < 1.0 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p; }
            return new CadColor((byte)Math.Round(F(h + 1.0 / 3) * 255), (byte)Math.Round(F(h) * 255), (byte)Math.Round(F(h - 1.0 / 3) * 255));
        }
        foreach (var l in new[] { 0.85, 0.7, 0.55, 0.4, 0.28 })
            for (var i = 0; i < 24; i++) pal.Children.Add(Swatch(Hsl(i / 24.0, 1, l), "", 14));
        for (var i = 0; i < 24; i++) { var g = (byte)(i * 255 / 23); pal.Children.Add(Swatch(new CadColor(g, g, g), "", 14)); }
        panel.Children.Add(pal);
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        line.Children.Add(new TextBlock { Text = "Color:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        line.Children.Add(preview); line.Children.Add(nameText); line.Children.Add(rgbBox);
        panel.Children.Add(line);
        rgbBox.LostFocus += (_, _) => { if (TryParseLayerColor(rgbBox.Text, out var c)) Show(c); else rgbBox.Text = $"{current.R},{current.G},{current.B}"; };
        rgbBox.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter && TryParseLayerColor(rgbBox.Text, out var c)) Show(c); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "OK", MinWidth = 80, Height = 26, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, Height = 26, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        ok.Click += (_, _) => { if (TryParseLayerColor(rgbBox.Text, out var c)) current = c; result = current; dlg.DialogResult = true; };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Show(current);
        dlg.Content = panel;
        ConfigureEditorDialog(dlg);
        return dlg.ShowDialog() == true ? result : null;
    }

    /// <summary>Linetype picker with dash previews and a "Load .lin" button.</summary>
    private string? PickLayerLineType(Window owner, CadDrawing drawing, string currentName)
    {
        string? result = null;
        var dlg = CreateCadChildWindow("Select Linetype", 460, 420);
        dlg.Owner = owner;
        var root = new DockPanel { Margin = new Thickness(12) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var load = new Button { Content = "Load .lin…", MinWidth = 90, Height = 26 };
        var ok = new Button { Content = "OK", MinWidth = 80, Height = 26, Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, Height = 26, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(load); buttons.Children.Add(ok); buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        var list = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        root.Children.Add(list);
        void Fill(string? select)
        {
            list.Items.Clear();
            var names = new[] { "Continuous" }.Concat(drawing.LineStyles.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            if (!string.IsNullOrWhiteSpace(currentName) && !names.Contains(currentName, StringComparer.OrdinalIgnoreCase)) names.Add(currentName);
            foreach (var n in names)
            {
                var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.Children.Add(new TextBlock { Text = n, VerticalAlignment = VerticalAlignment.Center });
                var ln = new System.Windows.Shapes.Line { X1 = 0, X2 = 400, Y1 = 8, Y2 = 8, Stroke = Brushes.Black, StrokeThickness = 1.5, Height = 16, Stretch = Stretch.None, ClipToBounds = true };
                if (drawing.LineStyles.TryGetValue(n, out var def) && def.Pattern.Count >= 2)
                    ln.StrokeDashArray = new DoubleCollection(def.Pattern.Select(v => Math.Max(Math.Abs(v), 0.3) / 1.5 * 1.0));
                Grid.SetColumn(ln, 1); grid.Children.Add(ln);
                var item = new ListBoxItem { Content = grid, Tag = n };
                list.Items.Add(item);
                if (string.Equals(n, select, StringComparison.OrdinalIgnoreCase)) { item.IsSelected = true; }
                item.MouseDoubleClick += (_, _) => { result = n; dlg.DialogResult = true; };
            }
        }
        Fill(currentName);
        load.Click += (_, _) =>
        {
            var open = new Microsoft.Win32.OpenFileDialog { Filter = "AutoCAD linetype files (*.lin)|*.lin|All files|*.*", Title = "Load linetypes" };
            if (open.ShowDialog(dlg) != true) return;
            var added = 0;
            try
            {
                string? name = null;
                foreach (var raw in System.IO.File.ReadLines(open.FileName))
                {
                    var t = raw.Trim();
                    if (t.Length == 0 || t[0] == ';') continue;
                    if (t[0] == '*') { name = t[1..].Split(',')[0].Trim(); continue; }
                    if (name is null || !(t[0] is 'A' or 'a')) continue;
                    var nums = new List<double>();
                    foreach (var part in t.Split(',').Skip(1))
                    {
                        var seg = part.Trim();
                        if (seg.StartsWith('[')) break;
                        if (double.TryParse(seg, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) nums.Add(v);
                    }
                    if (nums.Count >= 2 && nums.Count % 2 == 0)
                    {
                        drawing.LineStyles[name] = new CadLineStyleDefinition(name, nums.Select(v => Math.Max(Math.Abs(v), 0.3)).ToArray());
                        added++;
                    }
                    name = null;
                }
            }
            catch (Exception ex) { MessageBox.Show(dlg, ex.Message, "Load linetypes"); return; }
            Fill(currentName);
            dlg.Title = $"Select Linetype — loaded {added}";
        };
        ok.Click += (_, _) => { if (list.SelectedItem is ListBoxItem { Tag: string n }) { result = n; dlg.DialogResult = true; } };
        dlg.Content = root;
        ConfigureEditorDialog(dlg);
        return dlg.ShowDialog() == true ? result : null;
    }

    /// <summary>Hide/show entities according to layer On/Freeze, then refresh index and view.</summary>
    private void ApplyLayerVisibility(HashSet<string> previouslyHiddenLayers)
    {
        var d = _editor.Drawing;
        var changed = false;
        foreach (var entity in d.AllSpaceEntities)
        {
            if (!d.LayerStates.TryGetValue(entity.Layer, out var state)) continue;
            if (!state.Visible) { if (d.HiddenEntityIds.Add(entity.Id)) changed = true; }
            else if (previouslyHiddenLayers.Contains(entity.Layer) && d.HiddenEntityIds.Remove(entity.Id)) changed = true;
        }
        if (!changed) { RenderDrawing(false); return; }
        _selectedEntityIds.RemoveWhere(id => d.HiddenEntityIds.Contains(id));
        _drawingRevision++; _activeSession.GeometryRevision++;
        _lastRenderImage = null; _lastRenderImageKey = null; ClearFrameCache(); _lastSelectionOverlayKey = null;
        _spatialIndexDebounceTimer.Stop(); _pendingIndexSession = null;
        _activeSession.SpatialIndex = null;
        var entities = d.Entities;
        _spatialIndex = entities.Count < BackgroundIndexEntityThreshold
            ? _activeSession.SpatialIndex = new CadSpatialIndex(entities, excludedEntityIds: d.HiddenEntityIds)
            : new CadSpatialIndex(Array.Empty<CadEntity>());
        if (_activeSession.SpatialIndex is null) QueueSpatialIndexRebuild(_activeSession);
        QueueSnapCandidateBuild(_activeSession);
        RenderDrawing(false);
    }
}
