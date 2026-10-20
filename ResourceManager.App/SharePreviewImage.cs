using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Image = System.Windows.Controls.Image;
using Color = System.Windows.Media.Color;

namespace ResourceManager.App;

/// <summary>Lazy application-station thumbnail with a readable placeholder and an isolated test seam.</summary>
public sealed class SharePreviewImage : Border
{
    public static readonly DependencyProperty PreviewUriProperty = DependencyProperty.Register(nameof(PreviewUri),
        typeof(Uri), typeof(SharePreviewImage), new PropertyMetadata(null, SourceChanged));
    public static readonly DependencyProperty AltTextProperty = DependencyProperty.Register(nameof(AltText),
        typeof(string), typeof(SharePreviewImage), new PropertyMetadata("", AltChanged));
    public static readonly DependencyProperty ImageLoaderProperty = DependencyProperty.Register(nameof(ImageLoader),
        typeof(Func<Uri, CancellationToken, Task<ImageSource>>), typeof(SharePreviewImage),
        new PropertyMetadata(null, SourceChanged));
    public static readonly DependencyProperty StretchProperty = DependencyProperty.Register(nameof(Stretch),
        typeof(Stretch), typeof(SharePreviewImage), new PropertyMetadata(Stretch.Uniform, StretchChanged));

    private readonly Image image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock placeholder = new()
    {
        Text = "暂无预览图", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(12), Foreground = new SolidColorBrush(Color.FromRgb(99, 115, 129)), FontSize = 12
    };
    private CancellationTokenSource? pending;
    private DispatcherOperation? scheduled;
    private long generation;

    public SharePreviewImage()
    {
        Background = new SolidColorBrush(Color.FromRgb(239, 244, 247));
        ClipToBounds = true;
        var content = new Grid();
        content.Children.Add(image);
        content.Children.Add(placeholder);
        Child = content;
        Loaded += (_, _) => ScheduleLoad();
        Unloaded += (_, _) => Cancel();
    }

    public Uri? PreviewUri { get => (Uri?)GetValue(PreviewUriProperty); set => SetValue(PreviewUriProperty, value); }
    public string AltText { get => (string)GetValue(AltTextProperty); set => SetValue(AltTextProperty, value); }
    public Func<Uri, CancellationToken, Task<ImageSource>>? ImageLoader
    {
        get => (Func<Uri, CancellationToken, Task<ImageSource>>?)GetValue(ImageLoaderProperty);
        set => SetValue(ImageLoaderProperty, value);
    }
    public Stretch Stretch { get => (Stretch)GetValue(StretchProperty); set => SetValue(StretchProperty, value); }
    internal Task LoadingTask { get; private set; } = Task.CompletedTask;
    internal bool HasImage => image.Source is not null;
    internal string StatusText => placeholder.Text;

    private static void SourceChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var control = (SharePreviewImage)owner;
        control.Cancel();
        control.image.Source = null;
        control.SetPlaceholder(control.PreviewUri is null ? "暂无预览图" : "预览图待加载");
        if (control.IsLoaded) control.ScheduleLoad();
    }

    private static void AltChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var control = (SharePreviewImage)owner;
        control.ToolTip = string.IsNullOrWhiteSpace(control.AltText) ? "应用预览图" : control.AltText;
        AutomationProperties.SetName(control, string.IsNullOrWhiteSpace(control.AltText) ? "应用预览图" : control.AltText);
    }

    private static void StretchChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args) =>
        ((SharePreviewImage)owner).image.Stretch = (Stretch)args.NewValue;

    private void ScheduleLoad()
    {
        if (scheduled?.Status == DispatcherOperationStatus.Pending) return;
        // All URI/loader bindings can settle before the first request begins.
        scheduled = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            scheduled = null;
            if (IsLoaded) _ = LoadAsync();
        }));
    }

    internal Task LoadAsync()
    {
        scheduled?.Abort();
        scheduled = null;
        CancelRequest();
        var uri = PreviewUri;
        if (uri is null)
        {
            image.Source = null;
            SetPlaceholder("暂无预览图");
            return LoadingTask = Task.CompletedTask;
        }
        var cancellation = new CancellationTokenSource();
        pending = cancellation;
        var requestGeneration = generation;
        var loader = ImageLoader ?? SharePreviewLoader.Shared.LoadAsync;
        SetPlaceholder("正在加载预览图…");
        image.Source = null;
        return LoadingTask = LoadCoreAsync(uri, loader, cancellation, requestGeneration);
    }

    private async Task LoadCoreAsync(Uri uri, Func<Uri, CancellationToken, Task<ImageSource>> loader,
        CancellationTokenSource cancellation, long requestGeneration)
    {
        try
        {
            var result = await loader(uri, cancellation.Token);
            if (requestGeneration != generation || cancellation.IsCancellationRequested) return;
            if (result is null) throw new InvalidOperationException("预览图加载器未返回图像。");
            image.Source = result;
            placeholder.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (requestGeneration == generation && !cancellation.IsCancellationRequested)
                SetPlaceholder("预览图暂时无法加载");
        }
        finally
        {
            if (ReferenceEquals(pending, cancellation)) pending = null;
            cancellation.Dispose();
        }
    }

    private void SetPlaceholder(string message)
    {
        placeholder.Text = message;
        placeholder.Visibility = Visibility.Visible;
    }

    private void CancelRequest()
    {
        generation++;
        pending?.Cancel();
        pending = null;
    }

    private void Cancel()
    {
        scheduled?.Abort();
        scheduled = null;
        CancelRequest();
    }
}
