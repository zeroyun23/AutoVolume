using System.Runtime.InteropServices;
using AutoVolume.Audio.Interop;

namespace AutoVolume.Audio;

/// <summary>系统主音量（默认多媒体渲染端点）。按 endpoint 记录 ID，供音量方案按设备分别存储。</summary>
public sealed class EndpointVolumeController : IDisposable
{
    private IMMDeviceEnumerator? _enumerator;
    private IMMDevice? _device;
    private IAudioEndpointVolume? _volume;
    private bool _disposed;

    public string EndpointId { get; private set; } = "unknown";

    public EndpointVolumeController()
    {
        CreateEnumerator();
        Bind();
    }

    private void CreateEnumerator()
    {
        var type = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))
                   ?? throw new InvalidOperationException("MMDeviceEnumerator CLSID 不可用");
        _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
    }

    private void Bind()
    {
        Release();
        if (_enumerator is null) CreateEnumerator();

        int hr = _enumerator!.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out IMMDevice device);
        if (hr < 0) throw new COMException($"GetDefaultAudioEndpoint 失败 0x{hr:X8}", hr);
        _device = device;

        hr = device.GetId(out string id);
        if (hr >= 0) EndpointId = id;

        var iid = typeof(IAudioEndpointVolume).GUID;
        hr = device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out object obj);
        if (hr < 0) throw new COMException($"Activate(IAudioEndpointVolume) 失败 0x{hr:X8}", hr);
        _volume = (IAudioEndpointVolume)obj;
    }

    /// <summary>设备切换 / 音频服务重启后重新绑定默认端点。</summary>
    public void Rebind()
    {
        try
        {
            Bind();
        }
        catch
        {
            // 端点暂时不可用时保留旧对象，下一轮再试
        }
    }

    public bool TryGetMaster(out float level)
    {
        if (_volume is null)
        {
            level = 0f;
            return false;
        }
        return _volume.GetMasterVolumeLevelScalar(out level) >= 0;
    }

    public float GetMaster() => TryGetMaster(out float level) ? level : 0f;

    public bool SetMaster(float level)
    {
        if (_volume is null) return false;
        level = Math.Clamp(level, 0f, 1f);
        Guid ctx = Guid.Empty;
        return _volume.SetMasterVolumeLevelScalar(level, ref ctx) >= 0;
    }

    public bool IsMuted()
    {
        if (_volume is null) return false;
        return _volume.GetMute(out bool mute) >= 0 && mute;
    }

    private void Release()
    {
        if (_volume is not null) { try { Marshal.ReleaseComObject(_volume); } catch { } _volume = null; }
        if (_device is not null) { try { Marshal.ReleaseComObject(_device); } catch { } _device = null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release();
        if (_enumerator is not null) { try { Marshal.ReleaseComObject(_enumerator); } catch { } _enumerator = null; }
    }
}
