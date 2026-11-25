using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using iStCAD_1.Core.Model;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace iStCAD_1;

/// <summary>Simple style manager (Vietnam): pick a style, change one or two values (saved as you go), set current, create, delete.</summary>
public partial class MainWindow
{
    private sealed class StyleTab
    {
        public string Header = "";
        public Func<List<string>> Names = () => new();
        public Func<string> Current = () => "";
        public Action<string> SetCurrent = _ => { };
        public string[] Labels = Array.Empty<string>();
        public string[]?[] Choices = Array.Empty<string[]?>();
        public Func<string, string[]> Load = _ => Array.Empty<string>();
        public Func<string, string[], string?> Save = (_, _) => null;
        public Func<string, string?, string?> Create = (_, _) => null;
        public Func<string, string?> Delete = _ => null;
        public Func<bool> Locked = () => false;
        public bool ShowDashPreview, ShowTextPreview;
        public Func<int> Presets = () => 0;
    }

    private static readonly HashSet<string> s_builtInStyles = new(StringComparer.OrdinalIgnoreCase) { "Standard", "ISO-25", "Continuous", "Dashed", "Dotted", "Center", "ByLayer" };

    private static string? ValidateStyleName(string n)
    {
        if (string.IsNullOrWhiteSpace(n)) return "Nhập tên kiểu.";
        if (n.Length > 64 || n.IndexOfAny(new[] { '<', '>', '/', '\\', ':', ';', '?', '*', '|', '"', ',' }) >= 0) return "Tên không hợp lệ.";
        return null;
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private void StyleChanged()
    {
        if (_activeSession is not null) { _activeSession.IsDirty = true; _activeSession.Revision++; RefreshDrawingTabs(); }
        RefreshCadStyles();
    }

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private List<StyleTab> BuildStyleTabs(List<string> fonts)
    {
        var d = _editor.Drawing;
        var text = new StyleTab
        {
            Header = "Kiểu chữ", ShowTextPreview = true,
            Labels = new[] { "Font chữ", "Chiều cao chữ (0 = hỏi khi viết)" },
            Choices = new[] { fonts.ToArray(), new[] { "0", "2.5", "3.5", "5", "7" } },
            Names = () => d.TextStyles.Keys.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList(),
            Current = () => d.CurrentTextStyle, SetCurrent = n => d.CurrentTextStyle = n,
            Load = n => d.TextStyles.TryGetValue(n, out var s) ? new[] { s.FontFamily, Num(s.Height) } : new[] { "Arial", "2.5" },
            Save = (n, v) =>
            {
                if (!TryNumber(v[1], out var h) || h < 0) return "Chiều cao phải là số ≥ 0.";
                var font = string.IsNullOrWhiteSpace(v[0]) ? "Arial" : v[0].Trim();
                var previous = d.TextStyles.TryGetValue(n, out var p0) ? p0 : null;
                if (previous is not null && previous.FontFamily == font && previous.Height == h) return null;
                d.TextStyles[n] = new CadTextStyleDefinition(n, font, h, previous?.WidthFactor ?? 1);
                var old = previous?.FontFamily;
                if (old is not null && !old.Equals(font, StringComparison.OrdinalIgnoreCase))
                {
                    var remove = new List<CadEntity>(); var add = new List<CadEntity>();
                    foreach (var e in d.AllSpaceEntities)
                    {
                        if (e is CadTextEntity t && t.TextFontFamily.Equals(old, StringComparison.OrdinalIgnoreCase)) { remove.Add(t); add.Add(t with { TextFontFamily = font }); }
                        else if (e is CadMTextEntity m && m.TextFontFamily.Equals(old, StringComparison.OrdinalIgnoreCase)) { remove.Add(m); add.Add(m with { TextFontFamily = font }); }
                    }
                    if (remove.Count > 0) { _editor.ApplyEntityChanges(remove, add); AppendCommandHistory($"STYLE: {remove.Count:N0} chữ đang dùng \"{old}\" đã đổi sang \"{font}\". U để hoàn tác."); RenderDrawing(fitToDrawing: false); }
                }
                return null;
            },
            Create = (n, src) => { d.TextStyles[n] = src is not null && d.TextStyles.TryGetValue(src, out var c) ? c with { Name = n } : new CadTextStyleDefinition(n, "Arial", 2.5, 1); return null; },
            Delete = n => { if (d.TextStyles.Count <= 1) return "Phải còn ít nhất một kiểu chữ."; d.TextStyles.Remove(n); if (d.CurrentTextStyle.Equals(n, StringComparison.OrdinalIgnoreCase)) d.CurrentTextStyle = d.TextStyles.Keys.First(); return null; },
        };
        var dim = new StyleTab
        {
            Header = "Kiểu kích thước",
            Labels = new[] { "Tỉ lệ bản vẽ 1 : N  (nhập N)", "Số lẻ thập phân" },
            Choices = new[] { new[] { "1", "10", "20", "50", "100", "200", "500" }, new[] { "0", "1", "2", "3" } },
            Names = () => d.DimensionStyles.Keys.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList(),
            Current = () => d.CurrentDimensionStyle, SetCurrent = n => d.CurrentDimensionStyle = n,
            Load = n => d.DimensionStyles.TryGetValue(n, out var s) ? new[] { Num(s.Scale), s.Decimals.ToString(CultureInfo.InvariantCulture) } : new[] { "1", "0" },
            Save = (n, v) =>
            {
                if (!TryNumber(v[0], out var scale) || scale <= 0) return "Tỉ lệ phải lớn hơn 0.";
                if (!int.TryParse(v[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) || dec < 0 || dec > 8) return "Số lẻ từ 0 đến 8.";
                var old = d.DimensionStyles.TryGetValue(n, out var p0) ? p0 : null;
                if (old is not null && old.Scale == scale && old.Decimals == dec) return null;
                d.DimensionStyles[n] = old is null ? new CadDimensionStyleDefinition(n, 2.5, 2.5, scale, 0.625, 1.25, 0.625, dec) : old with { Scale = scale, Decimals = dec };
                return null;
            },
            Create = (n, src) => { d.DimensionStyles[n] = src is not null && d.DimensionStyles.TryGetValue(src, out var c) ? c with { Name = n } : new CadDimensionStyleDefinition(n, 2.5, 2.5, 1); return null; },
            Delete = n => { if (d.DimensionStyles.Count <= 1) return "Phải còn ít nhất một kiểu kích thước."; d.DimensionStyles.Remove(n); if (d.CurrentDimensionStyle.Equals(n, StringComparison.OrdinalIgnoreCase)) d.CurrentDimensionStyle = d.DimensionStyles.Keys.First(); return null; },
        };
        var line = new StyleTab
        {
            Header = "Kiểu đường", ShowDashPreview = true,
            Labels = new[] { "Nét (chọn mẫu hoặc nhập: vẽ, hở, vẽ, hở …)" },
            Choices = new[] { new[] { "6, 3", "4, 2", "12, 3, 2, 3", "12, 3, 2, 3, 2, 3", "1, 2" } },
            Names = () => new[] { "ByLayer" }.Concat(d.LineStyles.Keys.OrderBy(v => v, StringComparer.OrdinalIgnoreCase)).ToList(),
            Current = () => d.CurrentLineStyle, SetCurrent = n => d.CurrentLineStyle = n,
            Locked = () => false,
            Load = n => new[] { d.LineStyles.TryGetValue(n, out var s) ? string.Join(", ", s.Pattern.Select(Num)) : "" },
            Save = (n, v) =>
            {
                if (n.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) || n.Equals("Continuous", StringComparison.OrdinalIgnoreCase)) return null;
                var pattern = new List<double>();
                foreach (var part in v[0].Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                { if (!TryNumber(part, out var x) || x <= 0) return "Nhập độ dài lớn hơn 0, cách nhau bằng dấu phẩy."; pattern.Add(x); }
                if (pattern.Count > 32) return "Tối đa 32 đoạn.";
                if (pattern.Count % 2 == 1) pattern.Add(pattern[^1]);
                if (d.LineStyles.TryGetValue(n, out var old) && old.Pattern.SequenceEqual(pattern)) return null;
                d.LineStyles[n] = new CadLineStyleDefinition(n, pattern.ToArray());
                return null;
            },
            Create = (n, src) => { d.LineStyles[n] = src is not null && d.LineStyles.TryGetValue(src, out var c) ? c with { Name = n } : new CadLineStyleDefinition(n, new double[] { 6, 3 }); return null; },
            Delete = n => { d.LineStyles.Remove(n); if (d.CurrentLineStyle.Equals(n, StringComparison.OrdinalIgnoreCase)) d.CurrentLineStyle = "ByLayer"; return null; },
        };
        int AddPresets<T>(Dictionary<string, T> table, params (string Name, T Value)[] items) { var count = 0; foreach (var (name, value) in items) if (!table.ContainsKey(name)) { table[name] = value; count++; } return count; }
        text.Presets = () => AddPresets(d.TextStyles,
            ("TCVN-Arial", new CadTextStyleDefinition("TCVN-Arial", "Arial", 0, 1)),
            ("TCVN-Times", new CadTextStyleDefinition("TCVN-Times", "Times New Roman", 0, 1)),
            ("TCVN-Chữ 2.5", new CadTextStyleDefinition("TCVN-Chữ 2.5", "Arial", 2.5, 1)),
            ("TCVN-Chữ 3.5", new CadTextStyleDefinition("TCVN-Chữ 3.5", "Arial", 3.5, 1)),
            ("TCVN-Chữ 5", new CadTextStyleDefinition("TCVN-Chữ 5", "Arial", 5, 1)));
        dim.Presets = () => AddPresets(d.DimensionStyles,
            ("TCVN-1:10", new CadDimensionStyleDefinition("TCVN-1:10", 2.5, 2.5, 10, 0.625, 1.25, 0.625, 0)),
            ("TCVN-1:20", new CadDimensionStyleDefinition("TCVN-1:20", 2.5, 2.5, 20, 0.625, 1.25, 0.625, 0)),
            ("TCVN-1:50", new CadDimensionStyleDefinition("TCVN-1:50", 2.5, 2.5, 50, 0.625, 1.25, 0.625, 0)),
            ("TCVN-1:100", new CadDimensionStyleDefinition("TCVN-1:100", 2.5, 2.5, 100, 0.625, 1.25, 0.625, 0)),
            ("TCVN-1:200", new CadDimensionStyleDefinition("TCVN-1:200", 2.5, 2.5, 200, 0.625, 1.25, 0.625, 0)));
        line.Presets = () => AddPresets(d.LineStyles,
            ("Net-dut", new CadLineStyleDefinition("Net-dut", new double[] { 6, 3 })),
            ("Net-cham-gach", new CadLineStyleDefinition("Net-cham-gach", new double[] { 12, 3, 2, 3 })),
            ("Net-khuat", new CadLineStyleDefinition("Net-khuat", new double[] { 4, 2 })));
        return new List<StyleTab> { text, dim, line };
    }

    /// <summary>Fonts worth offering to Vietnamese users: Unicode fonts that show Vietnamese, plus installed TCVN3 (.Vn*) and VNI fonts.</summary>
    private static List<string> VietnameseFonts()
    {
        var all = System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).ToList();
        var wanted = new[] { "Arial", "Times New Roman", "Tahoma", "Calibri", "Cambria", "Verdana", "Segoe UI", "Consolas", "Courier New" };
        var list = all.Where(f => wanted.Contains(f, StringComparer.OrdinalIgnoreCase) || f.StartsWith(".Vn", StringComparison.OrdinalIgnoreCase) || f.StartsWith("VNI-", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count >= 3 ? list : all.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private sealed class StyleRow : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        private string _name = "", _a = "", _b = "", _mark = "";
        public string OriginalName { get; set; } = "";
        public string Name { get => _name; set { _name = value; Raise(nameof(Name)); } }
        public string A { get => _a; set { _a = value; Raise(nameof(A)); Raise(nameof(Dash)); Raise(nameof(Sample)); } }
        public string B { get => _b; set { _b = value; Raise(nameof(B)); } }
        public string Mark { get => _mark; set { _mark = value; Raise(nameof(Mark)); } }
        public bool Locked { get; set; }
        public bool BuiltIn { get; set; }
        public FontFamily Sample { get { try { return new FontFamily(string.IsNullOrWhiteSpace(_a) ? "Arial" : _a); } catch { return new FontFamily("Arial"); } } }
        public DoubleCollection? Dash
        {
            get
            {
                var p = _a.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => TryNumber(x, out var v) && v > 0 ? v / 2 : 0).ToList();
                if (p.Count < 2 || p.Any(x => x <= 0)) return null;
                if (p.Count % 2 == 1) p.Add(p[^1]);
                return new DoubleCollection(p);
            }
        }
    }

    /// <summary>One table per style kind (the same look as Layers and Aliases): edit a cell and it is saved, add and delete rows with the buttons.</summary>
    private void OpenCadStyleManager(int selectedCategory)
    {
        if (selectedCategory <= 0) { ShowLayerManager(); return; }
        var dialog = CreateCadChildWindow("Quản lý kiểu — chữ, kích thước, đường nét", 700, 460);
        var status = new TextBlock { Margin = new Thickness(14, 0, 14, 8), TextWrapping = TextWrapping.Wrap, MinHeight = 18, Foreground = Brushes.DimGray,
            Text = "Sửa trực tiếp trong bảng (tự lưu). Bấm đúp vào cột ✔ để dùng kiểu đó." };
        var specs = BuildStyleTabs(VietnameseFonts());
        foreach (var spec in specs) spec.Presets();
        var tabs = new TabControl { Margin = new Thickness(10) };

        foreach (var spec in specs)
        {
            var rows = new System.Collections.ObjectModel.ObservableCollection<StyleRow>();
            StyleRow MakeRow(string n)
            {
                var v = spec.Load(n);
                return new StyleRow { Name = n, OriginalName = n, A = v.Length > 0 ? v[0] : "", B = v.Length > 1 ? v[1] : "", BuiltIn = s_builtInStyles.Contains(n),
                    Locked = n.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) || n.Equals("Continuous", StringComparison.OrdinalIgnoreCase),
                    Mark = n.Equals(spec.Current(), StringComparison.OrdinalIgnoreCase) ? "✔" : "" };
            }
            void Fill() { rows.Clear(); foreach (var n in spec.Names()) rows.Add(MakeRow(n)); }
            Fill();
            void MarkCurrent() { var cur = spec.Current(); foreach (var r in rows) r.Mark = r.Name.Equals(cur, StringComparison.OrdinalIgnoreCase) ? "✔" : ""; }

            var grid = new DataGrid
            {
                ItemsSource = rows, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single,
                HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, RowHeight = 28, ColumnHeaderHeight = 30,
                HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(225, 229, 235)), Margin = new Thickness(0, 8, 0, 0)
            };
            grid.Columns.Add(new DataGridTextColumn { Header = "", Binding = new System.Windows.Data.Binding(nameof(StyleRow.Mark)) { Mode = System.Windows.Data.BindingMode.OneWay }, IsReadOnly = true, Width = 34 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Tên kiểu", Binding = new System.Windows.Data.Binding(nameof(StyleRow.Name)), Width = new DataGridLength(1.2, DataGridLengthUnitType.Star) });
            if (spec.ShowTextPreview)
            {
                var fontList = spec.Choices[0]!.Concat(rows.Select(r => r.A)).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                grid.Columns.Add(new DataGridComboBoxColumn { Header = spec.Labels[0], ItemsSource = fontList, SelectedItemBinding = new System.Windows.Data.Binding(nameof(StyleRow.A)), Width = new DataGridLength(1.4, DataGridLengthUnitType.Star) });
            }
            else grid.Columns.Add(new DataGridTextColumn { Header = spec.Labels[0], Binding = new System.Windows.Data.Binding(nameof(StyleRow.A)), Width = new DataGridLength(spec.ShowDashPreview ? 1.4 : 1, DataGridLengthUnitType.Star) });
            if (spec.Labels.Length > 1)
                grid.Columns.Add(new DataGridTextColumn { Header = spec.Labels[1], Binding = new System.Windows.Data.Binding(nameof(StyleRow.B)), Width = new DataGridLength(0.9, DataGridLengthUnitType.Star) });
            if (spec.ShowTextPreview)
            {
                var cell = new FrameworkElementFactory(typeof(TextBlock));
                cell.SetValue(TextBlock.TextProperty, "AaBb Tiếng Việt");
                cell.SetBinding(TextBlock.FontFamilyProperty, new System.Windows.Data.Binding(nameof(StyleRow.Sample)) { Mode = System.Windows.Data.BindingMode.OneWay });
                cell.SetValue(TextBlock.FontSizeProperty, 15d);
                cell.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0));
                grid.Columns.Add(new DataGridTemplateColumn { Header = "Mẫu", IsReadOnly = true, Width = new DataGridLength(1.2, DataGridLengthUnitType.Star), CellTemplate = new DataTemplate { VisualTree = cell } });
            }
            if (spec.ShowDashPreview)
            {
                var ln = new FrameworkElementFactory(typeof(System.Windows.Shapes.Line));
                ln.SetValue(System.Windows.Shapes.Line.X2Property, 160d);
                ln.SetValue(System.Windows.Shapes.Shape.StrokeProperty, Brushes.Black);
                ln.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 2d);
                ln.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                ln.SetBinding(System.Windows.Shapes.Shape.StrokeDashArrayProperty, new System.Windows.Data.Binding(nameof(StyleRow.Dash)) { Mode = System.Windows.Data.BindingMode.OneWay });
                grid.Columns.Add(new DataGridTemplateColumn { Header = "Xem trước", IsReadOnly = true, Width = new DataGridLength(1.2, DataGridLengthUnitType.Star), CellTemplate = new DataTemplate { VisualTree = ln } });
            }

            grid.BeginningEdit += (_, e) =>
            {
                if (e.Row.Item is not StyleRow r) return;
                var isName = ReferenceEquals(e.Column, grid.Columns[1]);
                if (r.Locked || (isName && r.BuiltIn)) { e.Cancel = true; status.Text = r.Locked ? "Kiểu này cố định." : "Không đổi tên kiểu mặc định."; }
            };
            void CommitRow(StyleRow r)
            {
                try
                {
                    var name = r.Name.Trim();
                    if (!name.Equals(r.OriginalName, StringComparison.Ordinal))
                    {
                        var err = ValidateStyleName(name) ?? (spec.Names().Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase) && !x.Equals(r.OriginalName, StringComparison.OrdinalIgnoreCase)) ? "Tên này đã có." : null);
                        if (err is not null) { status.Text = err; r.Name = r.OriginalName; return; }
                        var wasCurrent = spec.Current().Equals(r.OriginalName, StringComparison.OrdinalIgnoreCase);
                        spec.Create(name, r.OriginalName); spec.Delete(r.OriginalName);
                        if (wasCurrent) spec.SetCurrent(name);
                        r.OriginalName = name; r.Name = name;
                    }
                    var saveErr = spec.Save(r.Name, spec.Labels.Length > 1 ? new[] { r.A, r.B } : new[] { r.A });
                    if (saveErr is not null) { status.Text = saveErr; var v = spec.Load(r.Name); r.A = v.Length > 0 ? v[0] : r.A; if (v.Length > 1) r.B = v[1]; return; }
                    StyleChanged(); MarkCurrent(); status.Text = $"Đã lưu {r.Name}.";
                }
                catch (Exception ex) { status.Text = ex.Message; }
            }
            grid.CellEditEnding += (_, e) =>
            {
                if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not StyleRow r) return;
                grid.Dispatcher.BeginInvoke(new Action(() => CommitRow(r)), System.Windows.Threading.DispatcherPriority.Background);
            };
            void UseSelected() { if (grid.SelectedItem is not StyleRow r) return; spec.SetCurrent(r.Name); StyleChanged(); MarkCurrent(); status.Text = $"Đang dùng: {r.Name}."; }
            grid.MouseDoubleClick += (_, e) => { if (e.OriginalSource is DependencyObject o && FindParent<DataGridRow>(o) is not null && grid.CurrentCell.Column?.Header as string == "") UseSelected(); };

            Button Btn(string caption, Action act) { var b = new Button { Content = caption, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 4, 14, 4) }; b.Click += (_, _) => { try { act(); } catch (Exception ex) { status.Text = ex.Message; } }; return b; }
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            bar.Children.Add(Btn("＋ Thêm kiểu", () =>
            {
                var source = (grid.SelectedItem as StyleRow)?.Name; var baseName = (source is null || source == "ByLayer" ? "Kieu" : source) + "-";
                var k = 2; while (spec.Names().Any(x => x.Equals(baseName + k, StringComparison.OrdinalIgnoreCase))) k++;
                var n = baseName + k;
                spec.Create(n, source == "ByLayer" ? null : source); StyleChanged();
                var row = MakeRow(n); rows.Add(row); grid.SelectedItem = row; grid.ScrollIntoView(row);
                grid.CurrentCell = new DataGridCellInfo(row, grid.Columns[1]); grid.Focus(); grid.BeginEdit();
                status.Text = "Gõ tên kiểu mới rồi nhấn Enter.";
            }));
            bar.Children.Add(Btn("🗑 Xóa kiểu", () =>
            {
                if (grid.SelectedItem is not StyleRow r) return;
                if (r.BuiltIn) { status.Text = "Không xóa được kiểu mặc định."; return; }
                if (MessageBox.Show(dialog, $"Xóa kiểu \"{r.Name}\"?", spec.Header, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                var err = spec.Delete(r.Name);
                if (err is not null) { status.Text = err; return; }
                rows.Remove(r); StyleChanged(); MarkCurrent(); status.Text = $"Đã xóa {r.Name}.";
            }));
            bar.Children.Add(Btn("✔ Dùng kiểu này", UseSelected));

            var panel = new DockPanel { Margin = new Thickness(10) };
            DockPanel.SetDock(bar, Dock.Top);
            panel.Children.Add(bar); panel.Children.Add(grid);
            tabs.Items.Add(new TabItem { Header = spec.Header, Content = panel });
        }
        tabs.SelectedIndex = Math.Clamp(selectedCategory - 1, 0, tabs.Items.Count - 1);
        var root = new DockPanel();
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status); root.Children.Add(tabs);
        dialog.Content = root; dialog.ShowDialog(); DrawingCanvas.Focus();
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var o = child; o is not null; o = VisualTreeHelper.GetParent(o) ?? LogicalTreeHelper.GetParent(o)) if (o is T t) return t;
        return null;
    }

    // ---- Child windows: one look (white, black text) and remembered size / position for every form. ----

    private static string ChildLayoutPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iStCAD_1", "window-layout.json");
    private static Dictionary<string, double[]>? s_childLayouts;

    private static string ChildLayoutKey(string title)
    {
        var cut = title.IndexOfAny(new[] { '—', '-', ':' });
        return (cut > 0 ? title[..cut] : title).Trim();
    }

    private static Dictionary<string, double[]> ChildLayouts()
    {
        if (s_childLayouts is not null) return s_childLayouts;
        s_childLayouts = new(StringComparer.OrdinalIgnoreCase);
        try { s_childLayouts = JsonSerializer.Deserialize<Dictionary<string, double[]>>(System.IO.File.ReadAllText(ChildLayoutPath)) ?? s_childLayouts; } catch { }
        return s_childLayouts;
    }

    private static void RememberChildWindow(Window dialog)
    {
        var key = ChildLayoutKey(dialog.Title);
        dialog.Closing += (_, _) =>
        {
            if (dialog.WindowState != WindowState.Normal || dialog.Width < 100 || dialog.Height < 80) return;
            var layouts = ChildLayouts();
            layouts[key] = new[] { dialog.Left, dialog.Top, dialog.Width, dialog.Height };
            try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ChildLayoutPath)!); System.IO.File.WriteAllText(ChildLayoutPath, JsonSerializer.Serialize(layouts)); } catch { }
        };
        if (ChildLayouts().TryGetValue(key, out var v) && v.Length == 4 && v.All(double.IsFinite))
        {
            var fits = v[0] + 60 > SystemParameters.VirtualScreenLeft && v[1] >= SystemParameters.VirtualScreenTop - 1
                && v[0] < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 60 && v[1] < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 60;
            if (!fits) return;
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Left = v[0]; dialog.Top = v[1];
            if (dialog.ResizeMode != ResizeMode.NoResize) { dialog.Width = Math.Max(v[2], 200); dialog.Height = Math.Max(v[3], 120); }
        }
    }
}
