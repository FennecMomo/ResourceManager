using System.IO;
using System.IO.Pipes;

namespace ResourceManager.App;

/// <summary>
/// Prevents two interactive ResourceManager processes from opening in the same Windows session
/// and provides a small local channel for bringing the primary window to the foreground.
/// </summary>
internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string InstanceName = @"Local\FennecMomo.ResourceManager.SingleInstance.v1";
    private const string PipeName = "FennecMomo.ResourceManager.SingleInstance.v1";
    private readonly Mutex mutex;
    private readonly CancellationTokenSource cancellation = new();
    private readonly bool ownsMutex;
    private Task? listener;
    private bool disposed;

    public SingleInstanceCoordinator()
    {
        mutex = new Mutex(true, InstanceName, out ownsMutex);
    }

    public bool IsPrimary => ownsMutex;

    public static async Task<bool> WaitForPrimaryExitAsync(TimeSpan timeout)
    {
        return await Task.Run(() =>
        {
            Mutex? existing = null;
            var acquired = false;
            try
            {
                existing = Mutex.OpenExisting(InstanceName);
                try { acquired = existing.WaitOne(timeout); }
                catch (AbandonedMutexException) { acquired = true; }
                return acquired;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return true;
            }
            finally
            {
                if (acquired) existing?.ReleaseMutex();
                existing?.Dispose();
            }
        }).ConfigureAwait(false);
    }

    public void StartListening(Action activate)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!ownsMutex) throw new InvalidOperationException("只有主实例可以监听唤醒请求。");
        if (listener is not null) throw new InvalidOperationException("唤醒监听已经启动。");

        listener = ListenAsync(activate, cancellation.Token);
    }

    public static async Task<bool> ActivatePrimaryAsync(CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var pipe = new NamedPipeClientStream(
                    ".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(350, cancellationToken).ConfigureAwait(false);
                await using var writer = new StreamWriter(pipe) { AutoFlush = true };
                await writer.WriteLineAsync("activate".AsMemory(), cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException) { }
            catch (IOException) { }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task ListenAsync(Action activate, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(pipe);
                var command = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.Equals(command, "activate", StringComparison.Ordinal)) activate();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cancellation.Cancel();
        try { listener?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        cancellation.Dispose();
        if (ownsMutex) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
