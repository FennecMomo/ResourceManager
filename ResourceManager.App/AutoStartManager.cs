using System.IO;
using Microsoft.Win32;

namespace ResourceManager.App;

public static class AutoStartManager
{
    internal const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "ResourceManager";

    public static string Command => CommandFor(Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径。"));

    private static string CommandFor(string executablePath) => $"\"{Path.GetFullPath(executablePath)}\" --startup";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static void SetEnabled(bool enabled, string? executablePath = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        if (enabled) key.SetValue(ValueName, CommandFor(executablePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径。")), RegistryValueKind.String);
        else key.DeleteValue(ValueName, false);
    }

    public static void RefreshEnabledPath()
    {
        if (!IsEnabled()) return;
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, false);
        if (!string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.Ordinal)) SetEnabled(true);
    }
}
