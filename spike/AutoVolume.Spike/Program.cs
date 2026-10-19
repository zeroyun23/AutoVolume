using System.Diagnostics;
using System.Globalization;
using AutoVolume.Audio;

namespace AutoVolume.Spike;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
        string mode = args.Length > 0 ? args[0] : "probe";

        return mode switch
        {
            "probe" => Probe(),
            "bench" => Bench(),
            "perf" => await Perf(),
            "endpoints" => Endpoints(),
            "switch" => Switch(args),
            "sleep" => SleepTest(),
            "smtc" => SmtcCmd(args),
            "keys" => Keys(args),
            "windows" => Windows(),
            "winprobe" => WinProbe(args),
            "shot" => Shot(args),
            "screencap" => ScreenCap(args),
            "uia" => Uia(args),
            "uiadump" => UiaDump(args),
            "uiarect" => UiaRect(args),
            "move" => Move(args),
            "cursor" => CursorPos(),
            "showdesktop" => ShowDesktop(),
            "iconout" => IconOut(args),
            "mkshortcut" => MkShortcut(args),
            "sessmute" => SessionMute(args),
            "monitor" => await Monitor(Parse(args)),
            "fade" => await Fade(Parse(args)),
            "interrupt" => await FadeInterrupt(),
            "volume" => VolumeRoundTrip(),
            _ => Usage()
        };
    }

    private static int Usage()
    {
        Console.WriteLine("""
            spike <mode> [options]
              probe                              打印一次会话/SMTC 列表
              monitor --out prefix --interval 50 --seconds 30
              fade --from 0.10 --to 0.15 --ms 300
              volume                             主音量读写往返
            """);
        return 1;
    }

    private sealed class Opt
    {
        public string Out = "spike";
        public int Interval = 50;
        public int Seconds = 30;
        public float From = 0.10f;
        public float To = 0.15f;
        public int Ms = 300;
    }

    private static Opt Parse(string[] args)
    {
        var o = new Opt();
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out": o.Out = args[++i]; break;
                case "--interval": o.Interval = int.Parse(args[++i]); break;
                case "--seconds": o.Seconds = int.Parse(args[++i]); break;
                case "--from": o.From = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--to": o.To = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--ms": o.Ms = int.Parse(args[++i]); break;
            }
        }
        return o;
    }

    private static int Probe()
    {
        using var probe = new WasapiSessionProbe();
        var snap = probe.Read();
        Console.WriteLine($"endpoint={snap.EndpointId} elapsed={snap.ElapsedMs}ms sessions={snap.Sessions.Count}");
        foreach (var s in snap.Sessions)
            Console.WriteLine($"  pid={s.Pid,-7} exe={s.Exe,-24} state={s.State,-8} peak={s.Peak:0.0000}");

        var smtc = new SmtcService();
        var ss = smtc.GetSnapshotAsync(includeProperties: true).GetAwaiter().GetResult();
        Console.WriteLine($"smtc healthy={ss.Healthy} count={ss.Sessions.Count}");
        foreach (var s in ss.Sessions)
            Console.WriteLine($"  aumid={s.Aumid,-50} status={s.Status,-8} title={s.Title}");

        using var vol = new EndpointVolumeController();
        Console.WriteLine($"volume={vol.GetMaster():0.0000} muted={vol.IsMuted()} endpoint={vol.EndpointId}");
        return 0;
    }

    private static async Task<int> Monitor(Opt o)
    {
        string sessPath = o.Out + "-sessions.csv";
        string smtcPath = o.Out + "-smtc.csv";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(sessPath))!);

        using var probe = new WasapiSessionProbe();
        var smtc = new SmtcService();
        using var vol = new EndpointVolumeController();
        var sw = Stopwatch.StartNew();

        await using var sw1 = new StreamWriter(sessPath, false, System.Text.Encoding.UTF8);
        await using var sw2 = new StreamWriter(smtcPath, false, System.Text.Encoding.UTF8);
        await sw1.WriteLineAsync("wall,ts_ms,volume,muted,endpoint,pid,exe,state,peak");
        await sw2.WriteLineAsync("wall,ts_ms,aumid,status,title");

        Console.WriteLine($"monitor → {sessPath}");
        Console.WriteLine($"          {smtcPath}");
        Console.WriteLine($"interval={o.Interval}ms seconds={o.Seconds} endpoint={vol.EndpointId} volume={vol.GetMaster():0.000}");
        if (File.Exists(o.Out + "-meta.txt")) File.Delete(o.Out + "-meta.txt");
        await File.WriteAllTextAsync(o.Out + "-meta.txt",
            $"start={DateTime.Now:O}\nendpoint={vol.EndpointId}\nvolume={vol.GetMaster():0.0000}\ninterval={o.Interval}\n");

        long lastSmtc = -10000;
        SmtcSnapshot ss = SmtcSnapshot.Unhealthy;

        while (sw.ElapsedMilliseconds < o.Seconds * 1000L)
        {
            long ts = sw.ElapsedMilliseconds;
            string wall = DateTime.Now.ToString("HH:mm:ss.fff");
            float v = vol.GetMaster();
            bool muted = vol.IsMuted();

            SessionSnapshot snap;
            try
            {
                snap = probe.Read();
            }
            catch (Exception ex)
            {
                await sw1.WriteLineAsync($"{wall},{ts},{v:0.000},{(muted ? 1 : 0)},{vol.EndpointId},-1,ERROR,{ex.GetType().Name},0");
                await Task.Delay(o.Interval);
                continue;
            }

            foreach (var s in snap.Sessions)
            {
                await sw1.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"{wall},{ts},{v:0.000},{(muted ? 1 : 0)},{snap.EndpointId},{s.Pid},{s.Exe},{s.State},{s.Peak:0.0000}"));
            }
            if (snap.Sessions.Count == 0)
                await sw1.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"{wall},{ts},{v:0.000},{(muted ? 1 : 0)},{snap.EndpointId},-1,NONE,None,0"));

            if (ts - lastSmtc >= 100)
            {
                ss = await smtc.GetSnapshotAsync(includeProperties: true);
                lastSmtc = ts;
                foreach (var s in ss.Sessions)
                {
                    await sw2.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                        $"{wall},{ts},{s.Aumid},{s.Status},{s.Title}"));
                }
                if (!ss.Healthy)
                    await sw2.WriteLineAsync($"{wall},{ts},<unhealthy>,Unknown,");
            }

            await Task.Delay(o.Interval);
        }

        Console.WriteLine($"done at ts={sw.ElapsedMilliseconds}ms wall={DateTime.Now:HH:mm:ss.fff}");
        return 0;
    }

    private static async Task<int> Fade(Opt o)
    {
        using var vol = new EndpointVolumeController();
        vol.SetMaster(o.From);
        await Task.Delay(200);

        Console.WriteLine($"endpoint={vol.EndpointId}");
        Console.WriteLine($"start={vol.GetMaster():0.0000} target={o.To:0.000} ms={o.Ms}");

        var sw = Stopwatch.StartNew();
        const int steps = 8;
        int delay = Math.Max(1, o.Ms / steps);
        var samples = new List<(long Ms, float V)>();

        for (int i = 1; i <= steps; i++)
        {
            double t = i / (double)steps;
            double eased = t * t * (3 - 2 * t);
            float v = (float)(o.From + (o.To - o.From) * eased);
            vol.SetMaster(v);
            samples.Add((sw.ElapsedMilliseconds, vol.GetMaster()));
            if (i < steps) await Task.Delay(delay);
        }
        vol.SetMaster(o.To);
        samples.Add((sw.ElapsedMilliseconds, vol.GetMaster()));

        foreach (var (ms, v) in samples) Console.WriteLine($"  t={ms,4}ms readback={v:0.0000}");
        Console.WriteLine($"final={vol.GetMaster():0.0000} target={o.To:0.000} error={Math.Abs(vol.GetMaster() - o.To):0.00000}");
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokPriv1Luid newState,
        int bufferLength, IntPtr prev, IntPtr retLen);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
    private struct TokPriv1Luid
    {
        public int Count;
        public long Luid;
        public int Attr;
    }

    private static bool EnableShutdownPrivilege()
    {
        const uint TOKEN_ADJUST_PRIVILEGES = 0x0020, TOKEN_QUERY = 0x0008, SE_PRIVILEGE_ENABLED = 0x0002;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token)) return false;
        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out long luid)) return false;
            var state = new TokPriv1Luid { Count = 1, Luid = luid, Attr = (int)SE_PRIVILEGE_ENABLED };
            return AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero)
                   && System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 0;
        }
        finally { CloseHandle(token); }
    }

    private static int SleepTest()
    {
        Console.WriteLine($"pre  ={DateTime.Now:HH:mm:ss.fff} privilege={EnableShutdownPrivilege()}");
        DateTime before = DateTime.Now;
        var sw = Stopwatch.StartNew();
        bool ok = SetSuspendState(false, false, false);
        int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        DateTime after = DateTime.Now;
        Console.WriteLine($"SetSuspendState={ok} err={err} returned={after:HH:mm:ss.fff} " +
                          $"wallGap={(after - before).TotalSeconds:0.0}s stopwatch={sw.ElapsedMilliseconds}ms");
        return ok ? 0 : 1;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [System.Runtime.InteropServices.FieldOffset(0)] public uint type;
        [System.Runtime.InteropServices.FieldOffset(8)] public KEYBDINPUT ki;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int max);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder text, int max);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static List<(IntPtr Handle, string Title, string Class)> TopWindows()
    {
        var list = new List<(IntPtr, string, string)>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var t = new System.Text.StringBuilder(256);
            var c = new System.Text.StringBuilder(256);
            GetWindowText(h, t, 256);
            GetClassName(h, c, 256);
            if (t.Length > 0) list.Add((h, t.ToString(), c.ToString()));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private static int ClickAt(int x, int y)
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { } // 否则 125% 缩放下坐标会被二次缩放
        SetCursorPos(x, y);
        Thread.Sleep(150);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); // LEFTDOWN
        Thread.Sleep(60);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); // LEFTUP
        Console.WriteLine($"click {x},{y}");
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>抓整块屏幕（含 ToolTip / 弹出层、托盘），用于验证弹窗类 UI。</summary>
    private static int ScreenCap(string[] args)
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
        string path = args.Length > 1 ? args[1] : "screen.png";
        int x = GetSystemMetrics(76), y = GetSystemMetrics(77);   // SM_XVIRTUALSCREEN / SM_YVIRTUALSCREEN
        int w = GetSystemMetrics(78), h = GetSystemMetrics(79);   // SM_CXVIRTUALSCREEN / SM_CYVIRTUALSCREEN
        if (w <= 0 || h <= 0) { Console.WriteLine("no screen"); return 1; }

        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(x, y, 0, 0, bmp.Size);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"screencap → {path} ({w}x{h})");
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

    private static int Shot(string[] args)
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { } // PER_MONITOR_AWARE_V2
        string title = args.Length > 1 ? args[1] : "AutoVolume";
        string path = args.Length > 2 ? args[2] : "shot.png";

        var w = TopWindows().FirstOrDefault(t => t.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
        if (w.Handle == IntPtr.Zero) { Console.WriteLine("no window"); return 1; }
        GetWindowRect(w.Handle, out RECT r);
        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        if (width <= 0 || height <= 0) return 1;

        using var bmp = new System.Drawing.Bitmap(width, height);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            PrintWindow(w.Handle, hdc, 2); // PW_RENDERFULLCONTENT
            g.ReleaseHdc(hdc);
        }
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"shot → {path} ({width}x{height})");
        return 0;
    }

    private static int Uia(string[] args)
    {
        string winTitle = args.Length > 1 ? args[1] : "AutoVolume";
        string target = args.Length > 2 ? args[2] : "保存";

        var win = System.Windows.Automation.AutomationElement.RootElement.FindFirst(
            System.Windows.Automation.TreeScope.Children,
            new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.NameProperty, winTitle));

        if (win is null)
        {
            Console.WriteLine($"未找到窗口：{winTitle}");
            return 1;
        }

        var cond = new System.Windows.Automation.PropertyCondition(
            System.Windows.Automation.AutomationElement.NameProperty, target);

        var el = win.FindFirst(System.Windows.Automation.TreeScope.Descendants, cond);
        if (el is null)
        {
            Console.WriteLine($"未找到控件：{target}");
            return 1;
        }

        string type = el.Current.ControlType.ProgrammaticName;
        try
        {
            if (el.TryGetCurrentPattern(System.Windows.Automation.TogglePattern.Pattern, out object? tp))
            {
                var toggle = (System.Windows.Automation.TogglePattern)tp;
                toggle.Toggle();
                Console.WriteLine($"uia toggle [{target}] {type} → {toggle.Current.ToggleState}");
                return 0;
            }
            if (el.TryGetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern, out object? ip))
            {
                ((System.Windows.Automation.InvokePattern)ip).Invoke();
                Console.WriteLine($"uia invoke [{target}] {type}");
                return 0;
            }
            if (el.TryGetCurrentPattern(System.Windows.Automation.SelectionItemPattern.Pattern, out object? sp))
            {
                ((System.Windows.Automation.SelectionItemPattern)sp).Select();
                Console.WriteLine($"uia select [{target}] {type}");
                return 0;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"pattern 失败：{ex.GetType().Name}，改用点击");
        }

        var r = el.Current.BoundingRectangle;
        Console.WriteLine($"uia click [{target}] {type}");
        return ClickAt((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
    }

    /// <summary>只读导出窗口内所有 UIA 元素（排查勾选/滑块状态用）。</summary>
    private static int UiaDump(string[] args)
    {
        string winTitle = args.Length > 1 ? args[1] : "AutoVolume";
        var win = System.Windows.Automation.AutomationElement.RootElement.FindFirst(
            System.Windows.Automation.TreeScope.Children,
            new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.NameProperty, winTitle));

        if (win is null)
        {
            Console.WriteLine($"未找到窗口：{winTitle}");
            return 1;
        }

        var all = win.FindAll(System.Windows.Automation.TreeScope.Descendants,
            System.Windows.Automation.Condition.TrueCondition);

        foreach (System.Windows.Automation.AutomationElement e in all)
        {
            try
            {
                string extra = "";
                if (e.TryGetCurrentPattern(System.Windows.Automation.TogglePattern.Pattern, out object? tp))
                    extra += "  toggle=" + ((System.Windows.Automation.TogglePattern)tp).Current.ToggleState;
                if (e.TryGetCurrentPattern(System.Windows.Automation.RangeValuePattern.Pattern, out object? rp))
                    extra += "  value=" + ((System.Windows.Automation.RangeValuePattern)rp).Current.Value.ToString("0.###");

                Console.WriteLine($"{e.Current.ControlType.ProgrammaticName}\t{e.Current.Name}\t{e.Current.AutomationId}{extra}");
            }
            catch { }
        }
        return 0;
    }

    /// <summary>打印控件屏幕坐标（配合 move 做悬停测试）。</summary>
    private static int UiaRect(string[] args)
    {
        string winTitle = args.Length > 1 ? args[1] : "AutoVolume";
        string target = args.Length > 2 ? args[2] : "";
        var win = System.Windows.Automation.AutomationElement.RootElement.FindFirst(
            System.Windows.Automation.TreeScope.Children,
            new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.NameProperty, winTitle));
        if (win is null) { Console.WriteLine($"未找到窗口：{winTitle}"); return 1; }

        var el = win.FindFirst(System.Windows.Automation.TreeScope.Descendants,
            new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.NameProperty, target));
        if (el is null) { Console.WriteLine($"未找到控件：{target}"); return 1; }

        var r = el.Current.BoundingRectangle;
        Console.WriteLine($"{(int)(r.Left + r.Width / 2)},{(int)(r.Top + r.Height / 2)}  rect={r.Left:0},{r.Top:0},{r.Width:0},{r.Height:0}");
        return 0;
    }

    private static int Move(string[] args)
    {
        if (args.Length < 3 || !int.TryParse(args[1], out int x) || !int.TryParse(args[2], out int y))
        {
            Console.WriteLine("用法：move <x> <y>");
            return 2;
        }
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
        SetCursorPos(x, y);
        Console.WriteLine($"move {x},{y}");
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);

    private static int IconOut(string[] args)
    {
        if (args.Length < 4) { Console.WriteLine("用法：iconout <exe> <size> <png>"); return 2; }
        string exe = args[1];
        int size = int.Parse(args[2]);
        var handles = new IntPtr[1];
        var ids = new uint[1];
        uint n = PrivateExtractIcons(exe, 0, size, size, handles, ids, 1, 0);
        if (n == 0) { Console.WriteLine("no icon"); return 1; }
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(handles[0]);
            using var bmp = icon.ToBitmap();
            bmp.Save(args[3], System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"icon {size} → {bmp.Width}x{bmp.Height} {args[3]}");
        }
        finally
        {
            if (handles[0] != IntPtr.Zero) DestroyIcon(handles[0]);
        }
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr h);

    /// <summary>用 Shell 链接对象重建快捷方式（目标/工作目录/图标一起改）。</summary>
    /// <summary>读某应用当前音频会话是否静音。</summary>
    private static int SessionMute(string[] args)
    {
        string exe = args.Length > 1 ? args[1] : "QQMusic.exe";
        var vc = new AutoVolume.Audio.SessionVolumeController();
        Console.WriteLine($"muted={vc.IsMuted(exe)} exe={exe}");
        return 0;
    }

    private static int MkShortcut(string[] args)
    {
        if (args.Length < 3) { Console.WriteLine("用法：mkshortcut <lnk> <target> [icon]"); return 2; }
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) { Console.WriteLine("no WScript.Shell"); return 1; }

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic lnk = shell.CreateShortcut(args[1]);
        lnk.TargetPath = args[2];
        lnk.WorkingDirectory = System.IO.Path.GetDirectoryName(args[2]) ?? "";
        lnk.IconLocation = args.Length > 3 ? args[3] : args[2] + ",0";
        lnk.Save();
        Console.WriteLine($"shortcut → {args[2]}");
        return 0;
    }

    private static int ShowDesktop()
    {
        keybd_event(0x5B, 0, 0, IntPtr.Zero); // LWIN
        keybd_event(0x44, 0, 0, IntPtr.Zero); // D
        keybd_event(0x44, 0, 2, IntPtr.Zero);
        keybd_event(0x5B, 0, 2, IntPtr.Zero);
        Console.WriteLine("showdesktop");
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private static int CursorPos()
    {
        GetCursorPos(out POINT p);
        Console.WriteLine($"cursor={p.X},{p.Y}");
        return 0;
    }

    private static int WinProbe(string[] args)
    {
        string exe = args.Length > 1 ? args[1] : "";
        var procs = System.Diagnostics.Process.GetProcessesByName(
            Path.GetFileNameWithoutExtension(exe));
        Console.WriteLine($"{exe}: processes={procs.Length}");
        foreach (var p in procs)
            Console.WriteLine($"  pid={p.Id} visibleWindow={WindowProbe.HasVisibleWindow(p.Id)} title=[{p.MainWindowTitle}]");
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static int CloseWindow(string title)
    {
        var w = TopWindows().FirstOrDefault(t => t.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
        if (w.Handle == IntPtr.Zero)
        {
            Console.WriteLine($"未找到窗口：{title}");
            return 1;
        }
        PostMessage(w.Handle, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
        Console.WriteLine($"close → [{w.Title}]");
        return 0;
    }

    private static int Windows()
    {
        foreach (var w in TopWindows())
            Console.WriteLine($"{w.Class,-32} {w.Title}");
        return 0;
    }

    private static int Keys(string[] args)
    {
        if (args.Length > 1 && args[1] == "wclick")
        {
            string title = args[2];
            int rx = int.Parse(args[3]), ry = int.Parse(args[4]);
            var w = TopWindows().FirstOrDefault(t => t.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
            if (w.Handle == IntPtr.Zero) { Console.WriteLine("no window"); return 1; }
            GetWindowRect(w.Handle, out RECT r);
            SwitchToThisWindow(w.Handle, true);
            SetForegroundWindow(w.Handle);
            Thread.Sleep(250);
            return ClickAt(r.Left + rx, r.Top + ry);
        }

        if (args.Length > 1 && args[1] == "click")
            return ClickAt(int.Parse(args[2]), int.Parse(args[3]));

        if (args.Length > 1 && args[1] == "close")
            return CloseWindow(args.Length > 2 ? args[2] : "设置");

        if (args.Length > 1 && args[1] == "rect")
        {
            var w = TopWindows().FirstOrDefault(t => t.Title.Contains(args[2], StringComparison.OrdinalIgnoreCase));
            if (w.Handle == IntPtr.Zero) { Console.WriteLine("no window"); return 1; }
            GetWindowRect(w.Handle, out RECT r);
            Console.WriteLine($"rect {r.Left},{r.Top},{r.Right},{r.Bottom} title=[{w.Title}]");
            return 0;
        }

        if (args.Length > 1 && args[1] == "focus")
        {
            string needle = args.Length > 2 ? args[2] : "artifacts";
            var target = TopWindows().FirstOrDefault(w =>
                w.Title.Contains(needle, StringComparison.OrdinalIgnoreCase));
            if (target.Handle == IntPtr.Zero)
            {
                Console.WriteLine($"未找到包含 \"{needle}\" 的窗口");
                return 1;
            }
            SwitchToThisWindow(target.Handle, true);
            SetForegroundWindow(target.Handle);
            Thread.Sleep(300);
            Console.WriteLine($"focus → [{target.Title}] class={target.Class}");
            if (args.Length > 3) return Keys(new[] { "keys", args[3] });
            return 0;
        }

        string combo = args.Length > 1 ? args[1].ToLowerInvariant() : "";
        const uint KEYUP = 0x0002;

        var seq = new List<(ushort vk, bool up)>();
        switch (combo)
        {
            case "ctrl+space":
                seq.Add((0x11, false)); seq.Add((0x20, false));
                seq.Add((0x20, true)); seq.Add((0x11, true));
                break;
            case "space":
                seq.Add((0x20, false)); seq.Add((0x20, true));
                break;
            case "esc":
                seq.Add((0x1B, false)); seq.Add((0x1B, true));
                break;
            case "home":
                seq.Add((0x24, false)); seq.Add((0x24, true));
                break;
            case "tab":
                seq.Add((0x09, false)); seq.Add((0x09, true));
                break;
            case "t":
                seq.Add((0x54, false)); seq.Add((0x54, true));
                break;
            case "ctrl+tab":
                seq.Add((0x11, false)); seq.Add((0x09, false));
                seq.Add((0x09, true)); seq.Add((0x11, true));
                break;
            case "down":
                seq.Add((0x28, false)); seq.Add((0x28, true));
                break;
            case "enter":
                seq.Add((0x0D, false)); seq.Add((0x0D, true));
                break;
            default:
                Console.WriteLine("keys ctrl+space|space|esc");
                return 2;
        }

        var inputs = seq.Select(k => new INPUT
        {
            type = 1, // INPUT_KEYBOARD
            ki = new KEYBDINPUT { wVk = k.vk, dwFlags = k.up ? KEYUP : 0 }
        }).ToArray();

        uint sent = SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
        Console.WriteLine($"SendInput {combo} → {sent}/{inputs.Length} err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        return sent == inputs.Length ? 0 : 1;
    }

    private static int SmtcCmd(string[] args)
    {
        var svc = new SmtcService();
        string cmd = args.Length > 1 ? args[1] : "list";
        string exe = args.Length > 2 ? args[2] : "";

        switch (cmd)
        {
            case "list":
            {
                var s = svc.GetSnapshotAsync(includeProperties: true).GetAwaiter().GetResult();
                Console.WriteLine($"healthy={s.Healthy} count={s.Sessions.Count}");
                foreach (var x in s.Sessions)
                    Console.WriteLine($"  {x.Aumid,-40} {x.Status,-8} {x.Title}");
                return 0;
            }
            case "play":
            {
                var (ok, st) = svc.TryPlayAsync(exe).GetAwaiter().GetResult();
                Console.WriteLine($"TryPlay {exe} → {ok} {st}");
                return ok ? 0 : 1;
            }
            case "pause":
            {
                var (ok, st) = svc.TryPauseAsync(exe).GetAwaiter().GetResult();
                Console.WriteLine($"TryPause {exe} → {ok} {st}");
                return ok ? 0 : 1;
            }
            default:
                Console.WriteLine("smtc list|play|pause [exe]");
                return 2;
        }
    }

    private static int Switch(string[] args)
    {
        string? id = null;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--id") id = args[i + 1];

        if (id is null)
        {
            Console.WriteLine("用法：switch --id {0.0.0.00000000}.{guid}");
            return 2;
        }

        bool ok = EndpointSwitcher.SetDefault(id);
        Console.WriteLine($"SetDefault {id} → {ok}");
        Thread.Sleep(600);

        using var probe = new WasapiSessionProbe();
        var snap = probe.Read();
        Console.WriteLine($"default now = {snap.EndpointId} sessions={snap.Sessions.Count}");
        foreach (var s in snap.Sessions) Console.WriteLine($"  {s.Exe} {s.State} {s.Peak:0.000}");
        return ok ? 0 : 1;
    }

    private static int Endpoints()
    {
        using var probe = new WasapiSessionProbe();
        foreach (var e in probe.ListRenderEndpoints())
            Console.WriteLine($"state={e.State,-2} name={e.Name,-40} id={e.Id}");
        return 0;
    }

    private static async Task<int> Perf()
    {
        using var probe = new WasapiSessionProbe();
        probe.ExeFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vlc.exe", "msedge.exe", "QQMusic.exe" };
        probe.Read();
        var smtc = new SmtcService();
        await smtc.GetSnapshotAsync();

        var cached = new List<double>();
        for (int i = 0; i < 300; i++) cached.Add(Time(() => probe.Read(false)));

        var procs = new List<double>();
        for (int i = 0; i < 100; i++)
            procs.Add(Time(() =>
            {
                _ = System.Diagnostics.Process.GetProcessesByName("vlc").Length
                    + System.Diagnostics.Process.GetProcessesByName("msedge").Length
                    + System.Diagnostics.Process.GetProcessesByName("PotPlayerMini64").Length;
            }));

        var sm = new List<double>();
        for (int i = 0; i < 40; i++) sm.Add(Time(() => smtc.GetSnapshotAsync().GetAwaiter().GetResult()));

        static double Time(Action a)
        {
            long t0 = Stopwatch.GetTimestamp();
            a();
            return (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        }
        static void Rep(string n, List<double> t)
        {
            t.Sort();
            Console.WriteLine($"{n,-12} avg={t.Average():0.000}ms p95={t[(int)(t.Count * 0.95)]:0.000}ms");
        }

        Rep("probe-cached", cached);
        Rep("procsByName3", procs);
        Rep("smtc", sm);
        Console.WriteLine($"idle 2Hz: probe {cached.Average() * 2:0.00} + procs {procs.Average() * 2:0.00} + smtc {sm.Average() * 5:0.00} ms/s");
        return 0;
    }

    private static int Bench()
    {
        using var probe = new WasapiSessionProbe();
        probe.ExeFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "vlc.exe", "msedge.exe", "QQMusic.exe" };
        probe.Read(); // 预热：进程名解析会进缓存

        var cached = new List<double>(200);
        var refresh = new List<double>(20);
        var sw = Stopwatch.StartNew();
        int sessions = 0;

        for (int i = 0; i < 220; i++)
        {
            bool reb = i % 20 == 0;
            long t0 = sw.ElapsedTicks;
            var snap = probe.Read(reb);
            double ms = (sw.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;
            (reb ? refresh : cached).Add(ms);
            sessions = snap.Sessions.Count;
        }

        static void Report(string name, List<double> t)
        {
            t.Sort();
            Console.WriteLine($"{name,-8} n={t.Count,-4} avg={t.Average():0.000}ms p50={t[t.Count / 2]:0.000}ms " +
                              $"p95={t[(int)(t.Count * 0.95)]:0.000}ms max={t[^1]:0.000}ms");
        }

        Console.WriteLine($"sessions={sessions} endpoint={probe.EndpointId}");
        Report("refresh", refresh);
        Report("cached", cached);
        double perSecond = cached.Average() * 20 + refresh.Average() * 1;
        Console.WriteLine($"模拟 20Hz 读 + 1Hz 重建 → {perSecond:0.00}ms/s = {perSecond / 10:0.000}% 单核");
        return 0;
    }

    private static async Task<int> FadeInterrupt()
    {
        using var vol = new EndpointVolumeController();
        vol.SetMaster(0.10f);
        await Task.Delay(200);

        Console.WriteLine($"start={vol.GetMaster():0.0000} target=0.20 ms=600（200ms 处模拟用户拖到 0.35）");
        float last = vol.GetMaster();
        int delay = 600 / 8;
        bool aborted = false;

        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            vol.SetMaster(0.35f);
            Console.WriteLine($"  [外部写入] t≈200ms → 0.35, read={vol.GetMaster():0.0000}");
        });

        var sw = Stopwatch.StartNew();
        for (int i = 1; i <= 8; i++)
        {
            if (i > 1)
            {
                float now = vol.GetMaster();
                if (Math.Abs(now - last) > 0.02f)
                {
                    Console.WriteLine($"  [中止] t={sw.ElapsedMilliseconds}ms 当前={now:0.0000} 期望={last:0.0000}");
                    aborted = true;
                    break;
                }
            }
            double t = i / 8.0;
            double eased = t * t * (3 - 2 * t);
            float v = (float)(0.10 + 0.10 * eased);
            vol.SetMaster(v);
            last = vol.GetMaster();
            Console.WriteLine($"  step{i} t={sw.ElapsedMilliseconds}ms write={v:0.0000} read={last:0.0000}");
            await Task.Delay(delay);
        }

        Console.WriteLine($"aborted={aborted} final={vol.GetMaster():0.0000}");
        vol.SetMaster(0.10f);
        return 0;
    }

    private static int VolumeRoundTrip()
    {
        using var vol = new EndpointVolumeController();
        float original = vol.GetMaster();
        Console.WriteLine($"endpoint={vol.EndpointId}");
        Console.WriteLine($"original={original:0.0000} muted={vol.IsMuted()}");

        vol.SetMaster(0.15f);
        Thread.Sleep(100);
        float read1 = vol.GetMaster();
        Console.WriteLine($"after set 0.15 → read={read1:0.0000}");

        vol.SetMaster(original);
        Thread.Sleep(100);
        Console.WriteLine($"restored → read={vol.GetMaster():0.0000}");
        return 0;
    }
}
