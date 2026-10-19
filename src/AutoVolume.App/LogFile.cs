using System.IO;
using System.Text;

namespace AutoVolume.App;

/// <summary>
/// 按大小滚动的日志文件：app.log → app.1.log → app.2.log，单份上限 2 MB。
/// 写日志失败不能影响主流程，所有异常就地吞掉。
/// </summary>
public sealed class LogFile
{
    public const long MaxBytes = 2 * 1024 * 1024;
    public const int KeepFiles = 3; // app.log + app.1.log + app.2.log

    private readonly object _gate = new();
    private readonly string _dir;
    private readonly string _path;
    private long _size;

    public string FilePath => _path;

    public LogFile(string path)
    {
        _path = path;
        _dir = Path.GetDirectoryName(path) ?? ".";
        try
        {
            Directory.CreateDirectory(_dir);
            _size = File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch { _size = 0; }
    }

    public void Write(string message)
    {
        lock (_gate)
        {
            try
            {
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
                int bytes = Encoding.UTF8.GetByteCount(line);
                if (_size > 0 && _size + bytes > MaxBytes) Rotate();

                using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(line);
                _size += bytes;
            }
            catch { /* 日志失败静默 */ }
        }
    }

    /// <summary>app.log → app.1.log → app.2.log，最旧一份丢弃。</summary>
    private void Rotate()
    {
        try
        {
            string oldest = PathFor(KeepFiles - 1);
            if (File.Exists(oldest)) File.Delete(oldest);

            for (int i = KeepFiles - 2; i >= 1; i--)
            {
                string from = PathFor(i);
                if (File.Exists(from)) File.Move(from, PathFor(i + 1), overwrite: true);
            }

            if (File.Exists(_path)) File.Move(_path, PathFor(1), overwrite: true);
        }
        catch { }
        _size = 0;
    }

    private string PathFor(int index) =>
        index <= 0 ? _path : Path.Combine(_dir, $"app.{index}.log");
}
