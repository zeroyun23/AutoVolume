using System.Diagnostics;
using AutoVolume.Audio;
using AutoVolume.Core;
using AutoVolume.Runtime;

namespace AutoVolume.Cli;

internal static class Program
{
    private static AutoVolumeConfig _cfg = null!;
    private static bool _dev;
    private static bool _stop;

    private static async Task<int> Main(string[] args)
    {
        var options = ParseArgs(args);
        if (options.Help)
        {
            PrintHelp();
            return 0;
        }

        _dev = options.Dev;
        _cfg = ConfigStore.LoadOrCreate(_dev);
        if (!string.IsNullOrWhiteSpace(options.ModeOverride)) _cfg.Mode = options.ModeOverride!;

        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (options.List)
        {
            PrintConfig();
            return 0;
        }

        if (options.SetVolume is not null)
        {
            using var v = new EndpointVolumeController();
            return SeedVolume(options.SetVolume, v.EndpointId);
        }

        foreach (string err in _cfg.Validate())
            Console.WriteLine($"[配置警告] {err}");

        if (options.Once) return await PrintOnce();

        using var runtime = new AutoVolumeRuntime(_cfg, _dev, options.NoRecovery);
        var clock = Stopwatch.StartNew();
        DateTime lastPrint = DateTime.MinValue;

        runtime.Log += msg => Console.WriteLine($"[{clock.ElapsedMilliseconds,8}ms] {msg}");
        runtime.StatusChanged += status =>
        {
            if ((DateTime.Now - lastPrint).TotalMilliseconds < 1000) return;
            lastPrint = DateTime.Now;
            PrintStatus(clock.ElapsedMilliseconds, status);
        };

        Console.WriteLine($"AutoVolume diag  mode={_cfg.Mode} mutex={(_cfg.MutexEnabled ? "on" : "off")} dev={_dev}");
        Console.WriteLine($"方案: {string.Join(" | ", _cfg.Schemes.Select(s => $"{s.Id}({string.Join(",", s.Members)})"))}");
        await runtime.StartAsync();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _stop = true;
        };

        while (!_stop)
        {
            if (options.Seconds > 0 && clock.ElapsedMilliseconds >= options.Seconds * 1000L) break;
            await Task.Delay(100);
        }

        await runtime.StopAsync();
        return 0;
    }

    private static async Task<int> PrintOnce()
    {
        using var vol = new EndpointVolumeController();
        var smtc = new SmtcService();
        var builder = new SnapshotBuilder(_cfg, smtc);
        var snap = await builder.BuildAsync(0);

        Console.WriteLine($"endpoint={builder.EndpointId} volume={vol.GetMaster():0.000}");
        Console.WriteLine("--- sources ---");
        foreach (SourceSample s in snap.Sources)
        {
            string win = s.WindowOpen is null ? "-" : (s.WindowOpen.Value ? "Y" : "N");
            Console.WriteLine($"  {s.Exe,-24} alive={(s.ProcessAlive ? "Y" : "N")} win={win} " +
                              $"sess={s.SessionState,-8} peak={s.Peak:0.000} smtc={s.SmtcStatus}");
        }
        Console.WriteLine("--- players ---");
        foreach (PlayerSample p in snap.Players)
            Console.WriteLine($"  {p.Exe,-24} sess={p.SessionState,-8} smtc={p.SmtcStatus,-8} playing={(p.IsPlaying ? "Y" : "N")}");
        return 0;
    }

    private static void PrintStatus(long elapsedMs, RuntimeStatus s)
    {
        Console.WriteLine();
        Console.WriteLine($"[{TimeSpan.FromMilliseconds(elapsedMs):hh\\:mm\\:ss\\.fff}] vol={s.Volume:0.000} " +
                          $"state={s.State} scheme={s.Scheme ?? "-"} paused=[{string.Join(",", s.Paused)}] " +
                          $"smtc={(s.SmtcHealthy ? "ok" : "DOWN")}{(s.AutomationPaused ? " AUTO=OFF" : "")}");
        foreach (SourceView src in s.Sources)
        {
            string win = src.WindowOpen is null ? "-" : (src.WindowOpen.Value ? "Y" : "N");
            Console.WriteLine($"  SRC {src.Exe,-23} win={win} sess={src.Session,-8} " +
                              $"peak={src.Peak:0.000} smtc={src.Smtc,-8} snd={(src.Sounding ? "Y" : "-")}");
        }
        foreach (PlayerView p in s.Players)
            Console.WriteLine($"  PLY {p.Exe,-23} sess={p.Session,-8} smtc={p.Smtc,-8} playing={(p.Playing ? "Y" : "N")}");
    }

    private static int SeedVolume(string spec, string endpointId)
    {
        int eq = spec.IndexOf('=');
        if (eq <= 0)
        {
            Console.WriteLine("用法：--set-volume <schemeId>=<0..1>");
            return 2;
        }

        string id = spec[..eq].Trim();
        if (!double.TryParse(spec[(eq + 1)..], out double value))
        {
            Console.WriteLine("音量必须是数字");
            return 2;
        }

        SchemeConfig? scheme = _cfg.FindScheme(id);
        if (scheme is null)
        {
            Console.WriteLine($"方案不存在：{id}");
            return 2;
        }

        scheme.Volume[endpointId] = Math.Clamp(value, 0, 1);
        ConfigStore.Save(_cfg, _dev);
        Console.WriteLine($"{id} @ {endpointId} = {value:0.000}");
        return 0;
    }

    private static void PrintConfig()
    {
        Console.WriteLine($"mode={_cfg.Mode} mutex={_cfg.MutexEnabled} default={_cfg.DefaultScheme}");
        Console.WriteLine("roles: " + string.Join(", ", _cfg.Roles.Select(kv => $"{kv.Key}={kv.Value}")));
        if (_cfg.CloseSignal.Count > 0)
            Console.WriteLine("closeSignal: " + string.Join(", ", _cfg.CloseSignal.Select(kv => $"{kv.Key}={kv.Value}")));
        foreach (SchemeConfig s in _cfg.Schemes)
            Console.WriteLine($"scheme {s.Id} \"{s.Name}\" members=[{string.Join(",", s.Members)}]");
        Console.WriteLine($"config: {ConfigStore.ConfigPath(_dev)}");
    }

    private sealed class Options
    {
        public bool Dev;
        public bool Once;
        public bool List;
        public bool Help;
        public bool NoRecovery;
        public int Seconds;
        public string? ModeOverride;
        public string? SetVolume;
    }

    private static Options ParseArgs(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--dev": o.Dev = true; break;
                case "--once": o.Once = true; break;
                case "--list": o.List = true; break;
                case "--help" or "-h": o.Help = true; break;
                case "--no-recovery": o.NoRecovery = true; break;
                case "--diag": break;
                case "--seconds": if (i + 1 < args.Length) o.Seconds = int.Parse(args[++i]); break;
                case "--mode": if (i + 1 < args.Length) o.ModeOverride = args[++i]; break;
                case "--set-volume": if (i + 1 < args.Length) o.SetVolume = args[++i]; break;
                default:
                    if (a.StartsWith("--")) Console.WriteLine($"未知参数：{a}");
                    break;
            }
        }
        return o;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            AutoVolume M2 diag
              --diag              控制台诊断（默认）
              --dev               使用 %LOCALAPPDATA%\AutoVolume-dev
              --once              打印一次快照后退出
              --seconds N         运行 N 秒后退出
              --mode strict|loose 覆盖恢复策略
              --no-recovery       禁用启动自愈
              --set-volume id=0.15  为当前输出设备写入方案音量
              --list              打印配置
            """);
    }
}
