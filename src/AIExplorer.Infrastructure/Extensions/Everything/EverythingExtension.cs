using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using AIExplorer.Core.Extensions;
using AIExplorer.Core.Settings;

namespace AIExplorer.Infrastructure.Extensions.Everything;

/// <summary>
/// Everything 第一批必接扩展：通过 Everything IPC（Everything64.dll）搜索。
/// 未安装 / 服务未运行时 IsAvailable=false，宿主应降级。
/// </summary>
public sealed class EverythingExtension : IExplorerExtension, ISearchProvider
{
    public const string ExtensionId = "everything";

    private IExtensionContext? _context;
    private bool _initialized;

    public IExtensionManifest Manifest { get; } = new EverythingManifest();

    public string ProviderId => ExtensionId;

    public bool IsAvailable => EverythingIpc.IsEverythingAvailable();

    public Task InitializeAsync(IExtensionContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        _initialized = true;
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        _initialized = false;
        _context = null;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<SearchHit> SearchAsync(
        SearchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_initialized || !IsAvailable || string.IsNullOrWhiteSpace(request.Query))
        {
            yield break;
        }

        var hits = await Task.Run(
            () => EverythingIpc.Search(request.Query, request.ScopePath, request.MaxResults),
            cancellationToken).ConfigureAwait(false);

        foreach (var hit in hits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return hit;
        }
    }

    private sealed class EverythingManifest : IExtensionManifest
    {
        public string Id => ExtensionId;
        public string DisplayName => "Everything 搜索";
        public ExtensionKind Kind => ExtensionKind.Native;
        public IReadOnlyList<ExtensionCapability> Capabilities { get; } = [ExtensionCapability.Search];
        /// <summary>第一批必接：默认启用；真正可用性仍取决于 Everything 是否在运行。</summary>
        public bool DefaultEnabled => true;
    }
}

/// <summary>Everything IPC 薄封装（Everything SDK 风格）。</summary>
internal static class EverythingIpc
{
    private const int EverythingOk = 0;
    private const int RequestFileName = 0x00000001;
    private const int RequestPath = 0x00000002;
    private const int RequestSize = 0x00000010;
    private const int RequestAttributes = 0x00000100;

    public static bool IsEverythingAvailable()
    {
        try
        {
            return Everything_IsDBLoaded() != 0;
        }
        catch
        {
            return false;
        }
    }

    public static List<SearchHit> Search(string query, string? scopePath, int maxResults)
    {
        var results = new List<SearchHit>();
        try
        {
            Everything_Reset();
            var search = string.IsNullOrWhiteSpace(scopePath)
                ? query
                : $"{scopePath.TrimEnd('\\')}\\ {query}";

            Everything_SetSearchW(search);
            Everything_SetRequestFlags(RequestFileName | RequestPath | RequestSize | RequestAttributes);
            Everything_SetMax((uint)Math.Max(1, maxResults));

            if (Everything_QueryW(true) == 0)
            {
                return results;
            }

            var count = (int)Everything_GetNumResults();
            for (uint i = 0; i < count; i++)
            {
                var pathBuilder = new StringBuilder(520);
                Everything_GetResultFullPathNameW(i, pathBuilder, (uint)pathBuilder.Capacity);
                var fullPath = pathBuilder.ToString();
                if (string.IsNullOrWhiteSpace(fullPath))
                {
                    continue;
                }

                var isDir = Everything_IsFolderResult(i) != 0;
                long? size = null;
                if (!isDir && Everything_GetResultSize(i, out var fileSize) != 0)
                {
                    size = fileSize;
                }

                results.Add(new SearchHit
                {
                    FullPath = fullPath,
                    Name = Path.GetFileName(fullPath.TrimEnd('\\')) is { Length: > 0 } name
                        ? name
                        : fullPath,
                    IsDirectory = isDir,
                    Size = size,
                });
            }
        }
        catch
        {
            // DLL 缺失或 IPC 失败时静默降级
        }

        return results;
    }

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern uint Everything_SetSearchW(string search);

    [DllImport("Everything64.dll")]
    private static extern void Everything_SetRequestFlags(uint flags);

    [DllImport("Everything64.dll")]
    private static extern void Everything_SetMax(uint max);

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern int Everything_QueryW(bool wait);

    [DllImport("Everything64.dll")]
    private static extern uint Everything_GetNumResults();

    [DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
    private static extern uint Everything_GetResultFullPathNameW(uint index, StringBuilder buf, uint size);

    [DllImport("Everything64.dll")]
    private static extern int Everything_IsFolderResult(uint index);

    [DllImport("Everything64.dll")]
    private static extern int Everything_GetResultSize(uint index, out long size);

    [DllImport("Everything64.dll")]
    private static extern void Everything_Reset();

    [DllImport("Everything64.dll")]
    private static extern int Everything_IsDBLoaded();
}
