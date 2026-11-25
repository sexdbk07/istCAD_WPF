using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace iStCAD_1;

/// <summary>Edit menu: undo/redo, clipboard, selection, properties and the command alias manager.</summary>
public partial class MainWindow
{
    private void MenuEdit_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MenuEditButton, Placement = PlacementMode.Bottom };
        MenuItem Item(string header, Action action, string? gesture = null)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture };
            item.Click += (_, _) => { try { action(); } catch (Exception ex) { AppendCommandHistory("Edit menu: " + ex.Message); } DrawingCanvas.Focus(); };
            return item;
        }
        menu.Items.Add(Item("Undo", () => ProcessCommand("U"), "U"));
        menu.Items.Add(Item("Redo", () => ProcessCommand("Y"), "Y"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Cut", () => ProcessCommand("CUTCLIP")));
        menu.Items.Add(Item("Copy", () => ProcessCommand("COPYCLIP")));
        menu.Items.Add(Item("Paste", () => ProcessCommand("PASTECLIP")));
        menu.Items.Add(Item("Erase", () => ProcessCommand("ERASE"), "E"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Select all", SelectAllEntities, "Ctrl+A"));
        menu.Items.Add(Item("Select similar", () => ProcessCommand("SELECTSIMILAR")));
        menu.Items.Add(Item("Match properties", () => ProcessCommand("MATCHPROP"), "MA"));
        menu.Items.Add(Item("Properties", () => ProcessCommand("PROPERTIES"), "PR"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Command aliases…", ShowCommandAliasManager));
        menu.IsOpen = true;
    }
}
