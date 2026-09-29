using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ResourceManager.Core;

public sealed record UpdateTransferProgress(string Method, long Bytes, long Total);
public sealed record UpdateDownloadResult(string Path, string Method, string? FallbackReason = null);
public sealed record DeltaDownloadAttempt(string? Path, string? Reason = null);
public sealed record AppDeltaPatch(Version FromVersion, Version ToVersion, string FromSha256,
    string ToSha256, long ToSize, long PatchSize, string PatchSha256, Uri DownloadUrl);

public sealed partial class UpdateClient
{
    public async Task<UpdateDownloadResult> DownloadPreferredAsync(AppUpdate update, string currentExecutable,
        Version currentVersion, IProgress<UpdateTransferProgress>? progress = null, CancellationToken token = default)
    {
        var attempt = await TryDownloadDeltaAsync(update, currentExecutable, currentVersion, progress, token)
            .ConfigureAwait(false);
        if (attempt.Path is not null) return new UpdateDownloadResult(attempt.Path, "差分更新");
        var fullProgress = progress is null ? null : new Progress<long>(bytes =>
            progress.Report(new UpdateTransferProgress("完整更新", bytes, update.Size)));
        var full = await DownloadAsync(update, fullProgress, token).ConfigureAwait(false);
        return new UpdateDownloadResult(full, "完整更新", attempt.Reason);
    }

    public async Task<DeltaDownloadAttempt> TryDownloadDeltaAsync(AppUpdate update, string currentExecutable,
        Version currentVersion, IProgress<UpdateTransferProgress>? progress = null, CancellationToken token = default)
    {
        if (update.DeltaManifestUrl is null || !File.Exists(currentExecutable)) return new(null);
        try
        {
            var delta = await GetDeltaPatchAsync(update, token).ConfigureAwait(false);
            if (delta is null || delta.FromVersion != currentVersion) return new(null);
            if (await HashFileAsync(currentExecutable, token) != delta.FromSha256)
                return new(null, "当前程序与差分包要求的旧版本哈希不符。");
            var result = await DownloadDeltaAsync(update, delta, currentExecutable, progress, token)
                .ConfigureAwait(false);
            return new(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or
                                   UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException or
                                   InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or
                                   OperationCanceledException)
        { return new(null, ex.Message); }
    }

    private async Task<AppDeltaPatch?> GetDeltaPatchAsync(AppUpdate update, CancellationToken token)
    {
        if (update.DeltaManifestUrl is null || update.DeltaManifestSha256 is null ||
            update.DeltaManifestSize is <= 0 or > MaxDeltaManifestBytes) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = CreateRequest(update.DeltaManifestUrl);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength && contentLength != update.DeltaManifestSize)
            throw new InvalidDataException("差分清单大小不符。");
        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await source.ReadAsync(chunk, timeout.Token)) != 0)
        {
            if (buffer.Length + read > MaxDeltaManifestBytes) throw new InvalidDataException("差分清单过大。");
            await buffer.WriteAsync(chunk.AsMemory(0, read), timeout.Token);
        }
        var data = buffer.ToArray();
        if (data.Length != update.DeltaManifestSize ||
            Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() != update.DeltaManifestSha256)
            throw new InvalidDataException("差分清单校验失败。");
        return ParseDeltaManifest(data, update);
    }

    public static AppDeltaPatch ParseDeltaManifest(ReadOnlySpan<byte> json, AppUpdate update)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var root = document.RootElement;
        if (root.GetProperty("schema").GetInt32() != 1 ||
            root.GetProperty("algorithm").GetString() != "xdelta3-3.2.1-lzma")
            throw new InvalidDataException("不支持的差分清单格式。");
        var from = ParseVersion(root.GetProperty("fromVersion").GetString() ?? "");
        var to = ParseVersion(root.GetProperty("toVersion").GetString() ?? "");
        var fromHash = root.GetProperty("fromSha256").GetString() ?? "";
        var toHash = root.GetProperty("toSha256").GetString() ?? "";
        var patchHash = root.GetProperty("patchSha256").GetString() ?? "";
        var toSize = root.GetProperty("toSize").GetInt64();
        var patchSize = root.GetProperty("patchSize").GetInt64();
        if (from >= to || to != update.Version || toSize != update.Size ||
            !toHash.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(fromHash, "^[0-9a-fA-F]{64}$") ||
            !Regex.IsMatch(patchHash, "^[0-9a-fA-F]{64}$") ||
            patchSize <= 0 || patchSize > MaxPackageBytes || patchSize >= update.Size * 0.6)
            throw new InvalidDataException("差分清单与正式更新包不匹配。");
        var patchName = $"ResourceManager-v{from}-to-v{to}.xdelta";
        var expectedUrl = new Uri($"https://github.com/FennecMomo/ResourceManager/releases/download/v{to}/{patchName}");
        if (root.GetProperty("patchUrl").GetString() != expectedUrl.AbsoluteUri)
            throw new InvalidDataException("差分包地址无效。");
        return new AppDeltaPatch(from, to, fromHash.ToLowerInvariant(), toHash.ToLowerInvariant(),
            toSize, patchSize, patchHash.ToLowerInvariant(), expectedUrl);
    }

    private async Task<string> DownloadDeltaAsync(AppUpdate update, AppDeltaPatch delta,
        string currentExecutable, IProgress<UpdateTransferProgress>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(updateDirectory);
        var destination = Path.Combine(updateDirectory, $"ResourceManager-{update.Version}-{update.Sha256[..12]}.exe");
        if (File.Exists(destination) && new FileInfo(destination).Length == update.Size &&
            await HashFileAsync(destination, token) == update.Sha256)
        {
            ValidatePatchedExecutable(destination, update.Version);
            return destination;
        }
        var patchPath = Path.Combine(updateDirectory,
            $"ResourceManager-{delta.FromVersion}-to-{delta.ToVersion}-{delta.PatchSha256[..12]}.xdelta");
        var patchProgress = progress is null ? null : new Progress<long>(bytes =>
            progress.Report(new UpdateTransferProgress("差分更新", bytes, delta.PatchSize)));
        await DownloadVerifiedAsync(patchPath, delta.PatchSize, delta.PatchSha256,
            async (offset, cancellation) =>
            {
                using var request = CreateRequest(delta.DownloadUrl);
                if (offset > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
                return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation)
                    .ConfigureAwait(false);
            }, patchProgress, requireExecutable: false, token).ConfigureAwait(false);
        StorageLocation.EnsureSpace(updateDirectory, delta.ToSize);
        var temporary = destination + ".reconstructed";
        try
        {
            await BinaryDelta.ApplyAsync(currentExecutable, patchPath, temporary, token).ConfigureAwait(false);
            if (new FileInfo(temporary).Length != delta.ToSize ||
                await HashFileAsync(temporary, token) != delta.ToSha256)
                throw new InvalidDataException("差分生成的新程序校验失败。");
            ValidatePatchedExecutable(temporary, update.Version);
            File.Move(temporary, destination, true);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ValidatePatchedExecutable(string path, Version expectedVersion)
    {
        VerifyExecutableHeader(path, true);
        var versionInfo = FileVersionInfo.GetVersionInfo(path);
        if (new Version(versionInfo.FileMajorPart, versionInfo.FileMinorPart, versionInfo.FileBuildPart) != expectedVersion)
            throw new InvalidDataException("差分生成的新程序版本不符。");
    }
}
