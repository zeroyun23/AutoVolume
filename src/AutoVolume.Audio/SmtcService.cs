using Windows.Media.Control;

namespace AutoVolume.Audio;

public enum SmtcPlayback
{
    Unknown = -1,
    Closed = 0,
    Opened = 1,
    Changing = 2,
    Stopped = 3,
    Playing = 4,
    Paused = 5
}

public sealed record SmtcSessionInfo(string Aumid, SmtcPlayback Status, string Title, string Artist);

public sealed record SmtcSnapshot(bool Healthy, IReadOnlyList<SmtcSessionInfo> Sessions)
{
    public static readonly SmtcSnapshot Unhealthy = new(false, Array.Empty<SmtcSessionInfo>());
}

/// <summary>GSMTC 会话读取与定向控制。按 AUMID 前缀匹配 exe，不做"当前会话"假设。</summary>
public sealed class SmtcService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    private async Task<GlobalSystemMediaTransportControlsSessionManager> GetManagerAsync()
    {
        if (_manager is not null) return _manager;
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        return _manager;
    }

    /// <summary>重新请求管理器（媒体服务重启 / 休眠唤醒后）。</summary>
    public void Invalidate() => _manager = null;

    public async Task<SmtcSnapshot> GetSnapshotAsync(bool includeProperties = false)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var mgr = await GetManagerAsync().ConfigureAwait(false);
            var result = new List<SmtcSessionInfo>(8);
            foreach (var s in mgr.GetSessions())
            {
                string title = "", artist = "";
                if (includeProperties)
                {
                    try
                    {
                        var props = await s.TryGetMediaPropertiesAsync();
                        if (props is not null)
                        {
                            title = props.Title ?? "";
                            artist = props.Artist ?? "";
                        }
                    }
                    catch { /* 属性读取失败不影响状态判定 */ }
                }

                result.Add(new SmtcSessionInfo(
                    s.SourceAppUserModelId ?? "",
                    Map(StatusOf(s)),
                    title,
                    artist));
            }
            return new SmtcSnapshot(true, result);
        }
        catch
        {
            _manager = null;
            return SmtcSnapshot.Unhealthy;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static bool MatchesExe(string aumid, string exe)
    {
        string stem = Path.GetFileNameWithoutExtension(exe);
        if (string.IsNullOrEmpty(aumid) || string.IsNullOrEmpty(stem)) return false;
        return aumid.Contains(stem, StringComparison.OrdinalIgnoreCase)
               || stem.Contains(aumid, StringComparison.OrdinalIgnoreCase);
    }

    private static GlobalSystemMediaTransportControlsSessionPlaybackStatus StatusOf(
        GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            return s.GetPlaybackInfo()?.PlaybackStatus
                   ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
        }
        catch
        {
            return GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
        }
    }

    private static GlobalSystemMediaTransportControlsSession? Find(
        GlobalSystemMediaTransportControlsSessionManager mgr, string exe)
    {
        foreach (var s in mgr.GetSessions())
        {
            if (MatchesExe(s.SourceAppUserModelId ?? "", exe)) return s;
        }
        return null;
    }

    public async Task<(bool Ok, SmtcPlayback Status)> TryPauseAsync(string exe)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var mgr = await GetManagerAsync().ConfigureAwait(false);
            var s = Find(mgr, exe);
            if (s is null) return (false, SmtcPlayback.Unknown);
            bool ok = await s.TryPauseAsync().AsTask().ConfigureAwait(false);
            return (ok, Map(StatusOf(s)));
        }
        catch
        {
            _manager = null;
            return (false, SmtcPlayback.Unknown);
        }
        finally { Gate.Release(); }
    }

    public async Task<(bool Ok, SmtcPlayback Status)> TryPlayAsync(string exe)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var mgr = await GetManagerAsync().ConfigureAwait(false);
            var s = Find(mgr, exe);
            if (s is null) return (false, SmtcPlayback.Unknown);
            bool ok = await s.TryPlayAsync().AsTask().ConfigureAwait(false);
            return (ok, Map(StatusOf(s)));
        }
        catch
        {
            _manager = null;
            return (false, SmtcPlayback.Unknown);
        }
        finally { Gate.Release(); }
    }

    public static SmtcPlayback Map(GlobalSystemMediaTransportControlsSessionPlaybackStatus s) => s switch
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => SmtcPlayback.Closed,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => SmtcPlayback.Opened,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => SmtcPlayback.Changing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => SmtcPlayback.Stopped,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => SmtcPlayback.Playing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => SmtcPlayback.Paused,
        _ => SmtcPlayback.Unknown
    };
}
