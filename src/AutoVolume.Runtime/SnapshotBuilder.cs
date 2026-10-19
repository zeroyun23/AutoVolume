using System.Diagnostics;
using AutoVolume.Audio;
using AutoVolume.Audio.Interop;
using AutoVolume.Core;

namespace AutoVolume.Runtime;

/// <summary>把 WASAPI 会话 + SMTC 会话合并成 Core 需要的快照。</summary>
public sealed class SnapshotBuilder
{
    private readonly WasapiSessionProbe _probe = new();
    private readonly SmtcService _smtc;
    private readonly AutoVolumeConfig _cfg;
    private readonly HashSet<string> _smtcSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, (bool Alive, long At, string Exe)> _pidCache = new();
    private readonly Dictionary<string, (bool Open, long At)> _winCache = new(StringComparer.OrdinalIgnoreCase);

    private SmtcSnapshot _lastSmtc = SmtcSnapshot.Unhealthy;
    private long _lastSmtcAt = long.MinValue;
    private long _lastTopologyAt = long.MinValue;

    public SnapshotBuilder(AutoVolumeConfig cfg, SmtcService smtc)
    {
        _cfg = cfg;
        _smtc = smtc;
        _probe.ExeFilter = cfg.Sources.Concat(cfg.Controlled)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public string EndpointId { get; private set; } = "unknown";
    public IReadOnlyList<SmtcSessionInfo> LastSmtcSessions => _lastSmtc.Sessions;
    public bool SmtcHealthy => _lastSmtc.Healthy;

    public async Task<Snapshot> BuildAsync(long nowMs)
    {
        // 全量枚举约 3ms：拓扑 1s 重建一次，逐 tick 只读白名单会话的 state/peak
        bool refresh = _lastTopologyAt == long.MinValue || nowMs - _lastTopologyAt >= 1000;
        var raw = _probe.Read(refresh);
        if (raw.Rebuilt) _lastTopologyAt = nowMs;
        EndpointId = raw.EndpointId;

        // SMTC 200ms 一次即可：关闭确认本身有 500ms 窗口
        if (_lastSmtcAt == long.MinValue || nowMs - _lastSmtcAt >= 200)
        {
            _lastSmtc = await _smtc.GetSnapshotAsync();
            _lastSmtcAt = nowMs;
        }

        var aggs = Aggregate(raw.Sessions);

        var sources = new List<SourceSample>();
        foreach (string exe in _cfg.Sources)
        {
            aggs.TryGetValue(exe, out Agg? a);
            bool has = a is not null;
            var smtc = FindSmtc(exe);
            if (smtc is not null) _smtcSeen.Add(exe);

            // 只有"有音频会话但没有 SMTC"的源才需要验活（防进程退出后会话残留）
            bool alive = smtc is not null || (has && IsPidAlive(a!.Pid, exe, nowMs));

            sources.Add(new SourceSample(
                exe,
                alive,
                has ? MapState(a!.State) : SessionStateKind.None,
                has ? a!.Peak : 0f,
                _smtcSeen.Contains(exe),
                smtc is not null,
                smtc is null ? SmtcPlaybackKind.Unknown : Map(smtc.Status),
                WindowOpenFor(exe, has ? a!.Pid : 0, nowMs)));
        }

        var players = new List<PlayerSample>();
        foreach (string exe in _cfg.Controlled)
        {
            aggs.TryGetValue(exe, out Agg? a);
            bool has = a is not null;
            var smtc = FindSmtc(exe);
            if (smtc is not null) _smtcSeen.Add(exe);

            bool alive = smtc is not null || (has && IsPidAlive(a!.Pid, exe, nowMs));

            var kind = smtc is null ? SmtcPlaybackKind.Unknown : Map(smtc.Status);
            bool playing = smtc is not null
                ? smtc.Status == SmtcPlayback.Playing
                : has && a!.State == AudioSessionState.Active; // D22：无 SMTC 用会话 Active

            players.Add(new PlayerSample(
                exe, alive,
                has ? MapState(a!.State) : SessionStateKind.None,
                _smtcSeen.Contains(exe),
                smtc is not null,
                kind,
                playing));
        }

        return new Snapshot(nowMs, _lastSmtc.Healthy, sources, players);
    }

    public void Reset()
    {
        _probe.Reset();
        _pidCache.Clear();
        _winCache.Clear();
    }

    /// <summary>当前所有发声进程（设置页选择成员用），不按白名单过滤。</summary>
    public static IReadOnlyList<string> ListRunningAudioApps()
    {
        try
        {
            using var probe = new WasapiSessionProbe();
            return probe.Read().Sessions
                .Where(s => s.Pid > 0 && s.Exe != "unknown")
                .Select(s => s.Exe)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private SmtcSessionInfo? FindSmtc(string exe)
    {
        foreach (var s in _lastSmtc.Sessions)
        {
            if (SmtcService.MatchesExe(s.Aumid, exe)) return s;
        }
        return null;
    }

    private static Dictionary<string, Agg> Aggregate(IReadOnlyList<SessionSample> sessions)
    {
        var map = new Dictionary<string, Agg>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sessions)
        {
            if (!map.TryGetValue(s.Exe, out Agg? agg))
            {
                agg = new Agg();
                map[s.Exe] = agg;
            }
            agg.Has = true;
            if (agg.Pid == 0) agg.Pid = s.Pid;
            if (s.State == AudioSessionState.Active) agg.State = AudioSessionState.Active;
            else if (agg.State != AudioSessionState.Active && s.State == AudioSessionState.Inactive) agg.State = AudioSessionState.Inactive;
            if (s.Peak > agg.Peak) agg.Peak = s.Peak;
        }
        return map;
    }

    /// <summary>配置为 window 判据的源：有可见窗口才算"打开"。null = 不适用。</summary>
    private bool? WindowOpenFor(string exe, int pid, long nowMs)
    {
        if (!_cfg.UsesWindowCloseSignal(exe)) return null;

        if (_winCache.TryGetValue(exe, out var c) && nowMs - c.At < 300) return c.Open;

        bool open = pid > 0 && WindowProbe.HasVisibleWindow(pid);
        _winCache[exe] = (open, nowMs);
        return open;
    }

    private bool IsPidAlive(int pid, string exe, long nowMs)
    {
        if (pid <= 0) return false;

        if (_pidCache.TryGetValue(pid, out var c) && nowMs - c.At < 500 &&
            string.Equals(c.Exe, exe, StringComparison.OrdinalIgnoreCase))
            return c.Alive;

        bool alive;
        try
        {
            using var p = Process.GetProcessById(pid);
            // 进程名比对：防 PID 复用时把新进程当成目标
            alive = string.Equals(p.ProcessName + ".exe", exe, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            alive = false;
        }

        if (_pidCache.Count > 256) _pidCache.Clear();
        _pidCache[pid] = (alive, nowMs, exe);
        return alive;
    }

    private static SessionStateKind MapState(AudioSessionState s) => s switch
    {
        AudioSessionState.Active => SessionStateKind.Active,
        AudioSessionState.Inactive => SessionStateKind.Inactive,
        AudioSessionState.Expired => SessionStateKind.Expired,
        _ => SessionStateKind.None
    };

    public static SmtcPlaybackKind Map(SmtcPlayback s) => s switch
    {
        SmtcPlayback.Closed => SmtcPlaybackKind.Closed,
        SmtcPlayback.Opened => SmtcPlaybackKind.Opened,
        SmtcPlayback.Changing => SmtcPlaybackKind.Changing,
        SmtcPlayback.Stopped => SmtcPlaybackKind.Stopped,
        SmtcPlayback.Playing => SmtcPlaybackKind.Playing,
        SmtcPlayback.Paused => SmtcPlaybackKind.Paused,
        _ => SmtcPlaybackKind.Unknown
    };

    private sealed class Agg
    {
        public bool Has;
        public int Pid;
        public AudioSessionState State = AudioSessionState.Inactive;
        public float Peak;
    }
}
