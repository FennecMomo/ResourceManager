using System.Net;
using System.Text.Json;

namespace ResourceManager.Server;

public static class ServerConfiguration
{
    public static ServerRuntimeOptions Load(string[] args, string version)
    {
        var cli = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = new[] { "config", "data-dir", "server-name", "api-port", "admin-port", "discovery-port", "api-address", "max-devices", "discovery-enabled", "upload-dir", "max-capacity-bytes", "max-file-bytes" };
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) { if (i == 0 && args[i] is "check-config" or "doctor" or "migrate") continue; throw new ArgumentException("未知命令。"); }
            var key = args[i][2..];
            if (!names.Contains(key) || i + 1 >= args.Length || args[i + 1].StartsWith("--")) throw new ArgumentException("无效的配置选项：" + args[i]);
            cli[key] = args[++i];
        }
        var path = cli.GetValueOrDefault("config") ?? Environment.GetEnvironmentVariable("RM_CONFIG") ??
            (OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, "server.json") : "/etc/resourcemanager/server.json");
        var config = new Dictionary<string, string>();
        if (File.Exists(path))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("配置文件须为 JSON 对象。");
            foreach (var property in document.RootElement.EnumerateObject())
            { if (!names.Contains(property.Name) || property.Name == "config") throw new ArgumentException("配置文件含未知选项：" + property.Name); config[property.Name] = property.Value.ToString(); }
        }
        else if (cli.ContainsKey("config") || Environment.GetEnvironmentVariable("RM_CONFIG") is not null) throw new ArgumentException("指定配置文件不存在。");
        string Value(string key, string fallback) => cli.GetValueOrDefault(key) ?? Environment.GetEnvironmentVariable("RM_" + key.Replace('-', '_').ToUpperInvariant()) ?? config.GetValueOrDefault(key) ?? fallback;
        int Number(string key, int fallback, int max = 65535) => int.TryParse(Value(key, fallback.ToString()), out var value) && value >= 1 && value <= max ? value : throw new ArgumentException("配置数值无效：" + key);
        long Bytes(string key, long fallback) => long.TryParse(Value(key, fallback.ToString()), out var value) && value >= 0 ? value : throw new ArgumentException("容量配置无效：" + key);
        var data = Path.GetFullPath(Value("data-dir", OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResourceManager.Server") : "/var/lib/resourcemanager"));
        var name = Value("server-name", Environment.MachineName).Trim(); if (name.Length is < 1 or > 80) throw new ArgumentException("服务器名称须为 1–80 个字符。");
        var address = Value("api-address", "0.0.0.0"); if (!IPAddress.TryParse(address, out _)) throw new ArgumentException("api-address 须为监听 IP。");
        if (!bool.TryParse(Value("discovery-enabled", "true"), out var discovery)) throw new ArgumentException("discovery-enabled 须为 true 或 false。");
        var result = new ServerRuntimeOptions(data, name, Number("api-port", ResourceManager.Core.FeedbackRules.DefaultApiPort), Number("discovery-port", ResourceManager.Core.FeedbackRules.DefaultDiscoveryPort), Number("admin-port", 37646), version,
            address, Number("max-devices", 256, 1000), discovery, Path.GetFullPath(Value("upload-dir", Path.Combine(data, "uploads"))), Bytes("max-capacity-bytes", 0), Bytes("max-file-bytes", 0));
        if (result.ApiPort == result.AdminPort) throw new ArgumentException("API 与管理端口不能相同。");
        return result;
    }
    public static int RunCommand(string command, ServerRuntimeOptions options)
    {
        try
        {
            if (command == "doctor")
            {
                foreach (var directory in new[] { options.DataDirectory, options.UploadDirectory ?? Path.Combine(options.DataDirectory, "uploads") })
                {
                    Directory.CreateDirectory(directory);
                    var probe = Path.Combine(directory, ".doctor-" + Guid.NewGuid().ToString("N"));
                    try { File.WriteAllText(probe, "probe"); } finally { if (File.Exists(probe)) File.Delete(probe); }
                }
            }
            if (command == "migrate") { var store = new ServerStore(options.DataDirectory, options.ServerName); using var hub = new WorkspaceHub(store, options); }
            Console.WriteLine(JsonSerializer.Serialize(new { ok = true, command, code = "OK", dataDirectory = options.DataDirectory, options.ApiPort, options.AdminPort, options.Version })); return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        { Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, command, code = "STORAGE_ERROR", error = ex.GetType().Name })); return 3; }
    }
}
