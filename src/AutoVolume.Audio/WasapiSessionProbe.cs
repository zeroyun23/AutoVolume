using System.Diagnostics;
using System.Runtime.InteropServices;
using AutoVolume.Audio.Interop;

namespace AutoVolume.Audio;

public sealed record SessionSample(
    int Pid,
    string Exe,
    AudioSessionState State,
    float Peak);

public sealed record SessionSnapshot(
    string EndpointId,
    IReadOnlyList<SessionSample> Sessions,
    long ElapsedMs,
    bool Rebuilt);

public sealed record EndpointInfo(string Id, uint State, string Name);

/// <summary>
/// 默认渲染端点的 WASAPI 会话枚举 + 每会话峰值读取。
/// 全量枚举约 3ms，不能 20Hz 跑：拓扑（会话对象）按需重建，逐 tick 只读 state/peak。
/// </summary>
public sealed class WasapiSessionProbe : IDisposable
{
    private IMMDeviceEnumerator? _enumerator;
    private IMMDevice? _device;
    private IAudioSessionManager2? _manager;
    private readonly List<CachedSession> _cache = new();
    private readonly Dictionary<int, string> _processNames = new();
    private bool _disposed;

    /// <summary>只保留这些 exe 的会话（白名单）。null = 全量。</summary>
    public IReadOnlySet<string>? ExeFilter { get; set; }

    private sealed class CachedSession
    {
        public int Pid;
        public string Exe = "";
        public IAudioSessionControl Control = null!;
        public IAudioMeterInformation? Meter;
    }

    public WasapiSessionProbe()
    {
        CreateEnumerator();
    }

    public string EndpointId { get; private set; } = "unknown";

    private void CreateEnumerator()
    {
        var type = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))
                   ?? throw new InvalidOperationException("MMDeviceEnumerator CLSID 不可用");
        _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
    }

    /// <summary>读一次。refreshTopology=true 时重建会话对象列表，否则只用缓存读 state/peak。</summary>
    public SessionSnapshot Read(bool refreshTopology)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sw = Stopwatch.StartNew();

        if (refreshTopology || _cache.Count == 0)
        {
            Rebuild();
            return new SessionSnapshot(EndpointId, Snapshot(), sw.ElapsedMilliseconds, true);
        }

        var list = new List<SessionSample>(_cache.Count);
        bool broken = false;

        foreach (CachedSession c in _cache)
        {
            int hr = c.Control.GetState(out AudioSessionState state);
            float peak = 0f;
            if (c.Meter is not null)
            {
                if (c.Meter.GetPeakValue(out float pk) < 0) broken = true;
                else peak = pk;
            }

            if (hr < 0 || state == AudioSessionState.Expired) broken = true;

            list.Add(new SessionSample(c.Pid, c.Exe, state, peak));
        }

        if (broken)
        {
            // 会话过期/失效 → 立即重建一次
            Rebuild();
            return new SessionSnapshot(EndpointId, Snapshot(), sw.ElapsedMilliseconds, true);
        }

        return new SessionSnapshot(EndpointId, list, sw.ElapsedMilliseconds, false);
    }

    public SessionSnapshot Read() => Read(true);

    /// <summary>列出所有渲染端点（用于设备切换排查）。</summary>
    public List<EndpointInfo> ListRenderEndpoints()
    {
        var result = new List<EndpointInfo>();
        if (_enumerator is null) return result;

        int hr = _enumerator.EnumAudioEndpoints(EDataFlow.Render, 0x0000000F, out IMMDeviceCollection col);
        if (hr < 0) return result;

        try
        {
            col.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                if (col.Item(i, out IMMDevice dev) < 0) continue;
                try
                {
                    dev.GetId(out string id);
                    dev.GetState(out uint state);
                    string name = "";
                    if (dev.OpenPropertyStore(0, out IPropertyStore store) >= 0 && store is not null)
                    {
                        try
                        {
                            var key = new PropertyKey
                            {
                                FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
                                PropertyId = 14
                            };
                            if (store.GetValue(ref key, out PropVariant pv) >= 0 && pv.ValueType == 31)
                                name = Marshal.PtrToStringUni(pv.PointerValue) ?? "";
                        }
                        finally { Marshal.ReleaseComObject(store); }
                    }
                    result.Add(new EndpointInfo(id, state, name));
                }
                finally { Marshal.ReleaseComObject(dev); }
            }
        }
        finally { Marshal.ReleaseComObject(col); }

        return result;
    }

    private List<SessionSample> Snapshot()
    {
        var list = new List<SessionSample>(_cache.Count);
        foreach (CachedSession c in _cache)
        {
            c.Control.GetState(out AudioSessionState state);
            float peak = 0f;
            c.Meter?.GetPeakValue(out peak);
            list.Add(new SessionSample(c.Pid, c.Exe, state, peak));
        }
        return list;
    }

    private void Rebuild()
    {
        ReleaseTopology();
        if (_enumerator is null) CreateEnumerator();

        int hr = _enumerator!.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out IMMDevice device);
        if (hr < 0) throw new COMException($"GetDefaultAudioEndpoint 失败 0x{hr:X8}", hr);
        _device = device;

        if (device.GetId(out string id) >= 0) EndpointId = id;

        var mgrIid = typeof(IAudioSessionManager2).GUID;
        hr = device.Activate(ref mgrIid, ClsCtx.All, IntPtr.Zero, out object mgrObj);
        if (hr < 0) throw new COMException($"Activate(IAudioSessionManager2) 失败 0x{hr:X8}", hr);
        _manager = (IAudioSessionManager2)mgrObj;

        hr = _manager.GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        if (hr < 0) throw new COMException($"GetSessionEnumerator 失败 0x{hr:X8}", hr);

        try
        {
            sessions.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out IAudioSessionControl? ctl) < 0 || ctl is null) continue;

                int pid = 0;
                if (ctl is IAudioSessionControl2 ctl2 && ctl2.GetProcessId(out uint p) >= 0)
                    pid = unchecked((int)p);

                string exe = ResolveExe(pid);
                if (ExeFilter is not null && !ExeFilter.Contains(exe))
                {
                    Marshal.ReleaseComObject(ctl);
                    continue;
                }

                _cache.Add(new CachedSession
                {
                    Pid = pid,
                    Exe = exe,
                    Control = ctl,
                    Meter = ctl as IAudioMeterInformation
                });
            }
        }
        finally
        {
            Marshal.ReleaseComObject(sessions);
        }
    }

    private string ResolveExe(int pid)
    {
        if (pid == 0) return "system";
        if (_processNames.TryGetValue(pid, out string? cached)) return cached;

        string name;
        try
        {
            using var p = Process.GetProcessById(pid);
            name = p.ProcessName + ".exe";
        }
        catch
        {
            name = "unknown";
        }

        if (_processNames.Count < 4096) _processNames[pid] = name;
        return name;
    }

    private void ReleaseTopology()
    {
        foreach (CachedSession c in _cache)
        {
            try { Marshal.ReleaseComObject(c.Control); } catch { }
        }
        _cache.Clear();

        if (_manager is not null) { try { Marshal.ReleaseComObject(_manager); } catch { } _manager = null; }
        if (_device is not null) { try { Marshal.ReleaseComObject(_device); } catch { } _device = null; }
    }

    /// <summary>设备变化 / 音频服务重启后重建枚举器。</summary>
    public void Reset()
    {
        ReleaseTopology();
        if (_enumerator is not null) { try { Marshal.ReleaseComObject(_enumerator); } catch { } _enumerator = null; }
        CreateEnumerator();
        _processNames.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseTopology();
        if (_enumerator is not null) { try { Marshal.ReleaseComObject(_enumerator); } catch { } _enumerator = null; }
    }
}
