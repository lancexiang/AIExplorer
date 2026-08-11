namespace AIExplorer.Core.Metadata;

public sealed class FileMetadataRecord
{
    public required string Path { get; init; }
    public bool IsPinned { get; set; }
    public string? ColorKey { get; set; }
    public string? Note { get; set; }
    public int? Progress { get; set; }
}

/// <summary>用户元数据存储。打开目录时必须支持批量查询。</summary>
public interface IFileMetadataStore
{
    Task<IReadOnlyDictionary<string, FileMetadataRecord>> GetByPathsAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(FileMetadataRecord record, CancellationToken cancellationToken = default);
    Task DeleteAsync(string path, CancellationToken cancellationToken = default);
}
