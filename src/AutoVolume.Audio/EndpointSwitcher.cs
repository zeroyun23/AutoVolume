using System.Runtime.InteropServices;
using AutoVolume.Audio.Interop;

namespace AutoVolume.Audio;

/// <summary>
/// 切换默认输出设备（测试/诊断用）。走 IPolicyConfig 的 SetDefaultEndpoint，
/// 与系统声音设置面板等效；不需要管理员权限。
/// </summary>
public sealed class EndpointSwitcher
{
    [ComImport]
    [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient
    {
    }

    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultFormat, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultPeriod, out long min, out long max);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }

    public static bool SetDefault(string deviceId, ERole role = ERole.Multimedia)
    {
        var type = Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"));
        if (type is null) return false;

        object? obj = null;
        try
        {
            obj = Activator.CreateInstance(type);
            var policy = (IPolicyConfig)obj!;
            int hr = policy.SetDefaultEndpoint(deviceId, ERole.Console);
            hr |= policy.SetDefaultEndpoint(deviceId, ERole.Multimedia);
            hr |= policy.SetDefaultEndpoint(deviceId, ERole.Communications);
            return hr >= 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (obj is not null) Marshal.ReleaseComObject(obj);
        }
    }
}
