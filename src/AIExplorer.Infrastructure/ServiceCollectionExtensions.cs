using AIExplorer.Core.Extensions;
using AIExplorer.Core.Favorites;
using AIExplorer.Core.Files;
using AIExplorer.Core.Metadata;
using AIExplorer.Core.Session;
using AIExplorer.Core.Settings;
using AIExplorer.Core.Shell;
using AIExplorer.Infrastructure.Extensions;
using AIExplorer.Infrastructure.Extensions.BuiltIn;
using AIExplorer.Infrastructure.Extensions.Everything;
using AIExplorer.Infrastructure.Favorites;
using AIExplorer.Infrastructure.Files;
using AIExplorer.Infrastructure.Metadata;
using AIExplorer.Infrastructure.Session;
using AIExplorer.Infrastructure.Settings;
using AIExplorer.Infrastructure.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace AIExplorer.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAIExplorerInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IFileListSource, ShellFileListSource>();
        services.AddSingleton<IFileSystemService, ShellFileSystemService>();
        services.AddSingleton<IFavoritesRepository, JsonFavoritesRepository>();
        services.AddSingleton<ISessionStore, JsonSessionStore>();
        services.AddSingleton<IFileMetadataStore, JsonFileMetadataStore>();
        services.AddSingleton<IShellIconService, ShellIconService>();
        services.AddSingleton<IShellContextMenuService, ShellContextMenuService>();
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<IExtensionBus, ExtensionBus>();

        // 第一批必接：Everything；本地文件名搜索作为降级
        services.AddSingleton<EverythingExtension>();
        services.AddSingleton<LocalNameSearchProvider>();
        services.AddSingleton<IExplorerExtension>(sp => sp.GetRequiredService<EverythingExtension>());
        services.AddSingleton<IExplorerExtension>(sp => sp.GetRequiredService<LocalNameSearchProvider>());

        services.AddSingleton<IExtensionHost, ExtensionHost>();

        return services;
    }
}
