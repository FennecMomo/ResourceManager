using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;

namespace ResourceManager.App;

public sealed class ChatToastWindow : Window
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    public event EventHandler? OpenRequested;

    public ChatToastWindow(string sender, string preview)
    {
        Title = "新聊天消息";
        Width = 340;
        Height = 112;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        var border = new Border
        {
            Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(220, 228, 238)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(16),
            Cursor = Cursors.Hand
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = sender, FontWeight = FontWeights.SemiBold, FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromRgb(27, 43, 67)) });
        stack.Children.Add(new TextBlock { Text = preview, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(Color.FromRgb(83, 100, 123)), Margin = new Thickness(0, 7, 0, 0) });
        stack.Children.Add(new TextBlock { Text = "点击打开会话", FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(40, 103, 217)), Margin = new Thickness(0, 7, 0, 0) });
        border.Child = stack;
        Content = border;
        MouseLeftButtonUp += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        timer.Tick += (_, _) => Close();
        Loaded += (_, _) => timer.Start();
        Closed += (_, _) => timer.Stop();
    }
}
