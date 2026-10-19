using System.Diagnostics;
using System.Runtime.InteropServices;
using AutoVolume.Audio.Interop;

namespace AutoVolume.Audio;

/// <summary>
/// 按应用静音（走 Windows 音频会话的 ISimpleAudioVolume）。
/// 用途：播放器暂停后有约 1 秒淡出尾音，只调系统主音量会把这截尾音一起放大；
/// 先把这个应用的会话静音，尾音就哑了，主音量可以立刻切到目标值。
/// 只恢复自己改过的静音状态，用户原本手动静音的应用不动。
/// </summary>
public sealed class SessionVolumeController
{
    private readonly HashSet<string> _muted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _generation = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>把该应用的所有音频会话静音；返回是否真的改了状态。</summary>
    public bool Mute(string exe)
    {
        if (_muted.Contains(exe)) return true;

        bool changed = false;
        Guid ctx = Guid.Empty;
        ForEachSession(exe, volume =>
        {
            if (volume.GetMute(out bool muted) >= 0 && !muted && volume.SetMute(true, ref ctx) >= 0)
                changed = true;
        });

        if (changed)
        {
            _muted.Add(exe);
            _generation[exe] = _generation.GetValueOrDefault(exe) + 1;
        }
        return changed;
    }

    /// <summary>静音并在指定时间后自动解除（防止程序崩溃后应用一直哑着）。</summary>
    public void MuteTemporarily(string exe, int holdMs)
    {
        Mute(exe);
        int gen = _generation.GetValueOrDefault(exe);
        _ = Task.Run(async () =>
        {
            await Task.Delay(holdMs).ConfigureAwait(false);
            if (_generation.GetValueOrDefault(exe) == gen) Unmute(exe);
        });
    }

    /// <summary>读该应用当前是否有会话静音（测试用）。</summary>
    public bool IsMuted(string exe)
    {
        bool muted = false;
        ForEachSession(exe, volume =>
        {
            if (volume.GetMute(out bool m) >= 0) muted |= m;
        });
        return muted;
    }

    public void Unmute(string exe)
    {
        if (!_muted.Remove(exe)) return;

        Guid ctx = Guid.Empty;
        ForEachSession(exe, volume => volume.SetMute(false, ref ctx));
    }

    public void UnmuteAll()
    {
        foreach (string exe in _muted.ToList()) Unmute(exe);
    }

    /// <summary>只保留这些应用仍静音，其余全部恢复（用于引擎让权/配置重载）。</summary>
    public void UnmuteExcept(IEnumerable<string> keep)
    {
        if (_muted.Count == 0) return;
        var keepSet = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        foreach (string exe in _muted.ToList())
        {
            if (!keepSet.Contains(exe)) Unmute(exe);
        }
    }

    private static void ForEachSession(string exe, Action<ISimpleAudioVolume> action)
    {
        string want = Path.GetFileNameWithoutExtension(exe);
        if (string.IsNullOrEmpty(want)) return;

        try
        {
            Type? type = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            if (type is null) return;

            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out IMMDevice device) < 0)
                return;

            Guid iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out object obj) < 0) return;

            var manager = (IAudioSessionManager2)obj;
            if (manager.GetSessionEnumerator(out IAudioSessionEnumerator list) < 0) return;
            if (list.GetCount(out int count) < 0) return;

            for (int i = 0; i < count; i++)
            {
                if (list.GetSession(i, out IAudioSessionControl control) < 0) continue;

                var control2 = control as IAudioSessionControl2;
                if (control2 is null || control2.GetProcessId(out uint pid) < 0 || pid == 0) continue;

                string name;
                try { name = Process.GetProcessById((int)pid).ProcessName; }
                catch { continue; }

                if (!string.Equals(name, want, StringComparison.OrdinalIgnoreCase)) continue;
                if (control is ISimpleAudioVolume volume) action(volume);
            }
        }
        catch
        {
            // 会话枚举失败不影响主流程
        }
    }
}
