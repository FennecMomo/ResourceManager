namespace ResourceManager.Core;

public sealed record AiSearchScope(string Id, string DeviceId, string Directory, bool AutoApprove);
public sealed record AiFoundFile(string Id, string RelativePath, long Size, string? MessageId = null, string? ResourceId = null);
public sealed record AiSearchRequest(string Id, string Question, string? ScopeId = null);
public sealed record AiSearchAction(string Action, string? Text = null, string? FileId = null);
public sealed record AiSearchSession(string Id, string DeviceId, string Question, string State, DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc, string? ScopeId = null, string? Directory = null, string? Answer = null,
    AiFoundFile[]? Files = null, int Turns = 0, string? Error = null);
public sealed record AiSearchView(string Id, string State, string Question, DateTimeOffset ExpiresUtc,
    string? Answer, AiFoundFile[] Files, int Turns, string? Error);
public sealed record AiFinderResult(string Answer, string[] RelativePaths);

public interface IAiFileFinder
{
    Task<AiFinderResult> FindAsync(string executable, string directory, string question, string? previousAnswer,
        CancellationToken token);
}

public sealed partial class NodeStore
{
    public string GetAiCodexExecutable() => GetSetting("ai_codex_executable") ?? "";
    public void SetAiCodexExecutable(string executable)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable) || !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择已安装的 Codex CLI 可执行文件（codex.exe）。");
        SetSetting("ai_codex_executable", executable);
    }
    public AiSearchScope[] GetAiSearchScopes()
    {
        lock (gate) return System.Text.Json.JsonSerializer.Deserialize<AiSearchScope[]>(GetSetting("ai_search_scopes_v1") ?? "[]")!;
    }
    public AiSearchScope SaveAiSearchScope(string deviceId, string directory, bool autoApprove)
    {
        if (GetPeer(deviceId) is null) throw new ArgumentException("设备不存在。");
        directory = ScopedAiFiles.ValidateRoot(directory);
        lock (gate)
        {
            var scopes = GetAiSearchScopes().ToList();
            var previous = scopes.SingleOrDefault(s => s.DeviceId == deviceId && s.Directory.Equals(directory, StringComparison.OrdinalIgnoreCase));
            var scope = new AiSearchScope(previous?.Id ?? Guid.NewGuid().ToString("N"), deviceId, directory, autoApprove);
            scopes.RemoveAll(s => s.Id == scope.Id);
            if (scopes.Count >= 100) throw new InvalidOperationException("最多配置 100 条目录授权。");
            scopes.Add(scope);
            SetSetting("ai_search_scopes_v1", System.Text.Json.JsonSerializer.Serialize(scopes));
            return scope;
        }
    }
    public void RemoveAiSearchScope(string id)
    {
        lock (gate) SetSetting("ai_search_scopes_v1", System.Text.Json.JsonSerializer.Serialize(GetAiSearchScopes().Where(s => s.Id != id)));
    }
    public AiSearchSession[] GetAiSearchSessions()
    {
        lock (gate) return System.Text.Json.JsonSerializer.Deserialize<AiSearchSession[]>(GetSetting("ai_search_sessions_v1") ?? "[]")!;
    }
    internal void SaveAiSearchSession(AiSearchSession session)
    {
        lock (gate)
        {
            var sessions = GetAiSearchSessions().Where(s => s.Id != session.Id).Append(session)
                .OrderByDescending(s => s.CreatedUtc).Take(100).ToArray();
            SetSetting("ai_search_sessions_v1", System.Text.Json.JsonSerializer.Serialize(sessions));
        }
    }
}
