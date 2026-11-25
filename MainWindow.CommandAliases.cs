using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using iStCAD_1.Core.Model;

namespace iStCAD_1;

public partial class MainWindow
{
    private sealed class CommandAliasRow
    {
        public string Command { get; set; } = "";
        /// <summary>Built-in shortcuts (read-only).</summary>
        public string Default { get; set; } = "";
        /// <summary>The user's own shortcuts, in addition to the defaults.</summary>
        public string Alias { get; set; } = "";
        public string Status { get; set; } = "";
    }
    private Dictionary<string, string> _customAliases = new(StringComparer.OrdinalIgnoreCase);
    private string AliasSettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iStCAD_1", "command-aliases.json");

    private void LoadCustomCommandAliases()
    {
        try
        {
            if (!File.Exists(AliasSettingsPath)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(AliasSettingsPath));
            if (loaded is null) return;
            _customAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (command, value) in loaded)
            {
                var defaults = (CommandHints.FirstOrDefault(x => x.Name.Equals(command, StringComparison.OrdinalIgnoreCase))?.Aliases ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var own = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(alias => !defaults.Contains(alias)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (own.Length > 0) _customAliases[command] = string.Join(", ", own);
            }
        }
        catch { _customAliases = new(StringComparer.OrdinalIgnoreCase); }
    }
    private string GetAliasesForCommand(string canonical)
    {
        var hint = CommandHints.FirstOrDefault(x => x.Name.Equals(canonical, StringComparison.OrdinalIgnoreCase));
        var defaults = hint?.Aliases ?? "";
        if (!_customAliases.TryGetValue(canonical, out var custom) || string.IsNullOrWhiteSpace(custom)) return defaults;
        return string.IsNullOrWhiteSpace(defaults) ? custom : defaults + ", " + custom; // defaults always work; user aliases are added to them
    }
    private string ResolveCommandAlias(string entered)
    {
        // Preserve the exact characters typed (including Vietnamese); compare using ordinal ignore-case.
        foreach (var hint in CommandHints)
        {
            if (entered.Equals(hint.Name, StringComparison.OrdinalIgnoreCase)) return hint.Name;
            if (GetAliasesForCommand(hint.Name).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(alias => entered.Equals(alias, StringComparison.OrdinalIgnoreCase))) return hint.Name;
        }
        return entered;
    }
    private void Aliases_Click(object sender, RoutedEventArgs e) => ShowCommandAliasManager();

    private void ShowCommandAliasManager()
    {
        var dialog = CreateCadChildWindow("Command Aliases — main command, default shortcuts, your shortcuts", 790, 610);
        dialog.MinWidth = 640; dialog.MinHeight = 480; dialog.ResizeMode = ResizeMode.CanResize;
        var root = new DockPanel { Margin = new Thickness(14) };
        var info = new TextBlock { Text = "Nhập một hoặc nhiều lệnh tắt, phân cách bằng dấu phẩy. Giữ nguyên dấu tiếng Việt (ví dụ: á, â, đ). Các lệnh chưa lập trình được đánh dấu Pending.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,10) };
        DockPanel.SetDock(info, Dock.Top); root.Children.Add(info);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,10,0,0) };
        DockPanel.SetDock(toolbar, Dock.Bottom); root.Children.Add(toolbar);
        var save = new Button { Content = "Save aliases", MinWidth = 112, Height = 30, Margin = new Thickness(6,0,0,0), IsDefault = true };
        var reset = new Button { Content = "Restore defaults", MinWidth = 120, Height = 30, Margin = new Thickness(6,0,0,0) };
        var close = new Button { Content = "Close", MinWidth = 82, Height = 30, Margin = new Thickness(6,0,0,0), IsCancel = true };
        toolbar.Children.Add(reset); toolbar.Children.Add(save); toolbar.Children.Add(close);
        var rows = new ObservableCollection<CommandAliasRow>(CommandHints.OrderBy(x => x.Name).Select(x => new CommandAliasRow
        { Command = x.Name, Default = x.Aliases, Alias = _customAliases.TryGetValue(x.Name, out var own) ? own : "", Status = x.Description.Contains("pending", StringComparison.OrdinalIgnoreCase) ? "Pending" : "Available" }));
        var table = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            RowHeight = 27, ColumnHeaderHeight = 31, SelectionMode = DataGridSelectionMode.Single };
        table.Columns.Add(new DataGridTextColumn { Header = "Command", Binding = new System.Windows.Data.Binding(nameof(CommandAliasRow.Command)), IsReadOnly = true, Width = new DataGridLength(170) });
        table.Columns.Add(new DataGridTextColumn { Header = "Default shortcut", Binding = new System.Windows.Data.Binding(nameof(CommandAliasRow.Default)), IsReadOnly = true, Width = new DataGridLength(150) });
        table.Columns.Add(new DataGridTextColumn { Header = "Your shortcut / Vietnamese alias (editable)", Binding = new System.Windows.Data.Binding(nameof(CommandAliasRow.Alias)) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        table.Columns.Add(new DataGridTextColumn { Header = "Status", Binding = new System.Windows.Data.Binding(nameof(CommandAliasRow.Status)), IsReadOnly = true, Width = new DataGridLength(100) });
        root.Children.Add(table);
        reset.Click += (_, _) =>
        {
            var response = MessageBox.Show(dialog, "Remove all your own shortcuts and keep only the defaults?", "Restore shortcuts", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (response != MessageBoxResult.Yes) return;
            foreach (var row in rows) row.Alias = string.Empty; // defaults are built in; only the user's own shortcuts are cleared
            table.Items.Refresh();
        };
        save.Click += (_, _) =>
        {
            table.CommitEdit(DataGridEditingUnit.Cell, true); table.CommitEdit(DataGridEditingUnit.Row, true);
            var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hint in CommandHints)
                foreach (var builtIn in hint.Aliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) keys[builtIn] = hint.Name;
            foreach (var row in rows)
            {
                foreach (var alias in row.Alias.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (alias.Length > 40 || alias.Any(char.IsControl)) { MessageBox.Show(dialog, $"Invalid alias: {alias}"); return; }
                    if (keys.TryGetValue(alias, out var other) && !other.Equals(row.Command, StringComparison.OrdinalIgnoreCase)) { MessageBox.Show(dialog, $"Alias '{alias}' is already used by {other}."); return; }
                    if (keys.TryGetValue(alias, out _) ) continue; // same command already has it as a default
                    if (CommandHints.Any(x => x.Name.Equals(alias, StringComparison.OrdinalIgnoreCase) && x.Name != row.Command))
                    { MessageBox.Show(dialog, $"Alias '{alias}' conflicts with an existing command."); return; }
                    keys.Add(alias, row.Command);
                }
            }
            var next = rows.Where(row => !string.IsNullOrWhiteSpace(row.Alias)).ToDictionary(row => row.Command, row => row.Alias.Trim(), StringComparer.OrdinalIgnoreCase);
            try { Directory.CreateDirectory(Path.GetDirectoryName(AliasSettingsPath)!); File.WriteAllText(AliasSettingsPath, JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true })); _customAliases = next; dialog.DialogResult = true; }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Save aliases failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        };
        dialog.Content = root;
        ConfigureEditorDialog(dialog);
        dialog.ShowDialog();
    }
}
