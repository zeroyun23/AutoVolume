using System.Diagnostics;
using AutoVolume.Audio;
using AutoVolume.Core;

namespace AutoVolume.Runtime;

public sealed record SourceView(
    string Exe, string Session, float Peak, string Smtc, bool? WindowOpen, bool Sounding);

public sealed record PlayerView(string Exe, string Session, string Smtc, bool Playing);

public sealed record RuntimeStatus(
    EngineState State,
    string? Scheme,
    string EndpointId,
    float Volume,
    bool AutomationPaused,
    IReadOnlyList<string> Paused,
    IReadOnlyList<SourceView> Sources,
    IReadOnlyList<PlayerView> Players,
    bool SmtcHealthy,
    string Mode,
    DateTime At);

/// <summary>
/// 运行时：会话采集 → 状态机 → 执行动作 → 音量学习 + 崩溃自愈。
/// CLI 与 WPF 托盘共用这一份逻辑。
/// </summary>
public sealed class AutoVolumeRuntime : IDisposable
{
    private readonly bool _dev;
    private readonly bool _noRecovery;

    private AutoVolumeConfig _cfg;
    private SmtcService _smtc = new();
    private SnapshotBuilder _builder;
    private AutoVolumeEngine _engine;
    private EndpointVolumeController _vol;
    private VolumeFader _fader;
    private readonly SessionVolumeController _sessions = new();
    private DeviceWatcher? _watcher;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _lastLoopAt;
    private volatile bool _deviceChanged;
    private string _lastEndpoint = "";
    private RuntimeStatus? _status;
    private DateTime _lastStatusAt = DateTime.MinValue;
    private Journal? _journal;
    private int _disposed;
    /// <summary>崩溃自愈只在进程生命周期内跑一次；改配置触发的 Reload 不能再跑。</summary>
    private bool _recoveryAttempted;

    public AutoVolumeRuntime(AutoVolumeConfig cfg, bool dev, bool noRecovery = false)
    {
        _cfg = cfg;
        _dev = dev;
        _noRecovery = noRecovery;

        _vol = new EndpointVolumeController();
        _fader = new VolumeFader(_vol);
        _builder = new SnapshotBuilder(cfg, _smtc);
        _engine = new AutoVolumeEngine(BuildOptions(cfg));

        _watcher = new DeviceWatcher();
        _watcher.DefaultRenderDeviceChanged += _ => _deviceChanged = true;
        _watcher.RenderDeviceStateChanged += (_, _) => _deviceChanged = true;
    }

    public event Action<string>? Log;
    public event Action<RuntimeStatus>? StatusChanged;

    public AutoVolumeConfig Config => _cfg;
    public bool AutomationPaused { get; private set; }
    public RuntimeStatus? Status => _status;

    private void Emit(string message) => Log?.Invoke(message);

    /// <summary>异步启动。自愈要调 SMTC/WinRT，绝不能同步阻塞调用线程（UI 线程会死锁）。</summary>
    public async Task StartAsync()
    {
        await RecoverIfNeededAsync().ConfigureAwait(false);
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        Emit("运行时已启动");
    }

    /// <summary>后台启动，不阻塞调用方（WPF 启动用）。</summary>
    public void Start() => _ = StartAsync();

    public async Task StopAsync(bool restore = true)
    {
        await StopLoopAsync();
        if (restore) await RestoreAllAsync("退出");
    }

    private async Task StopLoopAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        try { if (_loop is not null) await _loop; } catch { }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    public async Task ReloadAsync(AutoVolumeConfig cfg)
    {
        List<string> paused = _engine.PausedByUs.ToList();
        await StopLoopAsync();

        _cfg = cfg;
        _builder = new SnapshotBuilder(cfg, _smtc);
        _engine = new AutoVolumeEngine(BuildOptions(cfg));
        _engine.SeedPausedByUs(paused);

        await StartAsync();
        Emit("配置已重载");
    }

    /// <summary>暂停自动化（只停决策，仍采集状态）。</summary>
    public void SetAutomationPaused(bool paused)
    {
        AutomationPaused = paused;
        Emit(paused ? "自动化已暂停" : "自动化已恢复");
    }

    public async Task RestoreAllAsync(string reason)
    {
        List<string> players = _engine.PausedByUs.ToList();
        _sessions.UnmuteAll();
        if (players.Count == 0) return;

        foreach (string exe in players)
        {
            try
            {
                (bool ok, _) = await _smtc.TryPlayAsync(exe);
                _engine.NotifyResumeResult(exe, ok);
                Emit(ok ? $"{reason}：恢复 {exe}" : $"{reason}：恢复 {exe} 失败");
            }
            catch { }
        }

        // 手动恢复后接管本周期：避免下一 tick 又把音乐按回去
        _engine.MarkUserOverride();

        JournalStore.Clear(_dev);
        _journal = null;
    }

    /// <summary>设置页"测试暂停"：暂停 1.2 秒再恢复，验证控制通道。</summary>
    public async Task<(bool Ok, string Detail)> TestPauseAsync(string exe)
    {
        (bool ok, SmtcPlayback st) = await _smtc.TryPauseAsync(exe);
        if (!ok) return (false, $"TryPauseAsync 返回 false（当前 {st}）");
        await Task.Delay(1200);
        (bool back, SmtcPlayback st2) = await _smtc.TryPlayAsync(exe);
        return (back, back ? $"暂停/恢复成功（{st} → {st2}）" : $"已暂停但恢复失败（{st2}）");
    }

    public async Task<bool> ApplySchemeVolumeAsync(string schemeId)
    {
        SchemeConfig? scheme = _cfg.FindScheme(schemeId);
        if (scheme is null || !scheme.Volume.TryGetValue(_vol.EndpointId, out double target)) return false;
        return await _fader.FadeToAsync((float)target, _cfg.Timing.FadeMs, _cfg.Timing.UserTolerance);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        _lastLoopAt = 0;
        while (!ct.IsCancellationRequested)
        {
            long now = _clock.ElapsedMilliseconds;

            try
            {
                if (_lastLoopAt != 0 && now - _lastLoopAt > 3000)
                {
                    _engine.Suppress(now, _cfg.Timing.DeviceSuppressMs, $"休眠唤醒/时钟跳变 {now - _lastLoopAt}ms");
                    _builder.Reset();
                    _vol.Rebind();
                    _smtc.Invalidate();
                    DrainEvents();
                }
                if (_deviceChanged)
                {
                    _deviceChanged = false;
                    _engine.Suppress(now, _cfg.Timing.DeviceSuppressMs, "输出设备变化");
                    _builder.Reset();
                    _vol.Rebind();
                    _smtc.Invalidate();
                    DrainEvents();
                }

                Snapshot snap;
                try
                {
                    snap = await _builder.BuildAsync(now);
                }
                catch (Exception ex)
                {
                    _engine.Suppress(now, _cfg.Timing.AudioServiceSuppressMs, $"音频枚举失败 {ex.GetType().Name}");
                    _builder.Reset();
                    _vol.Rebind();
                    await Task.Delay(500, ct);
                    _lastLoopAt = now;
                    continue;
                }

                if (!string.Equals(_builder.EndpointId, _lastEndpoint, StringComparison.OrdinalIgnoreCase))
                {
                    if (_lastEndpoint.Length > 0)
                    {
                        _vol.Rebind();
                        _engine.Suppress(now, _cfg.Timing.DeviceSuppressMs, "默认端点变化");
                        Emit($"端点切换 → {ShortId(_builder.EndpointId)}");
                    }
                    _lastEndpoint = _builder.EndpointId;
                }

                if (!AutomationPaused)
                {
                    IReadOnlyList<EngineAction> actions = _engine.Tick(snap);
                    foreach (EngineAction action in actions)
                        await ExecuteAsync(action, now, ct);
                }

                DrainEvents();
                UpdateJournal();
                // 引擎让权 / 用户手动恢复时，别把应用一直按在静音上
                _sessions.UnmuteExcept(_engine.PausedByUs);
                CheckUserVolumeChange();

                if (DateTime.Now - _lastStatusAt > TimeSpan.FromMilliseconds(400))
                {
                    PublishStatus(snap);
                    _lastStatusAt = DateTime.Now;
                }

                bool active = _engine.State != EngineState.Idle
                              || snap.Sources.Any(s => s.Peak > _cfg.Timing.PeakThreshold || s.SessionState == SessionStateKind.Active);
                await Task.Delay(active ? _cfg.Timing.ActivePollMs : _cfg.Timing.IdlePollMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Emit($"循环异常：{ex.GetType().Name} {ex.Message}");
                await Task.Delay(500, ct);
            }

            _lastLoopAt = now;
        }
    }

    private async Task ExecuteAsync(EngineAction action, long now, CancellationToken ct)
    {
        switch (action)
        {
            case PausePlayersAction pause:
                var pausedOk = new List<string>(pause.Players.Count);
                foreach (string exe in pause.Players)
                {
                    bool ok = await PausePlayerAsync(exe, ct);
                    _engine.NotifyPauseResult(_clock.ElapsedMilliseconds, exe, ok);
                    if (ok) pausedOk.Add(exe);
                }
                if (pausedOk.Count > 0)
                {
                    // 暂停后先把该应用的会话静音，掐掉约 1 秒的淡出尾音；
                    // 否则随后的音量渐变会把这截尾音放大（"音乐先变大再消失"）。
                    foreach (string exe in pausedOk) _sessions.MuteTemporarily(exe, 1500);

                    int settle = Math.Min(_cfg.Timing.PauseSettleMs, 80);
                    if (settle > 0) await Task.Delay(settle, ct);
                }
                break;

            case SetVolumeAction set:
                await ApplyVolumeAsync(set.SchemeId);
                break;

            case ResumePlayersAction resume:
                foreach (string exe in resume.Players)
                {
                    _sessions.Unmute(exe);
                    (bool ok, _) = await _smtc.TryPlayAsync(exe);
                    _engine.NotifyResumeResult(exe, ok);
                }
                break;
        }
    }

    private async Task<bool> PausePlayerAsync(string exe, CancellationToken ct)
    {
        if (_cfg.Control.TryGetValue(exe, out ControlConfig? c) &&
            string.Equals(c.Method, "hotkey", StringComparison.OrdinalIgnoreCase))
        {
            Emit($"{exe} 配置为热键控制，尚未实现 → 跳过");
            return false;
        }

        (bool accepted, _) = await _smtc.TryPauseAsync(exe);
        if (!accepted) return false;

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 6; i++)
        {
            await Task.Delay(50, ct);
            SmtcSnapshot snap = await _smtc.GetSnapshotAsync();
            SmtcSessionInfo? s = snap.Sessions.FirstOrDefault(x => SmtcService.MatchesExe(x.Aumid, exe));
            if (s is null) return false;
            if (s.Status == SmtcPlayback.Paused)
            {
                Emit($"{exe} 暂停确认 {sw.ElapsedMilliseconds}ms");
                return true;
            }
        }
        return false;
    }

    private async Task ApplyVolumeAsync(string schemeId)
    {
        SchemeConfig? scheme = _cfg.FindScheme(schemeId);
        if (scheme is null) return;

        if (!scheme.Volume.TryGetValue(_vol.EndpointId, out double target))
        {
            Emit($"方案 {schemeId} 在本设备无音量值（首次进入不动）");
            return;
        }

        if (Math.Abs(_vol.GetMaster() - (float)target) < 0.005f) return;

        bool ok = await _fader.FadeToAsync((float)target, _cfg.Timing.FadeMs, _cfg.Timing.UserTolerance);
        if (ok) Emit($"音量 → {schemeId} = {target:0.000}");
        else
        {
            Emit($"渐变被打断（{_fader.LastAbort}），以当前值为准");
            LearnVolume(schemeId, _vol.GetMaster());
        }
    }

    private void CheckUserVolumeChange()
    {
        if (!_vol.TryGetMaster(out float cur)) return;

        // 学习阈值取容差的一半：2% 的整数步进（8%→6%）也要能被记住
        double learnTolerance = Math.Max(0.01, _cfg.Timing.UserTolerance / 2);
        if (Math.Abs(cur - _fader.LastWritten) <= learnTolerance) return;
        if (Environment.TickCount64 - _fader.LastWriteAt < _cfg.Timing.UserWindowMs) return;
        if (_engine.CurrentScheme is null) return;

        LearnVolume(_engine.CurrentScheme, cur);
    }

    private void LearnVolume(string schemeId, float value)
    {
        SchemeConfig? scheme = _cfg.FindScheme(schemeId);
        if (scheme is null) return;

        float rounded = MathF.Round(value, 3);
        if (scheme.Volume.TryGetValue(_vol.EndpointId, out double old) && Math.Abs(old - rounded) < 0.001) return;

        scheme.Volume[_vol.EndpointId] = rounded;
        ConfigStore.Save(_cfg, _dev);
        _fader.SyncExpected(rounded);
        Emit($"学习音量 {schemeId} = {rounded:0.000}");
    }

    private void UpdateJournal()
    {
        List<string> paused = _engine.PausedByUs.ToList();

        if (paused.Count == 0)
        {
            if (_journal is not null)
            {
                JournalStore.Clear(_dev);
                _journal = null;
            }
            return;
        }

        if (_journal is not null) return; // 一次挂起周期只记一次

        _journal = new Journal
        {
            EndpointId = _vol.EndpointId,
            VolumeBefore = _vol.GetMaster(),
            VolumeSet = _engine.CurrentScheme is not null &&
                        _cfg.FindScheme(_engine.CurrentScheme) is { } s &&
                        s.Volume.TryGetValue(_vol.EndpointId, out double t) ? t : _vol.GetMaster(),
            PausedPlayers = paused,
            Scheme = _engine.CurrentScheme ?? "",
            At = DateTimeOffset.Now
        };
        JournalStore.Save(_dev, _journal);
    }

    private async Task RecoverIfNeededAsync()
    {
        if (_noRecovery || _recoveryAttempted) return;
        _recoveryAttempted = true;

        Journal? j = JournalStore.Load(_dev);
        if (j is null) return;

        Emit($"检测到上次未正常退出：恢复 {string.Join(",", j.PausedPlayers)}");

        foreach (string exe in j.PausedPlayers)
        {
            try
            {
                _sessions.Unmute(exe); // 上次崩溃可能留下会话静音，先解除再恢复播放
                (bool ok, _) = await _smtc.TryPlayAsync(exe)
                    .WaitAsync(TimeSpan.FromSeconds(3))
                    .ConfigureAwait(false);
                Emit(ok ? $"自愈：恢复 {exe}" : $"自愈：恢复 {exe} 失败");
            }
            catch (TimeoutException)
            {
                Emit($"自愈：恢复 {exe} 超时（>3s），跳过");
            }
            catch { }
        }

        try
        {
            if (string.Equals(j.EndpointId, _vol.EndpointId, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(_vol.GetMaster() - j.VolumeSet) < 0.05)
            {
                _vol.SetMaster((float)j.VolumeBefore);
                Emit($"自愈：音量还原 {j.VolumeBefore:0.000}");
            }
        }
        catch { }

        JournalStore.Clear(_dev);
    }

    private void PublishStatus(Snapshot snap)
    {
        var sources = new List<SourceView>(snap.Sources.Count);
        foreach (SourceSample s in snap.Sources)
        {
            sources.Add(new SourceView(
                s.Exe,
                s.SessionState.ToString(),
                s.Peak,
                s.SmtcStatus.ToString(),
                s.WindowOpen,
                s.Peak > _cfg.Timing.PeakThreshold));
        }

        var players = new List<PlayerView>(snap.Players.Count);
        foreach (PlayerSample p in snap.Players)
        {
            players.Add(new PlayerView(p.Exe, p.SessionState.ToString(), p.SmtcStatus.ToString(), p.IsPlaying));
        }

        _status = new RuntimeStatus(
            _engine.State,
            _engine.CurrentScheme,
            _vol.EndpointId,
            _vol.GetMaster(),
            AutomationPaused,
            _engine.PausedByUs.ToList(),
            sources,
            players,
            snap.SmtcHealthy,
            _cfg.Mode,
            DateTime.Now);

        StatusChanged?.Invoke(_status);
    }

    private void DrainEvents()
    {
        foreach (string e in _engine.DrainEvents()) Emit(e);
    }

    private static EngineOptions BuildOptions(AutoVolumeConfig cfg)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (SchemeConfig s in cfg.Schemes)
            foreach (string m in s.Members)
                map[m] = s.Id;

        return new EngineOptions
        {
            Mode = cfg.RecoveryMode,
            MutexEnabled = cfg.MutexEnabled,
            DefaultScheme = cfg.DefaultScheme,
            ConfirmMs = cfg.Timing.ConfirmMs,
            FastConfirmMs = cfg.Timing.FastConfirmEnabled ? cfg.Timing.FastConfirmMs : cfg.Timing.ConfirmMs,
            FastPeakThreshold = (float)cfg.Timing.FastPeakThreshold,
            PeakThreshold = (float)cfg.Timing.PeakThreshold,
            SoundingHoldMs = cfg.Timing.SoundingHoldMs,
            RestoreMs = cfg.Timing.RestoreMs,
            SmtcGoneConfirmMs = cfg.Timing.SmtcGoneConfirmMs,
            InactiveMeansClosedForNonSmtc = cfg.InactiveMeansClosedForNonSmtc,
            SchemeByExe = map
        };
    }

    private static string ShortId(string id)
    {
        int brace = id.IndexOf('}');
        if (id.StartsWith('{') && brace > 0) return id[..(brace + 1)];
        return id.Length <= 12 ? id : id[..12];
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try { _cts?.Cancel(); } catch { }
        try { _sessions.UnmuteAll(); } catch { } // 退出前务必解除会话静音，别让播放器哑着
        try { _watcher?.Dispose(); } catch { }
        try { _vol.Dispose(); } catch { }
        _watcher = null;
    }
}
