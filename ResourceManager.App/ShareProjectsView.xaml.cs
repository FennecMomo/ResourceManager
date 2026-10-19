using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ResourceManager.Core;
using UserControl = System.Windows.Controls.UserControl;

namespace ResourceManager.App;

public partial class ShareProjectsView : UserControl, IDisposable
{
    private readonly ShareCatalogClient? client;
    private readonly Func<CancellationToken, Task<ShareCatalog>> loadCatalog;
    private readonly Action<Uri> openLink;
    private IReadOnlyList<ShareProject> projects = [];
    private CancellationTokenSource? loadingCancellation;
    private bool initialized, loading, loaded, disposed;
    private DateTimeOffset? lastLoadedAt;

    public ObservableCollection<ShareProjectRow> VisibleProjects { get; } = [];

    public ShareProjectsView()
    {
        client = new ShareCatalogClient();
        loadCatalog = client.GetProjectsAsync;
        openLink = uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        InitializeView();
    }

    internal ShareProjectsView(Func<CancellationToken, Task<ShareCatalog>> loadCatalog, Action<Uri> openLink)
    {
        this.loadCatalog = loadCatalog;
        this.openLink = openLink;
        InitializeView();
    }

    private void InitializeView()
    {
        InitializeComponent();
        DataContext = this;
        initialized = true;
    }

    private async void View_Loaded(object sender, RoutedEventArgs e) => await EnsureLoadedAsync();
    internal Task EnsureLoadedAsync() => loaded || disposed ? Task.CompletedTask : RefreshAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    internal async Task RefreshAsync()
    {
        if (loading || disposed) return;
        loading = true;
        using var cancellation = new CancellationTokenSource();
        loadingCancellation = cancellation;
        RefreshButton.IsEnabled = false;
        CatalogError.Visibility = Visibility.Collapsed;
        CatalogStatus.Text = "正在加载分享站项目…";
        try
        {
            var catalog = await loadCatalog(cancellation.Token);
            if (disposed) return;
            projects = catalog.Projects;
            loaded = true;
            lastLoadedAt = DateTimeOffset.Now;
            ApplyFilter();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
        {
            if (disposed) return;
            CatalogError.Text = loaded
                ? "刷新失败，仍显示上次加载的项目。请检查网络后点击“刷新”重试。"
                : "无法加载分享站项目，请检查网络后点击“刷新”重试，也可以打开分享站。";
            CatalogError.Visibility = Visibility.Visible;
            if (!loaded)
            {
                ProjectEmpty.Text = "项目列表尚未加载";
                ProjectEmpty.Visibility = Visibility.Visible;
            }
            UpdateCount();
        }
        finally
        {
            loading = false;
            loadingCancellation = null;
            if (!disposed) RefreshButton.IsEnabled = true;
        }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) { if (initialized) ApplyFilter(); }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (initialized) ApplyFilter(); }

    private void ApplyFilter()
    {
        var selectedSlug = (ProjectList.SelectedItem as ShareProjectRow)?.Project.Slug;
        var query = ProjectSearch.Text.Trim();
        var filter = ProjectFilter.SelectedIndex;
        var matches = projects.Where(project =>
            (filter != 1 || project.WebUrl is not null) &&
            (filter != 2 || project.Downloads.Count > 0) &&
            (query.Length == 0 || project.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             project.Summary.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             project.Tags.Concat(project.Categories).Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase))));
        VisibleProjects.Clear();
        foreach (var project in matches) VisibleProjects.Add(new ShareProjectRow(project));
        ProjectList.SelectedItem = VisibleProjects.FirstOrDefault(row => row.Project.Slug == selectedSlug) ?? VisibleProjects.FirstOrDefault();
        ProjectEmpty.Text = !loaded ? "项目列表尚未加载" : projects.Count == 0 ? "暂无已发布项目" : "没有匹配的项目，试试其他关键词或类型";
        ProjectEmpty.Visibility = VisibleProjects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCount();
    }

    private void UpdateCount() => CatalogStatus.Text = lastLoadedAt is { } time
        ? $"{VisibleProjects.Count} / {projects.Count} 个项目 · 来自 share.fennecmomo.top · 更新于 {time:HH:mm:ss}"
        : "项目来自 share.fennecmomo.top";

    private void Project_Changed(object sender, SelectionChangedEventArgs e)
    {
        var row = ProjectList.SelectedItem as ShareProjectRow;
        ProjectDetail.DataContext = row;
        DetailPlaceholder.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        DetailScroll.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        DetailScroll.ScrollToTop();
    }

    private void OpenSite_Click(object sender, RoutedEventArgs e) => Open(ShareCatalogClient.SiteUrl);
    private void OpenProject_Click(object sender, RoutedEventArgs e) => Open((ProjectList.SelectedItem as ShareProjectRow)?.Project.DetailsUrl);
    private void OpenWeb_Click(object sender, RoutedEventArgs e) => Open((ProjectList.SelectedItem as ShareProjectRow)?.Project.WebUrl);
    private void Download_Click(object sender, RoutedEventArgs e) => Open((sender as FrameworkElement)?.Tag as Uri);

    private void Open(Uri? uri)
    {
        if (uri is null) return;
        try { openLink(uri); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            CatalogError.Text = "无法打开默认浏览器，请稍后重试。";
            CatalogError.Visibility = Visibility.Visible;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        loadingCancellation?.Cancel();
        client?.Dispose();
    }
}

public sealed class ShareProjectRow(ShareProject project)
{
    public ShareProject Project => project;
    public string TagsLabel => string.Join(" · ", project.Categories.Concat(project.Tags).Distinct());
    public string AvailabilityLabel => string.Join(" · ", new[]
    {
        project.WebUrl is null ? null : "在线体验",
        project.Downloads.Count == 0 ? null : $"{project.Downloads.Count} 个下载",
        project.Demo ? "演示项目" : null
    }.OfType<string>());
    public string VersionLabel => string.Join(" · ", new[]
    {
        string.IsNullOrWhiteSpace(project.DisplayVersion) ? null : $"版本 {project.DisplayVersion}",
        project.UpdatedAt is { } date ? $"更新于 {date.ToLocalTime():yyyy-MM-dd}" : null
    }.OfType<string>());
    public string Description => string.IsNullOrWhiteSpace(project.Description) ? project.Summary : project.Description;
    public Visibility WebVisibility => project.WebUrl is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DownloadsVisibility => project.Downloads.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public IReadOnlyList<ShareDownloadRow> Downloads { get; } = project.Downloads.Select(download => new ShareDownloadRow(download)).ToArray();
}

public sealed class ShareDownloadRow(ShareDownload download)
{
    public ShareDownload Download => download;
    public string Metadata => string.Join(" · ", new[]
    {
        download.Platform,
        string.IsNullOrWhiteSpace(download.Version) ? null : $"v{download.Version}",
        download.Size < 1024 * 1024 ? $"{download.Size / 1024d:0.#} KiB" : $"{download.Size / (1024d * 1024):0.#} MiB",
        download.IsDefault ? "推荐" : null
    }.Where(value => !string.IsNullOrWhiteSpace(value)));
}
