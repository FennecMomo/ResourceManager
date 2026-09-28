using System.Windows;
using System.Windows.Controls;
using ResourceManager.Core;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace ResourceManager.App;

internal sealed class GroupPermissionDialog : Window
{
    private readonly ComboBox modes = new() { DisplayMemberPath = "Label", SelectedValuePath = "Value", Margin = new Thickness(0, 8, 0, 12) };
    private readonly List<(string Id, CheckBox Box)> devices = [];
    internal GroupAccess Access => (GroupAccess)modes.SelectedValue;
    internal IEnumerable<string> AllowedDevices => devices.Where(d => d.Box.IsChecked == true).Select(d => d.Id);

    internal GroupPermissionDialog(NodeStore store, ResourceGroup group)
    {
        Title = "分组权限 · " + group.Name; Width = 530; Height = 510; MinWidth = 460; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = System.Windows.Media.Brushes.White;
        var grid = new Grid { Margin = new Thickness(24) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "谁可以访问这个分组", FontSize = 20, FontWeight = FontWeights.SemiBold });
        modes.ItemsSource = new[] { new Choice(GroupAccess.Inherit, group.ParentId is null ? "默认（公开）" : "继承上级分组"), new Choice(GroupAccess.Public, "公开 · 已连接设备可访问"), new Choice(GroupAccess.Private, "私有 · 仅本机可见"), new Choice(GroupAccess.AllowList, "白名单 · 仅下方勾选的设备") };
        var permission = store.GetGroupPermission(group.Id); modes.SelectedValue = permission.Access; top.Children.Add(modes);
        var inherited = group.ParentId is null ? null : store.GetEffectiveGroupPermission(group.ParentId);
        top.Children.Add(new TextBlock { Text = inherited is null ? "顶层分组默认公开。子分组默认继承，也可以单独设置。" : $"上级当前权限：{AccessText(inherited.Access)}。子分组默认继承，也可以单独设置。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        grid.Children.Add(top);
        var list = new StackPanel();
        foreach (var peer in store.GetPeers())
        {
            var note = store.GetPeerNote(peer.DeviceId);
            var box = new CheckBox { Content = new TextBlock { Text = $"{(string.IsNullOrWhiteSpace(note) ? peer.Nickname : note + " · " + peer.Nickname)}\nID：{peer.DeviceId}\n最近地址：{peer.Ip}:{peer.Port}", TextWrapping = TextWrapping.Wrap }, IsChecked = permission.DeviceIds.Contains(peer.DeviceId), Margin = new Thickness(0, 0, 0, 14) };
            devices.Add((peer.DeviceId, box)); list.Children.Add(box);
        }
        foreach (var member in store.GetWorkspaceDevices().Where(m => m.Profile.DeviceId != store.GetSettings().Profile.DeviceId && devices.All(d => d.Id != m.Profile.DeviceId)))
        {
            var box = new CheckBox { Content = new TextBlock { Text = $"{member.Profile.Nickname} · 服务器设备\nID：{member.Profile.DeviceId}", TextWrapping = TextWrapping.Wrap }, IsChecked = permission.DeviceIds.Contains(member.Profile.DeviceId), Margin = new Thickness(0, 0, 0, 14) };
            devices.Add((member.Profile.DeviceId, box)); list.Children.Add(box);
        }
        if (devices.Count == 0) list.Children.Add(new TextBlock { Text = "暂无已连接设备。空白名单不允许任何远端设备访问。", TextWrapping = TextWrapping.Wrap });
        list.IsEnabled = Access == GroupAccess.AllowList;
        modes.SelectionChanged += (_, _) => list.IsEnabled = Access == GroupAccess.AllowList;
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        bottom.Children.Add(new TextBlock { Text = "白名单设备需使用 0.5.3 或更新版本。保存后对新的目录和下载请求生效；已被对方下载的文件不会收回。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var save = new Button { Content = "保存权限", Padding = new Thickness(18, 8, 18, 8), IsDefault = true };
        save.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(new Button { Content = "取消", Padding = new Thickness(18, 8, 18, 8), IsCancel = true, Margin = new Thickness(0, 0, 8, 0) }); buttons.Children.Add(save); bottom.Children.Add(buttons);
        Grid.SetRow(bottom, 2); grid.Children.Add(bottom); Content = grid;
    }

    internal static string AccessText(GroupAccess access) => access switch { GroupAccess.Public => "公开", GroupAccess.Private => "私有", GroupAccess.AllowList => "设备白名单", _ => "继承" };
    private sealed record Choice(GroupAccess Value, string Label);
}
