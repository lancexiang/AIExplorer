using System.Text.Json;
using AIExplorer.Core.Metadata;

namespace AIExplorer.Infrastructure.Metadata;

/// <summary>Phase 0.5：JSON 批量元数据；Phase 3 可换 SQLite。</summary>
public sealed class JsonFileMetadataStore : IFileMetadataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _storagePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, FileMetadataRecord> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public JsonFileMetadataStore()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIExplorer");
        Directory.CreateDirectory(folder);
        _storagePath = Path.Combine(folder, "file-metadata.json");
    }

    public async Task<IReadOnlyDictionary<string, FileMetadataRecord>> GetByPathsAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        var result = new Dictionary<string, FileMetadataRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (_cache.TryGetValue(path, out var record))
            {
                result[path] = record;
            }
        }

        return result;
    }

    public async Task UpsertAsync(FileMetadataRecord record, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnlockedAsync(cancellationToken).ConfigureAwait(false);
            _cache[record.Path] = record;
            await PersistUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnlockedAsync(cancellationToken).ConfigureAwait(false);
            _cache.Remove(path);
            await PersistUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task EnsureLoadedUnlockedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        if (File.Exists(_storagePath))
        {
            try
            {
                await using var stream = File.OpenRead(_storagePath);
                var list = await JsonSerializer.DeserializeAsync<List<FileMetadataRecord>>(stream, JsonOptions, cancellationToken);
                if (list is not null)
                {
                    _cache = list.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch
            {
                _cache = new Dictionary<string, FileMetadataRecord>(StringComparer.OrdinalIgnoreCase);
            }
        }

        _loaded = true;
    }

    private async Task PersistUnlockedAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_storagePath);
        await JsonSerializer.SerializeAsync(stream, _cache.Values.ToList(), JsonOptions, cancellationToken);
    }
}
