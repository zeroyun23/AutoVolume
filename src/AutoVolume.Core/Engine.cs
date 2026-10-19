namespace AutoVolume.Core;

public enum SessionStateKind
{
    None = 0,
    Inactive = 1,
    Active = 2,
    Expired = 3
}

public enum SmtcPlaybackKind
{
    Unknown = -1,
    Closed = 0,
    Opened = 1,
    Changing = 2,
    Stopped = 3,
    Playing = 4,
    Paused = 5
}

public sealed record SourceSample(
    string Exe,
    bool ProcessAlive,
    SessionStateKind SessionState,
    float Peak,
    bool SmtcCapable,
    bool SmtcPresent,
    SmtcPlaybackKind SmtcStatus,
    bool? WindowOpen = null);

public sealed record PlayerSample(
    string Exe,
    bool ProcessAlive,
    SessionStateKind SessionState,
    bool SmtcCapable,
    bool SmtcPresent,
    SmtcPlaybackKind SmtcStatus,
    bool IsPlaying);

public sealed record Snapshot(
    long NowMs,
    bool SmtcHealthy,
    IReadOnlyList<SourceSample> Sources,
    IReadOnlyList<PlayerSample> Players);

public enum EngineState
{
    Idle,
    Suspended,
    PendingRestore,
    Restoring
}

public abstract record EngineAction;

/// <summary>暂停指定播放器；执行器应在暂停成功后再升音量。</summary>
public sealed record PausePlayersAction(IReadOnlyList<string> Players) : EngineAction;

/// <summary>先渐变到目标音量，再恢复播放。</summary>
public sealed record ResumePlayersAction(IReadOnlyList<string> Players) : EngineAction;

/// <summary>切换系统主音量到指定方案（渐变由执行器完成）。</summary>
public sealed record SetVolumeAction(string SchemeId) : EngineAction;

public sealed class EngineOptions
{
    public RecoveryMode Mode { get; init; } = RecoveryMode.Strict;
    public bool MutexEnabled { get; init; } = true;
    public string DefaultScheme { get; init; } = "music";
    public int ConfirmMs { get; init; } = 1500;

    /// <summary>明显有声时的快速确认时间：视频/音乐一开口就能确认，不必等满 ConfirmMs。</summary>
    public int FastConfirmMs { get; init; } = 600;

    /// <summary>触发快速确认的峰值阈值。速览预览只有 0.005–0.01，不能走快通道。</summary>
    public float FastPeakThreshold { get; init; } = 0.02f;
    public int RestoreMs { get; init; } = 3000;
    public int SmtcGoneConfirmMs { get; init; } = 500;
    public int PauseRetryMs { get; init; } = 2000;

    /// <summary>峰值归零后的保持时间：视频里的短静音不应被当成"停止发声"。</summary>
    public int SoundingHoldMs { get; init; } = 1500;

    /// <summary>判定"有声"的峰值阈值。</summary>
    public float PeakThreshold { get; init; } = 0.003f;

    /// <summary>没有 SMTC 的源，音频会话转 Inactive 是否算关闭（由 VLC spike 决定）。</summary>
    public bool InactiveMeansClosedForNonSmtc { get; init; }

    public IReadOnlyDictionary<string, string> SchemeByExe { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 纯逻辑状态机：输入会话快照，输出动作。不碰 COM，不碰真实时钟。
/// </summary>
public sealed class AutoVolumeEngine
{
    private readonly EngineOptions _opt;
    private EngineState _state = EngineState.Idle;

    private readonly Dictionary<string, long> _soundingSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _lastSoundingAt = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>本轮发声以来的最大峰值，用于决定走快通道还是慢通道（避免峰值抖动来回切）。</summary>
    private readonly Dictionary<string, float> _maxPeak = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>已经确认过"有声"的源；确认后不再因瞬时峰值回落而取消。</summary>
    private readonly HashSet<string> _confirmedSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _smtcGoneSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pausedByUs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _pauseFailedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _episodeSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _events = new();

    private long? _silentSince;
    private long? _pendingRestoreSince;
    private long _suppressUntil;
    private bool _userOverride;
    private string? _lastScheme;
    private bool _started;

    public AutoVolumeEngine(EngineOptions options) => _opt = options;

    public EngineState State => _state;
    public IReadOnlyCollection<string> PausedByUs => _pausedByUs;
    public bool UserOverride => _userOverride;
    public string? CurrentScheme => _lastScheme;

    public IReadOnlyList<string> DrainEvents()
    {
        if (_events.Count == 0) return Array.Empty<string>();
        var copy = _events.ToArray();
        _events.Clear();
        return copy;
    }

    public void Suppress(long nowMs, int durationMs, string reason)
    {
        _suppressUntil = Math.Max(_suppressUntil, nowMs + durationMs);
        _soundingSince.Clear();
        _lastSoundingAt.Clear();
        _maxPeak.Clear();
        _confirmedSources.Clear();
        _smtcGoneSince.Clear();
        _silentSince = null;
        _pendingRestoreSince = null;
        _events.Add($"抑制 {durationMs}ms：{reason}");
    }

    public bool IsSuppressed(long nowMs) => nowMs < _suppressUntil;

    public void Reset()
    {
        _state = EngineState.Idle;
        _soundingSince.Clear();
        _lastSoundingAt.Clear();
        _maxPeak.Clear();
        _confirmedSources.Clear();
        _smtcGoneSince.Clear();
        _pausedByUs.Clear();
        _pauseFailedAt.Clear();
        _episodeSources.Clear();
        _silentSince = null;
        _pendingRestoreSince = null;
        _userOverride = false;
        _started = false;
        _lastScheme = null;
    }

    public IReadOnlyList<EngineAction> Tick(Snapshot snap)
    {
        var actions = new List<EngineAction>(4);

        if (snap.NowMs < _suppressUntil)
            return actions;

        // ---- 1. 有声计时（峰值 > PeakThreshold，短静音按 SoundingHoldMs 保持） ----
        foreach (var s in snap.Sources)
        {
            bool instant = s.ProcessAlive && s.Peak > _opt.PeakThreshold;
            if (instant)
            {
                if (!_soundingSince.ContainsKey(s.Exe)) _soundingSince[s.Exe] = snap.NowMs;
                _lastSoundingAt[s.Exe] = snap.NowMs;
                _maxPeak[s.Exe] = Math.Max(_maxPeak.GetValueOrDefault(s.Exe), s.Peak);
            }
            else if (_soundingSince.ContainsKey(s.Exe)
                     && snap.NowMs - _lastSoundingAt.GetValueOrDefault(s.Exe, 0) > _opt.SoundingHoldMs)
            {
                _soundingSince.Remove(s.Exe);
                _lastSoundingAt.Remove(s.Exe);
                _maxPeak.Remove(s.Exe);
                _confirmedSources.Remove(s.Exe);
                _events.Add($"{s.Exe} 有声保持到期（峰值 {s.Peak:0.000}）");
            }
        }

        var confirmed = new List<SourceSample>();
        foreach (var s in snap.Sources)
        {
            if (!_soundingSince.TryGetValue(s.Exe, out long since)) continue;
            if (_confirmedSources.Contains(s.Exe))
            {
                confirmed.Add(s);
                continue;
            }

            float peak = _maxPeak.GetValueOrDefault(s.Exe);
            int required = peak >= _opt.FastPeakThreshold
                ? Math.Min(_opt.ConfirmMs, _opt.FastConfirmMs)
                : _opt.ConfirmMs;

            if (snap.NowMs - since < required) continue;

            _confirmedSources.Add(s.Exe);
            if (required < _opt.ConfirmMs)
                _events.Add($"{s.Exe} 快速确认 {required}ms（峰值 {peak:0.000}）");
            confirmed.Add(s);
        }

        // 新一轮互斥开始时重置"本轮源"集合：上一轮遗留的源不参与本轮关闭判定
        if (confirmed.Count > 0 && _state == EngineState.Idle && _pausedByUs.Count == 0)
            _episodeSources.Clear();
        foreach (var s in confirmed) _episodeSources.Add(s.Exe);

        bool anySounding = confirmed.Count > 0;

        // ---- 2. 关闭判定 ----
        var closed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in snap.Sources)
        {
            bool hasSession = s.SessionState != SessionStateKind.None;
            bool isClosed;

            if (!s.ProcessAlive)
            {
                isClosed = true;
                _smtcGoneSince.Remove(s.Exe);
            }
            else if (s.WindowOpen == false)
            {
                // 进程常驻但窗口已关（PowerToys 速览）：SMTC 会话会残留，不能作为关闭判据
                isClosed = true;
                _smtcGoneSince.Remove(s.Exe);
            }
            else if (!hasSession && !s.SmtcPresent)
            {
                // 进程在，但没有任何音频/媒体会话 → 与关闭等效
                isClosed = true;
                _smtcGoneSince.Remove(s.Exe);
            }
            else if (!snap.SmtcHealthy)
            {
                isClosed = false; // SMTC 不可用时不判关闭，宁可晚恢复
                _smtcGoneSince.Remove(s.Exe);
            }
            else if (s.SmtcCapable)
            {
                if (s.SmtcPresent)
                {
                    isClosed = false;
                    _smtcGoneSince.Remove(s.Exe);
                }
                else
                {
                    if (!_smtcGoneSince.ContainsKey(s.Exe)) _smtcGoneSince[s.Exe] = snap.NowMs;
                    isClosed = snap.NowMs - _smtcGoneSince[s.Exe] >= _opt.SmtcGoneConfirmMs;
                }
            }
            else
            {
                isClosed = _opt.InactiveMeansClosedForNonSmtc && s.SessionState == SessionStateKind.Inactive;
            }
            closed[s.Exe] = isClosed;
        }

        // 只有"本轮参与过发声"的源才参与关闭判定；从没响过的浏览器不应阻塞恢复
        bool allClosed = _episodeSources.All(exe => !closed.TryGetValue(exe, out bool c) || c);
        if (_episodeSources.Count == 0) allClosed = true;

        if (!anySounding) _silentSince ??= snap.NowMs;
        else _silentSince = null;

        // ---- 3. 用户手动恢复播放 → 本周期让权 ----
        foreach (var p in snap.Players)
        {
            if (_pausedByUs.Contains(p.Exe) && p.IsPlaying)
            {
                _pausedByUs.Remove(p.Exe);
                _userOverride = true;
                _events.Add($"{p.Exe} 被用户手动恢复 → 本周期让权");
            }
        }
        if (_userOverride && allClosed && _opt.Mode == RecoveryMode.Strict)
        {
            _userOverride = false;
            _events.Add("所有源已关闭 → 解除让权");
        }
        if (_userOverride && _opt.Mode == RecoveryMode.Loose && !anySounding)
        {
            _userOverride = false;
        }

        // ---- 4. 互斥暂停 ----
        if (_opt.MutexEnabled && anySounding && _pausedByUs.Count == 0 && !_userOverride)
        {
            var playing = snap.Players
                .Where(p => p.ProcessAlive && p.IsPlaying)
                .Select(p => p.Exe)
                .Where(exe => !_pauseFailedAt.TryGetValue(exe, out long at) || snap.NowMs - at >= _opt.PauseRetryMs)
                .ToList();

            if (playing.Count > 0)
            {
                foreach (string exe in playing) _pauseFailedAt[exe] = snap.NowMs;
                actions.Add(new PausePlayersAction(playing));
                string src = string.Join(",", confirmed.Select(c => $"{c.Exe}({c.Peak:0.00})"));
                _events.Add($"源有声 → 暂停 {string.Join(",", playing)}（源: {src}）");
            }
            if (_state != EngineState.Suspended)
            {
                _state = EngineState.Suspended;
                _events.Add("状态 → SUSPENDED");
            }
        }

        // ---- 5. 恢复判定 ----
        bool resumeReady = false;
        if (!anySounding)
        {
            if (_opt.Mode == RecoveryMode.Strict)
            {
                if (allClosed)
                {
                    if (_pausedByUs.Count > 0)
                    {
                        _pendingRestoreSince ??= snap.NowMs;
                        if (_state != EngineState.PendingRestore)
                        {
                            _state = EngineState.PendingRestore;
                            _events.Add($"全部源关闭 → 等待 {_opt.RestoreMs}ms 后恢复");
                        }
                        resumeReady = snap.NowMs - _pendingRestoreSince.Value >= _opt.RestoreMs;
                    }
                    else if (_state != EngineState.Idle)
                    {
                        _state = EngineState.Idle;
                        _pendingRestoreSince = null;
                        _events.Add("状态 → IDLE");
                    }
                }
                else
                {
                    if (_pendingRestoreSince is not null)
                        _events.Add("等待期间源重新出现 → 取消恢复");
                    _pendingRestoreSince = null;
                    if (_pausedByUs.Count == 0 && _state != EngineState.Suspended && _state != EngineState.Idle)
                    {
                        _state = EngineState.Idle;
                        _events.Add("状态 → IDLE");
                    }
                }
            }
            else
            {
                if (_silentSince is long silentAt && snap.NowMs - silentAt >= _opt.RestoreMs)
                {
                    if (_pausedByUs.Count > 0)
                    {
                        if (_state != EngineState.PendingRestore)
                        {
                            _state = EngineState.PendingRestore;
                            _events.Add($"全部源静音 {_opt.RestoreMs}ms → 准备恢复");
                        }
                        resumeReady = true;
                    }
                    else if (_state != EngineState.Idle)
                    {
                        _state = EngineState.Idle;
                        _events.Add("状态 → IDLE");
                    }
                }
            }
        }
        else
        {
            _pendingRestoreSince = null;
        }

        // ---- 6. 音量归属（恢复等待期内不提前改音量） ----
        // 挂起期间按"本轮仍打开的源"归属，避免峰值短暂归零导致音量在两个方案间抖动
        bool resumeReadyForVolume = resumeReady;
        string target = ComputeTargetScheme(snap, confirmed, closed,
            stable: _pausedByUs.Count > 0 && !resumeReadyForVolume);
        bool deferVolume = _state == EngineState.PendingRestore && _pausedByUs.Count > 0 && !resumeReady;

        if (!_started)
        {
            _started = true;
            _lastScheme = target; // 启动首个快照只记基线，不动音量（D2）
        }
        else if (resumeReady)
        {
            // 先渐降音量，再恢复播放（D21）
            _lastScheme = target;
            actions.Add(new SetVolumeAction(target));
            var list = _pausedByUs.ToList();
            actions.Add(new ResumePlayersAction(list));
            _events.Add($"恢复播放 {string.Join(",", list)}（先渐降到 {target}）");
            _state = EngineState.Restoring;
            _pendingRestoreSince = null;
        }
        else if (!deferVolume && !string.Equals(_lastScheme, target, StringComparison.OrdinalIgnoreCase))
        {
            _lastScheme = target;
            string detail = confirmed.Count > 0
                ? "源: " + string.Join(",", confirmed.Select(c => $"{c.Exe}({c.Peak:0.00})"))
                : "无源在响";
            _events.Add($"音量归属 → {target}（{detail}）");
            actions.Add(new SetVolumeAction(target));
        }

        return actions;
    }

    private string ComputeTargetScheme(Snapshot snap, List<SourceSample> confirmed,
        Dictionary<string, bool> closed, bool stable)
    {
        // 稳定模式：本轮响过、且仍未关闭的源里，取最近发声的那个
        if (stable)
        {
            SourceSample? best = null;
            long bestAt = long.MinValue;
            foreach (var s in snap.Sources)
            {
                if (!_episodeSources.Contains(s.Exe)) continue;
                if (closed.TryGetValue(s.Exe, out bool c) && c) continue;
                long at = _lastSoundingAt.GetValueOrDefault(s.Exe, 0);
                if (at >= bestAt)
                {
                    bestAt = at;
                    best = s;
                }
            }
            if (best is not null) return SchemeOf(best.Exe) ?? _opt.DefaultScheme;
        }

        if (confirmed.Count > 0)
        {
            SourceSample latest = confirmed[0];
            long latestSince = long.MinValue;
            foreach (var s in confirmed)
            {
                if (_soundingSince.TryGetValue(s.Exe, out long since) && since >= latestSince)
                {
                    latestSince = since;
                    latest = s;
                }
            }
            return SchemeOf(latest.Exe) ?? _opt.DefaultScheme;
        }

        foreach (var p in snap.Players)
        {
            if (p.ProcessAlive && p.IsPlaying)
                return SchemeOf(p.Exe) ?? _opt.DefaultScheme;
        }

        return _opt.DefaultScheme;
    }

    private string? SchemeOf(string exe) =>
        _opt.SchemeByExe.TryGetValue(exe, out string? scheme) ? scheme : null;

    public void NotifyPauseResult(long nowMs, string exe, bool ok)
    {
        if (ok)
        {
            _pausedByUs.Add(exe);
            _events.Add($"{exe} 已暂停");
        }
        else
        {
            _pauseFailedAt[exe] = nowMs;
            _events.Add($"{exe} 暂停失败（2s 内不重试）");
        }
    }

    public void NotifyResumeResult(string exe, bool ok)
    {
        _pausedByUs.Remove(exe);
        if (ok) _events.Add($"{exe} 已恢复播放");
        else _events.Add($"{exe} 恢复失败（不再重试）");
    }

    /// <summary>配置重载后恢复"我们暂停过谁"的记忆，避免重启循环时丢失恢复责任。</summary>
    public void SeedPausedByUs(IEnumerable<string> exes)
    {
        foreach (string e in exes) _pausedByUs.Add(e);
    }

    /// <summary>用户手动接管（托盘"立即恢复播放"）：本周期内不再自动暂停，直到源全部关闭。</summary>
    public void MarkUserOverride()
    {
        _userOverride = true;
        _events.Add("用户手动接管 → 本周期不再自动暂停");
    }
}
