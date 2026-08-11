using System.Text.Json;
using AIExplorer.Core.Settings;

namespace AIExplorer.Infrastructure.Settings;

public sealed class JsonSettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _storagePath;
    private SettingsDocument _document = new();

    public JsonSettingsService()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIExplorer");
        Directory.CreateDirectory(folder);
        _storagePath = Path.Combine(folder, "settings.json");
    }

    public FeatureFlags Features => _document.Features;

    public IList<FileColorDefinition> FileColors => _document.FileColors;

    public bool IsExtensionEnabled(string extensionId)
    {
        if (_document.Extensions.TryGetValue(extensionId, out var state))
        {
            return state.Enabled;
        }

        // Everything 第一批必接；本地搜索为降级，均默认启用
        if (string.Equals(extensionId, "everything", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extensionId, "local-name-search", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public void SetExtensionEnabled(string extensionId, bool enabled)
    {
        if (!_document.Extensions.TryGetValue(extensionId, out var state))
        {
            state = new ExtensionState();
            _document.Extensions[extensionId] = state;
        }

        state.Enabled = enabled;
    }

    public string? GetExtensionSetting(string extensionId, string key)
    {
        if (_document.Extensions.TryGetValue(extensionId, out var state) &&
            state.Values.TryGetValue(key, out var value))
        {
            return value;
        }

        return null;
    }

    public void SetExtensionSetting(string extensionId, string key, string? value)
    {
        if (!_document.Extensions.TryGetValue(extensionId, out var state))
        {
            state = new ExtensionState();
            _document.Extensions[extensionId] = state;
        }

        if (value is null)
        {
            state.Values.Remove(key);
        }
        else
        {
            state.Values[key] = value;
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_storagePath))
        {
            _document.FileColors = FileColorPalette.MergeWithDefaults(null);
            return;
        }

        try
        {
            await using var stream = File.OpenRead(_storagePath);
            var doc = await JsonSerializer.DeserializeAsync<SettingsDocument>(stream, JsonOptions, cancellationToken);
            if (doc is not null)
            {
                _document = doc;
            }
        }
        catch
        {
        }

        _document.FileColors = FileColorPalette.MergeWithDefaults(_document.FileColors);
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        _document.FileColors = FileColorPalette.MergeWithDefaults(_document.FileColors);
        await using var stream = File.Create(_storagePath);
        await JsonSerializer.SerializeAsync(stream, _document, JsonOptions, cancellationToken);
    }

    private sealed class SettingsDocument
    {
        public FeatureFlags Features { get; set; } = new();
        public List<FileColorDefinition> FileColors { get; set; } = FileColorPalette.MergeWithDefaults(null);
        public Dictionary<string, ExtensionState> Extensions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ExtensionState
    {
        public bool Enabled { get; set; }
        public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
