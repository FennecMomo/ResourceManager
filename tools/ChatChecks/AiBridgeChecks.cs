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

        store.SavePeerCapabilities("peer", [NodeDefaults.ChatCapability]);
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
    }
}
