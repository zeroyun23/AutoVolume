using System.IO;
using System.Windows;
using AutoVolume.Core;
using AutoVolume.Runtime;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace AutoVolume.App;

/// <summary>托盘图标与菜单。所有更新都切回 UI 线程。</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _statusItem;
    private readonly WinForms.ToolStripMenuItem _pauseItem;
    private readonly WinForms.ToolStripMenuItem _resumeItem;
    private readonly WinForms.ToolStripMenuItem _restoreItem;
    private readonly AutoVolumeRuntime _runtime;

    public TrayIcon(AutoVolumeRuntime runtime, Action openSettings, Action exit)
    {
        _runtime = runtime;

        var menu = new WinForms.ContextMenuStrip { ShowImageMargin = false };

        _statusItem = new WinForms.ToolStripMenuItem("状态：启动中") { Enabled = false };
        _pauseItem = new WinForms.ToolStripMenuItem("暂停自动化");
        _resumeItem = new WinForms.ToolStripMenuItem("恢复自动化") { Enabled = false };
        _restoreItem = new WinForms.ToolStripMenuItem("立即恢复播放") { Enabled = false };
        var settingsItem = new WinForms.ToolStripMenuItem("设置…");
        var exitItem = new WinForms.ToolStripMenuItem("退出");

        _pauseItem.Click += (_, _) =>
        {
            _runtime.SetAutomationPaused(true);
            _pauseItem.Enabled = false;
            _resumeItem.Enabled = true;
            _statusItem.Text = "状态：自动化已暂停";
        };

        _resumeItem.Click += (_, _) =>
        {
            _runtime.SetAutomationPaused(false);
            _resumeItem.Enabled = false;
            _pauseItem.Enabled = true;
            _statusItem.Text = "状态：监听中…";
        };

        _restoreItem.Click += async (_, _) => await _runtime.RestoreAllAsync("手动恢复");
        settingsItem.Click += (_, _) => openSettings();
        exitItem.Click += (_, _) => exit();

        menu.Items.Add(_statusItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_resumeItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_restoreItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(exitItem);

        Drawing.Icon? icon = AppIcon.LoadTray();

        _icon = new WinForms.NotifyIcon
        {
            Icon = icon ?? Drawing.SystemIcons.Application,
            Text = "AutoVolume",
            Visible = true,
            ContextMenuStrip = menu
        };

        _icon.DoubleClick += (_, _) => openSettings();
        runtime.StatusChanged += Update;
    }

    public void Update(RuntimeStatus? status)
    {
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (status is null) return;

            _statusItem.Text = DescribeStatus(status);

            _pauseItem.Enabled = !status.AutomationPaused;
            _resumeItem.Enabled = status.AutomationPaused;
            _restoreItem.Enabled = status.Paused.Count > 0;

            string tip = status.Paused.Count > 0
                ? "AutoVolume · 已暂停 " + string.Join(",", status.Paused)
                : "AutoVolume · 监听中";
            _icon.Text = tip.Length <= 63 ? tip : tip[..63];
        }));
    }

    /// <summary>状态行显示"方案名 + 方案值"，实际音量与方案值不一致时补一个当前值。</summary>
    private string DescribeStatus(RuntimeStatus s)
    {
        if (s.AutomationPaused) return "状态：自动化已暂停";
        if (s.Paused.Count > 0) return $"状态：已暂停 {string.Join("、", s.Paused)}";

        AutoVolumeConfig cfg = _runtime.Config;
        SchemeConfig? scheme = cfg.FindScheme(s.Scheme ?? cfg.DefaultScheme);
        if (scheme is null) return $"状态：监听中 · 当前 {s.Volume * 100:0}%";

        if (!scheme.Volume.TryGetValue(s.EndpointId, out double configured))
            return $"状态：监听中 · {scheme.Name}（未设音量）";

        return Math.Abs(s.Volume - configured) >= 0.01
            ? $"状态：监听中 · {scheme.Name} {configured * 100:0}%（当前 {s.Volume * 100:0}%）"
            : $"状态：监听中 · {scheme.Name} {configured * 100:0}%";
    }

    public void Dispose()
    {
        _runtime.StatusChanged -= Update;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
