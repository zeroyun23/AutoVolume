using System.Runtime.InteropServices;
using AutoVolume.Audio.Interop;

namespace AutoVolume.Audio;

/// <summary>
/// 默认输出设备变化监听（插拔耳机 / 蓝牙 / 设备禁用）。
/// 回调来自系统线程，必须马上返回，只投递事件。
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class DeviceWatcher : IMMNotificationClient, IDisposable
{
    private IMMDeviceEnumerator? _enumerator;
    private bool _registered;

    /// <summary>默认渲染设备发生变化（参数为新的 endpoint id）。</summary>
    public event Action<string>? DefaultRenderDeviceChanged;

    /// <summary>任意渲染设备状态变化（启用/禁用/拔出）。</summary>
    public event Action<string, uint>? RenderDeviceStateChanged;

    public DeviceWatcher()
    {
        var type = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))
                   ?? throw new InvalidOperationException("MMDeviceEnumerator CLSID 不可用");
        _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
        int hr = _enumerator.RegisterEndpointNotificationCallback(this);
        _registered = hr >= 0;
    }

    public bool Registered => _registered;

    public int OnDeviceStateChanged(string deviceId, uint newState)
    {
        if (newState != 1) // DEVICE_STATE_ACTIVE = 1
        {
            try { RenderDeviceStateChanged?.Invoke(deviceId, newState); } catch { }
        }
        return 0;
    }

    public int OnDeviceAdded(string deviceId) => 0;
    public int OnDeviceRemoved(string deviceId) => 0;

    public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string deviceId)
    {
        if (flow == EDataFlow.Render && role == ERole.Multimedia)
        {
            try { DefaultRenderDeviceChanged?.Invoke(deviceId); } catch { }
        }
        return 0;
    }

    public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;

    public void Dispose()
    {
        if (_enumerator is not null)
        {
            if (_registered)
            {
                try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
                _registered = false;
            }
            try { Marshal.ReleaseComObject(_enumerator); } catch { }
            _enumerator = null;
        }
    }
}
