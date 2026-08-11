using System.Text;

namespace AIExplorer_App;

internal static class CrashLog
{
    private static readonly object Gate = new();

    public static string LogPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIExplorer",
            "crash.log");

    public static void Write(string source, Exception? ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var sb = new StringBuilder();
            sb.AppendLine("==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " ====");
            sb.AppendLine("Source: " + source);
            if (ex is null)
            {
                sb.AppendLine("(no exception object)");
            }
            else
            {
                sb.AppendLine(ex.ToString());
            }

            sb.AppendLine();

            lock (Gate)
            {
                File.AppendAllText(LogPath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // 写日志失败时不能再抛
        }
    }
}
