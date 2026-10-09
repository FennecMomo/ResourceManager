using System.IO;
using System.Text.Json;
using ResourceManager.App;
using ResourceManager.Core;

internal static partial class Program
{
    private static async Task CheckAiBridgeAsync(MainWindow window, NodeStore store)
    {
        Task<AiBridgeResponse> Call(string command, object? args = null) =>
            window.HandleAiBridgeAsync(new AiBridgeRequest(1, command, Environment.ProcessId,
                JsonSerializer.SerializeToElement(args ?? new { }, LocalAiBridge.Json)), default);
        JsonElement Data(AiBridgeResponse response) => JsonSerializer.SerializeToElement(response.Data, LocalAiBridge.Json);

        var status = await Call("status");
        Require(status.Success && !Data(status).GetProperty("windowVisible").GetBoolean(),
            "MCP status reads the hidden client without activating it");
        var devices = await Call("list_devices");
        Require(devices.Success && Data(devices).GetProperty("devices").GetArrayLength() >= 1,
            "MCP lists registered devices");
        var search = await Call("search_resources", new { query = "absent-check", kind = (string?)null,
            minSizeBytes = (long?)null, maxSizeBytes = (long?)null, limit = 10 });
        Require(search.Success && Data(search).GetProperty("total").GetInt32() == 0,
            "MCP resource search returns an empty filtered snapshot");
        Require(!(await Call("search_resources", new { source = "server", serverId = "unbound" })).Success,
            "MCP search rejects an unbound server filter");
        var scopedSearch = await Call("search_resources", new { source = "device", deviceId = "absent-device" });
        Require(scopedSearch.Success && Data(scopedSearch).GetProperty("total").GetInt32() == 0,
            "MCP search scopes results to the requested device");

        var source = Path.Combine(store.DataDirectory, "mcp-publish-check.txt");
        await File.WriteAllTextAsync(source, "isolated MCP publication");
        Require(!(await Call("publish", new { path = "relative.txt", mode = "Reference" })).Success,
            "MCP rejects relative publication paths");
        Require(!(await Call("publish", new { path = source, mode = "123" })).Success,
            "MCP rejects undefined publication modes");
        var published = await Call("publish", new { path = source, mode = "Reference" });
        var operationId = Data(published).GetProperty("operationId").GetString();
        Require(published.Success && operationId is not null, "MCP starts publication and returns an operation ID");
        AiBridgeResponse publicationStatus = null!;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            publicationStatus = await Call("get_publication_operation", new { operationId });
            if (Data(publicationStatus).GetProperty("state").GetString() != "Running") break;
            await Task.Delay(20);
        }
        var publicationId = Data(publicationStatus).GetProperty("resourceId").GetString();
        Require(published.Success && publicationId is not null && store.GetResource(publicationId) is not null,
            "MCP publishes an isolated file without a picker");
        var publications = await Call("list_publications");
        Require(publications.Success && Data(publications).GetProperty("publications").EnumerateArray()
            .Any(item => item.GetProperty("id").GetString() == publicationId),
            "MCP reads back the new publication");

        var servers = await Call("list_servers");
        Require(servers.Success && Data(servers).GetProperty("servers").GetArrayLength() == 0,
            "MCP lists bound servers without creating a connection");
        var downloads = await Call("list_downloads");
        Require(downloads.Success && Data(downloads).GetProperty("downloads").GetArrayLength() == 0,
            "MCP lists download jobs without opening the downloads tab");
        Require(!(await Call("download_resource", new { source = "device", deviceId = "peer",
            resourceId = "missing", targetDirectory = "relative" })).Success,
            "MCP refuses a download destination that is not an existing absolute directory");
        var favorites = await Call("list_favorites");
        Require(favorites.Success && Data(favorites).GetProperty("total").GetInt32() ==
            store.GetFavorites().Count + store.GetServerFavorites().Count,
            "MCP lists favorites from isolated storage");
        var noted = await Call("set_publication_note", new { resourceId = publicationId, note = "MCP 备注" });
        Require(noted.Success && store.GetResource(publicationId!)!.Note == "MCP 备注",
            "MCP updates publication metadata");
        var group = store.SaveResourceGroup("MCP 隔离组");
        var moved = await Call("move_publication", new { resourceId = publicationId, groupId = group.Id });
        Require(moved.Success && store.GetResource(publicationId!)!.GroupId == group.Id,
            "MCP moves a publication into an existing group");
        var permission = await Call("set_group_permission", new { groupId = group.Id,
            access = "Private", allowedDeviceIds = Array.Empty<string>() });
        Require(permission.Success && store.GetEffectiveGroupPermission(group.Id).Access == GroupAccess.Private,
            "MCP changes group visibility in the isolated store");
        Require(!(await Call("set_server_publication", new { serverId = "missing", kind = "resource",
            id = publicationId, enabled = true })).Success,
            "MCP cannot publish to an unbound server");
        var uploads = await Call("list_uploads");
        Require(uploads.Success && Data(uploads).GetProperty("total").GetInt32() == 0,
            "MCP lists server upload jobs without using a picker");
        Require(!(await Call("upload_to_server", new { serverId = "missing", path = source,
            access = "Public" })).Success,
            "MCP cannot upload to an unbound server");
        var updateStatus = await Call("get_update_status");
        Require(updateStatus.Success && Data(updateStatus).TryGetProperty("currentVersion", out _),
            "MCP exposes update state without starting an installation");

        store.SavePeerCapabilities("peer", [NodeDefaults.ChatCapability, NodeDefaults.PrivateResourceCapability]);
        var before = store.GetChatMessages("peer").Count;
        var card = await Call("send_resource_card", new { deviceId = "peer", resourceId = publicationId });
        Require(card.Success && store.GetChatMessages("peer").Count == before + 1 &&
                store.GetChatMessages("peer").Last().ResourceId == publicationId,
            "MCP queues a card for an available publication");
        before = store.GetChatMessages("peer").Count;
        var sent = await Call("send_message", new { deviceId = "peer", text = "MCP 隔离消息" });
        Require(sent.Success && store.GetChatMessages("peer").Count == before + 1,
            "MCP queues one message to a known chat-capable peer");
        var messages = await Call("list_messages", new { deviceId = "peer", limit = 1 });
        Require(messages.Success && Data(messages).GetProperty("messages")[0].GetProperty("text").GetString() == "MCP 隔离消息",
            "MCP reads the latest message without a UI action");
        Require(!(await Call("send_message", new { deviceId = "unknown", text = "not delivered" })).Success,
            "MCP cannot send to an unknown device");
        var muted = await Call("mute_conversation", new { deviceId = "peer", minutes = 5 });
        Require(muted.Success && store.GetChatConversations().First(item => item.PeerId == "peer").MutedUntilUtc is not null,
            "MCP mutes a known conversation");
        Require(!(await Call("retry_message", new { deviceId = "peer", messageId = "missing" })).Success,
            "MCP refuses to retry a message that did not fail");
        var privateSend = await Call("send_private_resource", new { deviceId = "peer", path = source });
        var privateOperationId = Data(privateSend).GetProperty("operationId").GetString();
        Require(privateSend.Success && privateOperationId is not null,
            "MCP prepares a private resource in the background");
        AiBridgeResponse privateStatus = null!;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            privateStatus = await Call("get_private_resource_operation", new { operationId = privateOperationId });
            if (Data(privateStatus).GetProperty("state").GetString() != "Running") break;
            await Task.Delay(20);
        }
        Require(privateStatus.Success && Data(privateStatus).GetProperty("state").GetString() == "Completed" &&
            store.GetChatMessages("peer").Any(item => item.Kind == "PrivateResource"),
            "MCP queues a private copy after completing its preparation operation");
        var sample = store.GetChatMessages("peer").First() with { State = "Delivered", Kind = "PrivateResource", Outgoing = true };
        Require(new ChatMessageRow(sample).StateText.Contains("不支持"), "legacy peers do not falsely show unread or download completion");
        var receipt = new ChatProgressReceipt(sample.MessageId, true, "下载中", 50, 100, DateTimeOffset.UtcNow);
        var row = new ChatMessageRow(sample, receipt, true);
        Require(row.StateText.Contains("已读") && row.StateText.Contains("50%") && row.StateText.Contains("上次回执"),
            "chat cards distinguish read, attachment progress and cached observation time");
        Require(row.CancelVisibility == System.Windows.Visibility.Visible, "delivered private card still exposes revoke and cleanup");
        var revoked = await Call("revoke_publication", new { resourceId = publicationId });
        Require(revoked.Success && store.GetResource(publicationId!) is null && File.Exists(source),
            "MCP revokes a reference publication while preserving its original file");
    }
}
