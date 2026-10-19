using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace AutoVolume.App;

/// <summary>
/// 应用图标：优先读 exe 旁边的 app.ico（可热替换），没有再读程序集内嵌资源，
/// 最后才从 exe 里抽。托盘要 16px 清晰，必须拿到多尺寸 ICO 而不是 32px 缩略图。
/// </summary>
internal static class AppIcon
{
    private const string ResourceUri = "pack://application:,,,/app.ico";

    public static Drawing.Icon? LoadTray()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(path)) return new Drawing.Icon(path);
        }
        catch { }

        try
        {
            Stream? stream = Application.GetResourceStream(new Uri(ResourceUri))?.Stream;
            if (stream is not null) return new Drawing.Icon(stream);
        }
        catch { }

        try
        {
            if (Environment.ProcessPath is string exe) return Drawing.Icon.ExtractAssociatedIcon(exe);
        }
        catch { }

        return null;
    }

    public static BitmapImage? LoadImage()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(path)) return new BitmapImage(new Uri(path));
        }
        catch { }

        try { return new BitmapImage(new Uri(ResourceUri)); }
        catch { return null; }
    }
}
