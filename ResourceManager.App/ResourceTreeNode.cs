using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ResourceManager.App;

public sealed class ResourceTreeNode : INotifyPropertyChanged
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string GroupId { get; init; }
    public bool IsGroup { get; init; }
    public bool IsFolder { get; init; }
    public string? Path { get; init; }
    public string? OwnerResourceId { get; init; }
    public LocalResourceRow? LocalRow { get; init; }
    public ResourceRow? RemoteRow { get; init; }
    public bool ChildrenLoaded { get; set; }
    public ObservableCollection<ResourceTreeNode> Children { get; } = [];
    public string IconGeometry => IsGroup || IsFolder ? "M1,4 L6,4 8,6 17,6 17,16 1,16 Z" : "M4,1 L11,1 15,5 15,17 4,17 Z M11,1 L11,5 15,5";
    public string IconFill => IsGroup ? "#DCE9FF" : IsFolder ? "#FFE6A3" : "#EDF3FB";
    public string IconStroke => IsFolder && !IsGroup ? "#AE7918" : "#3972B9";
    public string RemoteDescription => RemoteRow is { } row ? $"{row.Kind} · {row.Size} · {row.Status}" + (row.Note.Length > 0 ? $"\n{row.Note}" : "") : "";
    private bool expanded, selected;
    public bool IsExpanded { get => expanded; set { expanded = value; Changed(); } }
    public bool IsSelected { get => selected; set { selected = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
