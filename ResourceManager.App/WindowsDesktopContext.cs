using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ResourceManager.Core;

namespace ResourceManager.App;

/// <summary>
/// A process launched by an MSIX host can inherit file virtualization without package identity.
/// Resolve an actual file handle before reading profile configuration; broker a relaunch through
/// the existing Explorer desktop when AppData is redirected into the host's private cache.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsDesktopContext
{
    internal const string RelaunchArgument = "--native-desktop-context";

    internal static bool EnsureNativeDesktop(string[] arguments)
    {
        var directory = Path.GetDirectoryName(StorageLocation.DefaultConfigurationPath)!;
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(directory, ".context-probe-" + Guid.NewGuid().ToString("N"));
        string actual;
        using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite,
                   FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.DeleteOnClose))
            actual = GetActualPath(stream.SafeFileHandle);
        if (!IsHostRedirected(probe, actual)) return false;
        if (arguments.Contains(RelaunchArgument, StringComparer.Ordinal))
            throw new IOException("启动环境仍在重定向程序资料。为保护已有记录，已停止启动。请从 Windows 文件资源管理器直接打开程序。");
        LaunchThroughDesktop([.. arguments, RelaunchArgument]);
        return true;
    }

    internal static bool IsHostRedirected(string requested, string actual)
    {
        actual = StripExtendedPrefix(actual);
        requested = StripExtendedPrefix(requested);
        return !requested.Equals(actual, StringComparison.OrdinalIgnoreCase) &&
            actual.Contains(@"\AppData\Local\Packages\", StringComparison.OrdinalIgnoreCase) &&
            actual.Contains(@"\LocalCache\", StringComparison.OrdinalIgnoreCase);
    }

    internal static string StripExtendedPrefix(string path) => path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
        ? @"\\" + path[8..] : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;

    internal static string GetActualPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        return StripExtendedPrefix(buffer.ToString());
    }

    private static void LaunchThroughDesktop(string[] arguments)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("无法确定程序启动路径。");
        var forwarded = new List<string>();
#if !PUBLISHED_SINGLE_FILE
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            forwarded.Add(Assembly.GetExecutingAssembly().Location);
#endif
        forwarded.AddRange(arguments);
        object? shell = null, windows = null, desktop = null, document = null, application = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")
                ?? throw new IOException("Windows 桌面不可用。"));
            windows = ((dynamic)shell!).Windows();
            object location = 0, root = 0;
            int hwnd;
            desktop = ((dynamic)windows).FindWindowSW(ref location, ref root, 8, out hwnd, 1);
            if (desktop is null) throw new IOException("无法连接 Windows 桌面，请从文件资源管理器打开程序。");
            document = ((dynamic)desktop).Document;
            application = ((dynamic)document).Application;
            ((dynamic)application).ShellExecute(executable, string.Join(" ", forwarded.Select(QuoteArgument)),
                AppContext.BaseDirectory, "open", 1);
        }
        finally
        {
            foreach (var item in new[] { application, document, desktop, windows, shell })
                if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
        }
    }

    internal static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { backslashes++; continue; }
            result.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
            result.Append(character);
            backslashes = 0;
        }
        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
}
