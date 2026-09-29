using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ResourceManager.Core;

namespace ResourceManager.Tests;

public sealed class LocalControlTests
{
    [Fact]
    public async Task StatusAndShutdownAcknowledgeWithoutActivation_AndValidateTarget()
    {
        var name = "ResourceManager.Tests." + Guid.NewGuid().ToString("N");
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        await using var server = new LocalControlServer((request, _) =>
        {
            calls.Add(request.Command);
            return Task.FromResult(new LocalControlResponse(true, Data: new { version = "test", visible = false })
            { AfterResponse = request.Command == "shutdown" ? () => stopped.TrySetResult() : null });
        }, name);
        var status = await Send(name, new(1, "status", Environment.ProcessId));
        Assert.True(status.GetProperty("success").GetBoolean());
        Assert.False(status.GetProperty("data").GetProperty("visible").GetBoolean());
        Assert.False(stopped.Task.IsCompleted);
        foreach (var invalid in new[] { new LocalControlRequest(2, "status", Environment.ProcessId),
                     new(1, "status", -1), new(1, "activate", Environment.ProcessId), new(1, "execute", Environment.ProcessId) })
            Assert.False((await Send(name, invalid)).GetProperty("success").GetBoolean());
        Assert.Single(calls);
        Assert.True((await Send(name, new(1, "shutdown", Environment.ProcessId))).GetProperty("success").GetBoolean());
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { "status", "shutdown" }, calls);
    }

    [Fact]
    public async Task InvalidOrOversizedRequestDoesNotDisableSubsequentStatus()
    {
        var name = "ResourceManager.Tests." + Guid.NewGuid().ToString("N");
        await using var server = new LocalControlServer((_, _) => Task.FromResult(new LocalControlResponse(true)), name);
        foreach (var body in new[] { "{broken", new string('x', LocalControlServer.MaxRequestCharacters + 1) })
            Assert.False((await SendText(name, body)).GetProperty("success").GetBoolean());
        Assert.True((await Send(name, new(1, "status", Environment.ProcessId))).GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task ShutdownCanRefuseUnsavedWorkWithoutRunningExitCallback()
    {
        var name = "ResourceManager.Tests." + Guid.NewGuid().ToString("N");
        await using var server = new LocalControlServer((_, _) =>
            Task.FromResult(new LocalControlResponse(false, "unsaved_settings")), name);
        var result = await Send(name, new(1, "shutdown", Environment.ProcessId));
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("unsaved_settings", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task FailedHandlerAndIdleClientDoNotPermanentlyBlockControl()
    {
        var name = "ResourceManager.Tests." + Guid.NewGuid().ToString("N");
        var fail = true;
        await using var server = new LocalControlServer((_, _) =>
        {
            if (fail) { fail = false; throw new InvalidOperationException("private diagnostic detail"); }
            return Task.FromResult(new LocalControlResponse(true));
        }, name);
        var failure = await Send(name, new(1, "status", Environment.ProcessId));
        Assert.Equal("handler_failed", failure.GetProperty("error").GetString());
        using (var idle = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await idle.ConnectAsync(2000);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var buffer = new byte[1];
            Assert.Equal(0, await idle.ReadAsync(buffer, deadline.Token));
        }
        Assert.True((await Send(name, new(1, "status", Environment.ProcessId))).GetProperty("success").GetBoolean());
    }

    private static Task<JsonElement> Send(string name, LocalControlRequest request) =>
        SendText(name, JsonSerializer.Serialize(request, LocalControlServer.Json));

    private static async Task<JsonElement> SendText(string name, string text)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true);
        await writer.WriteLineAsync(text.AsMemory(), timeout.Token);
        await writer.FlushAsync(timeout.Token);
        var line = await reader.ReadLineAsync(timeout.Token);
        return JsonDocument.Parse(line!).RootElement.Clone();
    }
}

public sealed class LocalAiBridgeTests
{
    [Fact]
    public async Task ClientUsesCurrentUserPipeAndReportsHandlerErrors()
    {
        await using var server = new LocalAiBridge((request, _) =>
            Task.FromResult(request.Command == "status"
                ? new AiBridgeResponse(true, Data: new { ready = true })
                : new AiBridgeResponse(false, "not_allowed")));
        var client = new LocalAiBridgeClient(Environment.ProcessId);
        Assert.True((await client.CallAsync("status")).GetProperty("ready").GetBoolean());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CallAsync("publish"));
        Assert.Equal("not_allowed", error.Message);
    }

    [Fact]
    public async Task AiPipeValidatesCommandsAndRecoversAfterInvalidInput()
    {
        var name = "ResourceManager.Tests.Ai." + Guid.NewGuid().ToString("N");
        var calls = new List<string>();
        await using var server = new LocalAiBridge((request, _) =>
        {
            calls.Add(request.Command);
            return Task.FromResult(new AiBridgeResponse(true, Data: new { value = request.Command }));
        }, name);
        var good = await Send(new { protocol = 1, command = "list_devices", processId = Environment.ProcessId, arguments = new { } });
        Assert.True(good.GetProperty("success").GetBoolean());
        Assert.Equal("list_devices", good.GetProperty("data").GetProperty("value").GetString());
        foreach (var bad in new[]
        {
            new { protocol = 2, command = "list_devices", processId = Environment.ProcessId, arguments = new { } },
            new { protocol = 1, command = "list_devices", processId = -1, arguments = new { } },
            new { protocol = 1, command = "execute", processId = Environment.ProcessId, arguments = new { } }
        }) Assert.False((await Send(bad)).GetProperty("success").GetBoolean());
        Assert.False((await SendText("{broken")).GetProperty("success").GetBoolean());
        Assert.False((await SendText(new string('x', LocalAiBridge.MaxRequestCharacters + 1))).GetProperty("success").GetBoolean());
        Assert.Single(calls);
        Assert.True((await Send(new { protocol = 1, command = "status", processId = Environment.ProcessId, arguments = new { } }))
            .GetProperty("success").GetBoolean());

        Task<JsonElement> Send(object request) => SendText(JsonSerializer.Serialize(request, LocalAiBridge.Json));
        async Task<JsonElement> SendText(string payload)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true);
            await writer.WriteLineAsync(payload.AsMemory(), timeout.Token);
            await writer.FlushAsync(timeout.Token);
            return JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!).RootElement.Clone();
        }
    }
}
