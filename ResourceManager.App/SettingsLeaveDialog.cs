using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Panel = System.Windows.Controls.Panel;
using Color = System.Windows.Media.Color;

namespace ResourceManager.App;

internal sealed class SettingsLeaveDialog : Window
{
    private SettingsLeaveChoice choice = SettingsLeaveChoice.KeepEditing;

    private SettingsLeaveDialog(Window owner, string message)
    {
        Owner = owner;
        Title = "尚未保存设置";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(247, 249, 252));
        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 15 });
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        AddAction(actions, "保存并离开", SettingsLeaveChoice.Save);
        AddAction(actions, "放弃修改", SettingsLeaveChoice.Discard);
        AddAction(actions, "继续编辑", SettingsLeaveChoice.KeepEditing);
        content.Children.Add(actions);
        Content = content;
    }

    private void AddAction(Panel panel, string text, SettingsLeaveChoice result)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(6, 0, 0, 0), IsCancel = result == SettingsLeaveChoice.KeepEditing };
        button.Click += (_, _) => { choice = result; Close(); };
        panel.Children.Add(button);
    }

    public static SettingsLeaveChoice Ask(Window owner, string message)
    {
        var dialog = new SettingsLeaveDialog(owner, message);
        dialog.ShowDialog();
        return dialog.choice;
    }
}
