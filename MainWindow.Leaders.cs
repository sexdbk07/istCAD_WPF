using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1;

/// <summary>AutoCAD-style QLEADER (LE) and MLEADER (MLD): arrow + one straight leader segment + a short horizontal
/// landing line + one line of text, built as ONE selectable object (like DIMENSION/HATCH) and saved as a real native
/// LEADER entity so it opens correctly in AutoCAD. Deliberately simplified vs. real AutoCAD, for ease of use:
/// exactly one leader segment (arrow point then landing point, no extra vertices), no Settings dialog, no spline path,
/// and the text is a plain MTEXT placed next to the landing rather than a formally-linked annotation object (AutoCAD
/// still displays it correctly; it just isn't drag-linked at the file-association level). See docs/AUTOCAD_COMMANDS.md.</summary>
public partial class MainWindow
{
    private Point2? _leaderArrowPoint;

    private void BeginLeaderTool(string tool)
    {
        _leaderArrowPoint = null;
        BeginPickTool(tool, $"{tool}: Specify first leader point (arrowhead location):");
    }

    private void HandleLeaderClick(Point screen)
    {
        var p = PickWorldPoint(screen);
        if (_leaderArrowPoint is null)
        {
            _leaderArrowPoint = p;
            AppendCommandHistory($"{_pickTool}: Specify next point (leader landing):");
            return;
        }
        var arrow = _leaderArrowPoint.Value;
        var tool = _pickTool!;
        CancelPickTool(silent: true);
        var text = PromptForLeaderText();
        if (string.IsNullOrWhiteSpace(text)) { AppendCommandHistory($"{tool}: cancelled (no text entered)."); return; }
        CommitLeader(new[] { arrow, p }, text.Trim(), isMultiLeader: tool == "MLEADER");
    }

    /// <summary>Small modal dialog asking for the leader's annotation text; null/empty means the user cancelled.</summary>
    private string? PromptForLeaderText()
    {
        var dialog = CreateCadChildWindow("Leader text", 380, 220);
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = "Annotation text", Margin = new Thickness(0, 0, 0, 5) });
        var content = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, MaxHeight = 130,
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
        dialog.Loaded += (_, _) => content.Focus();
        dialog.ShowDialog();
        return result;
    }

    private double LeaderTextHeight() { var s = CurrentDimStyle(); return s.TextHeight * (s.Scale <= 0 ? 1 : s.Scale); }
    private double LeaderArrowSize() { var s = CurrentDimStyle(); return s.ArrowSize * (s.Scale <= 0 ? 1 : s.Scale); }

    /// <summary>Drawing parts of a leader from its parametric data: leader line(s), arrowhead at Points[0], a short
    /// horizontal landing line at the end, then the text (same AddArrow/DimLine/DimText helpers as DIMENSION).</summary>
    private List<CadEntity> BuildLeaderParts(CadLeaderData d)
    {
        var pts = d.Points;
        var parts = new List<CadEntity>();
        for (var i = 0; i < pts.Count - 1; i++) parts.Add(DimLine(pts[i], pts[i + 1]));
        var p0 = pts[0]; var p1 = pts.Count > 1 ? pts[1] : pts[0];
        var dx = p1.X - p0.X; var dy = p1.Y - p0.Y; var segLen = Math.Sqrt(dx * dx + dy * dy);
        var inward = segLen < 1e-9 ? new Point2(1, 0) : new Point2(dx / segLen, dy / segLen);
        AddArrow(parts, p0, inward, d.ArrowSize);
        var last = pts[^1]; var prev = pts.Count > 1 ? pts[^2] : pts[0];
        var goingRight = last.X >= prev.X;
        var landingLen = Math.Max(d.ArrowSize * 2.2, d.TextHeight * 1.2);
        var landingEnd = new Point2(last.X + (goingRight ? landingLen : -landingLen), last.Y);
        parts.Add(DimLine(last, landingEnd));
        var gap = d.TextHeight * 0.5;
        var textPos = new Point2(landingEnd.X + (goingRight ? gap : -gap), landingEnd.Y);
        parts.Add(DimText(d.Text, textPos, d.TextHeight, 0, goingRight ? 4 : 6));
        return parts;
    }

    /// <summary>Composes ONE selectable object (a proxy carrying its parametric data), same pattern as ComposeDimension.</summary>
    private ProxyCadEntity? ComposeLeader(CadLeaderData data, Guid id, string layer)
    {
        var parts = BuildLeaderParts(data);
        var paths = new List<ProxyPreviewPath>(); var texts = new List<ProxyPreviewText>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Grow(Point2 pt) { minX = Math.Min(minX, pt.X); minY = Math.Min(minY, pt.Y); maxX = Math.Max(maxX, pt.X); maxY = Math.Max(maxY, pt.Y); }
        foreach (var part in parts)
        {
            switch (part)
            {
                case LineEntity l: paths.Add(new ProxyPreviewPath(new[] { l.Start, l.End })); Grow(l.Start); Grow(l.End); break;
                case CadMTextEntity t:
                {
                    texts.Add(new ProxyPreviewText(t.Value, t.Position, t.Height, t.RotationRadians, t.Width, t.LineSpacing, t.AttachmentPoint));
                    var half = Math.Max(t.Value.Length, 1) * t.Height * 0.35;
                    Grow(new Point2(t.Position.X - half, t.Position.Y - t.Height)); Grow(new Point2(t.Position.X + half, t.Position.Y + t.Height)); break;
                }
            }
        }
        if (minX > maxX) return null;
        return new ProxyCadEntity(data.IsMultiLeader ? "MULTILEADER" : "LEADER", new Point2(minX, minY), new Point2(maxX, maxY), true, id, layer,
            ProxyGraphicsPaths: paths, ProxyGraphicsTexts: texts, Leader: data);
    }

    private void CommitLeader(IReadOnlyList<Point2> points, string text, bool isMultiLeader = false)
    {
        var data = new CadLeaderData(points.ToArray(), text, LeaderTextHeight(), LeaderArrowSize(), IsMultiLeader: isMultiLeader);
        var composed = ComposeLeader(data, Guid.NewGuid(), _editor.Drawing.CurrentLayer);
        if (composed is null) { AppendCommandHistory("LEADER: cannot build."); return; }
        var leader = (ProxyCadEntity)_editor.StyleNew(composed);
        _editor.ApplyEntityChanges(Array.Empty<CadEntity>(), new CadEntity[] { leader });
        _selectedEntityIds.Clear(); _selectedEntityIds.Add(leader.Id);
        RefreshPropertiesPalette(); RenderSelectionOverlay(); RenderDrawing(false);
        AppendCommandHistory(isMultiLeader
            ? "MLEADER: created. Saved as a true native MULTILEADER object. Move/copy/erase it like any single object; EXPLODE turns it into separate lines and text if you ever need that."
            : "LEADER: created. Move/copy/erase it like any single object; EXPLODE turns it into separate lines and text if you ever need that.");
    }
}
