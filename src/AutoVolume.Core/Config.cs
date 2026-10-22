using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoVolume.Core;

public enum RecoveryMode
{
    Strict,
    Loose
}

public enum AppRole
{
    None,
    Source,
    Controlled
}

public sealed class SchemeConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>endpoint id → 系统主音量 0..1。缺 key = 该设备上首次进入，不动音量。</summary>
    public Dictionary<string, double> Volume { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Members { get; set; } = new();
}

public sealed class ControlConfig
{
    public string Method { get; set; } = "auto"; // auto | smtc | hotkey
    public string? Hotkey { get; set; }
}

public sealed class TimingConfig
{
    public int ConfirmMs { get; set; } = 1500;
    /// <summary>明显有声（峰值 ≥ FastPeakThreshold）时的快速确认时间；弱信号仍走 ConfirmMs。</summary>
    public int FastConfirmMs { get; set; } = 600;
    public double FastPeakThreshold { get; set; } = 0.02;
    /// <summary>是否启用快速确认。关闭后一律等满 ConfirmMs。</summary>
    public bool FastConfirmEnabled { get; set; } = true;
    public int SoundingHoldMs { get; set; } = 1500;
    /// <summary>峰值阈值：低于此值视为无声。实测速览预览视频峰值只有 0.005–0.01，0.01 会漏判。</summary>
    public double PeakThreshold { get; set; } = 0.003;
    /// <summary>暂停确认后再等多久升音量，避免音乐尾音被一起放大。</summary>
    public int PauseSettleMs { get; set; } = 200;
    public int RestoreMs { get; set; } = 3000;
    public int DeviceSuppressMs { get; set; } = 5000;
    public int AudioServiceSuppressMs { get; set; } = 10000;
    public int SmtcGoneConfirmMs { get; set; } = 500;
    public int FadeMs { get; set; } = 300;
    public int ActivePollMs { get; set; } = 50;
    public int IdlePollMs { get; set; } = 500;
    public double UserTolerance { get; set; } = 0.02;
    public int UserWindowMs { get; set; } = 500;
}

public sealed class SafetyConfig
{
    public string RestoreHotkey { get; set; } = "Ctrl+Alt+Shift+R";
    public bool RestoreOnExit { get; set; } = true;
}

public sealed class AutoVolumeConfig
{
    public string Mode { get; set; } = "strict";
    public bool MutexEnabled { get; set; } = true;
    public string DefaultScheme { get; set; } = "music";
    /// <summary>无 SMTC 的源（VLC 等）会话转 Inactive 是否算关闭。由 VLC spike 标定后决定默认值。</summary>
    public bool InactiveMeansClosedForNonSmtc { get; set; }
    public Dictionary<string, string> Roles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SchemeConfig> Schemes { get; set; } = new();
    public Dictionary<string, ControlConfig> Control { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>exe → 关闭判据：process（默认，进程退出）/ window（可见窗口消失，如 PowerToys 速览）</summary>
    public Dictionary<string, string> CloseSignal { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public TimingConfig Timing { get; set; } = new();
    public SafetyConfig Safety { get; set; } = new();

    [JsonIgnore]
    public RecoveryMode RecoveryMode =>
        string.Equals(Mode, "loose", StringComparison.OrdinalIgnoreCase) ? RecoveryMode.Loose : RecoveryMode.Strict;

    public void Normalize()
    {
        Roles = new Dictionary<string, string>(Roles ?? new(), StringComparer.OrdinalIgnoreCase);
        Control = new Dictionary<string, ControlConfig>(Control ?? new(), StringComparer.OrdinalIgnoreCase);
        CloseSignal = new Dictionary<string, string>(CloseSignal ?? new(), StringComparer.OrdinalIgnoreCase);
        Schemes ??= new();
        foreach (var s in Schemes)
        {
            s.Volume = new Dictionary<string, double>(s.Volume ?? new(), StringComparer.OrdinalIgnoreCase);
            s.Members = (s.Members ?? new())
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        Timing ??= new();
        Safety ??= new();
    }

    public AppRole RoleOf(string exe)
    {
        foreach (var kv in Roles)
        {
            if (string.Equals(kv.Key, exe, StringComparison.OrdinalIgnoreCase))
                return string.Equals(kv.Value, "source", StringComparison.OrdinalIgnoreCase) ? AppRole.Source
                     : string.Equals(kv.Value, "controlled", StringComparison.OrdinalIgnoreCase) ? AppRole.Controlled
                     : AppRole.None;
        }
        return AppRole.None;
    }

    [JsonIgnore]
    public IEnumerable<string> Sources =>
        Roles.Where(kv => string.Equals(kv.Value, "source", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key);

    [JsonIgnore]
    public IEnumerable<string> Controlled =>
        Roles.Where(kv => string.Equals(kv.Value, "controlled", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key);

    public string? SchemeOf(string exe)
    {
        foreach (var s in Schemes)
        {
            if (s.Members.Any(m => string.Equals(m, exe, StringComparison.OrdinalIgnoreCase))) return s.Id;
        }
        return null;
    }

    /// <summary>窗口关闭信号（PowerToys 速览等进程常驻的源）。</summary>
    public bool UsesWindowCloseSignal(string exe)
    {
        foreach (var kv in CloseSignal)
        {
            if (string.Equals(kv.Key, exe, StringComparison.OrdinalIgnoreCase))
                return string.Equals(kv.Value, "window", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public SchemeConfig? FindScheme(string id) =>
        Schemes.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>校验角色冲突：同一 exe 不能既是源又是播放器；值必须合法。</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        foreach (var kv in Roles)
        {
            if (!string.Equals(kv.Value, "source", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(kv.Value, "controlled", StringComparison.OrdinalIgnoreCase))
                errors.Add($"角色值非法：{kv.Key} = {kv.Value}");
            if (!kv.Key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                errors.Add($"必须用 exe 名：{kv.Key}");
        }

        var dup = Schemes.GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key);
        foreach (string d in dup) errors.Add($"方案 id 重复：{d}");

        foreach (var kv in CloseSignal)
        {
            if (!string.Equals(kv.Value, "process", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(kv.Value, "window", StringComparison.OrdinalIgnoreCase))
                errors.Add($"关闭判据非法：{kv.Key} = {kv.Value}");
        }

        if (FindScheme(DefaultScheme) is null) errors.Add($"默认方案不存在：{DefaultScheme}");

        // 同一 exe 出现在不同方案的成员里 = 归属不确定
        var membership = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Schemes)
        {
            foreach (string m in s.Members)
            {
                if (membership.TryGetValue(m, out string? prev) && !string.Equals(prev, s.Id, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"应用 {m} 同时属于方案 {prev} 与 {s.Id}");
                else membership[m] = s.Id;
            }
        }
        return errors;
    }

    public static AutoVolumeConfig CreateDefault() => new()
    {
        Mode = "strict",
        MutexEnabled = true,
        DefaultScheme = "music",
        Roles = new(StringComparer.OrdinalIgnoreCase)
        {
            ["QQMusic.exe"] = "controlled",
            ["cloudmusic.exe"] = "controlled",
            ["msedge.exe"] = "source",
            ["vlc.exe"] = "source",
            ["PotPlayerMini64.exe"] = "source",
            ["PowerToys.Peek.UI.exe"] = "source"
        },
        Schemes =
        {
            new SchemeConfig
            {
                Id = "music", Name = "音乐",
                Members = { "QQMusic.exe", "cloudmusic.exe" }
            },
            new SchemeConfig
            {
                Id = "video", Name = "视频",
                Members = { "msedge.exe", "vlc.exe", "PotPlayerMini64.exe", "PowerToys.Peek.UI.exe" }
            }
        },
        CloseSignal = new(StringComparer.OrdinalIgnoreCase)
        {
            // 速览进程常驻，关闭预览后 SMTC 会话仍以 Paused 残留 → 只看窗口
            ["PowerToys.Peek.UI.exe"] = "window"
        },
        Control = new(StringComparer.OrdinalIgnoreCase)
        {
            ["QQMusic.exe"] = new ControlConfig { Method = "auto" },
            ["cloudmusic.exe"] = new ControlConfig { Method = "auto" }
        }
    };
}

public static class ConfigStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string RootDir(bool dev)
    {
        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string name = dev ? "AutoVolume-dev" : "AutoVolume";
        return Path.Combine(baseDir, name);
    }

    public static string ConfigPath(bool dev) => Path.Combine(RootDir(dev), "config.json");

    public static AutoVolumeConfig LoadOrCreate(bool dev)
    {
        string path = ConfigPath(dev);
        if (!File.Exists(path))
        {
            var cfg = AutoVolumeConfig.CreateDefault();
            Save(cfg, dev);
            return cfg;
        }

        string json = File.ReadAllText(path);
        var loaded = JsonSerializer.Deserialize<AutoVolumeConfig>(json, JsonOptions) ?? AutoVolumeConfig.CreateDefault();
        loaded.Normalize();

        // 兼容手写配置：mode 大小写、scheme id 缺省
        foreach (var s in loaded.Schemes)
        {
            if (string.IsNullOrWhiteSpace(s.Id)) s.Id = s.Name;
        }
        return loaded;
    }

    public static void Save(AutoVolumeConfig cfg, bool dev)
    {
        string path = ConfigPath(dev);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, JsonOptions));
        if (File.Exists(path))
            File.Replace(tmp, path, null);
        else
            File.Move(tmp, path);
    }
}
