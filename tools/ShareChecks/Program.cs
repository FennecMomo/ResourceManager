using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ResourceManager.App;
using ResourceManager.Core;

internal static class Program
{
    private static int checks;

    [STAThread]
    private static int Main(string[] args)
    {
        NodeDefaults.UseDataDirectory(Path.GetFullPath(Path.Combine("dist", "share-checks", "data-" + Guid.NewGuid().ToString("N"))));
        var store = new NodeStore();
        var settings = store.GetSettings();
        store.SaveSettings("应用站隔离检查", null, settings.ListenPort, false, false);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var result = 0;
        app.Dispatcher.BeginInvoke(async () =>
        {
            MainWindow? main = null;
            var previewRequests = new List<Uri>();
            Func<Uri, CancellationToken, Task<ImageSource>> loadPreview = (uri, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                previewRequests.Add(uri);
                return uri.AbsolutePath.Contains("broken", StringComparison.Ordinal)
                    ? Task.FromException<ImageSource>(new InvalidDataException("isolated invalid preview fixture"))
                    : Task.FromResult(PreviewFixture());
            };
            try
            {
                await PreviewLoaderChecks.RunAsync(Require);
                await CheckPreviewLifecycleAsync();
                var services = new SettingsEditorServices(() => false,
                    _ => throw new InvalidOperationException("unexpected registry write"),
                    (_, _) => throw new InvalidOperationException("unexpected network restart"),
                    _ => SettingsLeaveChoice.KeepEditing,
                    (message, error) => throw new Exception(message, error));
                main = new MainWindow(false, services, false, () => Task.CompletedTask);
                var root = (FrameworkElement)main.Content;
                var navigation = (ListBox)main.FindName("NavList");
                var tabs = (TabControl)main.FindName("Tabs");
                var expected = new[] { "设备", "我的发布", "Git 协作", "聊天", "收藏", "下载", "反馈", "设置", "服务器", "更新历程", "应用站" };
                Require(tabs.Items.Cast<TabItem>().Select(tab => tab.Header?.ToString()).SequenceEqual(expected),
                    "new share tab is appended at index 10 and all previous tab indexes remain unchanged");
                Require(navigation.Items.Count == 11 && Descendants<TextBlock>((DependencyObject)navigation.Items[10]).Any(text => text.Text == "应用站"),
                    "sidebar includes the matching share entry at index 10");
                ((ShareProjectsView)main.FindName("ShareProjects")).Dispose();

                var fixture = Catalog();
                var opened = new List<Uri>();
                var calls = 0;
                Exception? failure = null;
                using var view = new ShareProjectsView(_ =>
                {
                    calls++;
                    return failure is not null ? Task.FromException<ShareCatalog>(failure) : Task.FromResult(fixture);
                }, opened.Add, loadPreview);
                ((TabItem)tabs.Items[10]).Content = view;
                Layout(root, new Size(1010, 670));
                Require(calls == 0 && view.VisibleProjects.Count == 0, "constructing and laying out another tab does not request the catalog");
                navigation.SelectedIndex = 10;
                Layout(root, new Size(1010, 670));
                Require(tabs.SelectedIndex == 10 && ((TextBlock)main.FindName("PageTitleText")).Text == "应用站",
                    "sidebar selection opens the share page and updates its header");
                await view.EnsureLoadedAsync();
                await FlushLayoutAsync(root);
                Require(calls == 1 && view.VisibleProjects.Count == 3, "first entry loads the catalog and hides demo and explicitly tagged test projects by default");
                await view.EnsureLoadedAsync();
                Require(calls == 1, "revisiting the share page uses its loaded catalog");

                var search = Find<TextBox>(view, "ProjectSearch");
                var filter = Find<ComboBox>(view, "ProjectFilter");
                var tagFilter = Find<ComboBox>(view, "TagFilter");
                var showTests = Find<CheckBox>(view, "ShowTestProjects");
                var list = Find<ListBox>(view, "ProjectList");
                var empty = Find<TextBlock>(view, "ProjectEmpty");
                var detail = Find<StackPanel>(view, "ProjectDetail");
                var detailScroll = Find<ScrollViewer>(view, "DetailScroll");
                Require(list.SelectedIndex == 0 && detail.DataContext is ShareProjectRow selected && selected.Project.Slug == "work-orders",
                    "a loaded catalog selects its first project and binds its details");
                Require(showTests.IsChecked == false && tagFilter.SelectedItem as string == "全部标签" &&
                        !view.VisibleProjects.Any(row => row.Project.IsTestProject),
                    "test visibility and tag selection start with production projects and all tags");
                Require(view.AvailableTags[0] == "全部标签" && new[] { "效率", "3D", "localStorage", "素材", "测试项目", "演示", "测试项目说明" }
                        .All(view.AvailableTags.Contains) && view.AvailableTags.Distinct(StringComparer.OrdinalIgnoreCase).Count() == view.AvailableTags.Count,
                    "tag choices combine catalog and project tags, including hidden tests, without duplicates");
                Require(view.VisibleProjects.Any(row => row.Project.Slug == "whale-tools"),
                    "a longer tag containing the test phrase does not hide a normal project");
                showTests.IsChecked = true;
                Require(view.VisibleProjects.Count == 5 && view.VisibleProjects.Count(row => row.Project.IsTestProject) == 2,
                    "show-test switch reveals both demo projects and non-demo projects with the exact test tag");
                tagFilter.SelectedItem = "效率";
                Require(view.VisibleProjects.Select(row => row.Project.Slug).SequenceEqual(new[] { "work-orders", "whale-tools", "tagged-test" }),
                    "an ordinary tag filters the catalog while preserving the test-visibility choice");
                showTests.IsChecked = false;
                Require(view.VisibleProjects.Select(row => row.Project.Slug).SequenceEqual(new[] { "work-orders", "whale-tools" }) &&
                        tagFilter.SelectedItem as string == "效率", "hiding tests preserves an ordinary tag filter");
                search.Text = "鲸鱼";
                Require(view.VisibleProjects.Count == 1 && view.VisibleProjects[0].Project.Slug == "whale-tools",
                    "tag, search and test visibility filters combine");
                filter.SelectedIndex = 1;
                Require(view.VisibleProjects.Count == 0 && empty.Visibility == Visibility.Visible && detailScroll.Visibility == Visibility.Collapsed,
                    "availability combines with tags and search and clears details when the intersection is empty");
                filter.SelectedIndex = 0;
                search.Text = "";
                tagFilter.SelectedItem = "测试项目";
                Require(showTests.IsChecked == true && view.VisibleProjects.Count == 1 && view.VisibleProjects[0].Project.Slug == "tagged-test",
                    "choosing the exact test tag explicitly enables test projects");
                showTests.IsChecked = false;
                Require(tagFilter.SelectedItem as string == "全部标签" && view.VisibleProjects.Count == 3,
                    "turning tests off clears a conflicting test-tag selection and restores normal projects");

                list.SelectedItem = view.VisibleProjects.Single(row => row.Project.Slug == "whale-tools");
                tagFilter.SelectedItem = "效率";
                Require((list.SelectedItem as ShareProjectRow)?.Project.Slug == "whale-tools", "filtering preserves an eligible selected project");
                await view.RefreshAsync();
                Require(tagFilter.SelectedItem as string == "效率" && (list.SelectedItem as ShareProjectRow)?.Project.Slug == "whale-tools",
                    "refreshing preserves a still-valid tag and selected project");
                tagFilter.SelectedItem = "素材";
                var originalFixture = fixture;
                fixture = fixture with { Projects = fixture.Projects.Select(project => project with { Tags = project.Tags.Where(tag => tag != "素材").ToArray() }).ToArray() };
                await view.RefreshAsync();
                Require(tagFilter.SelectedItem as string == "全部标签" && !view.AvailableTags.Contains("素材") && view.VisibleProjects.Count == 3,
                    "refresh falls back to all tags when the selected tag disappears");
                fixture = originalFixture;
                await view.RefreshAsync();
                list.SelectedIndex = 0;
                foreach (var (query, slug) in new[] { ("鲸鱼", "whale-tools"), ("驾驶", "space-flight"), ("LOCALSTORAGE", "work-orders"), ("桌面工具", "whale-tools") })
                {
                    search.Text = query;
                    Require(view.VisibleProjects.Count == 1 && view.VisibleProjects[0].Project.Slug == slug,
                        $"search matches title, summary, tags and categories: {query}");
                }
                search.Text = "no-project-matches-this-fixture";
                Require(view.VisibleProjects.Count == 0 && empty.Visibility == Visibility.Visible && detailScroll.Visibility == Visibility.Collapsed,
                    "no search results show a readable empty state and clear stale details");
                search.Text = "";
                filter.SelectedIndex = 1;
                Require(view.VisibleProjects.Select(row => row.Project.Slug).SequenceEqual(new[] { "work-orders", "space-flight" }),
                    "online filter only includes projects with a web experience");
                filter.SelectedIndex = 2;
                Require(view.VisibleProjects.Select(row => row.Project.Slug).SequenceEqual(new[] { "work-orders", "whale-tools" }),
                    "download filter only includes projects with downloadable files");
                search.Text = "鲸鱼";
                Require(view.VisibleProjects.Count == 1 && view.VisibleProjects[0].Project.Slug == "whale-tools", "search combines with the selected availability filter");
                search.Text = "";
                filter.SelectedIndex = 0;
                list.SelectedIndex = 1;
                Require(detail.DataContext is ShareProjectRow second && second.Project.Slug == "space-flight" && second.DownloadsVisibility == Visibility.Collapsed,
                    "selecting another list item updates project details and hides absent downloads");
                list.SelectedIndex = 0;
                await FlushLayoutAsync(root);
                var previews = Descendants<SharePreviewImage>(view).ToArray();
                await Task.WhenAll(previews.Select(preview => preview.LoadAsync()));
                Require(previews.Count(preview => preview.PreviewUri == fixture.Projects[0].CoverUrl) >= 2 &&
                        previews.Any(preview => preview.PreviewUri == fixture.Projects[0].CoverUrl && preview.HasImage),
                    "project cards and selected details bind their cover and load it through the isolated image loader");
                Require(fixture.Projects[0].Screenshots.All(screenshot => previews.Any(preview => preview.PreviewUri == screenshot.Url && preview.AltText == screenshot.Alt)),
                    "project detail screenshot controls bind each catalog image and its alternative text");
                Require(previews.Any(preview => preview.PreviewUri?.AbsolutePath.Contains("broken", StringComparison.Ordinal) == true &&
                        !preview.HasImage && preview.StatusText.Contains("无法加载")),
                    "a broken preview shows a readable placeholder");
                Click(view, "打开应用站");
                Click(view, "项目网页 ↗");
                Click(view, "在线体验 ↗");
                Click(view, "下载 ↗");
                Require(opened.SequenceEqual(new[] { ShareCatalogClient.SiteUrl, fixture.Projects[0].DetailsUrl, fixture.Projects[0].WebUrl!, fixture.Projects[0].Downloads[0].DownloadUrl }),
                    "share, detail, online and download buttons retain exact targets even when a preview fails");
                Require(previewRequests.Count > 0 && previewRequests.All(uri => uri.Host == ShareCatalogClient.SiteUrl.Host),
                    "all fixture previews use the isolated loader without contacting an image server");

                foreach (var size in new[] { new Size(1010, 670), new Size(1260, 820) })
                {
                    Layout(root, size);
                    await FlushLayoutAsync(root);
                    navigation.ScrollIntoView(navigation.Items[10]);
                    // With no presentation source, BringIntoView can be deferred indefinitely.
                    // Move only this hidden list's own scroll viewer to expose the appended item.
                    Descendants<ScrollViewer>(navigation).Single().ScrollToBottom();
                    await FlushLayoutAsync(root);
                    Require(Inside((FrameworkElement)navigation.Items[10], navigation),
                        $"appended share sidebar entry remains reachable by scrolling at {size.Width} x {size.Height}");
                    Require(view.ActualWidth > 700 && view.ActualHeight > 400 && list.ActualWidth > 280 && list.ActualHeight > 250,
                        $"project list and detail columns retain usable dimensions at {size.Width} x {size.Height}");
                    Require(search.ActualWidth >= 150 && filter.ActualWidth >= 130 && tagFilter.ActualWidth >= 130 && Find<Button>(view, "RefreshButton").ActualHeight >= 36,
                        $"search, availability, tags and refresh controls retain readable dimensions at {size.Width} x {size.Height}");
                    Require(Inside(tagFilter, view) && Inside(showTests, view),
                        $"tag and test-visibility controls stay within the page at {size.Width} x {size.Height}");
                    foreach (var button in Descendants<Button>(view).Where(button => button.Content?.ToString() is "打开应用站" or "刷新"))
                        Require(Inside(button, view), $"toolbar action {button.Content} stays within page bounds at {size.Width} x {size.Height}");
                    Require(detailScroll.ViewportHeight > 250 && detailScroll.ExtentHeight > detailScroll.ViewportHeight && detailScroll.ScrollableWidth <= 1,
                        $"long project details scroll vertically without horizontal clipping at {size.Width} x {size.Height}");
                    if (args.Contains("--render-preview")) SavePreview(root, size, $"share-fixture-{(int)size.Width}.png");
                    detailScroll.ScrollToBottom();
                    await FlushLayoutAsync(root);
                    var download = Descendants<Button>(view).Single(button => button.Content?.ToString() == "下载 ↗");
                    Require(Inside(download, detailScroll), $"download action remains reachable at the bottom of long details at {size.Width} x {size.Height}");
                    detailScroll.ScrollToTop();
                    Require(!main.IsVisible, "layout checks never show a client window");
                }

                var callsBeforeFailures = calls;
                failure = new HttpRequestException("isolated offline fixture");
                await view.RefreshAsync();
                Require(calls == callsBeforeFailures + 1 && view.VisibleProjects.Count == 3 && Find<TextBlock>(view, "CatalogError").Visibility == Visibility.Visible &&
                        Find<TextBlock>(view, "CatalogError").Text.Contains("上次加载") && Find<Button>(view, "RefreshButton").IsEnabled,
                    "failed refresh retains previously loaded projects and offers a retry");
                failure = new InvalidDataException("isolated malformed catalog fixture");
                await view.RefreshAsync();
                Require(calls == callsBeforeFailures + 2 && view.VisibleProjects.Count == 3 && Find<TextBlock>(view, "CatalogError").Visibility == Visibility.Visible &&
                        Find<Button>(view, "RefreshButton").IsEnabled,
                    "invalid catalog data is handled without discarding the previously loaded project list");
                failure = null;
                await view.RefreshAsync();
                Require(calls == callsBeforeFailures + 3 && Find<TextBlock>(view, "CatalogError").Visibility == Visibility.Collapsed && view.VisibleProjects.Count == 3,
                    "retry recovers the catalog and clears the failure message");

                using var initiallyOffline = new ShareProjectsView(_ => Task.FromException<ShareCatalog>(new HttpRequestException("isolated initial failure")), opened.Add, loadPreview);
                await initiallyOffline.EnsureLoadedAsync();
                Require(initiallyOffline.VisibleProjects.Count == 0 && Find<TextBlock>(initiallyOffline, "CatalogError").Visibility == Visibility.Visible &&
                        Find<TextBlock>(initiallyOffline, "ProjectEmpty").Text == "项目列表尚未加载",
                    "initial network failure is distinct from an empty published catalog");
                var invalidInitial = true;
                using var initiallyInvalid = new ShareProjectsView(_ => invalidInitial
                    ? Task.FromException<ShareCatalog>(new InvalidDataException("isolated initial malformed catalog"))
                    : Task.FromResult(fixture), opened.Add, loadPreview);
                await initiallyInvalid.EnsureLoadedAsync();
                Require(initiallyInvalid.VisibleProjects.Count == 0 && Find<TextBlock>(initiallyInvalid, "CatalogError").Visibility == Visibility.Visible &&
                        Find<Button>(initiallyInvalid, "RefreshButton").IsEnabled,
                    "initial invalid catalog data shows an error and leaves retry available");
                invalidInitial = false;
                await initiallyInvalid.EnsureLoadedAsync();
                Require(initiallyInvalid.VisibleProjects.Count == 3 && Find<TextBlock>(initiallyInvalid, "CatalogError").Visibility == Visibility.Collapsed,
                    "retry after an initially invalid catalog loads projects successfully");
                using var emptyView = new ShareProjectsView(_ => Task.FromResult(fixture with { Projects = [] }), opened.Add, loadPreview);
                await emptyView.EnsureLoadedAsync();
                Require(emptyView.VisibleProjects.Count == 0 && Find<TextBlock>(emptyView, "ProjectEmpty").Text == "暂无已发布项目" &&
                        Find<TextBlock>(emptyView, "ProjectEmpty").Visibility == Visibility.Visible && Find<ScrollViewer>(emptyView, "DetailScroll").Visibility == Visibility.Collapsed,
                    "a valid empty catalog shows its own empty state without stale details");
                using var testsOnlyView = new ShareProjectsView(_ => Task.FromResult(fixture with
                {
                    Projects = fixture.Projects.Where(project => project.IsTestProject).ToArray()
                }), opened.Add, loadPreview);
                await testsOnlyView.EnsureLoadedAsync();
                Require(testsOnlyView.VisibleProjects.Count == 0 && Find<TextBlock>(testsOnlyView, "ProjectEmpty").Visibility == Visibility.Visible &&
                        Find<TextBlock>(testsOnlyView, "ProjectEmpty").Text.Contains("测试项目"),
                    "a catalog containing only hidden tests explains how to reveal its projects");
                Find<CheckBox>(testsOnlyView, "ShowTestProjects").IsChecked = true;
                Require(testsOnlyView.VisibleProjects.Count == 2, "the test switch reveals an otherwise empty test-only catalog");

                var pending = new TaskCompletionSource<ShareCatalog>();
                var pendingCalls = 0;
                using var pendingView = new ShareProjectsView(_ => { pendingCalls++; return pending.Task; }, opened.Add, loadPreview);
                var loading = pendingView.EnsureLoadedAsync();
                await pendingView.RefreshAsync();
                Require(pendingCalls == 1 && !Find<Button>(pendingView, "RefreshButton").IsEnabled, "an in-flight request disables refresh and ignores duplicate loads");
                pending.SetResult(fixture);
                await loading;
                Require(pendingView.VisibleProjects.Count == 3 && Find<Button>(pendingView, "RefreshButton").IsEnabled, "completing a request restores its refresh action");
                Require(opened.Count == 4 && !main.IsVisible, "isolated checks never launch a browser or display a window");
                if (args.Contains("--live") || args.Contains("--live-images"))
                {
                    using var liveClient = new ShareCatalogClient();
                    var liveCatalog = await liveClient.GetProjectsAsync();
                    Require(liveCatalog.SchemaVersion == 1, "live public catalog uses the supported schema");
                    Console.WriteLine($"Live public catalog: revision {liveCatalog.Revision}, {liveCatalog.Projects.Count} published projects");
                    foreach (var project in liveCatalog.Projects) Console.WriteLine($"  {project.Title} ({project.Slug})");
                    var expectedVisible = liveCatalog.Projects.Count(project => !project.IsTestProject);
                    Console.WriteLine($"Default station filter: {expectedVisible} visible, {liveCatalog.Projects.Count - expectedVisible} test projects hidden");
                    Func<Uri, CancellationToken, Task<ImageSource>> livePreviewLoader = loadPreview;
                    if (args.Contains("--live-images")) livePreviewLoader = async (uri, token) =>
                    {
                        try { return await SharePreviewLoader.Shared.LoadAsync(uri, token); }
                        catch (Exception error)
                        {
                            Console.WriteLine($"Live preview failed: {uri.AbsolutePath}: {error.GetType().Name}: {error.Message}");
                            throw;
                        }
                    };
                    using var liveView = new ShareProjectsView(_ => Task.FromResult(liveCatalog), opened.Add, livePreviewLoader);
                    ((TabItem)tabs.Items[10]).Content = liveView;
                    await liveView.EnsureLoadedAsync();
                    var liveList = Find<ListBox>(liveView, "ProjectList");
                    foreach (var size in new[] { new Size(1010, 670), new Size(1260, 820) })
                    {
                        Layout(root, size);
                        await FlushLayoutAsync(root);
                        var previewProject = liveView.VisibleProjects.FirstOrDefault(row => row.Project.Screenshots.Count > 0)
                            ?? liveView.VisibleProjects.FirstOrDefault();
                        liveList.SelectedItem = previewProject;
                        Layout(root, size);
                        await FlushLayoutAsync(root);
                        Require(ReferenceEquals(Find<StackPanel>(liveView, "ProjectDetail").DataContext, previewProject),
                            "live preview validation selects the intended project after its item bindings settle");
                        Require(liveView.VisibleProjects.Count == expectedVisible && !main.IsVisible,
                            $"live public catalog binds with tests hidden in the background WPF view at {size.Width} x {size.Height}");
                        var livePreviews = Descendants<SharePreviewImage>(liveView).ToArray();
                        Require(Descendants<SharePreviewImage>(Find<ItemsControl>(liveView, "ScreenshotList")).Count() ==
                            (previewProject?.Project.Screenshots.Count ?? 0), "live preview validation includes every selected screenshot");
                        await Task.WhenAll(livePreviews.Select(preview => preview.LoadAsync()));
                        if (args.Contains("--live-images"))
                            Require(livePreviews.Where(preview => preview.PreviewUri is not null).All(preview => preview.HasImage),
                                $"live covers and screenshots decode in the hidden station at {size.Width} x {size.Height}");
                        if (args.Contains("--render-preview")) SavePreview(root, size, $"share-live-{(int)size.Width}.png");
                    }
                }
                Console.WriteLine($"Share checks passed: {checks}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                result = 1;
            }
            finally
            {
                main?.Close();
                app.Shutdown();
            }
        });
        app.Run();
        return result;
    }

    private static ShareCatalog Catalog()
    {
        var now = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.FromHours(8));
        var description = string.Join("\n\n", Enumerable.Repeat("在这里记录设备、维修工单、处理进度与负责人。长段落用于验证真实详情容器能滚动到下载入口，窄窗口下文本正常换行。", 24));
        ShareDownload Download(string slug, string fileName) => new(fileName, "Windows", "1.0.0", 2_097_152, null, true, new Uri(ShareCatalogClient.SiteUrl, $"downloads/{slug}/{fileName}"));
        ShareProject Project(string slug, string title, string summary, string category, string[] tags, bool web, ShareDownload[] downloads) =>
            new(slug, slug, title, summary, description, [category], tags, "1.0.0", now, now, false,
                new Uri(ShareCatalogClient.SiteUrl, "?p=" + slug), null, web ? new Uri(ShareCatalogClient.SiteUrl, "play/" + slug + "/") : null, downloads);
        return new ShareCatalog(1, 12, now, now, ["效率", "3D"],
        [
            Project("work-orders", "维修工单管理台", "离线设备维修记录", "Web 工具", ["localStorage", "效率"], true, [Download("work-orders", "index.html")]) with
            {
                CoverUrl = new Uri(ShareCatalogClient.SiteUrl, "media/work-orders-cover.png"),
                CoverAlt = "维修工单仪表板预览",
                Screenshots =
                [
                    new SharePreview(new Uri(ShareCatalogClient.SiteUrl, "media/work-orders-list.png"), "工单列表界面"),
                    new SharePreview(new Uri(ShareCatalogClient.SiteUrl, "media/broken-fixture.png"), "暂时不可用的工单详情预览")
                ]
            },
            Project("space-flight", "星际漫游", "在浏览器驾驶飞船", "在线游戏", ["3D", "游戏"], true, []),
            Project("whale-tools", "鲸鱼工具箱", "管理本地素材与快捷任务", "桌面工具", ["素材", "效率", "测试项目说明"], false, [Download("whale-tools", "whale-tools.zip")]),
            Project("demo-project", "浏览器演示项目", "仅演示用的试验页面", "Web 工具", ["演示"], true, []) with { Demo = true },
            Project("tagged-test", "非 Demo 测试应用", "通过精确测试标签隐藏", "桌面工具", ["效率", "测试项目"], false, [Download("tagged-test", "test.zip")])
        ]);
    }

    private static ImageSource PreviewFixture()
    {
        const int width = 320, height = 180;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            var card = x > 48 && x < 272 && y > 36 && y < 144;
            var line = card && y % 25 < 7 && x > 66 && x < 242;
            pixels[offset] = (byte)(line ? 196 : card ? 243 : 160 + x / 5);
            pixels[offset + 1] = (byte)(line ? 156 : card ? 239 : 95 + y / 3);
            pixels[offset + 2] = (byte)(line ? 80 : card ? 231 : 35 + x / 6);
            pixels[offset + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static async Task CheckPreviewLifecycleAsync()
    {
        var calls = 0;
        var control = new SharePreviewImage
        {
            ImageLoader = (_, _) => { calls++; return Task.FromResult(PreviewFixture()); }
        };
        await control.LoadAsync();
        Require(calls == 0 && !control.HasImage && control.StatusText == "暂无预览图",
            "an application without a preview keeps a placeholder and starts no image request");
        control.PreviewUri = new Uri(ShareCatalogClient.SiteUrl, "preview/lazy.png");
        await Dispatcher.Yield(DispatcherPriority.Background);
        Require(calls == 0 && !control.HasImage, "binding a preview in an unshown view does not initiate an image download");

        var oldResult = new TaskCompletionSource<ImageSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken oldToken = default;
        var currentImage = PreviewFixture();
        control.ImageLoader = (uri, token) =>
        {
            if (uri.AbsolutePath.EndsWith("old.png", StringComparison.Ordinal))
            {
                oldToken = token;
                return oldResult.Task;
            }
            return Task.FromResult(currentImage);
        };
        control.PreviewUri = new Uri(ShareCatalogClient.SiteUrl, "preview/old.png");
        var oldLoad = control.LoadAsync();
        control.PreviewUri = new Uri(ShareCatalogClient.SiteUrl, "preview/current.png");
        await control.LoadAsync();
        Require(oldToken.IsCancellationRequested && control.HasImage && ReferenceEquals(Descendants<Image>(control).Single().Source, currentImage),
            "changing an image source cancels the previous request and binds the new image");
        oldResult.SetResult(PreviewFixture());
        await oldLoad;
        Require(ReferenceEquals(Descendants<Image>(control).Single().Source, currentImage),
            "a late result from an obsolete image request cannot overwrite the current preview");

        CancellationToken unloadToken = default;
        control.ImageLoader = async (_, token) =>
        {
            unloadToken = token;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return PreviewFixture();
        };
        var unloadLoad = control.LoadAsync();
        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        await unloadLoad;
        Require(unloadToken.IsCancellationRequested && !control.HasImage,
            "unloading a preview cancels its pending request without applying an image");
    }

    private static T Find<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        (T)(root.FindName(name) ?? throw new InvalidOperationException($"Missing control {name}"));

    private static void Click(FrameworkElement root, string content) =>
        Descendants<Button>(root).Single(button => button.Content?.ToString() == content).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var pending = new Stack<DependencyObject>();
        var seen = new HashSet<DependencyObject>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current)) continue;
            if (current is T matching) yield return matching;
            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>()) pending.Push(child);
            // Data templates are only materialized in the visual tree.
            if (current is Visual)
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++) pending.Push(VisualTreeHelper.GetChild(current, i));
        }
    }

    private static bool Inside(FrameworkElement child, FrameworkElement container)
    {
        var topLeft = child.TranslatePoint(new Point(), container);
        return child.ActualWidth > 0 && child.ActualHeight > 0 && topLeft.X >= -1 && topLeft.Y >= -1 &&
               topLeft.X + child.ActualWidth <= container.ActualWidth + 1 && topLeft.Y + child.ActualHeight <= container.ActualHeight + 1;
    }

    private static void Layout(FrameworkElement root, Size size)
    {
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
    }

    private static async Task FlushLayoutAsync(FrameworkElement root)
    {
        await Dispatcher.Yield(DispatcherPriority.Background);
        root.UpdateLayout();
    }

    private static void SavePreview(FrameworkElement root, Size size, string name)
    {
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(246, 248, 251)), null, new Rect(size));
        bitmap.Render(background);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.GetFullPath(Path.Combine("dist", "share-checks", name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var output = File.Create(path);
        encoder.Save(output);
        Console.WriteLine($"Offscreen share preview: {path}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
        Console.WriteLine($"PASS {checks}: {message}");
    }
}
