using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ResourceManager.Core;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace ResourceManager.App;

internal sealed class PublicationPicker : Window
{
    private readonly TextBox path = new();
    private readonly ListBox entries = new() { SelectionMode = System.Windows.Controls.SelectionMode.Extended };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button choose = new() { Content = "选择所选项目", IsEnabled = false, Padding = new Thickness(14, 7, 14, 7) };
    private readonly Button current = new() { Content = "选择当前文件夹", IsEnabled = false, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0) };
    private string? directory;
    private int generation;
    internal IReadOnlyList<string> SelectedPaths { get; private set; } = [];

    internal PublicationPicker(string initialDirectory)
    {
        Title = "选择要发布的文件或文件夹"; Width = 760; Height = 540; MinWidth = 540; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var grid = new Grid { Margin = new Thickness(18) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var location = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var up = new Button { Content = "上一级", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0) };
        up.Click += async (_, _) => { if (directory is not null && Directory.GetParent(directory) is { } parent) await NavigateAsync(parent.FullName); };
        location.Children.Add(up);
        var drives = new ComboBox { Width = 85, Margin = new Thickness(0, 0, 8, 0), ItemsSource = DriveInfo.GetDrives().Select(d => d.Name).ToArray() };
        drives.SelectionChanged += async (_, _) => { if (drives.SelectedItem is string drive) await NavigateAsync(drive); };
        location.Children.Add(drives); location.Children.Add(path); grid.Children.Add(location);
        path.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await NavigateAsync(path.Text); } };
        entries.DisplayMemberPath = nameof(PickerEntry.Label); Grid.SetRow(entries, 1); grid.Children.Add(entries);
        entries.SelectionChanged += (_, _) => choose.IsEnabled = entries.SelectedItems.Count > 0;
        entries.MouseDoubleClick += async (_, e) =>
        {
            if (entries.SelectedItem is not PickerEntry entry) return;
            e.Handled = true;
            if (entry.Folder) await NavigateAsync(entry.Path);
            else Complete([entry.Path]);
        };
        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; bottom.Children.Add(status);
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        current.Click += (_, _) => { if (directory is not null) Complete([directory]); };
        choose.Click += (_, _) => Complete(entries.SelectedItems.Cast<PickerEntry>().Select(e => e.Path).ToArray());
        buttons.Children.Add(current); buttons.Children.Add(choose);
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 7, 14, 7) };
        buttons.Children.Add(cancel); bottom.Children.Add(buttons); Grid.SetRow(bottom, 2); grid.Children.Add(bottom);
        Content = grid;
        Loaded += async (_, _) => await NavigateAsync(initialDirectory);
    }

    internal async Task NavigateAsync(string target)
    {
        var request = ++generation;
        choose.IsEnabled = current.IsEnabled = false;
        entries.ItemsSource = null;
        status.Text = "正在读取…";
        try
        {
            var resolved = System.IO.Path.GetFullPath(target);
            var items = await Task.Run(() => new DirectoryInfo(resolved).EnumerateFileSystemInfos()
                .Where(f => !f.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) && (f.Attributes & FileAttributes.ReparsePoint) == 0)
                .Select(f => new PickerEntry(f.FullName, f.Name, (f.Attributes & FileAttributes.Directory) != 0))
                .OrderByDescending(f => f.Folder).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray());
            if (request != generation) return;
            directory = resolved; path.Text = resolved; entries.ItemsSource = items; current.IsEnabled = true;
            status.Text = "单击选择文件或文件夹；Ctrl / Shift 可多选，双击文件夹进入。";
        }
        catch (Exception ex) { if (request == generation) { directory = null; status.Text = "无法读取目录：" + ex.Message; } }
    }

    private void Complete(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        SelectedPaths = paths; DialogResult = true;
    }
    internal sealed record PickerEntry(string Path, string Name, bool Folder) { public string Label => (Folder ? "📁  " : "📄  ") + Name; }
}

internal sealed class GroupEditorDialog : Window
{
    private readonly TextBox name = new() { Margin = new Thickness(0, 6, 0, 14), MaxLength = 80 };
    private readonly ComboBox parents = new() { Margin = new Thickness(0, 6, 0, 18), DisplayMemberPath = "Label", SelectedValuePath = "Id" };
    internal string GroupName => name.Text;
    internal string? ParentId => parents.SelectedValue as string;
    internal GroupEditorDialog(string title, IReadOnlyList<ResourceGroup> groups, string text, string? parentId, bool editName, bool allowRoot, IReadOnlySet<string>? excluded = null)
    {
        Title = title; Width = 430; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(22) }; name.Text = text;
        if (editName) { panel.Children.Add(new TextBlock { Text = "分组名称" }); panel.Children.Add(name); }
        panel.Children.Add(new TextBlock { Text = "目标位置" });
        var options = new List<GroupChoice>();
        if (allowRoot) options.Add(new(null, "顶层"));
        foreach (var group in groups.Where(g => excluded?.Contains(g.Id) != true))
        {
            var names = new List<string> { group.Name }; var cursor = group.ParentId; var seen = new HashSet<string>();
            while (cursor is not null && seen.Add(cursor)) { var ancestor = groups.FirstOrDefault(g => g.Id == cursor); if (ancestor is null) break; names.Insert(0, ancestor.Name); cursor = ancestor.ParentId; }
            options.Add(new(group.Id, string.Join(" / ", names)));
        }
        parents.ItemsSource = options; parents.SelectedItem = options.FirstOrDefault(g => g.Id == parentId) ?? options.FirstOrDefault(); panel.Children.Add(parents);
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var save = new Button { Content = "确定", IsDefault = true, Padding = new Thickness(20, 7, 20, 7) };
        save.Click += (_, _) => { if ((!editName || !string.IsNullOrWhiteSpace(name.Text)) && parents.SelectedItem is not null) DialogResult = true; };
        buttons.Children.Add(save); buttons.Children.Add(new Button { Content = "取消", IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(20, 7, 20, 7) });
        panel.Children.Add(buttons); Content = panel;
    }
    private sealed record GroupChoice(string? Id, string Label);
}
