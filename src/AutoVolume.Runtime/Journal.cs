using System.Text.Json;
using AutoVolume.Core;

namespace AutoVolume.Runtime;

/// <summary>崩溃自愈：记录"我们暂停了谁、音量原来是多少"。</summary>
public sealed class Journal
{
    public string EndpointId { get; set; } = "";
    public double VolumeBefore { get; set; }
    public double VolumeSet { get; set; }
    public List<string> PausedPlayers { get; set; } = new();
    public string Scheme { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

public static class JournalStore
{
    private static string PathFor(bool dev) => Path.Combine(ConfigStore.RootDir(dev), "journal.json");

    public static Journal? Load(bool dev)
    {
        try
        {
            string path = PathFor(dev);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Journal>(File.ReadAllText(path), ConfigStore.JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(bool dev, Journal journal)
    {
        try
        {
            string path = PathFor(dev);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(journal, ConfigStore.JsonOptions));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch { /* 自愈文件写失败不影响主流程 */ }
    }

    public static void Clear(bool dev)
    {
        try
        {
            string path = PathFor(dev);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
