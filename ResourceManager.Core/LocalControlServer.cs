using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResourceManager.Core;

public sealed record LocalControlRequest(int Protocol, string Command, int ProcessId);
public sealed record LocalControlResponse(bool Success, string? Error = null, object? Data = null)
{
    [JsonIgnore] public Action? AfterResponse { get; init; }
}

/// <summary>A bounded, current-user-only local management endpoint; no network listener or UI input.</summary>
public sealed class LocalControlServer : IDisposable, IAsyncDisposable
{
    public static string PipeName(int processId) => $"FennecMomo.ResourceManager.Control.v1.{processId}";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const int MaxRequestCharacters = 4096;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task listener;
    private bool disposed;

    public LocalControlServer(Func<LocalControlRequest, CancellationToken, Task<LocalControlResponse>> handler,
        string? pipeName = null)
    {
        listener = ListenAsync(pipeName ?? PipeName(Environment.ProcessId), handler, cancellation.Token);
    }

    private static async Task ListenAsync(string name,
        Func<LocalControlRequest, CancellationToken, Task<LocalControlResponse>> handler, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 1024, true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true);
                LocalControlResponse response;
                try
                {
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
                    var request = JsonSerializer.Deserialize<LocalControlRequest>(text.ToString(), Json);
                    response = request is null || request.Protocol != 1 || request.ProcessId != Environment.ProcessId
                        ? new(false, "invalid_protocol_or_process")
                        : request.Command is not ("status" or "shutdown")
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
                // Never close the application before the caller has received its acknowledgement.
                response.AfterResponse?.Invoke();
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
        // A UI-thread handler may still be unwinding. Do not synchronously join it from OnExit.
        _ = listener.ContinueWith(_ => cancellation.Dispose(), TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        try { await listener.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}
