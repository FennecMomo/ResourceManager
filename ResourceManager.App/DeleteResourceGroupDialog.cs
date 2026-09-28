using System.Windows;
using System.Windows.Controls;
using ResourceManager.Core;
using Button = System.Windows.Controls.Button;

namespace ResourceManager.App;

internal sealed class DeleteResourceGroupDialog : Window
{
    private bool? revoke;
    private DeleteResourceGroupDialog(Window owner, string name, int children, IReadOnlyList<LocalResource> resources)
    {
        Owner = owner; Icon = owner.Icon; Title = "删除分组"; Width = 570; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = $"删除“{name}”及其 {children} 个子分组", FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = $"影响 {resources.Count} 个发布项。可以保留发布并移到默认组，也可以一并撤销。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 10) });
        panel.Children.Add(new ScrollViewer { MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = string.Join("\n", resources.Select(r => $"• {r.Name}（{(r.Mode == PublishMode.Copy ? "管理副本" : "引用原位置")}）")), TextWrapping = TextWrapping.Wrap } });
        panel.Children.Add(new TextBlock { Text = "同时撤销会删除软件管理的副本；引用的原文件不会删除。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 20) });
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        void Add(string label, bool? action)
        {
            var button = new Button { Content = label, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(8, 0, 0, 0), IsCancel = action is null };
            button.Click += (_, _) => { revoke = action; Close(); }; buttons.Children.Add(button);
        }
        Add("资源移到默认组", false); Add("同时撤销发布", true); Add("取消", null);
        panel.Children.Add(buttons); Content = panel;
    }
    internal static bool? Ask(Window owner, string name, int children, IReadOnlyList<LocalResource> resources)
    {
        var dialog = new DeleteResourceGroupDialog(owner, name, children, resources); dialog.ShowDialog(); return dialog.revoke;
    }
}
