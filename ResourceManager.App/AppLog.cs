using System.IO;
using System.Text;
using ResourceManager.Core;

namespace ResourceManager.App;

internal static class AppLog
{
    private static readonly object Gate = new();

    public static void Write(string context, Exception exception)
    {
        try
        {
            var directory = Path.Combine(NodeDefaults.DataDirectory, "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"ResourceManager-{DateTime.Now:yyyyMMdd}.log");
            var entry = $"[{DateTimeOffset.Now:O}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
            lock (Gate) File.AppendAllText(path, entry, new UTF8Encoding(false));
        }
        catch
        {
            // Diagnostics must never terminate the application.
        }
    }
}
