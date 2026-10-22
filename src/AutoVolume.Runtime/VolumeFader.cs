using AutoVolume.Audio;

namespace AutoVolume.Runtime;

/// <summary>主音量渐变（8 步缓动）。渐变中关键判定：连续两次偏差才中止（蓝牙回读滞后）。</summary>
public sealed class VolumeFader
{
    private readonly EndpointVolumeController _vol;

    public VolumeFader(EndpointVolumeController vol)
    {
        _vol = vol;
        if (vol.TryGetMaster(out float v)) LastWritten = v;
    }

    public float LastWritten { get; private set; }
    public long LastWriteAt { get; private set; } = long.MinValue;
    public string? LastAbort { get; private set; }

    public void SyncExpected(float value)
    {
        LastWritten = value;
        LastWriteAt = Environment.TickCount64;
    }

    public async Task<bool> FadeToAsync(float target, int durationMs, double tolerance = 0.02)
    {
        target = Math.Clamp(target, 0f, 1f);
        if (!_vol.TryGetMaster(out float start)) return true; // 读不到就放弃本次渐变
        LastAbort = null;

        if (durationMs <= 0)
        {
            _vol.SetMaster(target);
            LastWritten = target;
            LastWriteAt = Environment.TickCount64;
            return true;
        }

        const int steps = 8;
        // 写入间隔不能太密：低于 25ms 时音频端点会把中间值合并，最后一步容易“回弹”。
        int stepDelay = Math.Max(25, durationMs / steps);
        int deviations = 0;

        for (int i = 1; i <= steps; i++)
        {
            // 蓝牙端点回读会滞后 50-150ms，单次偏差不算用户操作；连续两次才算
            if (i > 1 && _vol.TryGetMaster(out float now))
            {
                if (Math.Abs(now - LastWritten) > tolerance)
                {
                    deviations++;
                    if (deviations >= 2)
                    {
                        LastAbort = $"t={Environment.TickCount64 - LastWriteAt}ms expected={LastWritten:0.0000} actual={now:0.0000}";
                        return false;
                    }
                }
                else
                {
                    deviations = 0;
                }
            }

            double t = i / (double)steps;
            double eased = t * t * (3 - 2 * t); // smoothstep
            float v = (float)(start + (target - start) * eased);
            _vol.SetMaster(v);
            LastWritten = v;
            LastWriteAt = Environment.TickCount64;
            if (_vol.TryGetMaster(out float applied)) LastWritten = applied;

            if (i < steps) await Task.Delay(stepDelay);
        }

        // 端点对密集写入会回弹（约 50ms 后落到中间值）：写完等 120ms 核对，没到位就补写。
        for (int attempt = 0; attempt < 3; attempt++)
        {
            _vol.SetMaster(target);
            LastWritten = target;
            LastWriteAt = Environment.TickCount64;

            await Task.Delay(120);
            if (!_vol.TryGetMaster(out float actual)) break;
            if (Math.Abs(actual - target) <= 0.005f)
            {
                LastWritten = actual;
                return true;
            }
        }

        if (_vol.TryGetMaster(out float final))
        {
            LastWritten = final;
            if (Math.Abs(final - target) > 0.005f)
            {
                LastAbort = $"final={final:0.000} target={target:0.000}";
                return false;
            }
        }
        return true;
    }
}
