using System.IO;
using System.Windows;
using AutoVolume.Core;
using AutoVolume.Runtime;

namespace AutoVolume.App;

public partial class App : System.Windows.Application
{
    public static App Instance => (App)Current;

    public AutoVolumeRuntime Runtime { get; private set; } = null!;
    public bool IsDev { get; private set; }

    private SingleInstance? _single;
    private TrayIcon? _tray;
    private SettingsWindow? _settings;
    private LogFile _log = null!;
    /// <summary>已进入退出流程：此后的异常只记日志，不弹模态框。</summary>
    private volatile bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool dev = e.Args.Any(a => string.Equals(a, "--dev", StringComparison.OrdinalIgnoreCase));
        bool autostart = e.Args.Any(a => string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase));
        bool noRecovery = e.Args.Any(a => string.Equals(a, "--no-recovery", StringComparison.OrdinalIgnoreCase));
        int exitAfter = ParseExitAfter(e.Args);
        IsDev = dev;

        _log = new LogFile(Path.Combine(ConfigStore.RootDir(dev), "logs", "app.log"));

        // 必须在任何 WPF 渲染之前：退出期 CRT 延迟加载要靠这次固定（详见 NativePin）
        IntPtr pinned = NativePin.PinVcRuntime();
        Log(pinned != IntPtr.Zero
            ? "已固定 vcruntime140_cor3.dll（退出期 CRT 延迟加载保护）"
            : "未固定 vcruntime140_cor3.dll（非单文件发布或文件缺失，不影响运行）");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log("崩溃：" + args.ExceptionObject);

            // 退出期异常只记日志：进程已在收尾，模态框只会挡住退出、让用户以为程序坏了。
            if (_exiting || Environment.HasShutdownStarted)
            {
                Log("退出期异常，不弹窗（进程已在收尾）");
                return;
            }

            try
            {
                MessageBox.Show("AutoVolume 出错：\n" + args.ExceptionObject, "AutoVolume",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* 关闭中弹不出窗口是正常的 */ }
        };

        _single = new SingleInstance(dev);
        if (!_single.IsOwner)
        {
            _single.Signal();
            Shutdown();
            return;
        }
        _single.Activated += () => Dispatcher.Invoke(ShowSettings);

        string configPath = ConfigStore.ConfigPath(dev);
        bool firstRun = !File.Exists(configPath);
        AutoVolumeConfig cfg = ConfigStore.LoadOrCreate(dev);

        foreach (string err in cfg.Validate()) Log("配置警告：" + err);

        Runtime = new AutoVolumeRuntime(cfg, dev, noRecovery);
        Runtime.Log += Log;
        _ = StartRuntimeAsync();

        _tray = new TrayIcon(Runtime, ShowSettings, ExitApp);
        _tray.Update(Runtime.Status);

        Log($"日志文件 {_log.FilePath}（2 MB × 3 滚动）");
        Log("启动完成");
        if (firstRun || !autostart) ShowSettings();

        // 测试钩子：--exit-after N 走与托盘「退出」完全相同的路径，用于验证退出期行为
        if (exitAfter > 0)
        {
            Log($"测试钩子：{exitAfter}s 后自动退出");
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(exitAfter) };
            timer.Tick += (_, _) => { timer.Stop(); ExitApp(); };
            timer.Start();
        }
    }

    private static int ParseExitAfter(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--exit-after", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out int seconds))
                return Math.Max(0, seconds);
        }

        return 0;
    }

    /// <summary>后台启动运行时：自愈要等 SMTC，不能卡住 UI 线程。</summary>
    private async Task StartRuntimeAsync()
    {
        try { await Runtime.StartAsync(); }
        catch (Exception ex) { Log("启动异常：" + ex); }
    }

    public void ShowSettings()
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow(Runtime, IsDev);
            _settings.Closed += (_, _) =>
            {
                _settings = null;
                // 设置窗口按需创建，关闭后尽快把内存还给系统
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            };
        }

        _settings.Show();
        if (_settings.WindowState == WindowState.Minimized)
            _settings.WindowState = WindowState.Normal;
        _settings.Activate();
    }

    public async void ExitApp()
    {
        _exiting = true;
        Log("用户退出");
        _tray?.Dispose();
        _tray = null;

        try { await Runtime.StopAsync(restore: true); } catch { }
        Runtime.Dispose();
        Shutdown();
    }

    private void Log(string message) => _log.Write(message);
}
