namespace AIExplorer.Core.Files;

/// <summary>文件系统 stat 层（列表首屏必有，禁止打开文件内容）。</summary>
public sealed class FileStat
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTimeOffset ModifiedTime { get; init; }
}

/// <summary>用户标签层（SQLite 批量加载）。</summary>
public sealed class FileUserTags
{
    public bool IsPinned { get; init; }
    public string? ColorKey { get; init; }
    public string? Note { get; init; }
    public int? Progress { get; init; }
}

/// <summary>探针层（可选列 / 扩展 Probe，默认 null）。</summary>
public sealed class FileProbe
{
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? PageCount { get; init; }
    public string? FormatHint { get; init; }
}

/// <summary>分层文件快照：Stat 必有，UserTags/Probe 可后续补齐。</summary>
public sealed class FileEntrySnapshot
{
    public required FileStat Stat { get; init; }
    public FileUserTags? UserTags { get; set; }
    public FileProbe? Probe { get; set; }

    public string Name => Stat.Name;
    public string FullPath => Stat.FullPath;
    public bool IsDirectory => Stat.IsDirectory;
    public long Size => Stat.Size;
    public DateTimeOffset ModifiedTime => Stat.ModifiedTime;

    public string IconGlyph => IsDirectory ? "\uE8B7" : "\uE7C3";

    public string Details
    {
        get
        {
            var time = ModifiedTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            if (IsDirectory)
            {
                return time;
            }

            return $"{FormatSize(Size)}  {time}";
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{size:0.##} {units[unit]}";
    }
}
