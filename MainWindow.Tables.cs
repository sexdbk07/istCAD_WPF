using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1;

/// <summary>AutoCAD-style TABLE (TB): a simplified grid - pick number of columns/rows, then drag a rectangle (both
/// width AND height at once, "kéo 2 phương", like RECTANG) to size the whole grid; cells divide evenly and start
/// empty - double-click any cell afterwards to type/edit its text. Saved as a real native TABLE entity (post-2008
/// AcDbTable/TABLECONTENT). Deliberately simpler than real AutoCAD TABLE (no Insert-from-style dialog, no merged
/// cells, no per-column/per-row custom sizing) - same "cách vẽ gọn hơn, object lưu ra đúng native" principle already
/// used for ELLIPSE/SPLINE/WIPEOUT/MLEADER. ROTATE/MIRROR are not supported yet (the object stays in place): this
/// simplified model stores only an axis-aligned Position+ColumnWidth+RowHeight, no rotation angle, so a rotated/
/// mirrored table cannot be represented exactly yet - MOVE/COPY/SCALE (uniform, like this app's SCALE command
/// always is) all work correctly. NOTE: the vendored ACadSharp DXF writer has no TABLE support at all (DWG only,
/// confirmed by reading its isEntitySupported check) - saving to .dxf will drop the table; only .dwg keeps it.
/// See docs/AUTOCAD_COMMANDS.md.</summary>
public partial class MainWindow
{
    private static double DefaultTableTextHeight(double columnWidth, double rowHeight) =>
        Math.Max(1.0, Math.Min(columnWidth, rowHeight) * 0.3);

    /// <summary>Cell-text previews, row-major, row 0 at the TOP of the grid (matches how AutoCAD/this app reads a table
    /// top-to-bottom) even though Position is the BOTTOM-left corner in world Y-up coordinates.</summary>
    private static List<ProxyPreviewText> BuildTableCellTexts(CadTableData data)
    {
        var maxY = data.Position.Y + data.RowHeight * data.Rows;
        var texts = new List<ProxyPreviewText>();
        for (var r = 0; r < data.Rows; r++)
        for (var c = 0; c < data.Columns; c++)
        {
            var index = r * data.Columns + c;
            var text = index < data.CellTexts.Count ? data.CellTexts[index] : string.Empty;
            if (string.IsNullOrEmpty(text)) continue;
            var cx = data.Position.X + (c + 0.5) * data.ColumnWidth;
            var cy = maxY - (r + 0.5) * data.RowHeight;
            texts.Add(new ProxyPreviewText(text, new Point2(cx, cy), data.TextHeight, AttachmentPoint: 5, Tag: $"cell:{r}:{c}"));
        }
        return texts;
    }

    /// <summary>Builds the grid lines + cell-text previews for a CadTableData - the SAME builder used for the live
    /// drag preview and for the final committed object, so what's drawn while dragging matches exactly what gets
    /// created (same convention as every other wizard-based command in this app).</summary>
    private static ProxyCadEntity? ComposeTable(CadTableData data, Guid id, string layer)
    {
        if (data.Columns < 1 || data.Rows < 1 || data.ColumnWidth <= 1e-9 || data.RowHeight <= 1e-9) return null;
        var minX = data.Position.X; var minY = data.Position.Y;
        var maxX = minX + data.ColumnWidth * data.Columns; var maxY = minY + data.RowHeight * data.Rows;
        var paths = new List<ProxyPreviewPath>();
        for (var i = 0; i <= data.Columns; i++)
        {
            var x = minX + data.ColumnWidth * i;
            paths.Add(new ProxyPreviewPath(new[] { new Point2(x, minY), new Point2(x, maxY) }));
        }
        for (var j = 0; j <= data.Rows; j++)
        {
            var y = minY + data.RowHeight * j;
            paths.Add(new ProxyPreviewPath(new[] { new Point2(minX, y), new Point2(maxX, y) }));
        }
        return new ProxyCadEntity("TABLE", new Point2(minX, minY), new Point2(maxX, maxY), true, id, layer,
            ProxyGraphicsPaths: paths, ProxyGraphicsTexts: BuildTableCellTexts(data), Table: data);
    }

    private void TableCommand()
    {
        ProxyCadEntity? Build(Wizard x)
        {
            if (!x.Has(0) || !x.Has(1) || !x.Has(2) || !x.Has(3)) return null;
            var cols = Math.Max(1, (int)Math.Round(x.D(0)));
            var rows = Math.Max(1, (int)Math.Round(x.D(1)));
            var p1 = x.P(2); var p2 = x.P(3);
            var minX = Math.Min(p1.X, p2.X); var maxX = Math.Max(p1.X, p2.X);
            var minY = Math.Min(p1.Y, p2.Y); var maxY = Math.Max(p1.Y, p2.Y);
            var w = maxX - minX; var h = maxY - minY;
            if (w < 1e-9 || h < 1e-9) return null;
            var colWidth = w / cols; var rowHeight = h / rows;
            var cellTexts = Enumerable.Repeat(string.Empty, cols * rows).ToArray();
            var data = new CadTableData(new Point2(minX, minY), cols, rows, colWidth, rowHeight, cellTexts, DefaultTableTextHeight(colWidth, rowHeight));
            return ComposeTable(data, Guid.NewGuid(), _editor.Drawing.CurrentLayer) is { } composed ? (ProxyCadEntity)_editor.StyleNew(composed) : null;
        }
        StartWizard(new Wizard
        {
            Name = "TABLE",
            Steps = new[]
            {
                new WizStep { Kind = WizKind.Number, Prompt = "Enter number of columns", Default = 3.0 },
                new WizStep { Kind = WizKind.Number, Prompt = "Enter number of rows", Default = 3.0 },
                new WizStep { Kind = WizKind.Point, Prompt = "Specify first corner:" },
                new WizStep { Kind = WizKind.Point, Prompt = "Specify opposite corner (drag width and height together):" }
            },
            Preview = x => Build(x) is { } t ? new List<CadEntity> { t } : null,
            Commit = x =>
            {
                var item = Build(x);
                if (item is null) { AppendCommandHistory("TABLE: invalid size."); return false; }
                AddNewEntities("TABLE", new[] { item }, "table created - double-click any cell to type its text.");
                return true;
            }
        });
    }

    /// <summary>Double-click on a TABLE cell: a small text-entry dialog (same pattern as PromptForLeaderText), pre-filled
    /// with the cell's current text. Converts the double-click's world point into a row/column index, edits just that
    /// one cell's text, and rebuilds the table's preview from the SAME ComposeTable builder used at creation time, so
    /// what's on screen and what gets saved never drift apart. Returns false (not handled) when the click missed every
    /// cell, so the normal double-click behavior still runs.</summary>
    private bool EditTableCell(CadEntity entity, Point screen)
    {
        if (entity is not ProxyCadEntity { Table: { } data } proxy) return false;
        var world = ScreenToWorld(screen);
        var col = (int)Math.Floor((world.X - data.Position.X) / data.ColumnWidth);
        var rowFromTop = (int)Math.Floor((data.Position.Y + data.RowHeight * data.Rows - world.Y) / data.RowHeight);
        if (col < 0 || col >= data.Columns || rowFromTop < 0 || rowFromTop >= data.Rows) return false;
        var index = rowFromTop * data.Columns + col;
        var current = index < data.CellTexts.Count ? data.CellTexts[index] : string.Empty;
        var text = PromptForTableCellText(current);
        if (text is null) return true; // cancelled - still "handled" (don't fall through to normal selection)
        var cells = data.CellTexts.ToArray();
        cells[index] = text.Trim();
        var updatedData = data with { CellTexts = cells };
        var updatedProxy = (ProxyCadEntity)(proxy with { Table = updatedData, ProxyGraphicsTexts = BuildTableCellTexts(updatedData) });
        _editor.ApplyEntityChanges(new CadEntity[] { proxy }, new CadEntity[] { updatedProxy });
        _selectedEntityIds.Clear(); _selectedEntityIds.Add(updatedProxy.Id);
        RefreshPropertiesPalette(); RenderSelectionOverlay(); RenderDrawing(false);
        return true;
    }

    private string? PromptForTableCellText(string current)
    {
        var dialog = CreateCadChildWindow("Table cell text", 380, 220);
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = "Cell text", Margin = new Thickness(0, 0, 0, 5) });
        var content = new TextBox
        {
            Text = current, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, MaxHeight = 130,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.White, Foreground = Brushes.Black
        };
        panel.Children.Add(content);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "OK", Width = 82, Margin = new Thickness(0, 0, 8, 0), IsDefault = true, Padding = new Thickness(8, 4, 8, 4) };
        var cancel = new Button { Content = "Cancel", Width = 82, IsCancel = true, Padding = new Thickness(8, 4, 8, 4) };
        string? result = null;
        ok.Click += (_, _) => { result = content.Text; dialog.DialogResult = true; };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => { content.Focus(); content.SelectAll(); };
        dialog.ShowDialog();
        return result;
    }
}
