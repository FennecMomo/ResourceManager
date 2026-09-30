using ResourceManager.Core;

namespace ResourceManager.Tests;

public sealed class CatalogConcurrencyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ResourceManagerCatalogSafety", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowInspectionDoesNotBlockIdentityAndRechecksRevocation(bool removePublication)
    {
        var store = new NodeStore(root);
        var path = Path.Combine(root, "file.txt");
        File.WriteAllText(path, "content");
        var group = store.SaveResourceGroup("shared");
        var resource = store.AddResource(path, PublishMode.Reference, groupId: group.Id);
        var identity = store.GetSettings().Profile.DeviceId;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var scan = Task.Run(() => store.VisibleCatalog(null, local =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fixture release timed out");
            return new RemoteResource(local.Id, local.Name, local.Kind, local.Mode, 7, local.PublishedUtc, true, GroupId: local.GroupId);
        }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var liveAccess = Task.Run(() =>
            {
                Assert.Equal(identity, store.GetSettings().Profile.DeviceId);
                if (removePublication) store.RemoveResource(resource.Id);
                else store.SetGroupPermission(group.Id, GroupAccess.Private, []);
            });
            await liveAccess.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { release.Set(); }
        Assert.Empty((await scan).Resources);
    }

    [Fact]
    public void CanceledCatalogAndTreeStopBeforeReadingResources()
    {
        var store = new NodeStore(root);
        var catalog = new ResourceCatalog(store);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => store.VisibleCatalog(null, catalog, cancel.Token));
        Assert.Throws<OperationCanceledException>(() => catalog.ListFiles("missing", cancellationToken: cancel.Token));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
