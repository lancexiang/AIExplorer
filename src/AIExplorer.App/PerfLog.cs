using System.Diagnostics;

namespace AIExplorer_App;

/// <summary>
/// 轻量性能日志：Debug 输出 + 追加到 %LocalAppData%\AIExplorer\logs\perf.log。
/// 只记耗时，不落敏感路径内容时可自行截断。
/// </summary>
internal static class PerfLog
{
    private static readonly object Gate = new();
    private static string? _logPath;

    public static Scope Measure(string name) => new(name);

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        Debug.WriteLine("[Perf] " + line);
        try
        {
            _logPath ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AIExplorer",
                "logs",
                "perf.log");
            var dir = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            lock (Gate)
            {
                // 简单截断：超过 ~512KB 则轮转
                if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 512 * 1024)
                {
                    var bak = _logPath + ".1";
                    File.Copy(_logPath, bak, overwrite: true);
                    File.WriteAllText(_logPath, string.Empty);
                }

                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly string _name;
        private readonly long _start;

        public Scope(string name)
        {
            _name = name;
            _start = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            var ms = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            // 16ms 以下属一帧内，仍记录便于对齐慢路径；用标记区分
            Write(ms >= 50
                ? $"{_name}: {ms:F0}ms ⚠"
                : $"{_name}: {ms:F1}ms");
        }
    }
}
