using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ResourceManager.Core;

public sealed record AiBridgeRequest(int Protocol, string Command, int ProcessId, JsonElement Arguments);
public sealed record AiBridgeResponse(bool Success, string? Error = null, object? Data = null);

/// <summary>Current-user-only endpoint for the separately launched MCP stdio process.</summary>
public sealed class LocalAiBridge : IDisposable, IAsyncDisposable
{
    public static string PipeName(int processId) => $"FennecMomo.ResourceManager.Ai.v1.{processId}";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const int MaxRequestCharacters = 32768;
    private static readonly HashSet<string> Commands =
    [
        "status", "list_devices", "refresh_devices", "list_device_resources", "search_resources",
        "list_publications", "publish", "get_publication_operation", "list_conversations", "list_messages", "send_message", "send_resource_card"
    ];
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task listener;
    private bool disposed;

    public LocalAiBridge(Func<AiBridgeRequest, CancellationToken, Task<AiBridgeResponse>> handler,
        string? pipeName = null)
    {
        listener = ListenAsync(pipeName ?? PipeName(Environment.ProcessId), handler, cancellation.Token);
    }

    private static async Task ListenAsync(string name,
        Func<AiBridgeRequest, CancellationToken, Task<AiBridgeResponse>> handler, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromMinutes(2));
                using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 1024, true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true);
                AiBridgeResponse response;
                try
                {
                    // Bound the input independently of the caller's advertised content length.
                    var text = new StringBuilder();
                    var buffer = new char[1];
                    while (true)
                    {
                        if (await reader.ReadAsync(buffer, deadline.Token).ConfigureAwait(false) == 0)
                            throw new InvalidDataException("incomplete_request");
                        if (buffer[0] == '\n') break;
                        if (text.Length >= MaxRequestCharacters) throw new InvalidDataException("request_too_large");
                        text.Append(buffer[0]);
                    }
                    var request = JsonSerializer.Deserialize<AiBridgeRequest>(text.ToString(), Json);
                    response = request is null || request.Protocol != 1 || request.ProcessId != Environment.ProcessId
                        ? new(false, "invalid_protocol_or_process")
                        : !Commands.Contains(request.Command)
                            ? new(false, "unsupported_command")
                            : await handler(request, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or DecoderFallbackException)
                {
                    response = new(false, "invalid_request");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    response = new(false, "handler_failed");
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, Json).AsMemory(), deadline.Token).ConfigureAwait(false);
                await writer.FlushAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { }
            catch (IOException) { if (!token.IsCancellationRequested) await Task.Delay(50, token).ConfigureAwait(false); }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cancellation.Cancel();
        _ = listener.ContinueWith(_ => cancellation.Dispose(), TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        try { await listener.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}

public sealed class LocalAiBridgeClient
{
    private readonly int? preferredProcessId;
    public LocalAiBridgeClient(int? preferredProcessId = null) => this.preferredProcessId = preferredProcessId;

    public async Task<JsonElement> CallAsync(string command, object? arguments = null, CancellationToken token = default)
    {
        var targets = preferredProcessId is int id ? [id]
            : Process.GetProcessesByName("ResourceManager").Select(process => process.Id)
                .Where(id => id != Environment.ProcessId).ToArray();
        if (targets.Length == 0)
            throw new InvalidOperationException("ResourceManager 客户端未运行。请先启动客户端。");
        if (targets.Length == 1)
        {
            try { return Data(await SendAsync(targets[0], command, arguments, TimeSpan.FromMinutes(2), token)); }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                throw new InvalidOperationException("ResourceManager 客户端版本不支持 MCP，或本机通道暂不可用。", ex);
            }
        }
        var available = new List<int>();
        foreach (var target in targets)
        {
            try
            {
                var status = await SendAsync(target, "status", null, TimeSpan.FromSeconds(3), token);
                if (status.GetProperty("success").GetBoolean()) available.Add(target);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException && !token.IsCancellationRequested) { }
        }
        if (available.Count == 0) throw new InvalidOperationException("ResourceManager 客户端未运行或版本不支持 MCP。请先启动新版客户端。");
        if (available.Count != 1) throw new InvalidOperationException("检测到多个 ResourceManager 实例；请设置 RESOURCEMANAGER_CLIENT_PID 指定目标。");
        return Data(await SendAsync(available[0], command, arguments, TimeSpan.FromMinutes(2), token));
    }

    private static JsonElement Data(JsonElement response)
    {
        if (!response.GetProperty("success").GetBoolean())
            throw new InvalidOperationException(response.TryGetProperty("error", out var error) ? error.GetString() : "operation_failed");
        return response.GetProperty("data").Clone();
    }

    private static async Task<JsonElement> SendAsync(int processId, string command, object? arguments,
        TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        await using var pipe = new NamedPipeClientStream(".", LocalAiBridge.PipeName(processId),
            PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000, deadline.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true);
        var request = new { protocol = 1, command, processId, arguments };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, LocalAiBridge.Json).AsMemory(), deadline.Token);
        await writer.FlushAsync(deadline.Token);
        var line = await reader.ReadLineAsync(deadline.Token) ?? throw new IOException("ResourceManager MCP 通道已断开。");
        return JsonDocument.Parse(line).RootElement.Clone();
    }
}
