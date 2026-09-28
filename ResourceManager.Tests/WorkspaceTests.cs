using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.Core;
using ResourceManager.Server;

namespace ResourceManager.Tests;

public sealed class WorkspaceTests
{
    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("https://example.com/admin")]
    [InlineData("https://example.com/?token=secret")]
    public void PublicAddressRequiresHttpsAndDoesNotAcceptCredentialsOrPaths(string address) => Assert.Throws<ArgumentException>(() => WorkspaceProtocol.NormalizeAddress(address));

    [Fact]
    public async Task MultipleServersPushPresenceAndKeepIdentityAndStateSeparate()
    {
        using var space = new Space(); await using var one = await Host.Start(space.Path("one")); await using var two = await Host.Start(space.Path("two"));
        var a = new NodeStore(space.Path("a")); var b = new NodeStore(space.Path("b"));
        using var ca = new WorkspaceClient(a, "0.6.1"); using var cb = new WorkspaceClient(b, "0.6.1");
        var binding1 = await Bind(ca, one.Address); var binding2 = await Bind(ca, two.Address);
        a.SaveServerBinding(binding1); a.SaveServerBinding(binding2);
        var sa = await ca.JoinAsync(binding1); var sa2 = await ca.JoinAsync(binding2);
        var first = await ca.WatchAsync(binding1, sa, "", default);
        Assert.Single(first.Members); var push = ca.WatchAsync(binding1, sa, first.Cursor, default);
        var sb = await cb.JoinAsync(binding1); var changed = await push.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, changed.Members.Count); Assert.All(changed.Members, m => Assert.True(m.Online));
        Assert.Single((await ca.WatchAsync(binding2, sa2, "", default)).Members);
        b.SaveSettings("Updated profile", null, 37642, true); sb = await cb.JoinAsync(binding1);
        Assert.Contains((await ca.WatchAsync(binding1, sa, "", default)).Members, m => m.Profile.Nickname == "Updated profile");
        await cb.LeaveAsync(binding1, sb, default);
        Assert.False((await ca.WatchAsync(binding1, sa, "", default)).Members.Single(m => m.Profile.DeviceId == b.GetSettings().Profile.DeviceId).Online);
        a.SaveServerBinding(binding1 with { Cached = changed, Status = "在线" });
        Assert.Equal(2, new NodeStore(a.DataDirectory).GetServerBindings().Count);
        a.RemoveServerBinding(binding1.Id); Assert.Equal(binding2.Id, Assert.Single(a.GetServerBindings()).Id);
    }

    [Fact]
    public async Task NewSessionReplacesOldAndAdminRevocationBlocksAutomaticJoin()
    {
        using var space = new Space(); await using var host = await Host.Start(space.Path("host")); var store = new NodeStore(space.Path("client"));
        using var client = new WorkspaceClient(store, "0.6.1"); var binding = await Bind(client, host.Address);
        var old = await client.JoinAsync(binding); var current = await client.JoinAsync(binding);
        var replaced = await Assert.ThrowsAsync<WorkspaceException>(() => client.WatchAsync(binding, old, "", default)); Assert.Equal(HttpStatusCode.Conflict, replaced.Code);
        host.Hub.SetBlocked(store.GetSettings().Profile.DeviceId, true);
        var revoked = await Assert.ThrowsAsync<WorkspaceException>(() => client.WatchAsync(binding, current, "", default)); Assert.Equal(HttpStatusCode.Forbidden, revoked.Code);
        await Assert.ThrowsAsync<WorkspaceException>(() => client.JoinAsync(binding));
        host.Hub.SetBlocked(store.GetSettings().Profile.DeviceId, false);
        Assert.NotNull(await client.JoinAsync(binding));
    }

    [Fact]
    public async Task ServerRestartPreservesIdentityAndClientRejectsAddressWithDifferentIdentity()
    {
        using var space = new Space(); var store = new NodeStore(space.Path("client")); using var client = new WorkspaceClient(store, "0.6.1");
        var host = await Host.Start(space.Path("host")); var binding = await Bind(client, host.Address); await client.JoinAsync(binding); await host.DisposeAsync();
        await using var restarted = await Host.Start(space.Path("host"));
        var newAddress = binding with { Address = restarted.Address }; Assert.NotNull(await client.JoinAsync(newAddress));
        await using var other = await Host.Start(space.Path("other"));
        var error = await Assert.ThrowsAsync<WorkspaceException>(() => client.JoinAsync(binding with { Address = other.Address })); Assert.Equal(HttpStatusCode.Conflict, error.Code);
    }

    [Fact]
    public async Task RegistrationRejectsImpersonationReplayAndExpiredSignature()
    {
        using var space = new Space(); await using var host = await Host.Start(space.Path("host"));
        var profile = new WorkspaceProfile(Guid.NewGuid().ToString("N"), "Member", null, "0.6.1"); var key = WorkspaceProtocol.CreateKey();
        WorkspaceRegistration Request(byte[] signingKey, long timestamp)
        {
            var request = new WorkspaceRegistration(profile, WorkspaceProtocol.PublicKey(signingKey), timestamp, Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)), "");
            return request with { Signature = WorkspaceProtocol.Sign(signingKey, WorkspaceProtocol.RegistrationBytes(host.Hub.Capabilities.ServerId, request)) };
        }
        var valid = Request(key, DateTimeOffset.UtcNow.ToUnixTimeSeconds()); host.Hub.Join(valid, "test");
        Assert.Equal(HttpStatusCode.Conflict, Assert.Throws<WorkspaceException>(() => host.Hub.Join(valid, "test")).Code);
        Assert.Equal(HttpStatusCode.Conflict, Assert.Throws<WorkspaceException>(() => host.Hub.Join(Request(WorkspaceProtocol.CreateKey(), DateTimeOffset.UtcNow.ToUnixTimeSeconds()), "test")).Code);
        Assert.Equal(HttpStatusCode.BadRequest, Assert.Throws<WorkspaceException>(() => host.Hub.Join(Request(key, 1), "test")).Code);
    }

    [Fact]
    public async Task AnonymousExpiredAndOfflineSessionsAreHandledWithoutLosingIdentity()
    {
        using var space = new Space(); await using var host = await Host.Start(space.Path("host"));
        using var http = new HttpClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(host.Address + "/api/v1/workspace/members")).StatusCode);
        var store = new NodeStore(space.Path("client")); using var client = new WorkspaceClient(store, "0.6.1");
        var binding = await Bind(client, host.Address); var session = await client.JoinAsync(binding);
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + System.IO.Path.Combine(space.Path("host"), "workspace.db")))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE members SET expires=1,seen=1"; command.ExecuteNonQuery();
        }
        var expired = await Assert.ThrowsAsync<WorkspaceException>(() => client.WatchAsync(binding, session, "", default));
        Assert.Equal(HttpStatusCode.Unauthorized, expired.Code);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (host.Hub.Snapshot().Members.Single().Online) await Task.Delay(100, timeout.Token);
        var renewed = await client.JoinAsync(binding);
        Assert.True(Assert.Single((await client.WatchAsync(binding, renewed, "", default)).Members).Online);
        Assert.NotEqual(session.Token, renewed.Token);
    }

    [Fact]
    public void ConfigurationPrecedenceValidationAndDefaults()
    {
        using var space = new Space(); var config = space.Path("server.json"); File.WriteAllText(config, "{\"api-port\":42001,\"server-name\":\"JSON\"}");
        var previous = Environment.GetEnvironmentVariable("RM_SERVER_NAME");
        try
        {
            Environment.SetEnvironmentVariable("RM_SERVER_NAME", "ENV");
            Assert.Equal("ENV", ServerConfiguration.Load(["--config", config], "0.1.0").ServerName);
            var options = ServerConfiguration.Load(["--config", config, "--server-name", "CLI"], "0.1.0"); Assert.Equal("CLI", options.ServerName); Assert.Equal(42001, options.ApiPort);
            Assert.Throws<ArgumentException>(() => ServerConfiguration.Load(["--api-port", "oops"], "0.1.0"));
            Assert.Throws<ArgumentException>(() => ServerConfiguration.Load(["doctor", "migrate"], "0.1.0"));
            File.WriteAllText(config, "[]");
            Assert.Throws<ArgumentException>(() => ServerConfiguration.Load(["--config", config], "0.1.0"));
            Assert.Equal(FeedbackRules.DefaultApiPort, ServerConfiguration.Load([], "0.1.0").ApiPort);
        }
        finally { Environment.SetEnvironmentVariable("RM_SERVER_NAME", previous); }
    }
    private static async Task<ServerBinding> Bind(WorkspaceClient client, string address)
    { var info = await client.InspectAsync(address); return new(Guid.NewGuid().ToString("N"), info.Name, address, info.ServerId, info.PublicKey, "待连接", null, null); }
    private sealed class Host(WebApplication app, WorkspaceHub hub) : IAsyncDisposable
    {
        public WorkspaceHub Hub => hub;
        public string Address => app.Urls.Single();
        public static async Task<Host> Start(string directory)
        {
            var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(new ServerStore(directory, "Workspace"));
            builder.Services.AddSingleton(new ServerRuntimeOptions(directory, "Workspace", 1, 2, 3, "0.1.0"));
            builder.Services.AddSingleton<WorkspaceHub>(); builder.Services.AddHostedService(s => s.GetRequiredService<WorkspaceHub>());
            var app = builder.Build(); app.MapWorkspace(); await app.StartAsync(); return new(app, app.Services.GetRequiredService<WorkspaceHub>());
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
    }
    private sealed class Space : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ResourceWorkspaceTests", Guid.NewGuid().ToString("N"));
        public string Path(string name) { Directory.CreateDirectory(root); return System.IO.Path.Combine(root, name); }
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
