namespace AIExplorer.Core.Files;

/// <summary>路径同名冲突工具。</summary>
public static class FilePathConflict
{
    public static bool TargetExists(string path, bool preferDirectory) =>
        preferDirectory ? Directory.Exists(path) || File.Exists(path) : File.Exists(path) || Directory.Exists(path);

    public static string EnsureUniquePath(string dest, bool isDirectory)
    {
        if (!(isDirectory ? Directory.Exists(dest) : File.Exists(dest)))
        {
            return dest;
        }

        var dir = Path.GetDirectoryName(dest) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(dest);
        var ext = isDirectory ? string.Empty : Path.GetExtension(dest);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{baseName} ({i}){ext}");
            if (!(isDirectory ? Directory.Exists(candidate) : File.Exists(candidate)))
            {
                return candidate;
            }
        }

        return dest;
    }
}

/// <summary>单条已解析的复制/移动操作（目标路径与覆盖策略由 UI 冲突询问后确定）。</summary>
public sealed class FileTransferOperation
{
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }

    /// <summary>目标已存在时覆盖：文件直接覆盖；文件夹先删除再拷贝。</summary>
    public bool Overwrite { get; init; }
}

/// <summary>同名冲突时的用户选择。</summary>
public enum FileConflictAction
{
    CancelAll,
    Replace,
    Skip,
    Rename,
}

public sealed class FileConflictPrompt
{
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }
    public required string DisplayName { get; init; }
    public bool IsDirectory { get; init; }
    public int ConflictIndex { get; init; }
    public int ConflictTotal { get; init; }
}

public sealed class FileConflictDecision
{
    public required FileConflictAction Action { get; init; }
    public bool ApplyToAll { get; init; }
}
