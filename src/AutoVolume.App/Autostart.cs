using System.IO;
using Microsoft.Win32;

namespace AutoVolume.App;

/// <summary>
/// 开机自启（HKCU Run）。开发构建不写注册表，避免调试版劫持自启。
/// 注册命令固定带 <c>--autostart</c>，否则开机时会弹出设置窗口。
/// </summary>
public static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AutoVolume";

    /// <summary>注册路径必须指向当前 exe；exe 被移动后应视为未启用。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (key?.GetValue(ValueName) is not string command || command.Length == 0) return false;

            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return true;

            string registered = ExtractExe(command);
            return string.Equals(Path.GetFullPath(registered), Path.GetFullPath(exe),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled, bool dev)
    {
#if DEBUG
        _ = enabled;
        _ = dev;
        return;
#else
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
            {
                string exe = Environment.ProcessPath ?? "";
                // --autostart：启动进托盘，不弹设置页
                string args = dev ? " --dev --autostart" : " --autostart";
                key.SetValue(ValueName, $"\"{exe}\"{args}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch { }
#endif
    }

    /// <summary>从 Run 值里取出 exe 路径，兼容带引号与不带引号两种写法。</summary>
    private static string ExtractExe(string command)
    {
        string value = command.Trim();
        if (value.StartsWith("\"", StringComparison.Ordinal))
        {
            int end = value.IndexOf('"', 1);
            return end > 0 ? value[1..end] : value.Trim('"');
        }

        int space = value.IndexOf(' ');
        return space > 0 ? value[..space] : value;
    }
}
