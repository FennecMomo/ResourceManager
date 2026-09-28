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
