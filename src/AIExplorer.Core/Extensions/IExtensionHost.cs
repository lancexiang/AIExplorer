using AIExplorer.Core.Settings;

namespace AIExplorer.Core.Extensions;

public enum ExtensionKind
{
    BuiltIn,
    Native,
    Process,
}

[Flags]
public enum ExtensionCapability
{
    None = 0,
    Search = 1,
    Preview = 2,
    Navigation = 4,
    ContextMenu = 8,
    Toolbar = 16,
    Probe = 32,
    ScriptRunner = 64,
    Settings = 128,
}

public interface IExtensionManifest
{
    string Id { get; }
    string DisplayName { get; }
    ExtensionKind Kind { get; }
    IReadOnlyList<ExtensionCapability> Capabilities { get; }
    /// <summary>必须为 false（Built-in 基础能力除外）。</summary>
    bool DefaultEnabled { get; }
}

public interface IExplorerExtension
{
    IExtensionManifest Manifest { get; }
    Task InitializeAsync(IExtensionContext context, CancellationToken cancellationToken = default);
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}

public interface IExtensionContext
{
    ISettingsService Settings { get; }
    IServiceProvider Services { get; }
    void Publish<T>(T message);
}

public interface IExtensionHost
{
    IReadOnlyList<IExtensionManifest> Discover();
    Task InitializeEnabledAsync(CancellationToken cancellationToken = default);
    Task<IExplorerExtension?> LoadAsync(string extensionId, CancellationToken cancellationToken = default);
    void Unload(string extensionId);
    bool IsEnabled(string extensionId);
    T? GetCapability<T>() where T : class;
    IReadOnlyList<T> GetCapabilities<T>() where T : class;
}
