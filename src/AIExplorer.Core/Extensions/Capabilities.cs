using AIExplorer.Core.Files;
using AIExplorer.Core.Settings;

namespace AIExplorer.Core.Extensions;

public sealed class SearchRequest
{
    public required string Query { get; init; }
    public string? ScopePath { get; init; }
    public int MaxResults { get; init; } = 200;
}

public sealed class SearchHit
{
    public required string FullPath { get; init; }
    public required string Name { get; init; }
    public bool IsDirectory { get; init; }
    public long? Size { get; init; }
}

public interface ISearchProvider
{
    string ProviderId { get; }
    bool IsAvailable { get; }
    IAsyncEnumerable<SearchHit> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default);
}

public interface IPreviewProvider
{
    string ProviderId { get; }
    bool CanPreview(string path);
    Task PreviewAsync(string path, CancellationToken cancellationToken = default);
}

public interface INavigationProvider
{
    string ProviderId { get; }
    IReadOnlyList<string> GetQuickPaths();
}

public interface IProbeProvider
{
    string ProviderId { get; }
    bool CanProbe(string path);
    Task<FileProbe?> ProbeAsync(string path, CancellationToken cancellationToken = default);
}

public interface IScriptRunner
{
    string ProviderId { get; }
    Task RunAsync(IReadOnlyList<string> selectedPaths, CancellationToken cancellationToken = default);
}

public interface IExtensionBus
{
    void Publish<T>(T message);
    IDisposable Subscribe<T>(Action<T> handler);
}

public sealed class SelectionChangedMessage
{
    public required IReadOnlyList<string> Paths { get; init; }
}

public sealed class CurrentPathChangedMessage
{
    public required string Path { get; init; }
}

public sealed class SearchRequestedMessage
{
    public required SearchRequest Request { get; init; }
}

public sealed class PreviewRequestedMessage
{
    public required string Path { get; init; }
}
