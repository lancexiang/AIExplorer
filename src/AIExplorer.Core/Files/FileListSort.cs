namespace AIExplorer.Core.Files;

/// <summary>详情列表排序列。</summary>
public enum FileSortColumn
{
    Name,
    Size,
    Type,
    Modified,
}

/// <summary>判断路径是否适合自动递归算大小（本地盘；网络/UNC 不自动）。</summary>
public static class LocalPathPolicy
{
    public static bool IsLocalFixedOrRemovableDrive(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':')
            {
                return false;
            }

            var drive = new DriveInfo(root);
            return drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>置顶 → 文件夹 → 列键；用于单测与 UI 共用。</summary>
public static class FileListSort
{
    public static IOrderedEnumerable<T> OrderItems<T>(
        IEnumerable<T> source,
        FileSortColumn column,
        bool ascending,
        Func<T, bool> isPinned,
        Func<T, bool> isDirectory,
        Func<T, string> name,
        Func<T, long?> size,
        Func<T, string> type,
        Func<T, DateTime> modified)
    {
        var ordered = source
            .OrderByDescending(isPinned)
            .ThenByDescending(isDirectory);

        return column switch
        {
            FileSortColumn.Size => ascending
                ? ordered.ThenBy(x => size(x) ?? long.MaxValue).ThenBy(x => name(x), StringComparer.OrdinalIgnoreCase)
                : ordered.ThenByDescending(x => size(x) ?? long.MinValue).ThenByDescending(x => name(x), StringComparer.OrdinalIgnoreCase),
            FileSortColumn.Type => ascending
                ? ordered.ThenBy(type, StringComparer.OrdinalIgnoreCase).ThenBy(x => name(x), StringComparer.OrdinalIgnoreCase)
                : ordered.ThenByDescending(type, StringComparer.OrdinalIgnoreCase).ThenByDescending(x => name(x), StringComparer.OrdinalIgnoreCase),
            FileSortColumn.Modified => ascending
                ? ordered.ThenBy(modified).ThenBy(x => name(x), StringComparer.OrdinalIgnoreCase)
                : ordered.ThenByDescending(modified).ThenByDescending(x => name(x), StringComparer.OrdinalIgnoreCase),
            _ => ascending
                ? ordered.ThenBy(x => name(x), StringComparer.OrdinalIgnoreCase)
                : ordered.ThenByDescending(x => name(x), StringComparer.OrdinalIgnoreCase),
        };
    }
}
