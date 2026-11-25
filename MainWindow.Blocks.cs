using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1;

/// <summary>ATTDEF (attribute definition), BLOCK (define a reusable named block, optionally with attributes) and the
/// block-aware extension to INSERT (choose one of the drawing's own named blocks, prompting for attribute values).
/// Matches AutoCAD's ATTDEF/BLOCK/INSERT trio: an un-blocked ATTDEF shows its Tag text; once BLOCK absorbs it, every
/// ATTDEF in the selection becomes an attribute definition of the block and the replacement INSERT gets one ATTRIB
/// per definition, defaulted to the ATTDEF's default value (editable afterwards with the existing Enhanced Attribute Editor).</summary>
public partial class MainWindow
{
    // ============================ ATTDEF ============================
    private CadAttributeDefData? _pendingAttDef;

    private void BeginAttDefCommand()
    {
        var dialog = CreateCadChildWindow("Attribute Definition", 420, 420);
        var root = new StackPanel { Margin = new Thickness(14) };
        TextBox Box(string text = "") => new() { Text = text, Height = 24, Margin = new Thickness(0, 0, 0, 8), VerticalContentAlignment = VerticalAlignment.Center, Background = Brushes.White, Foreground = Brushes.Black };
        void Label(string text) => root.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 4, 0, 2) });
        Label("Tag:"); var tagBox = Box(); root.Children.Add(tagBox);
        Label("Prompt:"); var promptBox = Box(); root.Children.Add(promptBox);
        Label("Default value:"); var defaultBox = Box(); root.Children.Add(defaultBox);
        var invisible = new CheckBox { Content = "Invisible", Margin = new Thickness(0, 0, 0, 8) }; root.Children.Add(invisible);
        var justRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        justRow.Children.Add(new TextBlock { Text = "Justify:", Width = 70, VerticalAlignment = VerticalAlignment.Center });
        var justify = new ComboBox { ItemsSource = AttributeJustifications.Select(j => j.Name).ToArray(), SelectedIndex = 0, Width = 150, Height = 24 };
        justRow.Children.Add(justify); root.Children.Add(justRow);
        Label("Text height:"); var heightBox = Box("2.5"); root.Children.Add(heightBox);
        Label("Rotation (deg):"); var rotationBox = Box("0"); root.Children.Add(rotationBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var ok = new Button { Content = "OK", Width = 84, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };
        buttons.Children.Add(ok); buttons.Children.Add(cancel); root.Children.Add(buttons);
        dialog.Content = root;
        var accepted = false;
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(tagBox.Text)) { AppendCommandHistory("ATTDEF: Tag cannot be blank."); return; }
            accepted = true; dialog.DialogResult = true;
        };
        if (dialog.ShowDialog() != true || !accepted) { DrawingCanvas.Focus(); return; }
        var j = AttributeJustifications[Math.Max(0, justify.SelectedIndex)];
        double.TryParse(heightBox.Text, out var height); if (height <= 0) height = 2.5;
        double.TryParse(rotationBox.Text, out var rotation);
        _pendingAttDef = new CadAttributeDefData(tagBox.Text.Trim(), promptBox.Text.Trim(), defaultBox.Text, invisible.IsChecked == true, height, rotation, j.H, j.V);
        BeginPickTool("ATTDEF", "ATTDEF: Specify start point of attribute:");
    }

    /// <summary>Places the ATTDEF placeholder clicked at <paramref name="world"/>; shown as its Tag text, matching AutoCAD's
    /// display of an un-blocked attribute definition.</summary>
    private void PlaceAttributeDefinition(Point2 world)
    {
        if (_pendingAttDef is not { } def) return;
        var attachment = AttributeAttachment(def.HAlign, def.VAlign);
        var half = def.Height * Math.Max(1, def.Tag.Length) * 0.6;
        var proxy = new ProxyCadEntity("ATTDEF", new Point2(world.X - half, world.Y - def.Height * 0.3), new Point2(world.X + half, world.Y + def.Height * 1.1), true,
            Guid.NewGuid(), _editor.Drawing.CurrentLayer,
            PreviewText: def.Tag, PreviewTextHeight: def.Height, PreviewTextRotation: def.RotationDegrees * Math.PI / 180,
            PreviewTextPosition: world, PreviewTextAttachment: attachment, AttributeDef: def);
        _editor.ApplyEntityChanges([], [proxy]);
        _selectedEntityIds.Clear(); _selectedEntityIds.Add(proxy.Id);
        CancelPickTool(silent: true);
        RefreshPropertiesPalette(); RenderPreview();
        AppendCommandHistory($"ATTDEF: attribute \"{def.Tag}\" placed. Select it together with other objects, then use BLOCK to make a block.");
    }

    // ============================ BLOCK ============================
    private string? _pendingBlockName;
    private Point2 _pendingBlockBasePoint;

    private static readonly HashSet<string> BlockMemberTypes = new() { "LINE", "CIRCLE", "ARC", "POLYLINE", "TEXT", "MTEXT", "ATTDEF" };

    private static bool IsBlockMember(CadEntity e) => e switch
    {
        LineEntity or CircleEntity or ArcEntity or PolylineEntity or CadTextEntity or CadMTextEntity => true,
        ProxyCadEntity { AttributeDef: not null } => true,
        _ => false
    };

    private void BeginBlockCommand()
    {
        var dialog = CreateCadChildWindow("Block Definition", 360, 170);
        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock { Text = "Name:", Margin = new Thickness(0, 0, 0, 4) });
        var nameBox = new TextBox { Height = 24, Background = Brushes.White, Foreground = Brushes.Black, VerticalContentAlignment = VerticalAlignment.Center };
        root.Children.Add(nameBox);
        var hint = new TextBlock { Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap,
            Text = "After OK: click a base point, then select the objects to include and press Enter." };
        root.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "OK", Width = 84, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };
        buttons.Children.Add(ok); buttons.Children.Add(cancel); root.Children.Add(buttons);
        dialog.Content = root;
        var accepted = false;
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text)) { AppendCommandHistory("BLOCK: name cannot be blank."); return; }
            accepted = true; dialog.DialogResult = true;
        };
        if (dialog.ShowDialog() != true || !accepted) { DrawingCanvas.Focus(); return; }
        _pendingBlockName = nameBox.Text.Trim();
        if (_editor.Drawing.BlockDefinitions.ContainsKey(_pendingBlockName))
            AppendCommandHistory($"BLOCK: \"{_pendingBlockName}\" already exists and will be redefined.");
        _selectedEntityIds.Clear();
        BeginPickTool("BLOCKBASE", $"BLOCK \"{_pendingBlockName}\": Specify base point:");
    }

    /// <summary>Base point picked; now ask for the member objects (reuses the generic RequireSelection/_selectionCallback mechanism).</summary>
    private void BlockBasePointPicked(Point2 world)
    {
        if (_pendingBlockName is null) return;
        _pendingBlockBasePoint = world;
        CancelPickTool(silent: true);
        _selectedEntityIds.Clear();
        RequireSelection("BLOCK", FinishBlockDefinition);
    }

    private void FinishBlockDefinition()
    {
        var name = _pendingBlockName;
        if (name is null) return;
        var selected = SelectedGeometry();
        var members = selected.Where(IsBlockMember).ToArray();
        var skipped = selected.Length - members.Length;
        if (members.Length == 0)
        {
            AppendCommandHistory("BLOCK: no supported objects were selected (LINE/CIRCLE/ARC/POLYLINE/TEXT/MTEXT/ATTDEF only).");
            _pendingBlockName = null; _selectedEntityIds.Clear(); return;
        }
        var basePoint = _pendingBlockBasePoint;
        var offset = new Point2(-basePoint.X, -basePoint.Y);
        var localEntities = members.Select(e => TranslateEntity(e, offset, newId: false)).ToArray();
        _editor.Drawing.BlockDefinitions[name] = new CadBlockDefinition(name, new Point2(0, 0), localEntities);

        var (paths, texts, attributes) = BuildBlockInsertGraphics(members);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Grow(Point2 p) { minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); }
        foreach (var path in paths) foreach (var p in path.Points) Grow(p);
        foreach (var t in texts) Grow(t.Position);
        if (minX > maxX) { minX = basePoint.X; minY = basePoint.Y; maxX = basePoint.X; maxY = basePoint.Y; }

        var insert = new ProxyCadEntity("INSERT", new Point2(minX, minY), new Point2(maxX, maxY), true, Guid.NewGuid(), _editor.Drawing.CurrentLayer,
            ProxyGraphicsPaths: paths, ProxyGraphicsTexts: texts,
            InsertData: new CadInsertData(name, basePoint, 0, 1, 1, attributes, attributes.Count));
        _editor.ApplyEntityChanges(members, [insert]);
        _selectedEntityIds.Clear(); _selectedEntityIds.Add(insert.Id);
        _pendingBlockName = null;
        RefreshPropertiesPalette(); RenderPreview();
        AppendCommandHistory($"BLOCK: \"{name}\" defined from {members.Length} object(s){(skipped > 0 ? $" ({skipped} unsupported skipped)" : "")}{(attributes.Count > 0 ? $", {attributes.Count} attribute(s)" : "")}.");
    }

    /// <summary>Builds the visual (paths + texts) of a fresh block-reference INSERT from its member entities, as they currently
    /// stand in the drawing (world coordinates, so the new INSERT - placed at Rotation 0 / Scale 1 - looks exactly the same as
    /// the original objects). ATTDEF members become attribute VALUES (their default text) instead of their Tag placeholder,
    /// matching what AutoCAD actually shows for an inserted block reference.</summary>
    private (List<ProxyPreviewPath> Paths, List<ProxyPreviewText> Texts, List<CadInsertAttribute> Attributes) BuildBlockInsertGraphics(CadEntity[] members)
    {
        var paths = new List<ProxyPreviewPath>();
        var texts = new List<ProxyPreviewText>();
        var attributes = new List<CadInsertAttribute>();
        foreach (var e in members)
        {
            switch (e)
            {
                case LineEntity l: paths.Add(new ProxyPreviewPath(new[] { l.Start, l.End }, false)); break;
                case CircleEntity c:
                    paths.Add(new ProxyPreviewPath(Enumerable.Range(0, 96).Select(i => new Point2(c.Center.X + c.Radius * Math.Cos(i * Math.Tau / 96), c.Center.Y + c.Radius * Math.Sin(i * Math.Tau / 96))).ToArray(), true));
                    break;
                case ArcEntity a:
                {
                    var sweep = a.EndAngleDegrees - a.StartAngleDegrees; while (sweep <= 0) sweep += 360;
                    var count = Math.Clamp((int)(sweep / 5), 8, 96);
                    var pts = new Point2[count + 1];
                    for (var i = 0; i <= count; i++)
                    {
                        var ang = (a.StartAngleDegrees + sweep * i / count) * Math.PI / 180;
                        pts[i] = new Point2(a.Center.X + a.Radius * Math.Cos(ang), a.Center.Y + a.Radius * Math.Sin(ang));
                    }
                    paths.Add(new ProxyPreviewPath(pts, false));
                    break;
                }
                case PolylineEntity p:
                {
                    var ring = PolylineTessellator.Tessellate(p).ToArray();
                    paths.Add(new ProxyPreviewPath(ring, p.Closed));
                    break;
                }
                case CadTextEntity t:
                    texts.Add(new ProxyPreviewText(t.Value, t.Position, t.Height, t.RotationRadians, 0, 1, AttributeAttachment(t.HorizontalAlignment, t.VerticalAlignment),
                        t.TextFontFamily, t.TextBold, t.TextItalic, t.TextObliqueAngleRadians, t.TextWidthFactor, false, null, t.HorizontalAlignment, t.VerticalAlignment));
                    break;
                case CadMTextEntity m:
                    texts.Add(new ProxyPreviewText(m.Value, m.Position, m.Height, m.RotationRadians, m.Width, m.LineSpacing, m.AttachmentPoint,
                        m.TextFontFamily, m.TextBold, m.TextItalic, m.TextObliqueAngleRadians, m.TextWidthFactor, true, m.FormattedValue));
                    break;
                case ProxyCadEntity { AttributeDef: { } def } proxy when proxy.PreviewTextPosition is { } at:
                    texts.Add(new ProxyPreviewText(def.DefaultValue, at, def.Height, def.RotationDegrees * Math.PI / 180, 0, 1, AttributeAttachment(def.HAlign, def.VAlign),
                        null, false, false, 0, def.WidthFactor, false, null, def.HAlign, def.VAlign, def.Tag));
                    attributes.Add(new CadInsertAttribute(def.Tag, def.DefaultValue, def.Prompt, null, def.HAlign, def.VAlign, false, false,
                        def.Height, def.WidthFactor, def.RotationDegrees, 0, proxy.Layer, null, null, null, Detailed: true));
                    break;
            }
        }
        return (paths, texts, attributes);
    }

    // ============================ INSERT (internal blocks + external files) ============================

    /// <summary>INSERT now offers the drawing's own named blocks (from BLOCK) alongside the existing "insert a file" browse,
    /// matching AutoCAD's Insert dialog (Name combo vs Browse). With no internal blocks defined yet it falls back to the
    /// original file-only behavior unchanged.</summary>
    private void BeginInsertBlockOrFile()
    {
        var drawing = _editor.Drawing;
        if (drawing.BlockDefinitions.Count == 0) { BeginInsertFile(); return; }
        var dialog = CreateCadChildWindow("Insert", 360, 200);
        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock { Text = "Name:", Margin = new Thickness(0, 0, 0, 4) });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var combo = new ComboBox { ItemsSource = drawing.BlockDefinitions.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(), Width = 220, Height = 24 };
        combo.SelectedIndex = 0;
        row.Children.Add(combo); root.Children.Add(row);
        var browse = new Button { Content = "Browse for a drawing file...", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 0, 0, 10) };
        root.Children.Add(browse);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", Width = 84, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };
        buttons.Children.Add(ok); buttons.Children.Add(cancel); root.Children.Add(buttons);
        dialog.Content = root;
        var mode = 0; // 0 = cancelled, 1 = internal block, 2 = file
        browse.Click += (_, _) => { mode = 2; dialog.DialogResult = true; };
        ok.Click += (_, _) => { if (combo.SelectedItem is string) { mode = 1; dialog.DialogResult = true; } };
        var choice = dialog.ShowDialog();
        var pickedName = combo.SelectedItem as string;
        if (choice != true || mode == 0) { DrawingCanvas.Focus(); return; }
        if (mode == 2) { BeginInsertFile(); return; }
        if (pickedName is null || !drawing.BlockDefinitions.TryGetValue(pickedName, out var block)) return;
        BeginInsertInternalBlock(block);
    }

    private void BeginInsertInternalBlock(CadBlockDefinition block)
    {
        var attributeDefs = block.Entities.OfType<ProxyCadEntity>().Where(e => e.AttributeDef is not null).Select(e => e.AttributeDef!).ToArray();
        var values = attributeDefs.ToDictionary(a => a.Tag, a => a.DefaultValue, StringComparer.OrdinalIgnoreCase);
        if (attributeDefs.Length > 0 && !PromptInsertAttributeValues(block.Name, attributeDefs, values)) { DrawingCanvas.Focus(); return; }

        var (paths, texts, _) = BuildBlockInsertGraphics(block.Entities.ToArray());
        // Re-point the attribute value texts at the user-entered values (BuildBlockInsertGraphics used the block's defaults).
        texts = texts.Select(t => t.Tag is { } tag && values.TryGetValue(tag, out var v) ? t with { Text = v } : t).ToList();
        var attributes = attributeDefs.Select(def => new CadInsertAttribute(def.Tag, values.GetValueOrDefault(def.Tag, def.DefaultValue), def.Prompt, null,
            def.HAlign, def.VAlign, false, false, def.Height, def.WidthFactor, def.RotationDegrees, 0, null, null, null, null, Detailed: true)).ToList();

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Grow(Point2 p) { minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); }
        foreach (var path in paths) foreach (var p in path.Points) Grow(p);
        foreach (var t in texts) Grow(t.Position);
        if (minX > maxX) { minX = minY = 0; maxX = maxY = 0; }

        _selectedEntityIds.Clear();
        _stretchActive = false; _transformCopy = true;
        var placeholder = new ProxyCadEntity("INSERT", new Point2(minX, minY), new Point2(maxX, maxY), true, Guid.NewGuid(), _editor.Drawing.CurrentLayer,
            ProxyGraphicsPaths: paths, ProxyGraphicsTexts: texts, InsertData: new CadInsertData(block.Name, new Point2(0, 0), 0, 1, 1, attributes, attributes.Count));
        _transformEntities = new CadEntity[] { placeholder };
        _transformActive = true; _transformBasePoint = new Point2(0, 0);
        DynamicPromptText.Text = "Specify insertion point:";
        DynamicValueTextBox.Clear();
        UpdateDynamicInputMode(); PositionDynamicInput(_lastPointer); FocusDynamicInput();
        AppendCommandHistory($"INSERT: block \"{block.Name}\"{(attributes.Count > 0 ? $" ({attributes.Count} attribute(s))" : "")}. Specify insertion point:");
    }

    /// <summary>Simple attribute-value prompt shown once at INSERT time (distinct from the full Enhanced Attribute Editor used
    /// for post-insert editing): one row per attribute definition, defaulted to its ATTDEF default value.</summary>
    private bool PromptInsertAttributeValues(string blockName, CadAttributeDefData[] defs, Dictionary<string, string> values)
    {
        var dialog = CreateCadChildWindow($"Insert - {blockName} attributes", 420, Math.Min(560, 120 + defs.Length * 34));
        dialog.ResizeMode = ResizeMode.CanResize;
        var root = new StackPanel { Margin = new Thickness(14) };
        var boxes = new List<(string Tag, TextBox Box)>();
        foreach (var def in defs)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(new TextBlock { Text = (string.IsNullOrWhiteSpace(def.Prompt) ? def.Tag : def.Prompt) + ":", Width = 160, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var box = new TextBox { Text = def.DefaultValue, Width = 200, Height = 24, Background = Brushes.White, Foreground = Brushes.Black, VerticalContentAlignment = VerticalAlignment.Center };
            row.Children.Add(box); root.Children.Add(row); boxes.Add((def.Tag, box));
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var ok = new Button { Content = "OK", Width = 84, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };
        buttons.Children.Add(ok); buttons.Children.Add(cancel); root.Children.Add(buttons);
        dialog.Content = root;
        var accepted = false;
        ok.Click += (_, _) => { accepted = true; dialog.DialogResult = true; };
        if (dialog.ShowDialog() != true || !accepted) return false;
        foreach (var (tag, box) in boxes) values[tag] = box.Text;
        return true;
    }
}
