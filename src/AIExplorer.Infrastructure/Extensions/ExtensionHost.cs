using AIExplorer.Core.Extensions;
using AIExplorer.Core.Settings;

namespace AIExplorer.Infrastructure.Extensions;

public sealed class ExtensionBus : IExtensionBus
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, List<Delegate>> _handlers = [];

    public void Publish<T>(T message)
    {
        List<Delegate>? snapshot;
        lock (_gate)
        {
            if (!_handlers.TryGetValue(typeof(T), out var list))
            {
                return;
            }

            snapshot = [.. list];
        }

        foreach (var handler in snapshot)
        {
            ((Action<T>)handler)(message);
        }
    }

    public IDisposable Subscribe<T>(Action<T> handler)
    {
        lock (_gate)
        {
            if (!_handlers.TryGetValue(typeof(T), out var list))
            {
                list = [];
                _handlers[typeof(T)] = list;
            }

            list.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                if (_handlers.TryGetValue(typeof(T), out var list))
                {
                    list.Remove(handler);
                }
            }
        });
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }
}

public sealed class ExtensionContext : IExtensionContext
{
    private readonly IExtensionBus _bus;

    public ExtensionContext(ISettingsService settings, IServiceProvider services, IExtensionBus bus)
    {
        Settings = settings;
        Services = services;
        _bus = bus;
    }

    public ISettingsService Settings { get; }
    public IServiceProvider Services { get; }
    public void Publish<T>(T message) => _bus.Publish(message);
}

public sealed class ExtensionHost : IExtensionHost
{
    private readonly ISettingsService _settings;
    private readonly IServiceProvider _services;
    private readonly IExtensionBus _bus;
    private readonly List<IExplorerExtension> _registered;
    private readonly Dictionary<string, IExplorerExtension> _loaded = new(StringComparer.OrdinalIgnoreCase);

    public ExtensionHost(
        ISettingsService settings,
        IServiceProvider services,
        IExtensionBus bus,
        IEnumerable<IExplorerExtension> extensions)
    {
        _settings = settings;
        _services = services;
        _bus = bus;
        _registered = extensions.ToList();
    }

    public IReadOnlyList<IExtensionManifest> Discover() =>
        _registered.Select(x => x.Manifest).ToList();

    public bool IsEnabled(string extensionId) => _settings.IsExtensionEnabled(extensionId);

    public async Task InitializeEnabledAsync(CancellationToken cancellationToken = default)
    {
        foreach (var extension in _registered)
        {
            if (!IsEnabled(extension.Manifest.Id))
            {
                continue;
            }

            await LoadAsync(extension.Manifest.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IExplorerExtension?> LoadAsync(string extensionId, CancellationToken cancellationToken = default)
    {
        if (_loaded.TryGetValue(extensionId, out var existing))
        {
            return existing;
        }

        var extension = _registered.FirstOrDefault(x =>
            string.Equals(x.Manifest.Id, extensionId, StringComparison.OrdinalIgnoreCase));
        if (extension is null)
        {
            return null;
        }

        var context = new ExtensionContext(_settings, _services, _bus);
        await extension.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
        _loaded[extensionId] = extension;
        return extension;
    }

    public void Unload(string extensionId)
    {
        if (!_loaded.TryGetValue(extensionId, out var extension))
        {
            return;
        }

        _ = extension.ShutdownAsync();
        _loaded.Remove(extensionId);
    }

    public T? GetCapability<T>() where T : class =>
        GetCapabilities<T>().FirstOrDefault();

    public IReadOnlyList<T> GetCapabilities<T>() where T : class
    {
        var result = new List<T>();
        foreach (var extension in _loaded.Values)
        {
            if (extension is T capability)
            {
                result.Add(capability);
            }
        }

        return result;
    }
}
