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
    public string LastDetail { get; private set; } = "";

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

        if (durationMs <= 0 || durationMs < 150)
        {
            // 短渐变直接一步到位：分步写入会让端点回读排队滞后，反而把正确值读成旧值。
            _vol.SetMaster(target);
            LastWritten = target;
            LastWriteAt = Environment.TickCount64;

            await Task.Delay(350);
            float fastActual = target;
            if (_vol.TryGetMaster(out float read))
            {
                fastActual = read;
                if (Math.Abs(fastActual - target) > 0.01f)
                {
                    _vol.SetMaster(target);
                    LastWriteAt = Environment.TickCount64;
                    await Task.Delay(250);
                    if (_vol.TryGetMaster(out float again)) fastActual = again;
                }
            }
            LastWritten = fastActual;
            LastDetail = $"fast={fastActual:0.000}";
            return true;
        }

        const int steps = 8;
        // 写入间隔不能太密：低于 25ms 时音频端点会把中间值合并，最后一步容易“回弹”。
        int stepDelay = Math.Max(25, durationMs / steps);
        int deviations = 0;

        for (int i = 1; i <= steps; i++)
        {
            // 回读滞后时数值仍在 [start, target] 区间内，这属于正常滞后；
            // 只有跑出区间（含容差）并连续两次，才判定用户在抢音量。
            if (i > 1 && _vol.TryGetMaster(out float now))
            {
                float lo = Math.Min(start, target) - (float)tolerance;
                float hi = Math.Max(start, target) + (float)tolerance;
                bool against = now < lo || now > hi;

                if (against)
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

        // 端点回读会滞后：写完等 300ms 核对，没到位再补写。
        var tries = new List<string>(3);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            _vol.SetMaster(target);
            LastWritten = target;
            LastWriteAt = Environment.TickCount64;

            await Task.Delay(300);
            if (!_vol.TryGetMaster(out float actual)) break;
            tries.Add($"{actual:0.000}");
            if (Math.Abs(actual - target) <= 0.005f)
            {
                LastWritten = actual;
                LastDetail = string.Join("/", tries);
                return true;
            }
        }

        LastDetail = string.Join("/", tries);
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
