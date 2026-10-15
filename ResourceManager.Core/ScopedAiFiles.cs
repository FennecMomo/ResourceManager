using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.Core;

/// <summary>All model-requested file access goes through this read-only directory boundary.</summary>
public sealed class ScopedAiFiles
{
    public string Root { get; }
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".codex", ".ssh", ".aws", ".azure", "node_modules", ".env" };
    public ScopedAiFiles(string root) => Root = ValidateRoot(root);
    public static string ValidateRoot(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("授权目录必须是绝对路径。");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(root) || root == Path.GetPathRoot(root)) throw new ArgumentException("请选择存在的具体文件夹，不能授权整个磁盘。");
        CheckAncestors(root);
        return root;
    }
    private static void CheckAncestors(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            var name = Path.GetFileName(current);
            if (Excluded.Contains(name) || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("不读取工具配置、凭据或 Git 元数据。");
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("授权范围不允许链接或重解析路径。");
        }
    }
    public string Resolve(string relative)
    {
        if (!StoredPaths.Valid(relative) || Path.IsPathFullyQualified(relative)) throw new UnauthorizedAccessException("文件路径超出授权范围。");
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(p => Excluded.Contains(p) || p.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("不读取工具配置、凭据或 Git 元数据。");
        var path = Path.GetFullPath(Path.Combine(Root, relative));
        if (!StorageLocation.IsWithin(path, Root) || !File.Exists(path)) throw new FileNotFoundException("文件不存在或超出授权目录。");
        CheckAncestors(path);
        return path;
    }
    public FileStream OpenRead(string relative)
    {
        var path = Resolve(relative);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var buffer = new StringBuilder(32768);
                var length = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity) throw new IOException("无法校验文件的真实路径。");
                var final = buffer.ToString();
                if (final.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) final = "\\\\" + final[8..];
                else if (final.StartsWith("\\\\?\\", StringComparison.Ordinal)) final = final[4..];
                if (!StorageLocation.IsWithin(final, Root)) throw new UnauthorizedAccessException("文件真实路径超出授权目录。");
            }
            CheckAncestors(path);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }
    public IReadOnlyList<string> List(CancellationToken token, int limit = 5000)
    {
        var result = new List<string>();
        var directories = new Stack<string>(); directories.Push(Root);
        var visited = 0;
        while (directories.Count > 0 && result.Count < limit && visited++ < 10000)
        {
            token.ThrowIfCancellationRequested();
            var directory = directories.Pop();
            try
            {
                CheckAncestors(directory);
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(path);
                    if (Excluded.Contains(name) || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)) continue;
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0) directories.Push(path);
                    else result.Add(Path.GetRelativePath(Root, path).Replace('\\', '/'));
                    if (result.Count >= limit) break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }
    public string ReadExcerpt(string relative)
    {
        using var stream = OpenRead(relative);
        var bytes = new byte[Math.Min(stream.Length, 16384)];
        var count = stream.Read(bytes);
        if (bytes.Take(count).Contains((byte)0)) return "[二进制文件：仅按名称和路径判断，未解析内容]";
        return Encoding.UTF8.GetString(bytes, 0, count);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, int size, uint flags);
}
