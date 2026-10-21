using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ResourceManager.Core;
using Image = System.Windows.Controls.Image;
using MenuItem = System.Windows.Controls.MenuItem;
using Color = System.Windows.Media.Color;

namespace ResourceManager.App;

public sealed class ChatImageView : Border
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(ChatMessage), typeof(ChatImageView), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty ImageLoaderProperty = DependencyProperty.Register(nameof(ImageLoader), typeof(Func<ChatMessage, CancellationToken, Task<BitmapSource>>), typeof(ChatImageView), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty CopyImageProperty = DependencyProperty.Register(nameof(CopyImage), typeof(Func<ChatMessage, CancellationToken, Task>), typeof(ChatImageView));
    public static readonly DependencyProperty SaveImageAsProperty = DependencyProperty.Register(nameof(SaveImageAs), typeof(Func<ChatMessage, CancellationToken, Task>), typeof(ChatImageView));
    private readonly Image image = new() { Stretch = Stretch.Uniform, MaxHeight = 250, MaxWidth = 380 };
    private readonly TextBlock status = new() { Text = "图片待加载", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12), HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private CancellationTokenSource? current;
    private DispatcherOperation? scheduled;
    private long generation;

    public ChatImageView()
    {
        MinHeight = 90; MaxWidth = 400; Padding = new Thickness(4); CornerRadius = new CornerRadius(8);
        Background = new SolidColorBrush(Color.FromRgb(235, 241, 246));
        var content = new Grid(); content.Children.Add(image); content.Children.Add(status); Child = content;
        var menu = new System.Windows.Controls.ContextMenu();
        var copy = new MenuItem { Header = "复制图像" }; copy.Click += async (_, _) => await CopyAsync(); menu.Items.Add(copy);
        var save = new MenuItem { Header = "另存为…" }; save.Click += async (_, _) => await SaveAsAsync(); menu.Items.Add(save);
        ContextMenu = menu;
        Loaded += (_, _) => Schedule();
        Unloaded += (_, _) => Cancel();
    }

    public ChatMessage? Message { get => (ChatMessage?)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public Func<ChatMessage, CancellationToken, Task<BitmapSource>>? ImageLoader { get => (Func<ChatMessage, CancellationToken, Task<BitmapSource>>?)GetValue(ImageLoaderProperty); set => SetValue(ImageLoaderProperty, value); }
    public Func<ChatMessage, CancellationToken, Task>? CopyImage { get => (Func<ChatMessage, CancellationToken, Task>?)GetValue(CopyImageProperty); set => SetValue(CopyImageProperty, value); }
    public Func<ChatMessage, CancellationToken, Task>? SaveImageAs { get => (Func<ChatMessage, CancellationToken, Task>?)GetValue(SaveImageAsProperty); set => SetValue(SaveImageAsProperty, value); }
    internal Task LoadingTask { get; private set; } = Task.CompletedTask;
    internal bool HasImage => image.Source is not null;
    internal BitmapSource? LoadedImage => image.Source as BitmapSource;
    internal string StatusText => status.Text;

    private static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var view = (ChatImageView)owner; view.Cancel(); view.image.Source = null;
        view.Placeholder("图片待加载");
        view.ToolTip = view.Message?.ResourceName ?? "聊天图片";
        System.Windows.Automation.AutomationProperties.SetName(view, view.ToolTip.ToString());
        if (view.IsLoaded) view.Schedule();
    }

    private void Schedule()
    {
        if (scheduled?.Status == DispatcherOperationStatus.Pending) return;
        scheduled = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { scheduled = null; if (IsLoaded) _ = LoadAsync(); }));
    }

    internal Task LoadAsync()
    {
        Cancel();
        if (Message is not { } message || !ChatImageService.IsImageMessage(message) || ImageLoader is not { } loader)
            return LoadingTask = Task.CompletedTask;
        var cancellation = current = new CancellationTokenSource();
        var epoch = generation;
        image.Source = null; Placeholder("正在加载图片…");
        return LoadingTask = RunAsync();
        async Task RunAsync()
        {
            try
            {
                var loaded = await loader(message, cancellation.Token);
                if (generation != epoch || cancellation.IsCancellationRequested) return;
                image.Source = loaded; status.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (generation == epoch && !cancellation.IsCancellationRequested) Placeholder("图片暂时无法预览\n右键可另存为"); }
            // Keep the token for explicit copy/save actions until the row unloads or changes.
        }
    }

    internal Task CopyAsync() => ActAsync(CopyImage);
    internal Task SaveAsAsync() => ActAsync(SaveImageAs);
    private async Task ActAsync(Func<ChatMessage, CancellationToken, Task>? action)
    {
        if (Message is not { } message || action is null) return;
        current ??= new CancellationTokenSource();
        try { await action(message, current.Token); }
        catch (OperationCanceledException) { }
        catch (Exception) { ToolTip = "图片操作失败；可稍后重试或使用原下载入口。"; }
    }
    private void Placeholder(string message) { status.Text = message; status.Visibility = Visibility.Visible; }
    private void Cancel() { scheduled?.Abort(); scheduled = null; generation++; current?.Cancel(); current?.Dispose(); current = null; }
}
