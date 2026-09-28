using Microsoft.Data.Sqlite;
using ResourceManager.Core;

namespace ResourceManager.Tests;

public sealed class StorageLocationTests
{
    [Fact]
    public async Task MigrationPreservesIdentityResourcesFeedbackGitAndDownloadDestinations()
    {
        using var space = new Space();
        var store = new NodeStore(space.Source);
        store.SaveSettings("迁移测试", [1, 2, 3], 37642, true);
        var peer = new PeerInfo(Guid.NewGuid().ToString("N"), "127.0.0.1", 37642, "同事", null, DateTimeOffset.UtcNow);
        store.UpsertPeer(peer);
        store.SavePeerCapabilities(peer.DeviceId, [NodeDefaults.ChatCapability]);
        store.SavePeerNote(peer.DeviceId, "设备备注");
        var original = space.Write("external.txt", "保留原位置");
        var copy = store.AddResource(original, PublishMode.Copy);
        var reference = store.AddResource(original, PublishMode.Reference);
        store.SetResourceNote(copy.Id, "副本备注");
        var group = store.SaveResourceGroup("迁移分组");
        var childGroup = store.SaveResourceGroup("子分组", group.Id);
        store.MoveResourceToGroup(copy.Id, childGroup.Id);
        store.SetGroupPermission(group.Id, GroupAccess.AllowList, [peer.DeviceId]);
        var identityKey = store.GetDeviceSigningKey();
        var server = new ServerBinding("server-binding", "迁移服务器", "https://workspace.example.com", Guid.NewGuid().ToString("N"), WorkspaceProtocol.PublicKey(WorkspaceProtocol.CreateKey()), "离线", null,
            new("cursor", [new(new(peer.DeviceId, peer.Nickname, null, "0.6.1"), true, DateTimeOffset.UtcNow)]));
        store.SaveServerBinding(server);
        store.SetServerPublication(server.Id, "group", group.Id, true);
        store.SaveServerDownload("job", server.Id, peer.Nickname);
        store.TrustDeviceKey(peer.DeviceId, "migration-trust-fixture");
        store.SaveFavorite(new Favorite(peer.DeviceId, copy.Id, copy.Name, copy.Kind));
        var downloadTarget = space.Write("downloads/file.rm-part", "partial");
        store.SaveDownload(new DownloadJob("job", peer.DeviceId, copy.Id, copy.Name, copy.Kind,
            downloadTarget[..^8], "已暂停", 7, 100, null));
        var feedback = new FeedbackStore(space.Source);
        var attachment = Path.Combine(feedback.DraftDirectory, "attachment.txt");
        File.WriteAllText(attachment, "附件");
        feedback.SaveDraft(new FeedbackDraft(FeedbackCategory.Suggestion, "标题", "正文",
            [new FeedbackAttachmentDraft("attachment.txt", attachment, new FileInfo(attachment).Length)], DateTimeOffset.UtcNow));
        feedback.SetSetting("feedback_client_id", "stable-client-id");
        var git = new GitCollaborationStore(space.Source);
        var projectId = Guid.NewGuid().ToString("N");
        var repo = Path.Combine(space.Source, "repositories", "demo");
        Directory.CreateDirectory(repo);
        git.SetBinding(new GitProjectBinding(projectId, repo, "main"));
        var commitHash = new string('a', 40);
        var signedEvent = git.CreateEvent(projectId, "迁移项目", "main", store.GetSettings().Profile.DeviceId,
            "迁移测试", "Created", commitHash, null,
            [new GitCommitPoint(commitHash, [], "initial", DateTimeOffset.UtcNow)],
            new GitBundleInfo(new string('b', 64), 1, Path.Combine(git.BundleDirectory, "fixture.bundle")));
        using var client = new PeerClient(store);
        var chat = new ChatService(store, client);
        var message = chat.QueueText(peer.DeviceId, "聊天历史");
        var pending = space.Locations.ScheduleMigration(new StorageConfiguration(space.Source, true), space.Target);

        var result = await space.Locations.CompleteMigrationAsync(pending);

        Assert.Equal(space.Target, result.Directory);
        Assert.Null(result.Pending);
        Assert.Equal(space.Source, result.PreviousDirectory);
        var migrated = new NodeStore(space.Target);
        Assert.Equal(store.GetSettings().Profile.DeviceId, migrated.GetSettings().Profile.DeviceId);
        Assert.Equal("迁移测试", migrated.GetSettings().Profile.Nickname);
        Assert.Equal("设备备注", migrated.GetPeerNote(peer.DeviceId));
        var migratedCopy = migrated.GetResource(copy.Id)!;
        Assert.Equal(childGroup.Id, migratedCopy.GroupId);
        Assert.Equal(identityKey, migrated.GetDeviceSigningKey());
        var migratedServer = Assert.Single(migrated.GetServerBindings());
        Assert.Equal(server.Address, migratedServer.Address);
        Assert.Equal(server.PublicKey, migratedServer.PublicKey);
        Assert.True(migrated.IsServerPublished(server.Id, "resource", copy.Id));
        Assert.Equal(server.Id, migrated.GetServerDownload("job")!.Value.Server);
        Assert.Equal(peer.DeviceId, Assert.Single(migratedServer.Cached!.Members).Profile.DeviceId);
        Assert.Equal("migration-trust-fixture", migrated.GetTrustedDeviceKey(peer.DeviceId));
        Assert.True(migrated.CanAccessGroup(childGroup.Id, peer.DeviceId));
        Assert.False(migrated.CanAccessGroup(childGroup.Id, null));
        Assert.Contains(migrated.GetResourceGroups(), g => g.Id == childGroup.Id && g.ParentId == group.Id);
        Assert.True(StorageLocation.IsWithin(migratedCopy.SourcePath, space.Target));
        Assert.Equal("保留原位置", File.ReadAllText(migratedCopy.SourcePath));
        Assert.Equal("副本备注", migratedCopy.Note);
        Assert.Equal(original, migrated.GetResource(reference.Id)!.SourcePath);
        Assert.Equal(store.GetDownload("job"), migrated.GetDownload("job"));
        Assert.Equal("partial", File.ReadAllText(downloadTarget));
        Assert.Equal(store.GetFavorites(), migrated.GetFavorites());
        Assert.Equal(message.MessageId, Assert.Single(migrated.GetChatMessages(peer.DeviceId)).MessageId);
        var migratedFeedback = new FeedbackStore(space.Target);
        Assert.Equal("stable-client-id", migratedFeedback.GetSetting("feedback_client_id"));
        var migratedAttachment = Assert.Single(migratedFeedback.GetDraft().Attachments);
        Assert.True(StorageLocation.IsWithin(migratedAttachment.StagedPath, space.Target));
        Assert.Equal("附件", File.ReadAllText(migratedAttachment.StagedPath));
        var migratedGit = new GitCollaborationStore(space.Target);
        Assert.Equal(git.MemberId, migratedGit.MemberId);
        Assert.Equal(signedEvent.Signature, Assert.Single(migratedGit.GetEvents()).Signature);
        Assert.Equal(Path.Combine(space.Target, "repositories", "demo"), migratedGit.GetBinding(projectId)!.RepositoryPath);
        Assert.True(File.Exists(copy.SourcePath));
        Assert.Equal(copy.SourcePath, store.GetResource(copy.Id)!.SourcePath);
        Assert.Equal(attachment, feedback.GetDraft().Attachments[0].StagedPath);
    }

    [Fact]
    public async Task CancelledCopyCanResumeAndNeverSwitchesPointerEarly()
    {
        using var space = new Space();
        _ = new NodeStore(space.Source);
        space.Write("source/payload.bin", new string('a', 10000));
        var pending = space.Locations.ScheduleMigration(new StorageConfiguration(space.Source, true), space.Target);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(text => { if (text.Contains("正在复制")) cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            space.Locations.CompleteMigrationAsync(pending, progress, cancellation.Token));
        Assert.Equal(space.Source, space.Locations.Read()!.Directory);
        Assert.NotNull(space.Locations.Read()!.Pending);
        var result = await space.Locations.CompleteMigrationAsync(space.Locations.Read()!);
        Assert.Equal(space.Target, result.Directory);
        Assert.Equal(new string('a', 10000), File.ReadAllText(Path.Combine(space.Target, "payload.bin")));
    }

    [Fact]
    public async Task InterruptedPointerSwitchRecoversVerifiedDestination()
    {
        using var space = new Space();
        var store = new NodeStore(space.Source);
        var pending = space.Locations.ScheduleMigration(new StorageConfiguration(space.Source, true), space.Target);
        await space.Locations.CompleteMigrationAsync(pending);
        // Simulate power loss after the directory rename and before pointer persistence.
        space.Locations.Save(pending);
        var result = await space.Locations.CompleteMigrationAsync(space.Locations.Read()!);
        Assert.Equal(space.Target, result.Directory);
        Assert.Equal(store.GetSettings().Profile.DeviceId, new NodeStore(result.Directory).GetSettings().Profile.DeviceId);
    }

    [Fact]
    public async Task InterruptedRenameRecoversVerifiedStage()
    {
        using var space = new Space();
        _ = new NodeStore(space.Source);
        var pending = space.Locations.ScheduleMigration(new StorageConfiguration(space.Source, true), space.Target);
        await space.Locations.CompleteMigrationAsync(pending);
        Directory.Move(space.Target, pending.Pending!.Stage);
        space.Locations.Save(pending);
        var result = await space.Locations.CompleteMigrationAsync(pending);
        Assert.Equal(space.Target, result.Directory);
        Assert.False(Directory.Exists(pending.Pending.Stage));
    }

    [Fact]
    public async Task VerifiedDestinationCanRecoverWhenOriginalDiskIsUnavailable()
    {
        using var space = new Space();
        _ = new NodeStore(space.Source);
        var pending = space.Locations.ScheduleMigration(new StorageConfiguration(space.Source, true), space.Target);
        await space.Locations.CompleteMigrationAsync(pending);
        space.Locations.Save(pending);
        Directory.Move(space.Source, space.Source + "-unavailable");
        var result = await space.Locations.CompleteMigrationAsync(pending);
        Assert.Equal(space.Target, result.Directory);
    }

    [Fact]
    public void AnEmptyDatabaseCannotReplaceAnInitializedProfile()
    {
        using var space = new Space();
        space.Write("source/resources.db", "");
        Assert.Throws<SqliteException>(() => StorageLocation.ValidateCurrent(new StorageConfiguration(space.Source, true)));
    }

    [Fact]
    public async Task RecoveryRejectsModifiedDestinationAndPreservesOriginal()
    {
        using var space = new Space();
        _ = new NodeStore(space.Source);
        space.Write("source/important.txt", "original");
        var pending = space.Locations.ScheduleMigration(new StorageConfiguration(space.Source, true), space.Target);
        await space.Locations.CompleteMigrationAsync(pending);
        space.Locations.Save(pending);
        File.WriteAllText(Path.Combine(space.Target, "important.txt"), "modified");
        await Assert.ThrowsAsync<IOException>(() => space.Locations.CompleteMigrationAsync(pending));
        Assert.Equal(space.Source, space.Locations.Read()!.Directory);
        Assert.Equal("original", File.ReadAllText(Path.Combine(space.Source, "important.txt")));
    }

    [Fact]
    public async Task CorruptDatabaseDoesNotSwitchOrDestroyOriginal()
    {
        using var space = new Space();
        space.Write("source/resources.db", "not a database");
        var pending = space.Locations.ScheduleMigration(new StorageConfiguration(space.Source, true), space.Target);
        await Assert.ThrowsAsync<SqliteException>(() => space.Locations.CompleteMigrationAsync(pending));
        Assert.Equal(space.Source, space.Locations.Read()!.Directory);
        Assert.Equal("not a database", File.ReadAllText(Path.Combine(space.Source, "resources.db")));
    }

    [Fact]
    public void MissingConfiguredDirectoryOrDatabaseNeverCreatesEmptyProfile()
    {
        using var space = new Space();
        Assert.Throws<DirectoryNotFoundException>(() => StorageLocation.ValidateCurrent(new StorageConfiguration(space.Source, true)));
        Assert.False(Directory.Exists(space.Source));
        Directory.CreateDirectory(space.Source);
        Assert.Throws<IOException>(() => StorageLocation.ValidateCurrent(new StorageConfiguration(space.Source, true)));
        Assert.False(File.Exists(Path.Combine(space.Source, "resources.db")));
    }

    [Fact]
    public void MigrationRejectsNestedSameAndOccupiedDirectories()
    {
        using var space = new Space();
        _ = new NodeStore(space.Source);
        var configuration = new StorageConfiguration(space.Source, true);
        Assert.Throws<IOException>(() => space.Locations.ScheduleMigration(configuration, space.Source));
        Assert.Throws<IOException>(() => space.Locations.ScheduleMigration(configuration, Path.Combine(space.Source, "inside")));
        Assert.Throws<IOException>(() => space.Locations.ScheduleMigration(configuration, Path.GetDirectoryName(space.Source)!));
        space.Write("target/user.txt", "do not overwrite");
        Assert.Throws<IOException>(() => space.Locations.ScheduleMigration(configuration, space.Target));
        Assert.Equal("do not overwrite", File.ReadAllText(Path.Combine(space.Target, "user.txt")));
        Assert.Null(space.Locations.Read());
    }

    [Fact]
    public async Task TargetPopulatedAfterSchedulingIsNotOverwritten()
    {
        using var space = new Space();
        _ = new NodeStore(space.Source);
        var pending = space.Locations.ScheduleMigration(new StorageConfiguration(space.Source, true), space.Target);
        space.Write("target/user.txt", "keep");
        await Assert.ThrowsAsync<IOException>(() => space.Locations.CompleteMigrationAsync(pending));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(space.Target, "user.txt")));
        Assert.Equal(space.Source, space.Locations.Read()!.Directory);
    }

    [Fact]
    public async Task ASecondMigrationPreservesDataWithoutUsingTheOldManifest()
    {
        using var space = new Space();
        var store = new NodeStore(space.Source);
        var first = await space.Locations.CompleteMigrationAsync(
            space.Locations.ScheduleMigration(new StorageConfiguration(space.Source, true), space.Target));
        var migrated = new NodeStore(space.Target);
        migrated.SaveSettings("new nickname", null, 37642, true);
        var finalTarget = Path.Combine(Path.GetDirectoryName(space.Target)!, "third");
        var last = await space.Locations.CompleteMigrationAsync(space.Locations.ScheduleMigration(first, finalTarget));
        var final = new NodeStore(last.Directory);
        Assert.Equal("new nickname", final.GetSettings().Profile.Nickname);
        Assert.Equal(store.GetSettings().Profile.DeviceId, final.GetSettings().Profile.DeviceId);
    }

    [Fact]
    public void SpaceChecksRejectHugeRequestsAndMeasurementSupportsCancellation()
    {
        using var space = new Space();
        space.Write("source/a.txt", "12345");
        Assert.Throws<IOException>(() => StorageLocation.EnsureSpace(space.Source, long.MaxValue));
        Assert.Throws<IOException>(() => StorageLocation.EnsureSpace(space.Source, -1));
        var usage = StorageLocation.Measure(space.Source);
        Assert.Equal(5, usage.ProgramBytes);
        Assert.Equal(usage.Total - usage.Available, usage.Used);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => StorageLocation.Measure(space.Source, cancelled.Token));
    }

    [Fact]
    public async Task DatabaseWalContentsSurviveMigration()
    {
        using var space = new Space();
        _ = new NodeStore(space.Source);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(space.Source, "resources.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_autocheckpoint=0; UPDATE settings SET value='WAL nickname' WHERE key='nickname'";
        command.ExecuteNonQuery();
        Assert.True(File.Exists(Path.Combine(space.Source, "resources.db-wal")));
        // Capture a crash-like db + WAL snapshot, then close the writer before migration.
        var snapshot = Path.Combine(Path.GetDirectoryName(space.Source)!, "wal-snapshot");
        Directory.CreateDirectory(snapshot);
        foreach (var name in new[] { "resources.db", "resources.db-wal" })
        {
            using var input = new FileStream(Path.Combine(space.Source, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var output = File.Create(Path.Combine(snapshot, name));
            input.CopyTo(output);
        }
        connection.Close();
        foreach (var name in new[] { "resources.db", "resources.db-wal" })
            File.Copy(Path.Combine(snapshot, name), Path.Combine(space.Source, name), true);
        var pending = space.Locations.ScheduleMigration(new StorageConfiguration(space.Source, true), space.Target);
        await space.Locations.CompleteMigrationAsync(pending);
        Assert.Equal("WAL nickname", new NodeStore(space.Target).GetSettings().Profile.Nickname);
    }

    private sealed class InlineProgress(Action<string> action) : IProgress<string>
    {
        public void Report(string value) => action(value);
    }

    private sealed class Space : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "ResourceManagerStorageTests", Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(root, "source");
        public string Target => Path.Combine(root, "target");
        public StorageLocation Locations => new(Path.Combine(root, "bootstrap", "storage.json"));
        public string Write(string relative, string text)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return path;
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
