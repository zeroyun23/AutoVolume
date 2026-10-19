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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool dev = e.Args.Any(a => string.Equals(a, "--dev", StringComparison.OrdinalIgnoreCase));
        bool autostart = e.Args.Any(a => string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase));
        bool noRecovery = e.Args.Any(a => string.Equals(a, "--no-recovery", StringComparison.OrdinalIgnoreCase));
        IsDev = dev;

        _log = new LogFile(Path.Combine(ConfigStore.RootDir(dev), "logs", "app.log"));
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log("崩溃：" + args.ExceptionObject);
            MessageBox.Show("AutoVolume 出错：\n" + args.ExceptionObject, "AutoVolume",
                MessageBoxButton.OK, MessageBoxImage.Error);
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
        Log("用户退出");
        _tray?.Dispose();
        _tray = null;

        try { await Runtime.StopAsync(restore: true); } catch { }
        Runtime.Dispose();
        Shutdown();
    }

    private void Log(string message) => _log.Write(message);
}
