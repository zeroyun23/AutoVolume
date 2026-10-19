using System.Runtime.InteropServices;

namespace AutoVolume.Audio;

/// <summary>
/// 进程是否有可见顶层窗口。用于"进程常驻但窗口关掉就算结束"的源（PowerToys 速览）。
/// </summary>
public static class WindowProbe
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    public static bool HasVisibleWindow(int pid)
    {
        if (pid <= 0) return false;

        bool found = false;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint p);
            if (p != (uint)pid) return true;
            if (!IsWindowVisible(h)) return true;
            if (!GetWindowRect(h, out RECT r)) return true;
            if (r.Right - r.Left < 8 || r.Bottom - r.Top < 8) return true;
            found = true;
            return false; // 找到一个就停
        }, IntPtr.Zero);

        return found;
    }
}
