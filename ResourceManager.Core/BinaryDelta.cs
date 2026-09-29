using System.Diagnostics;
using System.Reflection;

namespace ResourceManager.Core;

public static class BinaryDelta
{
    private const string ResourceName = "ResourceManager.Xdelta3.exe";

    public static Task CreateAsync(string source, string target, string patch, CancellationToken token = default) =>
        RunAsync(["-9", "-S", "lzma", "-e", "-f", "-s", source, target, patch], token);

    public static Task ApplyAsync(string source, string patch, string target, CancellationToken token = default) =>
        RunAsync(["-d", "-f", "-s", source, patch, target], token);

    private static async Task RunAsync(string[] arguments, CancellationToken token)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ResourceManager-delta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var tool = Path.Combine(directory, "xdelta3.exe");
            await using (var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException("差分更新组件缺失。"))
            await using (var output = new FileStream(tool, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await embedded.CopyToAsync(output, token).ConfigureAwait(false);

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo(tool)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) throw new IOException("无法启动差分更新组件。");
            var error = process.StandardError.ReadToEndAsync(token);
            var outputText = process.StandardOutput.ReadToEndAsync(token);
            try { await process.WaitForExitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                throw;
            }
            var details = (await error.ConfigureAwait(false) + await outputText.ConfigureAwait(false)).Trim();
            if (process.ExitCode != 0)
                throw new InvalidDataException("差分合成失败：" + (details.Length > 300 ? details[..300] : details));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
