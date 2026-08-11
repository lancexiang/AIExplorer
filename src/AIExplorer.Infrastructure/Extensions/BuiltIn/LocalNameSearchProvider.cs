using System.Runtime.CompilerServices;
using AIExplorer.Core.Extensions;
using AIExplorer.Core.Files;

namespace AIExplorer.Infrastructure.Extensions.BuiltIn;

/// <summary>无 Everything 时的文件名过滤降级搜索（仅当前目录，非全盘）。</summary>
public sealed class LocalNameSearchProvider : IExplorerExtension, ISearchProvider
{
    public const string ExtensionId = "local-name-search";

    private IExtensionContext? _context;

    public IExtensionManifest Manifest { get; } = new LocalNameSearchManifest();
    public string ProviderId => ExtensionId;
    public bool IsAvailable => true;

    public Task InitializeAsync(IExtensionContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        _context = null;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<SearchHit> SearchAsync(
        SearchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Query) || string.IsNullOrWhiteSpace(request.ScopePath))
        {
            yield break;
        }

        if (!Directory.Exists(request.ScopePath))
        {
            yield break;
        }

        var query = request.Query.Trim();
        var list = await Task.Run(() =>
        {
            var hits = new List<SearchHit>();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(request.ScopePath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(entry);
                    if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        hits.Add(new SearchHit
                        {
                            FullPath = entry,
                            Name = name,
                            IsDirectory = Directory.Exists(entry),
                        });
                        if (hits.Count >= request.MaxResults)
                        {
                            break;
                        }
                    }
                }
            }
            catch
            {
            }

            return hits;
        }, cancellationToken).ConfigureAwait(false);

        foreach (var hit in list)
        {
            yield return hit;
        }
    }

    private sealed class LocalNameSearchManifest : IExtensionManifest
    {
        public string Id => ExtensionId;
        public string DisplayName => "本地文件名搜索";
        public ExtensionKind Kind => ExtensionKind.BuiltIn;
        public IReadOnlyList<ExtensionCapability> Capabilities { get; } = [ExtensionCapability.Search];
        public bool DefaultEnabled => true;
    }
}
