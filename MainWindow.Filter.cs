using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using iStCAD_1.Core.Model;
using Brush = System.Windows.Media.Brush;

namespace iStCAD_1;

/// <summary>FILTER: pick object types, layers, colors and linetypes directly in one compact form (no sub-dialogs); live, faceted counts.</summary>
public partial class MainWindow
{
    private Window? _filterWindow;

    private void FilterCommand()
    {
        if (_filterWindow is { IsLoaded: true }) { _filterWindow.Activate(); return; }
        var d = _editor.Drawing;
        var pool = d.Entities.Where(e => !d.HiddenEntityIds.Contains(e.Id) && !IsLayerLocked(e.Layer)).ToArray();
        if (pool.Length == 0) { AppendCommandHistory("FILTER: there are no selectable objects."); return; }
        var n = pool.Length;
        var baseline = _selectedEntityIds.ToHashSet();

        // ---- facets: type / layer / color / linetype ---------------------------------------------------------------------
        string ColorOf(CadEntity e) => e.ColorByBlock ? "ByBlock" : e.ExplicitColor is { } c ? $"{c.R},{c.G},{c.B}" : "ByLayer";
        var titles = new[] { "Type", "Layer", "Color", "Linetype" };
        var keyOf = new Func<CadEntity, string>[] { GetEntityTypeName, e => e.Layer, ColorOf, e => string.IsNullOrWhiteSpace(e.LineTypeName) ? "ByLayer" : e.LineTypeName! };
        var values = new List<string>[4]; var idx = new int[4][];
        for (var c = 0; c < 4; c++)
        {
            var keys = pool.Select(keyOf[c]).ToArray();
            values[c] = keys.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            var lookup = values[c].Select((v, i) => (v, i)).ToDictionary(t => t.v, t => t.i, StringComparer.OrdinalIgnoreCase);
            idx[c] = keys.Select(k => lookup[k]).ToArray();
        }
        var picked = Enumerable.Range(0, 4).Select(c => new bool[values[c].Count]).ToArray();

        // ---- look -------------------------------------------------------------------------------------------------------------
        var accent = new SolidColorBrush(Color.FromRgb(37, 99, 235)); accent.Freeze();
        var ink = new SolidColorBrush(Color.FromRgb(31, 41, 55)); ink.Freeze();
        var muted = new SolidColorBrush(Color.FromRgb(100, 110, 125)); muted.Freeze();
        var line = new SolidColorBrush(Color.FromRgb(210, 216, 224)); line.Freeze();
        var dialog = CreateCadChildWindow("Filter - select objects by type, layer, color, linetype", 820, 540);
        dialog.ResizeMode = ResizeMode.CanResize; dialog.MinWidth = 640; dialog.MinHeight = 380;
        dialog.Background = new SolidColorBrush(Color.FromRgb(238, 241, 245));
        System.Windows.Documents.TextElement.SetFontSize(dialog, 12);
        _filterWindow = dialog;

        Action<bool> refresh = _ => { };
        var mode = 0; var scopeSelection = baseline.Count > 0; var live = true;

        (Border Root, Action<int> Set) Segment(string[] items, int selected, Action<int> changed)
        {
            var buttons = new List<ToggleButton>(); var grid = new UniformGrid { Rows = 1 };
            void Paint(int on) { for (var i = 0; i < buttons.Count; i++) { buttons[i].IsChecked = i == on; buttons[i].Background = i == on ? accent : Brushes.White; buttons[i].Foreground = i == on ? Brushes.White : ink; } }
            for (var i = 0; i < items.Length; i++)
            {
                var index = i;
                var b = new ToggleButton { Content = items[i], Height = 24, Padding = new Thickness(10, 0, 10, 0), BorderThickness = new Thickness(0, 0, i < items.Length - 1 ? 1 : 0, 0), BorderBrush = line };
                b.Click += (_, _) => { Paint(index); changed(index); };
                buttons.Add(b); grid.Children.Add(b);
            }
            Paint(selected);
            return (new Border { BorderBrush = line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = grid, Margin = new Thickness(0, 0, 14, 0) }, Paint);
        }

        var root = new DockPanel { Margin = new Thickness(12) };
        // top bar: scope, mode, live
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Search in", Foreground = muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var scopeSeg = Segment(new[] { "Entire drawing", baseline.Count > 0 ? $"Selection ({baseline.Count:N0})" : "Selection (none)" }, scopeSelection ? 1 : 0, i => { scopeSelection = i == 1 && baseline.Count > 0; refresh(true); });
        top.Children.Add(scopeSeg.Root);
        top.Children.Add(new TextBlock { Text = "Result", Foreground = muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var modeSeg = Segment(new[] { "Replace selection", "Add to it", "Remove from it" }, 0, i => { mode = i; refresh(true); });
        top.Children.Add(modeSeg.Root);
        var liveBox = new CheckBox { Content = "Select as I click", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Foreground = ink };
        liveBox.Checked += (_, _) => { live = true; refresh(true); }; liveBox.Unchecked += (_, _) => live = false;
        top.Children.Add(liveBox);

        // footer: result text + buttons
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        Button Btn(string text, bool primary = false)
        {
            var b = new Button { Content = text, MinWidth = 84, Height = 28, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 0, 12, 0) };
            if (primary) { b.Background = accent; b.Foreground = Brushes.White; b.BorderBrush = accent; }
            return b;
        }
        var closeBtn = Btn("Close"); var applyBtn = Btn("Select", true); var resetBtn = Btn("Reset");
        foreach (var b in new[] { closeBtn, applyBtn, resetBtn }) { DockPanel.SetDock(b, Dock.Right); footer.Children.Add(b); }
        var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = muted, TextTrimming = TextTrimming.CharacterEllipsis };
        footer.Children.Add(status);

        // four facet columns
        var columns = new Grid();
        root.Children.Add(columns);
        var boxes = new CheckBox[4][]; var countText = new TextBlock[4][]; var rows = new FrameworkElement[4][];
        var headCount = new TextBlock[4]; var search = new TextBox[4];
        Brush? SwatchFor(int c, string v)
        {
            if (c == 1 && d.LayerColors.TryGetValue(v, out var lc)) return new SolidColorBrush(Color.FromRgb(lc.R, lc.G, lc.B));
            if (c == 2)
            {
                var p = v.Split(',');
                if (p.Length == 3 && byte.TryParse(p[0], out var r) && byte.TryParse(p[1], out var g) && byte.TryParse(p[2], out var b)) return new SolidColorBrush(Color.FromRgb(r, g, b));
            }
            return null;
        }
        for (var c = 0; c < 4; c++)
        {
            var col = c;
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var card = new Border { Background = Brushes.White, BorderBrush = line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(8), Margin = new Thickness(c == 0 ? 0 : 6, 0, 0, 0) };
            Grid.SetColumn(card, c); columns.Children.Add(card);
            var dock = new DockPanel(); card.Child = dock;
            var head = new DockPanel { Margin = new Thickness(2, 0, 2, 5) }; DockPanel.SetDock(head, Dock.Top); dock.Children.Add(head);
            TextBlock Link(string text, Action act)
            {
                var t = new TextBlock { Text = text, Foreground = accent, Cursor = Cursors.Hand, Margin = new Thickness(8, 0, 0, 0), FontSize = 11 };
                t.MouseLeftButtonUp += (_, _) => act(); DockPanel.SetDock(t, Dock.Right); return t;
            }
            head.Children.Add(Link("clear", () => { Array.Clear(picked[col]); foreach (var b in boxes[col]) b.IsChecked = false; refresh(true); }));
            head.Children.Add(Link("all", () =>
            {
                for (var i = 0; i < boxes[col].Length; i++) if (rows[col][i].Visibility == Visibility.Visible) { picked[col][i] = true; boxes[col][i].IsChecked = true; }
                refresh(true);
            }));
            var title = new TextBlock { Text = titles[c].ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = muted, VerticalAlignment = VerticalAlignment.Center };
            headCount[c] = new TextBlock { Foreground = accent, FontSize = 10.5, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal }; titleRow.Children.Add(title); titleRow.Children.Add(headCount[c]);
            head.Children.Add(titleRow);
            search[c] = new TextBox { Height = 22, Margin = new Thickness(0, 0, 0, 5), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Type to narrow the list" };
            DockPanel.SetDock(search[c], Dock.Top); dock.Children.Add(search[c]);
            search[c].TextChanged += (_, _) => refresh(false);
            var list = new StackPanel();
            dock.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list });
            boxes[c] = new CheckBox[values[c].Count]; countText[c] = new TextBlock[values[c].Count]; rows[c] = new FrameworkElement[values[c].Count];
            for (var i = 0; i < values[c].Count; i++)
            {
                var index = i;
                var rowGrid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = new StackPanel { Orientation = Orientation.Horizontal };
                if (SwatchFor(c, values[c][i]) is { } sw) label.Children.Add(new Border { Width = 10, Height = 10, Background = sw, BorderBrush = line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
                label.Children.Add(new TextBlock { Text = values[c][i], TextTrimming = TextTrimming.CharacterEllipsis, Foreground = ink, VerticalAlignment = VerticalAlignment.Center });
                var cb = new CheckBox { Content = label, VerticalContentAlignment = VerticalAlignment.Center };
                cb.Checked += (_, _) => { picked[col][index] = true; refresh(true); };
                cb.Unchecked += (_, _) => { picked[col][index] = false; refresh(true); };
                var cnt = new TextBlock { Foreground = muted, FontSize = 11, Margin = new Thickness(6, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(cnt, 1); rowGrid.Children.Add(cb); rowGrid.Children.Add(cnt);
                list.Children.Add(rowGrid);
                boxes[c][i] = cb; countText[c][i] = cnt; rows[c][i] = rowGrid;
            }
        }

        // ---- logic ----------------------------------------------------------------------------------------------------------
        var inScope = new bool[n];
        var matched = new bool[n];
        HashSet<Guid> Result(out int matchCount)
        {
            matchCount = 0;
            var matches = new HashSet<Guid>();
            for (var i = 0; i < n; i++) if (matched[i]) { matches.Add(pool[i].Id); matchCount++; }
            return mode switch
            {
                1 => new HashSet<Guid>(baseline.Concat(matches)),
                2 => new HashSet<Guid>(baseline.Where(id => !matches.Contains(id))),
                _ => matches
            };
        }
        void ApplySelection()
        {
            var result = Result(out var m);
            _selectedEntityIds.Clear(); foreach (var id in result) _selectedEntityIds.Add(id);
            RefreshPropertiesPalette(); RenderSelectionOverlay();
            AppendCommandHistory($"FILTER: {m:N0} object(s) matched, {result.Count:N0} selected.");
        }
        refresh = userAction =>
        {
            for (var i = 0; i < n; i++) inScope[i] = !scopeSelection || baseline.Contains(pool[i].Id);
            var any = Enumerable.Range(0, 4).Select(c => picked[c].Any(p => p)).ToArray();
            var counts = Enumerable.Range(0, 4).Select(c => new int[values[c].Count]).ToArray();
            var matchCount = 0;
            for (var i = 0; i < n; i++)
            {
                matched[i] = false;
                if (!inScope[i]) continue;
                var ok0 = !any[0] || picked[0][idx[0][i]]; var ok1 = !any[1] || picked[1][idx[1][i]];
                var ok2 = !any[2] || picked[2][idx[2][i]]; var ok3 = !any[3] || picked[3][idx[3][i]];
                if (ok1 && ok2 && ok3) counts[0][idx[0][i]]++;
                if (ok0 && ok2 && ok3) counts[1][idx[1][i]]++;
                if (ok0 && ok1 && ok3) counts[2][idx[2][i]]++;
                if (ok0 && ok1 && ok2) counts[3][idx[3][i]]++;
                if ((any[0] || any[1] || any[2] || any[3]) && ok0 && ok1 && ok2 && ok3) { matched[i] = true; matchCount++; }
            }
            // Smart lists: values that cannot occur with the other picks fold away (unless picked); the search box narrows further.
            for (var c = 0; c < 4; c++)
            {
                var text = search[c].Text.Trim();
                for (var i = 0; i < values[c].Count; i++)
                {
                    countText[c][i].Text = counts[c][i].ToString("N0", CultureInfo.CurrentCulture);
                    var show = (counts[c][i] > 0 || picked[c][i]) && (text.Length == 0 || values[c][i].Contains(text, StringComparison.OrdinalIgnoreCase));
                    rows[c][i].Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    rows[c][i].Opacity = counts[c][i] > 0 ? 1 : 0.5;
                }
                var p = picked[c].Count(x => x);
                headCount[c].Text = p > 0 ? $"· {p} picked" : "· any";
            }
            status.Text = any.Any(a => a) ? $"{matchCount:N0} of {(scopeSelection ? baseline.Count : n):N0} objects match" : "Tick values in any column — all columns combine (AND); empty column = any.";
            if (userAction && live && any.Any(a => a) || userAction && live && !any.Any(a => a)) ApplyLive();
        };
        void ApplyLive()
        {
            if (!picked.Any(p => p.Any(x => x))) { _selectedEntityIds.Clear(); foreach (var id in baseline) _selectedEntityIds.Add(id); RefreshPropertiesPalette(); RenderSelectionOverlay(); return; }
            var result = Result(out _);
            _selectedEntityIds.Clear(); foreach (var id in result) _selectedEntityIds.Add(id);
            RefreshPropertiesPalette(); RenderSelectionOverlay();
        }
        applyBtn.Click += (_, _) => { if (picked.Any(p => p.Any(x => x))) ApplySelection(); else AppendCommandHistory("FILTER: tick at least one value."); dialog.Close(); };
        resetBtn.Click += (_, _) =>
        {
            for (var c = 0; c < 4; c++) { Array.Clear(picked[c]); foreach (var b in boxes[c]) b.IsChecked = false; search[c].Text = ""; }
            refresh(true);
        };
        closeBtn.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => _filterWindow = null;
        dialog.Content = root;
        refresh(false);
        dialog.Show();
    }
}
