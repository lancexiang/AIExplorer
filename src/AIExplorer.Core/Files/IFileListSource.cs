using System.Runtime.CompilerServices;

namespace AIExplorer.Core.Files;

/// <summary>
/// 增量目录枚举。实现必须只做 stat，不得打开/解码文件内容。
/// </summary>
public interface IFileListSource
{
    /// <summary>边枚举边 yield，便于 UI 首屏尽快显示。</summary>
    IAsyncEnumerable<FileEntrySnapshot> EnumerateIncrementalAsync(
        string path,
        CancellationToken cancellationToken = default);

    string? GetParentPath(string path);
}

public interface IFileSystemService
{
    bool CanUndoDelete { get; }

    Task OpenPathAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>将源路径复制或移动到目标目录；同名冲突时自动重命名（兼容旧调用/测试）。</summary>
    Task<IReadOnlyList<string>> CopyOrMoveAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        bool move,
        CancellationToken cancellationToken = default);

    /// <summary>执行已解析的复制/移动清单（含覆盖）；返回实际写入的目标路径。</summary>
    Task<IReadOnlyList<string>> ExecuteTransferAsync(
        IReadOnlyList<FileTransferOperation> operations,
        bool move,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(IReadOnlyList<string> paths, bool toRecycleBin = true, CancellationToken cancellationToken = default);

    Task UndoLastDeleteAsync(CancellationToken cancellationToken = default);
}

public enum ClipboardPasteKind
{
    None,
    InternalFilePaths,
    StorageItems,
    Text,
}

/// <summary>只按剪贴板格式判断粘贴语义；纯文本即使长得像路径，也始终按文本处理。</summary>
public static class ClipboardPastePolicy
{
    public static ClipboardPasteKind Resolve(
        bool hasInternalFilePaths,
        bool hasStorageItems,
        bool hasText) =>
        hasInternalFilePaths ? ClipboardPasteKind.InternalFilePaths
        : hasStorageItems ? ClipboardPasteKind.StorageItems
        : hasText ? ClipboardPasteKind.Text
        : ClipboardPasteKind.None;
}

public enum FilePreviewKind
{
    None,
    Directory,
    Image,
    Text,
    Unsupported,
}

/// <summary>侧栏/空格预览可展示的文件类型判定（与 UI 共用）。</summary>
public static class FilePreviewPolicy
{
    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".ico",
    };

    private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".xml", ".csv", ".log", ".ini", ".cs", ".py", ".js", ".ts", ".xaml", ".csproj",
    };

    public const long MaxTextPreviewBytes = 512 * 1024;

    public static FilePreviewKind Resolve(string path, bool isDirectory, long fileLength = 0)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return FilePreviewKind.None;
        }

        if (isDirectory)
        {
            return FilePreviewKind.Directory;
        }

        var ext = Path.GetExtension(path);
        if (ImageExts.Contains(ext))
        {
            return FilePreviewKind.Image;
        }

        if (TextExts.Contains(ext) && fileLength <= MaxTextPreviewBytes)
        {
            return FilePreviewKind.Text;
        }

        if (TextExts.Contains(ext))
        {
            return FilePreviewKind.Unsupported;
        }

        return FilePreviewKind.Unsupported;
    }
}
