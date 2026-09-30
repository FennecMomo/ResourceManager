using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace ResourceManager.App;

public sealed record ReleaseHistoryChange(string Kind, string Text);
public sealed record ReleaseHistoryEntry(string Version, string Date, string Title, ReleaseHistoryChange[] Changes);

public partial class ReleaseHistoryView : UserControl
{
    private bool initialized;
    public IReadOnlyList<ReleaseHistoryEntry> Entries { get; }
    public ObservableCollection<ReleaseTimelineEntry> VisibleEntries { get; } = [];
    public string CurrentVersion { get; } = typeof(ReleaseHistoryView).Assembly.GetName().Version!.ToString(3);
    public string CurrentVersionLabel => $"当前 v{CurrentVersion}";

    public ReleaseHistoryView()
    {
        using var stream = typeof(ReleaseHistoryView).Assembly.GetManifestResourceStream("ResourceManager.ReleaseHistory.json")
            ?? throw new InvalidDataException("内置更新历程缺失。");
        Entries = (JsonSerializer.Deserialize<ReleaseHistoryEntry[]>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? []).OrderByDescending(e => Version.Parse(e.Version)).ToArray();
        InitializeComponent();
        DataContext = this;
        initialized = true;
        ApplySearch();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (initialized) ApplySearch();
    }

    private void ApplySearch()
    {
        var query = HistorySearch.Text.Trim();
        var versionQuery = query.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? query[1..] : query;
        var exactVersion = Version.TryParse(versionQuery, out var parsedVersion) && parsedVersion.Build >= 0;
        var matches = Entries.Where(entry => exactVersion ? Version.Parse(entry.Version) == parsedVersion :
            query.Length == 0 || entry.Version.Contains(versionQuery, StringComparison.OrdinalIgnoreCase) ||
            entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            entry.Changes.Any(change => change.Text.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        VisibleEntries.Clear();
        for (var i = 0; i < matches.Length; i++)
            VisibleEntries.Add(new ReleaseTimelineEntry(matches[i], matches[i].Version == CurrentVersion,
                i == 0, i == matches.Length - 1));
        HistoryCount.Text = query.Length == 0 ? $"{matches.Length} 个版本 · 从新到旧" : $"找到 {matches.Length} 个版本";
        HistoryEmpty.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (matches.Length > 0) HistoryList.ScrollIntoView(VisibleEntries[0]);
    }
}

public sealed class ReleaseTimelineEntry(ReleaseHistoryEntry entry, bool current, bool first, bool last)
{
    public ReleaseHistoryEntry Entry => entry;
    public string VersionLabel => $"v{entry.Version}";
    public string DateLabel => DateOnly.ParseExact(entry.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
    public Visibility CurrentVisibility => current ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TopLineVisibility => first ? Visibility.Collapsed : Visibility.Visible;
    public Visibility BottomLineVisibility => last ? Visibility.Collapsed : Visibility.Visible;
    public string DotColor => current ? "#2867D9" : "#9EB5D2";
    public string CardBorder => current ? "#B9CFF7" : "#E4EAF2";
    public IReadOnlyList<ReleaseTimelineChange> Changes { get; } = entry.Changes.Select(c => new ReleaseTimelineChange(c)).ToArray();
}

public sealed class ReleaseTimelineChange(ReleaseHistoryChange change)
{
    public string Kind => change.Kind;
    public string Text => change.Text;
    public string Foreground => change.Kind switch { "新增" => "#238264", "改进" => "#2867D9", _ => "#B67442" };
    public string Background => change.Kind switch { "新增" => "#EAF6EF", "改进" => "#EAF2FF", _ => "#FFF5E7" };
}
