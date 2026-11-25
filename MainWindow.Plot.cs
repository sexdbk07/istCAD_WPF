using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;
using iStCAD_1.Rendering;
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;
using IOPath = System.IO.Path;

namespace iStCAD_1;

public partial class MainWindow
{
    private static readonly (string Name, double Short, double Long)[] PlotPapers =
    {
        ("ISO A0", 841, 1189), ("ISO A1", 594, 841), ("ISO A2", 420, 594), ("ISO A3", 297, 420), ("ISO A4", 210, 297), ("ISO A5", 148, 210),
        ("ANSI A (Letter)", 215.9, 279.4), ("ANSI B (Tabloid)", 279.4, 431.8), ("ANSI C", 431.8, 558.8), ("ANSI D", 558.8, 863.6), ("Custom", 297, 420)
    };

    private static readonly (string Name, double PaperMm, double Units)[] PlotScales =
    {
        ("Fit to paper", 0, 0), ("1:1", 1, 1), ("1:2", 1, 2), ("1:5", 1, 5), ("1:10", 1, 10), ("1:20", 1, 20), ("1:50", 1, 50), ("1:100", 1, 100),
        ("1:200", 1, 200), ("1:500", 1, 500), ("1:1000", 1, 1000), ("2:1", 2, 1), ("5:1", 5, 1), ("Custom", -1, -1)
    };

    private static string PlotSetupsPath => IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iStCAD_1", "plot-setups.json");

    private static List<PlotSetup> LoadPlotSetups()
    {
        try
        {
            if (File.Exists(PlotSetupsPath)) return JsonSerializer.Deserialize<List<PlotSetup>>(File.ReadAllText(PlotSetupsPath)) ?? new();
        }
        catch { }
        return new();
    }

    private static void SavePlotSetups(List<PlotSetup> setups)
    {
        try
        {
            Directory.CreateDirectory(IOPath.GetDirectoryName(PlotSetupsPath)!);
            File.WriteAllText(PlotSetupsPath, JsonSerializer.Serialize(setups, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static string D(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static bool TryD(string? text, out double value) => double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static string RulesToText(PlotSetup setup) => string.Join(Environment.NewLine, setup.LayerRules.Select(r =>
        $"{r.Pattern} | {(r.Include ? "plot" : "skip")} | {r.ColorHex ?? ""} | {(r.LineWeightMm is { } w ? D(w) : "")}"));

    private static List<PlotLayerRule> ParseRules(string text)
    {
        var rules = new List<PlotLayerRule>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') && !line.Contains('|')) continue;
            var parts = line.Split('|').Select(p => p.Trim()).ToArray();
            var rule = new PlotLayerRule { Pattern = parts[0], Include = parts.Length < 2 || !(parts[1].StartsWith("skip", StringComparison.OrdinalIgnoreCase) || parts[1].StartsWith("no", StringComparison.OrdinalIgnoreCase) || parts[1].StartsWith("off", StringComparison.OrdinalIgnoreCase)) };
            if (parts.Length > 2 && parts[2].Length > 0) rule.ColorHex = parts[2];
            if (parts.Length > 3 && TryD(parts[3], out var w) && w > 0) rule.LineWeightMm = w;
            if (rule.Pattern.Length > 0) rules.Add(rule);
        }
        return rules;
    }

    private static string PensToText(PlotSetup setup) => string.Join(Environment.NewLine, setup.PenRules.Select(r => $"{r.ColorHex} | {r.OutputColorHex ?? ""} | {D(r.LineWeightMm)}"));

    private static List<PlotPenRule> ParsePens(string text)
    {
        var pens = new List<PlotPenRule>();
        foreach (var raw in text.Split('\n'))
        {
            var parts = raw.Trim().Split('|').Select(p => p.Trim()).ToArray();
            if (parts.Length == 0 || parts[0].Length == 0) continue;
            var pen = new PlotPenRule { ColorHex = parts[0] };
            if (parts.Length > 1 && parts[1].Length > 0) pen.OutputColorHex = parts[1];
            if (parts.Length > 2 && TryD(parts[2], out var w) && w > 0) pen.LineWeightMm = w;
            pens.Add(pen);
        }
        return pens;
    }

    private PlotView CurrentPlotView() => new(_viewCenter.X, _viewCenter.Y, _viewScale, Math.Max(1, DrawingCanvas.ActualWidth), Math.Max(1, DrawingCanvas.ActualHeight));

    private static BitmapSource RenderPlotPreview(PlotSheet sheet, int pixelWidth)
    {
        var scale = pixelWidth / sheet.WidthMm;
        var pixelHeight = Math.Max(1, (int)Math.Round(sheet.HeightMm * scale));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, pixelWidth, pixelHeight));
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(190, 190, 190)) { Opacity = 0.9 }, 1) { DashStyle = DashStyles.Dash },
                new Rect(sheet.Printable.X0 * scale, (sheet.HeightMm - sheet.Printable.Y1) * scale, (sheet.Printable.X1 - sheet.Printable.X0) * scale, (sheet.Printable.Y1 - sheet.Printable.Y0) * scale));
            foreach (var item in sheet.Items)
            {
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                    for (var f = 0; f < item.Figures.Count; f++)
                    {
                        var p = item.Figures[f];
                        ctx.BeginFigure(new Point(p[0] * scale, (sheet.HeightMm - p[1]) * scale), item.Fill, item.Closed[f]);
                        var pts = new List<Point>();
                        for (var k = 2; k + 1 < p.Length; k += 2) pts.Add(new Point(p[k] * scale, (sheet.HeightMm - p[k + 1]) * scale));
                        if (pts.Count > 0) ctx.PolyLineTo(pts, true, false);
                    }
                geometry.FillRule = item.EvenOdd ? FillRule.EvenOdd : FillRule.Nonzero;
                geometry.Freeze();
                var brush = new SolidColorBrush(Color.FromRgb(item.R, item.G, item.B)); brush.Freeze();
                var pen = item.Fill ? null : new Pen(brush, Math.Max(0.6, item.WidthMm * scale));
                if (item.Clip is { } c) dc.PushClip(new RectangleGeometry(new Rect(c.X0 * scale, (sheet.HeightMm - c.Y1) * scale, (c.X1 - c.X0) * scale, (c.Y1 - c.Y0) * scale)));
                dc.DrawGeometry(item.Fill ? brush : null, pen, geometry);
                if (item.Clip is not null) dc.Pop();
            }
            // Real text lines: the PDF embeds these as editable text; the preview draws them with the same placement.
            foreach (var run in sheet.Texts)
            {
                if (run.EmMm <= 0 || string.IsNullOrEmpty(run.Text)) continue;
                try
                {
                    var typeface = new Typeface(new FontFamily(run.FontFamily), run.Italic ? FontStyles.Italic : FontStyles.Normal, run.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
                    var brush = new SolidColorBrush(Color.FromRgb(run.R, run.G, run.B)); brush.Freeze();
                    var ft = new FormattedText(run.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, run.EmMm * scale, brush, 1.0);
                    double cos = Math.Cos(run.RotationRad), sin = Math.Sin(run.RotationRad), tan = Math.Tan(run.ObliqueRad);
                    // Paper is y-up, the bitmap y-down: x axis = (wf cos, -wf sin); "down" axis = -(up) with up = (tan cos - sin, tan sin + cos).
                    var matrix = new Matrix(run.WidthFactor * cos, -run.WidthFactor * sin, -(tan * cos - sin), tan * sin + cos,
                        run.X * scale, (sheet.HeightMm - run.Y) * scale);
                    if (run.Clip.X1 > run.Clip.X0 && run.Clip.Y1 > run.Clip.Y0)
                        dc.PushClip(new RectangleGeometry(new Rect(run.Clip.X0 * scale, (sheet.HeightMm - run.Clip.Y1) * scale, (run.Clip.X1 - run.Clip.X0) * scale, (run.Clip.Y1 - run.Clip.Y0) * scale)));
                    dc.PushTransform(new MatrixTransform(matrix));
                    dc.DrawText(ft, new Point(0, -ft.Baseline));
                    dc.Pop();
                    if (run.Clip.X1 > run.Clip.X0 && run.Clip.Y1 > run.Clip.Y0) dc.Pop();
                }
                catch { }
            }
        }
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        return bitmap;
    }

    /// <summary>AutoCAD Color Index (1-255) to RGB: 1-9 fixed, 10-249 = 24 hues x (5 values x full/half saturation), 250-255 grays.</summary>
    private static CadColor AciToRgb(int index)
    {
        switch (index)
        {
            case 1: return new CadColor(255, 0, 0); case 2: return new CadColor(255, 255, 0); case 3: return new CadColor(0, 255, 0);
            case 4: return new CadColor(0, 255, 255); case 5: return new CadColor(0, 0, 255); case 6: return new CadColor(255, 0, 255);
            case 7: return new CadColor(255, 255, 255); case 8: return new CadColor(128, 128, 128); case 9: return new CadColor(192, 192, 192);
        }
        if (index >= 250) { var g = new byte[] { 51, 91, 132, 173, 214, 255 }[Math.Clamp(index - 250, 0, 5)]; return new CadColor(g, g, g); }
        var n = Math.Clamp(index, 10, 249) - 10;
        var hue = n / 10 * 15.0; var position = n % 10;
        var value = new[] { 1.0, 0.65, 0.5, 0.3, 0.15 }[position / 2]; var saturation = position % 2 == 0 ? 1.0 : 0.5;
        double c = value * saturation, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = value - c;
        (double r, double g2, double b) = hue switch { < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x), < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x) };
        return new CadColor((byte)Math.Round((r + m) * 255), (byte)Math.Round((g2 + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private static readonly PlotColorMode[] PlotStyleModes = { PlotColorMode.Color, PlotColorMode.Grayscale, PlotColorMode.Monochrome, PlotColorMode.PenTable };

    /// <summary>Plot style settings that can be saved to / loaded from a file.</summary>
    private sealed class PlotStyleFile
    {
        public string Name { get; set; } = "";
        public PlotColorMode ColorMode { get; set; } = PlotColorMode.Monochrome;
        public bool PlotLineweights { get; set; } = true;
        public bool ScaleLineweights { get; set; }
        public double DefaultLineWeightMm { get; set; } = 0.25;
        public List<PlotPenRule>? PenRules { get; set; } = new();
    }

    private void ShowPlotDialog()
    {
        var drawing = _editor.Drawing;
        var layoutName = _activeLayout;
        var isModel = layoutName.Equals("Model", StringComparison.OrdinalIgnoreCase);
        PlotSetup setup;
        if (drawing.PlotSetups.TryGetValue(layoutName, out var own)) setup = own.Clone();
        else
        {
            setup = new PlotSetup { Name = "Standard", ColorMode = PlotColorMode.Monochrome, PlotLineweights = true, FitToPaper = true, CenterPlot = true, OpenAfterPlot = true };
            setup.Area = isModel ? PlotAreaKind.Extents : PlotAreaKind.Layout;
        }
        Rect? extentsCache = null;
        var updating = false;
        Action schedule = () => { };
        Action startWindowPick = () => { };
        var previewTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        var window = new double[] { setup.WindowMinX, setup.WindowMinY, setup.WindowMaxX, setup.WindowMaxY };
        var pens = setup.PenRules.ToDictionary(r => (r.ColorHex ?? "").ToUpperInvariant(), r => new PlotPenRule { ColorHex = r.ColorHex, OutputColorHex = r.OutputColorHex, LineWeightMm = r.LineWeightMm }, StringComparer.OrdinalIgnoreCase);

        var dialog = CreateCadChildWindow($"Plot (PDF) - {layoutName}", 900, isModel ? 700 : 830);
        dialog.ResizeMode = ResizeMode.CanResize; dialog.MinWidth = 860; dialog.MinHeight = isModel ? 640 : 760;
        dialog.Background = new SolidColorBrush(Color.FromRgb(238, 241, 245));

        // ---- small UI toolkit: cards, segmented controls ------------------------------------------------------------------
        var accent = new SolidColorBrush(Color.FromRgb(37, 99, 235)); accent.Freeze();
        var ink = new SolidColorBrush(Color.FromRgb(31, 41, 55)); ink.Freeze();
        var muted = new SolidColorBrush(Color.FromRgb(100, 110, 125)); muted.Freeze();
        var line = new SolidColorBrush(Color.FromRgb(210, 216, 224)); line.Freeze();
        Border Card(Panel column, string title, out StackPanel body)
        {
            body = new StackPanel();
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = muted, Margin = new Thickness(0, 0, 0, 8) });
            stack.Children.Add(body);
            var card = new Border { Background = Brushes.White, BorderBrush = line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8), Child = stack };
            column.Children.Add(card); return card;
        }
        StackPanel Row(Panel parent, double top = 4) { var r = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, top, 0, 0) }; parent.Children.Add(r); return r; }
        TextBlock Lbl(string t, double left = 0) => new() { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left, 0, 6, 0), Foreground = muted };
        TextBox Box(string t, double w = 56) => new() { Text = t, Width = w, Height = 24, Margin = new Thickness(0, 0, 4, 0), VerticalContentAlignment = VerticalAlignment.Center };
        CheckBox Tick(string t, bool c) => new() { Content = t, IsChecked = c, Margin = new Thickness(0, 4, 12, 2), Foreground = ink };
        (Border Root, Func<int> Get, Action<int> Set) Segmented(string[] items, int selected, Action changed, Action<int>? clicked = null)
        {
            var current = selected; var buttons = new List<ToggleButton>();
            var grid = new UniformGrid { Rows = 1 };
            void Paint()
            {
                for (var i = 0; i < buttons.Count; i++)
                {
                    var on = i == current; buttons[i].IsChecked = on;
                    buttons[i].Background = on ? accent : Brushes.White; buttons[i].Foreground = on ? Brushes.White : ink;
                }
            }
            for (var i = 0; i < items.Length; i++)
            {
                var index = i;
                var b = new ToggleButton { Content = items[i], Height = 26, Padding = new Thickness(8, 0, 8, 0), BorderThickness = new Thickness(0, 0, i < items.Length - 1 ? 1 : 0, 0), BorderBrush = line, FontSize = 12 };
                b.Click += (_, _) => { current = index; Paint(); changed(); clicked?.Invoke(index); };
                buttons.Add(b); grid.Children.Add(b);
            }
            Paint();
            var root = new Border { BorderBrush = line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = grid, HorizontalAlignment = HorizontalAlignment.Left };
            return (root, () => current, v => { current = Math.Clamp(v, 0, items.Length - 1); Paint(); });
        }

        // ---- layout ---------------------------------------------------------------------------------------------------------
        var root = new DockPanel { Margin = new Thickness(14) };
        System.Windows.Documents.TextElement.SetFontSize(root, 12);
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        Button Btn(string t, bool primary = false, double w = 96)
        {
            var b = new Button { Content = t, MinWidth = w, Height = 30, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 0, 12, 0), IsDefault = primary };
            if (primary) { b.Background = accent; b.Foreground = Brushes.White; b.BorderBrush = accent; }
            return b;
        }
        var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = muted, TextTrimming = TextTrimming.CharacterEllipsis };
        var allBtn = Btn("All layouts → PDF", w: 130); var applyBtn = Btn("Apply to layout", w: 110); var okBtn = Btn("Plot PDF", primary: true); var closeBtn = Btn("Close", w: 80);
        var footerRight = new StackPanel { Orientation = Orientation.Horizontal };
        footerRight.Children.Add(allBtn); if (!isModel) footerRight.Children.Add(applyBtn); footerRight.Children.Add(okBtn); footerRight.Children.Add(closeBtn);
        DockPanel.SetDock(footerRight, Dock.Right); footer.Children.Add(footerRight); footer.Children.Add(status);

        var page = new Grid();
        page.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(470) });
        page.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // Left column: Paper / Plot layout (auto height) and the Plot style card, which takes the remaining height so it ends level with the preview.
        var leftHost = new Grid { Margin = new Thickness(0, 0, 10, 0) };
        leftHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); leftHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var leftCol = new StackPanel(); Grid.SetRow(leftCol, 0); leftHost.Children.Add(leftCol);
        Grid.SetColumn(leftHost, 0); page.Children.Add(leftHost);
        root.Children.Add(page);

        // Paper
        Card(leftCol, "Paper", out var bPaper);
        var paper = new ComboBox { ItemsSource = PlotPapers.Select(p => $"{p.Name} ({D(p.Short)} x {D(p.Long)} MM)").ToArray(), Width = 258, Height = 26, Margin = new Thickness(0, 0, 8, 0) };
        var idx = Array.FindIndex(PlotPapers, p => p.Name == setup.PaperName); paper.SelectedIndex = idx < 0 ? PlotPapers.Length - 1 : idx;
        var marginBox = Box(D(setup.MarginLeftMm), 44);
        var rPaper = Row(bPaper, 0); rPaper.Children.Add(paper); rPaper.Children.Add(Lbl("Margin")); rPaper.Children.Add(marginBox); rPaper.Children.Add(Lbl("mm", 0));
        var shortBox = Box(D(setup.PaperShortMm)); var longBox = Box(D(setup.PaperLongMm));
        var rCustom = Row(bPaper); rCustom.Children.Add(Lbl("Custom size")); rCustom.Children.Add(shortBox); rCustom.Children.Add(Lbl("×")); rCustom.Children.Add(longBox); rCustom.Children.Add(Lbl("mm", 0));
        var orientIndex = setup.Landscape ? 2 : 1;
        var orient = Segmented(new[] { "Auto", "Portrait", "Landscape" }, isModel ? 0 : orientIndex, () => schedule());
        var rOrient = Row(bPaper, 8); rOrient.Children.Add(Lbl("Orientation")); rOrient.Children.Add(orient.Root);

        // Area
        Card(leftCol, "Plot layout", out var bArea);
        var areaNames = isModel ? new[] { "Extents", "Display", "Window" } : new[] { "Layout", "Extents", "Display", "Window" };
        var areaKinds = isModel ? new[] { PlotAreaKind.Extents, PlotAreaKind.Display, PlotAreaKind.Window } : new[] { PlotAreaKind.Layout, PlotAreaKind.Extents, PlotAreaKind.Display, PlotAreaKind.Window };
        var area = Segmented(areaNames, Math.Max(0, Array.IndexOf(areaKinds, setup.Area)), () => schedule(),
            clicked: index => { if (areaKinds[index] == PlotAreaKind.Window) startWindowPick(); }); // choosing Window starts the rectangle pick right away
        var rArea = Row(bArea, 0); rArea.Children.Add(area.Root);
        var windowText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = ink, FontSize = 11.5 };
        var rWindow = Row(bArea, 8); rWindow.Children.Add(windowText);

        // Scale & position
        var bScale = bArea; // one card: what to plot + scale + position
        var fit = Tick("Fit to paper", setup.FitToPaper);
        var scaleChoices = PlotScales.Skip(1).ToArray();
        var scale = new ComboBox { ItemsSource = scaleChoices.Select(q => q.Name).ToArray(), Width = 100, Height = 24, Margin = new Thickness(0, 0, 6, 0) };
        var scaleIdx = setup.FitToPaper ? 0 : Array.FindIndex(scaleChoices, q => Math.Abs(q.PaperMm - setup.ScalePaperMm) < 1e-9 && Math.Abs(q.Units - setup.ScaleDrawingUnits) < 1e-9);
        scale.SelectedIndex = scaleIdx < 0 ? scaleChoices.Length - 1 : scaleIdx;
        var sMm = Box(D(setup.ScalePaperMm), 46); var sUnits = Box(D(setup.ScaleDrawingUnits), 46);
        var scaleRead = new TextBlock { Foreground = muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontSize = 11.5 };
        var rScale = Row(bScale, 10); rScale.Children.Add(fit); rScale.Children.Add(scale); rScale.Children.Add(sMm); rScale.Children.Add(Lbl("mm =")); rScale.Children.Add(sUnits); rScale.Children.Add(Lbl("units", 0));
        var scaleWeights = Tick("Scale lineweights", setup.ScaleLineweights);
        var center = Tick("Center the plot", setup.CenterPlot);
        var offX = Box(D(setup.OffsetXMm), 46); var offY = Box(D(setup.OffsetYMm), 46);
        var rPos = Row(bScale, 4); rPos.Children.Add(center); rPos.Children.Add(Lbl("X")); rPos.Children.Add(offX); rPos.Children.Add(Lbl("Y")); rPos.Children.Add(offY); rPos.Children.Add(Lbl("mm", 0)); rPos.Children.Add(scaleRead);
        bScale.Children.Add(scaleWeights);

        // Output / plot style (inline pen-table editor)
        var bStyle = new StackPanel();
        var styleGrid = new Grid();
        styleGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); styleGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); styleGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var styleTitle = new TextBlock { Text = "PLOT STYLE", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = muted, Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(styleTitle, 0); Grid.SetRow(bStyle, 1); styleGrid.Children.Add(styleTitle); styleGrid.Children.Add(bStyle);
        var styleCard = new Border { Background = Brushes.White, BorderBrush = line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Child = styleGrid };
        Grid.SetRow(styleCard, 1); leftHost.Children.Add(styleCard);
        var style = Segmented(new[] { "Color", "Grayscale", "Black", "Custom pens" }, Math.Max(0, Array.IndexOf(PlotStyleModes, setup.ColorMode)), () => schedule());
        var styleName = new TextBox { Height = 24, VerticalContentAlignment = VerticalAlignment.Center, Text = string.IsNullOrWhiteSpace(setup.Name) ? "Standard" : setup.Name, ToolTip = "Name of this plot style (used as the default file name when saving)" };
        var saveStyleBtn = new Button { Content = "Save Style", Height = 24, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Save these plot style settings (color mode, lineweights, pen table) to a file" };
        var loadStyleBtn = new Button { Content = "Load Style", Height = 24, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Load plot style settings from a file" };
        var rName = new DockPanel { LastChildFill = true };
        var nameLabel = Lbl("Name"); DockPanel.SetDock(nameLabel, Dock.Left); rName.Children.Add(nameLabel);
        DockPanel.SetDock(loadStyleBtn, Dock.Right); DockPanel.SetDock(saveStyleBtn, Dock.Right);
        rName.Children.Add(loadStyleBtn); rName.Children.Add(saveStyleBtn); rName.Children.Add(styleName);
        bStyle.Children.Add(rName);
        var rStyle = Row(bStyle, 8); rStyle.Children.Add(style.Root);
        var plotWeights = Tick("Plot object lineweights", setup.PlotLineweights);
        var defWeight = Box(D(setup.DefaultLineWeightMm), 44);
        var openAfter = Tick("Open PDF after plot", setup.OpenAfterPlot);
        var rOpts = Row(bStyle, 6); rOpts.Children.Add(plotWeights); rOpts.Children.Add(Lbl("default")); rOpts.Children.Add(defWeight); rOpts.Children.Add(Lbl("mm", 0)); 
        bStyle.Children.Add(openAfter);
        // Inline pen table: Color 1-255 -> output color + lineweight
        var penPanel = new Grid { Margin = new Thickness(0, 8, 0, 0), MinHeight = 90 };
        penPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); penPanel.ColumnDefinitions.Add(new ColumnDefinition());
        var penList = new ListBox { BorderBrush = line };
        for (var i = 1; i <= 255; i++)
        {
            var c = AciToRgb(i);
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal };
            rowPanel.Children.Add(new Border { Width = 16, Height = 12, Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0) });
            rowPanel.Children.Add(new TextBlock { Text = $"Color {i}", Foreground = ink });
            penList.Items.Add(new ListBoxItem { Content = rowPanel, Tag = i });
        }
        var penProps = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        var outputNames = new[] { "Use object color", "Black", "White", "Red", "Yellow", "Green", "Cyan", "Blue", "Magenta", "Gray" };
        var outputHex = new Dictionary<string, string?> { ["Use object color"] = null, ["Black"] = "#000000", ["White"] = "#FFFFFF", ["Red"] = "#FF0000", ["Yellow"] = "#FFFF00", ["Green"] = "#00FF00", ["Cyan"] = "#00FFFF", ["Blue"] = "#0000FF", ["Magenta"] = "#FF00FF", ["Gray"] = "#808080" };
        var penColor = new ComboBox { IsEditable = true, ItemsSource = outputNames, Width = 170, Height = 24, HorizontalAlignment = HorizontalAlignment.Left };
        var penWeight = new ComboBox { IsEditable = true, ItemsSource = new[] { "Use object lineweight" }.Concat(LayerLineweights.Skip(1)).ToArray(), Width = 170, Height = 24, HorizontalAlignment = HorizontalAlignment.Left };
        penProps.Children.Add(Lbl("Output color")); penProps.Children.Add(penColor);
        penProps.Children.Add(new TextBlock { Text = "Lineweight", Foreground = muted, Margin = new Thickness(0, 8, 0, 0) }); penProps.Children.Add(penWeight);
        penProps.Children.Add(new TextBlock { Text = "Select a color on the left, then set how it plots.", Foreground = muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Width = 220, HorizontalAlignment = HorizontalAlignment.Left });
        Grid.SetColumn(penProps, 1); penPanel.Children.Add(penList); penPanel.Children.Add(penProps);
        Grid.SetRow(penPanel, 2); styleGrid.Children.Add(penPanel);
        var penCurrent = -1; var penLoading = false;
        string HexOf(int i) { var c = AciToRgb(i); return $"#{c.R:X2}{c.G:X2}{c.B:X2}"; }
        void CommitPen()
        {
            if (penCurrent < 1 || penLoading) return;
            var key = HexOf(penCurrent);
            var colorText = (penColor.Text ?? "").Trim(); var weightText = (penWeight.Text ?? "").Trim();
            string? output = outputHex.TryGetValue(colorText, out var named) ? named : (colorText.StartsWith('#') && colorText.Length == 7 ? colorText.ToUpperInvariant() : null);
            var weight = TryD(weightText, out var mm) && mm > 0 ? mm : 0;
            if (output is null && weight <= 0) pens.Remove(key); else pens[key] = new PlotPenRule { ColorHex = key, OutputColorHex = output, LineWeightMm = weight };
            schedule();
        }
        penList.SelectionChanged += (_, _) =>
        {
            if (penList.SelectedItem is not ListBoxItem { Tag: int index }) return;
            penCurrent = index; penLoading = true;
            if (pens.TryGetValue(HexOf(index), out var pen))
            {
                var name = outputHex.FirstOrDefault(q => string.Equals(q.Value, pen.OutputColorHex, StringComparison.OrdinalIgnoreCase)).Key;
                penColor.Text = pen.OutputColorHex is null ? "Use object color" : name ?? pen.OutputColorHex;
                penWeight.Text = pen.LineWeightMm > 0 ? D(pen.LineWeightMm) : "Use object lineweight";
            }
            else { penColor.Text = "Use object color"; penWeight.Text = "Use object lineweight"; }
            penLoading = false;
        };
        penColor.LostFocus += (_, _) => CommitPen(); penWeight.LostFocus += (_, _) => CommitPen();
        penColor.SelectionChanged += (_, _) => { if (!penLoading) Dispatcher.BeginInvoke(new Action(CommitPen)); };
        penWeight.SelectionChanged += (_, _) => { if (!penLoading) Dispatcher.BeginInvoke(new Action(CommitPen)); };
        penList.SelectedIndex = 0;

        // Layout viewport (paper-space layouts only)
        var useVp = Tick("Show model in this layout", setup.UseViewport);
        var vpX = Box(D(setup.ViewportXMm), 48); var vpY = Box(D(setup.ViewportYMm), 48); var vpW = Box(D(setup.ViewportWidthMm), 48); var vpH = Box(D(setup.ViewportHeightMm), 48);
        var vpCx = Box(D(setup.ViewportCenterX), 64); var vpCy = Box(D(setup.ViewportCenterY), 64);
        var vpScale = Box(D(setup.ViewportScale <= 0 ? 1 : 1 / setup.ViewportScale), 52); var vpFrame = Tick("Frame", setup.ViewportFrame);
        var vpCur = new Button { Content = "Current view", Height = 24, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(6, 0, 0, 0) };
        if (!isModel)
        {
            var vpCard = Card(leftCol, "Layout viewport", out var bVp);
            bVp.Children.Add(useVp);
            var r1 = Row(bVp); r1.Children.Add(Lbl("X")); r1.Children.Add(vpX); r1.Children.Add(Lbl("Y")); r1.Children.Add(vpY); r1.Children.Add(Lbl("W")); r1.Children.Add(vpW); r1.Children.Add(Lbl("H")); r1.Children.Add(vpH);
            var r2 = Row(bVp); r2.Children.Add(Lbl("Model center")); r2.Children.Add(vpCx); r2.Children.Add(vpCy); r2.Children.Add(Lbl("1:", 6)); r2.Children.Add(vpScale); r2.Children.Add(vpFrame); r2.Children.Add(vpCur);
        }

        // Preview (right)
        var previewImage = new Image { Stretch = Stretch.Uniform };
        var paperInfo = new TextBlock { Foreground = Brushes.White, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 8) };
        var previewHost = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(paperInfo, Dock.Bottom); previewHost.Children.Add(paperInfo); previewHost.Children.Add(previewImage);
        var previewCard = new Border { Background = new SolidColorBrush(Color.FromRgb(52, 58, 68)), CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Child = previewHost };
        Grid.SetColumn(previewCard, 1); page.Children.Add(previewCard);

        // ---- state <-> setup --------------------------------------------------------------------------------------------------
        PlotSetup Read()
        {
            var s = setup.Clone();
            var p = PlotPapers[Math.Max(0, paper.SelectedIndex)];
            s.PaperName = p.Name;
            if (p.Name == "Custom") { if (TryD(shortBox.Text, out var a) && a > 10) s.PaperShortMm = a; if (TryD(longBox.Text, out var b) && b > 10) s.PaperLongMm = b; }
            else { s.PaperShortMm = p.Short; s.PaperLongMm = p.Long; }
            var orientation = orient.Get();
            s.Landscape = orientation != 1;
            if (TryD(marginBox.Text, out var v)) { v = Math.Max(0, v); s.MarginLeftMm = s.MarginTopMm = s.MarginRightMm = s.MarginBottomMm = v; }
            s.Area = areaKinds[Math.Max(0, area.Get())];
            s.WindowMinX = window[0]; s.WindowMinY = window[1]; s.WindowMaxX = window[2]; s.WindowMaxY = window[3];
            s.FitToPaper = fit.IsChecked == true;
            var sc = scaleChoices[Math.Max(0, scale.SelectedIndex)];
            if (!s.FitToPaper)
            {
                if (sc.PaperMm > 0) { s.ScalePaperMm = sc.PaperMm; s.ScaleDrawingUnits = sc.Units; }
                else { if (TryD(sMm.Text, out v) && v > 0) s.ScalePaperMm = v; if (TryD(sUnits.Text, out v) && v > 0) s.ScaleDrawingUnits = v; }
            }
            s.CenterPlot = center.IsChecked == true; if (TryD(offX.Text, out v)) s.OffsetXMm = v; if (TryD(offY.Text, out v)) s.OffsetYMm = v;
            s.ColorMode = PlotStyleModes[Math.Clamp(style.Get(), 0, PlotStyleModes.Length - 1)];
            if (TryD(defWeight.Text, out v) && v > 0) s.DefaultLineWeightMm = v;
            s.PlotLineweights = plotWeights.IsChecked == true; s.ScaleLineweights = scaleWeights.IsChecked == true; s.OpenAfterPlot = openAfter.IsChecked == true;
            s.PenRules = pens.Values.Select(r => new PlotPenRule { ColorHex = r.ColorHex, OutputColorHex = r.OutputColorHex, LineWeightMm = r.LineWeightMm }).ToList();
            s.UseViewport = useVp.IsChecked == true;
            if (TryD(vpX.Text, out v)) s.ViewportXMm = v; if (TryD(vpY.Text, out v)) s.ViewportYMm = v; if (TryD(vpW.Text, out v) && v > 0) s.ViewportWidthMm = v; if (TryD(vpH.Text, out v) && v > 0) s.ViewportHeightMm = v;
            if (TryD(vpCx.Text, out v)) s.ViewportCenterX = v; if (TryD(vpCy.Text, out v)) s.ViewportCenterY = v;
            if (TryD(vpScale.Text, out v) && v > 0) s.ViewportScale = 1 / v;
            s.ViewportFrame = vpFrame.IsChecked == true;
            // One page, best fit: pick the orientation whose printable area matches the drawing's aspect ratio.
            if (isModel && orientation == 0 && s.FitToPaper && s.Area != PlotAreaKind.Layout)
            {
                try
                {
                    Rect world;
                    if (s.Area == PlotAreaKind.Extents) { extentsCache ??= PlotEngine.ModelBounds(drawing, layoutName, s, CurrentPlotView()); world = extentsCache.Value; }
                    else world = PlotEngine.ModelBounds(drawing, layoutName, s, CurrentPlotView());
                    if (!world.IsEmpty && world.Width > 0 && world.Height > 0) s.Landscape = world.Width >= world.Height;
                }
                catch { }
            }
            return s;
        }

        void SyncEnabled()
        {
            rCustom.Visibility = PlotPapers[Math.Max(0, paper.SelectedIndex)].Name == "Custom" ? Visibility.Visible : Visibility.Collapsed;
            rWindow.Visibility = areaKinds[Math.Max(0, area.Get())] == PlotAreaKind.Window ? Visibility.Visible : Visibility.Collapsed;
            windowText.Text = $"({window[0]:0.##}, {window[1]:0.##}) → ({window[2]:0.##}, {window[3]:0.##})";
            var fitOn = fit.IsChecked == true;
            scale.Visibility = sMm.Visibility = sUnits.Visibility = fitOn ? Visibility.Collapsed : Visibility.Visible;
            foreach (var el in rScale.Children.OfType<TextBlock>()) el.Visibility = fitOn ? Visibility.Collapsed : Visibility.Visible;
            sMm.IsEnabled = sUnits.IsEnabled = scaleChoices[Math.Max(0, scale.SelectedIndex)].PaperMm < 0;
            scaleWeights.Visibility = fitOn ? Visibility.Collapsed : Visibility.Visible;
            var centered = center.IsChecked == true;
            offX.Visibility = offY.Visibility = centered ? Visibility.Collapsed : Visibility.Visible;
            foreach (var el in rPos.Children.OfType<TextBlock>().Where(t => t != scaleRead)) el.Visibility = centered ? Visibility.Collapsed : Visibility.Visible;
            penPanel.Visibility = style.Get() == 3 ? Visibility.Visible : Visibility.Collapsed;
            orient.Root.IsEnabled = true;
        }

        PlotSheet? lastSheet = null;
        void UpdatePreview()
        {
            previewTimer.Stop();
            try
            {
                SyncEnabled();
                var s = Read();
                var sheet = PlotEngine.Build(drawing, layoutName, s, CurrentPlotView());
                lastSheet = sheet;
                previewImage.Source = RenderPlotPreview(sheet, 900);
                paperInfo.Text = $"{sheet.WidthMm:0.##} × {sheet.HeightMm:0.##} mm  ·  {(s.Landscape ? "Landscape" : "Portrait")}";
                scaleRead.Text = isModel ? $"Scale 1:{(sheet.ScaleUsed > 0 ? 1 / sheet.ScaleUsed : 0):0.###}" : "";
                status.Text = $"{sheet.PlottedEntities} objects" + (sheet.SkippedEntities > 0 ? $" · {sheet.SkippedEntities} skipped ({sheet.SkipSummary})" : "") + (sheet.OutsideWindowParts > 0 ? $" · {sheet.OutsideWindowParts} outside window" : "");
                status.ToolTip = sheet.Diagnostics.Count > 0 ? string.Join("\n", sheet.Diagnostics.Take(40)) : null;
                try
                {
                    var logDir = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iStCAD_1");
                    Directory.CreateDirectory(logDir);
                    File.WriteAllLines(IOPath.Combine(logDir, "plot-preview.log"), new[] { $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {drawing.Name}  area {s.Area}  window ({window[0]:0.##}, {window[1]:0.##}) - ({window[2]:0.##}, {window[3]:0.##})" }.Concat(sheet.Diagnostics));
                }
                catch { }
            }
            catch (Exception ex) { status.Text = "Preview failed: " + ex.Message; }
        }
        previewTimer.Tick += (_, _) => UpdatePreview();
        void Schedule() { if (updating) return; previewTimer.Stop(); previewTimer.Start(); SyncEnabled(); }
        schedule = Schedule;
        foreach (var tb in new[] { shortBox, longBox, marginBox, sMm, sUnits, offX, offY, defWeight, vpX, vpY, vpW, vpH, vpCx, vpCy, vpScale })
            tb.TextChanged += (_, _) => Schedule();
        foreach (var cb in new[] { paper, scale }) cb.SelectionChanged += (_, _) => Schedule();
        foreach (var ck in new[] { center, fit, plotWeights, scaleWeights, openAfter, useVp, vpFrame })
        { ck.Checked += (_, _) => Schedule(); ck.Unchecked += (_, _) => Schedule(); }
        SyncEnabled();

        void SetWindow(Rect r)
        {
            window = new[] { r.Left, r.Top, r.Right, r.Bottom };
            // window array is captured by Read(); keep the same reference semantics
            area.Set(Array.IndexOf(areaKinds, PlotAreaKind.Window)); Schedule();
        }
        void ReShow() { dialog.Show(); dialog.Activate(); Schedule(); }
        startWindowPick = () =>
        {
            if (_pickTool is not null) CancelPickTool(silent: true);
            dialog.Hide();
            AppendCommandHistory("PLOT: specify the plot window in the model.");
            List<CadEntity>? Build(Wizard x)
            {
                if (!x.Has(0) || !x.Has(1)) return null;
                var a = x.P(0); var b = x.P(1);
                if (Math.Abs(a.X - b.X) < 1e-9 || Math.Abs(a.Y - b.Y) < 1e-9) return null;
                return new List<CadEntity> { NewPoly(new[] { a, new Point2(b.X, a.Y), b, new Point2(a.X, b.Y) }, true) };
            }
            StartWizard(new Wizard
            {
                Name = "PLOT WINDOW",
                Steps = new[]
                {
                    new WizStep { Kind = WizKind.Point, Prompt = "Specify first corner:" },
                    new WizStep { Kind = WizKind.Point, Prompt = "Specify opposite corner:" }
                },
                Preview = Build, EndOnEsc = true, OnEnd = _ => ReShow(),
                Commit = x =>
                {
                    var a = x.P(0); var b = x.P(1);
                    if (Math.Abs(a.X - b.X) < 1e-9 || Math.Abs(a.Y - b.Y) < 1e-9) { AppendCommandHistory("PLOT WINDOW: the corners must differ in both X and Y."); return false; }
                    SetWindow(new Rect(new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Point(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y))));
                    AppendCommandHistory($"PLOT WINDOW: ({window[0]:0.##}, {window[1]:0.##}) → ({window[2]:0.##}, {window[3]:0.##})");
                    ReShow();
                    return true;
                }
            });
        };
        vpCur.Click += (_, _) =>
        {
            var v = CurrentPlotView(); vpCx.Text = D(v.CenterX); vpCy.Text = D(v.CenterY);
            if (TryD(vpW.Text, out var w) && w > 0 && v.ScalePxPerUnit > 0) vpScale.Text = D(Math.Max(1e-6, v.WidthPx / v.ScalePxPerUnit / w));
        };
        saveStyleBtn.Click += (_, _) =>
        {
            var s0 = Read();
            var dlg = new SaveFileDialog { Title = "Save plot style", Filter = "iStCAD plot style (*.istplot)|*.istplot|JSON (*.json)|*.json", DefaultExt = ".istplot", AddExtension = true, FileName = string.IsNullOrWhiteSpace(styleName.Text) ? "plot-style" : styleName.Text.Trim() };
            if (dlg.ShowDialog(dialog) != true) return;
            try
            {
                var file = new PlotStyleFile { Name = string.IsNullOrWhiteSpace(styleName.Text) ? IOPath.GetFileNameWithoutExtension(dlg.FileName) : styleName.Text.Trim(), ColorMode = s0.ColorMode, PlotLineweights = s0.PlotLineweights, DefaultLineWeightMm = s0.DefaultLineWeightMm, ScaleLineweights = s0.ScaleLineweights, PenRules = s0.PenRules };
                File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
                status.Text = $"Plot style saved: {IOPath.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex) { status.Text = "Could not save the plot style: " + ex.Message; }
        };
        loadStyleBtn.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Title = "Load plot style", Filter = "iStCAD plot style (*.istplot;*.json)|*.istplot;*.json|All files (*.*)|*.*" };
            if (dlg.ShowDialog(dialog) != true) return;
            try
            {
                var file = JsonSerializer.Deserialize<PlotStyleFile>(File.ReadAllText(dlg.FileName)) ?? throw new InvalidDataException("The file is empty.");
                updating = true;
                styleName.Text = string.IsNullOrWhiteSpace(file.Name) ? IOPath.GetFileNameWithoutExtension(dlg.FileName) : file.Name;
                style.Set(Math.Max(0, Array.IndexOf(PlotStyleModes, file.ColorMode)));
                plotWeights.IsChecked = file.PlotLineweights; scaleWeights.IsChecked = file.ScaleLineweights;
                if (file.DefaultLineWeightMm > 0) defWeight.Text = D(file.DefaultLineWeightMm);
                pens.Clear();
                foreach (var rule in file.PenRules ?? new List<PlotPenRule>())
                    if (!string.IsNullOrWhiteSpace(rule.ColorHex)) pens[rule.ColorHex.ToUpperInvariant()] = new PlotPenRule { ColorHex = rule.ColorHex, OutputColorHex = rule.OutputColorHex, LineWeightMm = rule.LineWeightMm };
                updating = false;
                var keep = Math.Max(0, penList.SelectedIndex); penList.SelectedIndex = -1; penList.SelectedIndex = keep;
                Schedule();
                status.Text = $"Plot style loaded: {IOPath.GetFileName(dlg.FileName)} ({pens.Count} pen rule(s))";
            }
            catch (Exception ex) { updating = false; status.Text = "Could not load the plot style: " + ex.Message; }
        };
        applyBtn.Click += (_, _) =>
        {
            var s = Read();
            drawing.PlotSetups[layoutName] = s.Clone();
            _cadFiles.SetPlotSettings(drawing, layoutName, s.PaperWidthMm, s.PaperHeightMm, s.ColorMode == PlotColorMode.Monochrome ? "monochrome.ctb" : "acad.ctb");
            _activeSession.IsDirty = true; _activeSession.Revision++; RefreshDrawingTabs();
            status.Text = "Page setup stored in this layout.";
        };

        void Export(IReadOnlyList<string> spaces)
        {
            var s = Read();
            var save = new SaveFileDialog { FileName = $"{drawing.Name}{(spaces.Count == 1 ? "" : "-all")}.pdf", DefaultExt = ".pdf", AddExtension = true, Filter = "PDF document (*.pdf)|*.pdf" };
            if (save.ShowDialog(dialog) != true) return;
            try
            {
                System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
                var sheets = new List<PlotSheet>();
                foreach (var space in spaces)
                {
                    var perSpace = s.Clone();
                    var spaceIsModel = space.Equals("Model", StringComparison.OrdinalIgnoreCase);
                    if (!spaceIsModel && drawing.PlotSetups.TryGetValue(space, out var stored) && space != layoutName) perSpace = stored.Clone();
                    sheets.Add(PlotEngine.Build(drawing, space, perSpace, CurrentPlotView()));
                }
                PdfPlotWriter.Write(save.FileName, sheets, drawing.Name);
                try
                {
                    var logDir = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iStCAD_1");
                    Directory.CreateDirectory(logDir);
                    var lines = new List<string> { $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {drawing.Name}  ->  {IOPath.GetFileName(save.FileName)}  (color mode {s.ColorMode}, lineweights {s.PlotLineweights}, scale lineweights {s.ScaleLineweights}, default {s.DefaultLineWeightMm:0.##} mm)" };
                    foreach (var sh in sheets) lines.AddRange(sh.Diagnostics);
                    File.AppendAllLines(IOPath.Combine(logDir, "plot-audit.log"), lines);
                }
                catch { }
                drawing.PlotSetups[layoutName] = s.Clone();
                AppendCommandHistory($"Plotted {string.Join(", ", spaces)} → {IOPath.GetFileName(save.FileName)} ({sheets.Sum(x => x.PlottedEntities)} objects).");
                if (s.OpenAfterPlot) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(save.FileName) { UseShellExecute = true }); } catch { } }
                dialog.Close();
            }
            catch (Exception ex) { ShowCadError("Could not export PDF", ex); }
            finally { System.Windows.Input.Mouse.OverrideCursor = null; }
        }
        okBtn.Click += (_, _) => Export(new[] { layoutName });
        allBtn.Click += (_, _) => Export(new[] { "Model" }.Concat(_layoutNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        closeBtn.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => { previewTimer.Stop(); if (_pickTool == "WIZ" && _wiz is { Name: "PLOT WINDOW" }) CancelPickTool(silent: true); };

        dialog.Content = root;
        ConfigureEditorDialog(dialog);
        dialog.Background = new SolidColorBrush(Color.FromRgb(238, 241, 245));
        dialog.Loaded += (_, _) => UpdatePreview();
        dialog.Show(); // modeless: the model stays reachable for Get Window
    }
}
