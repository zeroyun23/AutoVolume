using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AutoVolume.Audio;
using AutoVolume.Core;
using AutoVolume.Runtime;

namespace AutoVolume.App;

public sealed class SchemeEdit : INotifyPropertyChanged
{
    private string _name = "";
    public string Id { get; init; } = "";
    public string Name
    {
        get => _name;
        set { _name = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name))); }
    }
    public ObservableCollection<string> Members { get; } = new();
    public bool VolumeEnabled { get; set; }
    public double VolumePercent { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class RoleRow : INotifyPropertyChanged
{
    private int _roleIndex;
    public string Exe { get; init; } = "";
    public int RoleIndex
    {
        get => _roleIndex;
        set
        {
            _roleIndex = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RoleIndex)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MethodVisibility)));
        }
    }
    public int MethodIndex { get; set; }
    public Visibility MethodVisibility => _roleIndex == 2 ? Visibility.Visible : Visibility.Hidden;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class SettingsWindow : Window
{
    private readonly AutoVolumeRuntime _runtime;
    private readonly bool _dev;
    private readonly string _endpointId;
    private readonly AutoVolumeConfig _work;
    private readonly ObservableCollection<SchemeEdit> _schemes = new();
    private readonly ObservableCollection<RoleRow> _roles = new();
    private SchemeEdit? _current;
    private bool _loading = true;
    private readonly Dictionary<Button, ToolTip> _helpTips = new();
    private ToolTip? _openHelp;
    private Button? _helpButton;

    public SettingsWindow(AutoVolumeRuntime runtime, bool dev)
    {
        InitializeComponent();
        DetachHelpTips();
        _runtime = runtime;
        _dev = dev;
        _work = Clone(runtime.Config);

        _endpointId = runtime.Status?.EndpointId ?? "";
        if (string.IsNullOrEmpty(_endpointId))
        {
            using var v = new EndpointVolumeController();
            _endpointId = v.EndpointId;
        }

        TryLoadIcon();
        LoadSchemes();
        LoadRoles();
        LoadGlobals();
        LoadAppPicker();

        _runtime.StatusChanged += OnStatusChanged;
        Closed += (_, _) => _runtime.StatusChanged -= OnStatusChanged;
        OnStatusChanged(runtime.Status);

        _loading = false;
        if (SchemeList.SelectedItem is SchemeEdit first) LoadSchemeIntoEditor(first);
    }

    private void TryLoadIcon()
    {
        try
        {
            BitmapImage? bmp = AppIcon.LoadImage();
            if (bmp is null) return;
            AppIconImage.Source = bmp;
            Icon = bmp;
        }
        catch { }
    }

    private static AutoVolumeConfig Clone(AutoVolumeConfig cfg) =>
        JsonSerializer.Deserialize<AutoVolumeConfig>(
            JsonSerializer.Serialize(cfg, ConfigStore.JsonOptions), ConfigStore.JsonOptions)!;

    private void LoadSchemes()
    {
        foreach (SchemeConfig s in _work.Schemes)
        {
            var edit = new SchemeEdit { Id = s.Id, Name = s.Name };
            foreach (string m in s.Members) edit.Members.Add(m);
            if (_endpointId.Length > 0 && s.Volume.TryGetValue(_endpointId, out double v))
            {
                edit.VolumeEnabled = true;
                edit.VolumePercent = Math.Round(v * 100);
            }
            _schemes.Add(edit);
        }

        SchemeList.ItemsSource = _schemes;
        if (_schemes.Count > 0) SchemeList.SelectedIndex = 0;
    }

    private void LoadRoles()
    {
        var exes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _work.Roles) exes.Add(kv.Key);
        foreach (var s in _work.Schemes) foreach (string m in s.Members) exes.Add(m);
        foreach (var kv in _work.Control) exes.Add(kv.Key);

        foreach (string exe in exes)
        {
            AppRole role = _work.RoleOf(exe);
            int method = _work.Control.TryGetValue(exe, out ControlConfig? c) &&
                         string.Equals(c.Method, "hotkey", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            _roles.Add(new RoleRow
            {
                Exe = exe,
                RoleIndex = role switch { AppRole.Source => 1, AppRole.Controlled => 2, _ => 0 },
                MethodIndex = method
            });
        }

        RoleList.ItemsSource = _roles;
    }

    private void LoadGlobals()
    {
        MutexCheck.IsChecked = _work.MutexEnabled;
        ModeCombo.SelectedIndex = _work.RecoveryMode == RecoveryMode.Loose ? 1 : 0;
        AutostartCheck.IsChecked = Autostart.IsEnabled();

        RestoreSlider.Value = _work.Timing.RestoreMs / 1000.0;
        ConfirmSlider.Value = Math.Clamp(_work.Timing.ConfirmMs / 1000.0, 0.5, 5);
        FadeSlider.Value = Math.Clamp(_work.Timing.FadeMs, 0, 1000);
        SuppressSlider.Value = Math.Clamp(_work.Timing.DeviceSuppressMs / 1000.0, 2, 15);
        FastConfirmCheck.IsChecked = _work.Timing.FastConfirmEnabled;

        UpdateTimingLabels();
    }

    private void LoadAppPicker()
    {
        var apps = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string a in SnapshotBuilder.ListRunningAudioApps()) apps.Add(a);
        foreach (var s in _work.Schemes) foreach (string m in s.Members) apps.Add(m);
        foreach (var kv in _work.Roles) apps.Add(kv.Key);
        AppPicker.ItemsSource = apps.ToList();
    }

    private void OnStatusChanged(RuntimeStatus? status)
    {
        if (status is null) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            string state = status.AutomationPaused
                ? "自动化已暂停"
                : status.Paused.Count > 0
                    ? $"已暂停 {string.Join("、", status.Paused)}"
                    : "监听中";
            StatusText.Text = $"{state} · 音量归属：{SchemeNameOf(status.Scheme)} · {status.Volume:0.00}";
        }));
    }

    private string SchemeNameOf(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "—";
        SchemeConfig? s = _work.FindScheme(id);
        return s?.Name ?? id;
    }

    // ---------- 方案编辑 ----------

    private void OnSchemeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        CommitCurrent();
        if (SchemeList.SelectedItem is SchemeEdit edit) LoadSchemeIntoEditor(edit);
    }

    private void LoadSchemeIntoEditor(SchemeEdit edit)
    {
        _current = edit;
        SchemeName.Text = edit.Name;
        MemberList.ItemsSource = edit.Members;
        VolumeEnabled.IsChecked = edit.VolumeEnabled;
        VolumeSlider.Value = edit.VolumePercent;
        VolumeLabel.Text = $"{edit.VolumePercent:0}%";
        VolumeSlider.IsEnabled = edit.VolumeEnabled;
    }

    private void CommitCurrent()
    {
        if (_current is null) return;
        _current.Name = string.IsNullOrWhiteSpace(SchemeName.Text) ? _current.Id : SchemeName.Text.Trim();
        _current.VolumeEnabled = VolumeEnabled.IsChecked == true;
        _current.VolumePercent = VolumeSlider.Value;
    }

    private void OnSchemeNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _current is null) return;
        _current.Name = string.IsNullOrWhiteSpace(SchemeName.Text) ? _current.Id : SchemeName.Text.Trim();
    }

    private void OnAddScheme(object sender, RoutedEventArgs e)
    {
        CommitCurrent();
        int n = 1;
        while (_schemes.Any(s => string.Equals(s.Id, "scheme" + n, StringComparison.OrdinalIgnoreCase))) n++;
        var edit = new SchemeEdit { Id = "scheme" + n, Name = "新方案 " + n, VolumeEnabled = false, VolumePercent = 50 };
        _schemes.Add(edit);
        SchemeList.SelectedItem = edit;
    }

    private void OnRemoveScheme(object sender, RoutedEventArgs e)
    {
        if (SchemeList.SelectedItem is not SchemeEdit edit) return;
        if (_schemes.Count <= 1)
        {
            MessageBox.Show(this, "至少保留一个方案。", "AutoVolume", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, $"删除方案「{edit.Name}」？", "AutoVolume",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        _schemes.Remove(edit);
        _current = null;
        SchemeList.SelectedIndex = 0;
    }

    private void OnAddMember(object sender, RoutedEventArgs e)
    {
        string exe = (AppPicker.Text ?? "").Trim();
        if (exe.Length == 0) return;
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";

        if (_current is null) return;
        if (_current.Members.Any(m => string.Equals(m, exe, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, $"{exe} 已在当前方案里。", "AutoVolume", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _current.Members.Add(exe);
        if (!_roles.Any(r => string.Equals(r.Exe, exe, StringComparison.OrdinalIgnoreCase)))
            _roles.Add(new RoleRow { Exe = exe, RoleIndex = 0 });
        AppPicker.Text = "";
    }

    private void OnRemoveMember(object sender, RoutedEventArgs e)
    {
        if (_current is null || MemberList.SelectedItem is not string exe) return;
        _current.Members.Remove(exe);
    }

    private void OnVolumeEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading) VolumeSlider.IsEnabled = VolumeEnabled.IsChecked == true;
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeLabel is not null) VolumeLabel.Text = $"{VolumeSlider.Value:0}%";
    }

    private void OnTimingChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateTimingLabels();
    }

    private void UpdateTimingLabels()
    {
        if (RestoreLabel is null || ConfirmLabel is null || FadeLabel is null || SuppressLabel is null) return;
        RestoreLabel.Text = $"{RestoreSlider.Value:0} s";
        ConfirmLabel.Text = $"{ConfirmSlider.Value:0.0} s";
        FadeLabel.Text = $"{FadeSlider.Value:0} ms";
        SuppressLabel.Text = $"{SuppressSlider.Value:0} s";
    }

    /// <summary>
    /// 把 XAML 里的 ToolTip 从 ToolTipService 摘下来自己管。
    /// 挂在 ToolTip 属性上且 StaysOpen=false 时，WPF 悬停会抛 NotSupportedException（0.3.4 的崩溃原因）。
    /// </summary>
    private void DetachHelpTips()
    {
        var buttons = new[] { HelpRecoveryButton, HelpConfirmButton, HelpFadeButton, HelpSuppressButton, HelpFastButton };
        foreach (Button b in buttons)
        {
            if (b.ToolTip is not ToolTip tip) continue;
            b.ToolTip = null;
            tip.PlacementTarget = b;
            tip.StaysOpen = true; // 关闭由下面的鼠标/Esc/失焦事件负责
            _helpTips[b] = tip;
        }
    }

    /// <summary>点击 ? 打开说明气泡；再点一次、点别处、Esc 或窗口失焦都会关闭。</summary>
    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || !_helpTips.TryGetValue(b, out ToolTip? tip)) return;

        if (ReferenceEquals(_openHelp, tip) && tip.IsOpen)
        {
            CloseHelp();
            return;
        }

        CloseHelp();
        tip.PlacementTarget = b;
        tip.IsOpen = true;
        _openHelp = tip;
        _helpButton = b;
    }

    private void CloseHelp()
    {
        if (_openHelp is not null) _openHelp.IsOpen = false;
        _openHelp = null;
        _helpButton = null;
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        // 点在当前 ? 上不关，交给 Click 做「再点一次关闭」
        if (_openHelp is not null && _helpButton?.IsMouseOver != true) CloseHelp();
        base.OnPreviewMouseDown(e);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        CloseHelp();
        base.OnDeactivated(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) CloseHelp();
        base.OnPreviewKeyDown(e);
    }

    /// <summary>把四个时间滑块改回 TimingConfig 的默认值。只改界面，点保存才落盘。</summary>
    private void OnResetTiming(object sender, RoutedEventArgs e)
    {
        var defaults = new TimingConfig();
        RestoreSlider.Value = Math.Clamp(defaults.RestoreMs / 1000.0, 0, 30);
        ConfirmSlider.Value = Math.Clamp(defaults.ConfirmMs / 1000.0, 0.5, 5);
        FadeSlider.Value = Math.Clamp(defaults.FadeMs, 0, 1000);
        SuppressSlider.Value = Math.Clamp(defaults.DeviceSuppressMs / 1000.0, 2, 15);
        FastConfirmCheck.IsChecked = defaults.FastConfirmEnabled;
        UpdateTimingLabels();
        ShowHint(TimingHint, "已恢复默认值，保存后生效", 3.0);
    }

    // ---------- 保存 ----------

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        CommitCurrent();

        var cfg = Clone(_work);
        cfg.MutexEnabled = MutexCheck.IsChecked == true;
        cfg.Mode = ModeCombo.SelectedIndex == 1 ? "loose" : "strict";
        cfg.Timing.RestoreMs = (int)Math.Round(RestoreSlider.Value * 1000);
        cfg.Timing.ConfirmMs = (int)Math.Round(ConfirmSlider.Value * 1000);
        cfg.Timing.FadeMs = (int)Math.Round(FadeSlider.Value);
        cfg.Timing.DeviceSuppressMs = (int)Math.Round(SuppressSlider.Value * 1000);
        cfg.Timing.FastConfirmEnabled = FastConfirmCheck.IsChecked == true;

        cfg.Schemes.Clear();
        foreach (SchemeEdit edit in _schemes)
        {
            var scheme = new SchemeConfig { Id = edit.Id, Name = edit.Name };
            scheme.Members.AddRange(edit.Members);
            if (edit.VolumeEnabled && _endpointId.Length > 0)
                scheme.Volume[_endpointId] = Math.Round(edit.VolumePercent / 100.0, 3);
            cfg.Schemes.Add(scheme);
        }

        if (cfg.FindScheme(cfg.DefaultScheme) is null && cfg.Schemes.Count > 0)
            cfg.DefaultScheme = cfg.Schemes[0].Id;

        cfg.Roles.Clear();
        cfg.Control.Clear();
        foreach (RoleRow row in _roles)
        {
            if (row.RoleIndex == 1) cfg.Roles[row.Exe] = "source";
            else if (row.RoleIndex == 2)
            {
                cfg.Roles[row.Exe] = "controlled";
                cfg.Control[row.Exe] = new ControlConfig { Method = row.MethodIndex == 1 ? "hotkey" : "auto" };
            }
        }

        cfg.Normalize();
        var errors = cfg.Validate();
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", errors), "配置有问题",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ConfigStore.Save(cfg, _dev);
        Autostart.SetEnabled(AutostartCheck.IsChecked == true, _dev);
        await _runtime.ReloadAsync(cfg);

        // 不关窗：保存后可能还要继续改别的页签
        ShowSavedHint();
    }

    private void ShowSavedHint() => ShowHint(SaveHint, "✓ 已保存", 2.5);

    private void ShowHint(TextBlock target, string text, double seconds)
    {
        target.Text = text;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            target.Text = "";
            timer.Stop();
        };
        timer.Start();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private async void OnTestPlayer(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RoleRow row) return;

        if (row.RoleIndex != 2)
        {
            MessageBox.Show(this, "先把角色设为「受控播放器」再测试。", "AutoVolume",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var (ok, detail) = await _runtime.TestPauseAsync(row.Exe);
        MessageBox.Show(this, detail, ok ? "测试通过" : "测试失败",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }
}
