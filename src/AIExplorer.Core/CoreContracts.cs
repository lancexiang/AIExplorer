using AIExplorer.Core.Extensions;
using AIExplorer.Core.Favorites;
using AIExplorer.Core.Files;
using AIExplorer.Core.Metadata;
using AIExplorer.Core.Session;
using AIExplorer.Core.Settings;
using AIExplorer.Core.Shell;

namespace AIExplorer.Core;

/// <summary>Core 层服务契约汇总，便于 DI 与文档对照。</summary>
public static class CoreContracts
{
    public static readonly Type[] Required =
    [
        typeof(IFileListSource),
        typeof(IFileSystemService),
        typeof(IFavoritesRepository),
        typeof(ISessionStore),
        typeof(IFileMetadataStore),
        typeof(IShellIconService),
        typeof(IShellContextMenuService),
        typeof(ISettingsService),
        typeof(IExtensionHost),
        typeof(IExtensionBus),
    ];
}
