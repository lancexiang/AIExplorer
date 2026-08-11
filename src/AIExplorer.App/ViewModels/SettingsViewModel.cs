using System.Collections.ObjectModel;
using AIExplorer.Core.Extensions;
using AIExplorer.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AIExplorer_App.ViewModels;

public partial class ExtensionToggleItem : ObservableObject
{
    public ExtensionToggleItem(string id, string displayName, bool enabled)
    {
        Id = id;
        DisplayName = displayName;
        this.enabled = enabled;
    }

    public string Id { get; }
    public string DisplayName { get; }

    [ObservableProperty]
    private bool enabled;
}

public partial class FileColorEditItem : ObservableObject
{
    public FileColorEditItem(string key, string displayName, string hex, string description)
    {
        Key = key;
        this.displayName = displayName;
        this.hex = hex;
        this.description = description;
    }

    public string Key { get; }

    [ObservableProperty]
    private string displayName;

    [ObservableProperty]
    private string hex;

    [ObservableProperty]
    private string description;
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IExtensionHost _extensionHost;

    public SettingsViewModel(ISettingsService settings, IExtensionHost extensionHost)
    {
        _settings = settings;
        _extensionHost = extensionHost;
        Reload();
    }

    public ObservableCollection<ExtensionToggleItem> Extensions { get; } = [];
    public ObservableCollection<FileColorEditItem> FileColors { get; } = [];

    [ObservableProperty]
    private bool performanceMode;

    [ObservableProperty]
    private bool enableListThumbnails;

    [ObservableProperty]
    private bool enableImageProbeColumns;

    [ObservableProperty]
    private bool enableFolderRecursiveSize;

    [ObservableProperty]
    private bool copyMappedDriveAsUnc = true;

    [ObservableProperty]
    private bool autoRevealInTree = true;

    [ObservableProperty]
    private string theme = "Default";

    [ObservableProperty]
    private string accentColor = "Default";

    [ObservableProperty]
    private string windowBackdrop = WindowBackdropOptions.Mica;

    public IReadOnlyList<string> ThemeOptions { get; } = ["Default", "Light", "Dark"];

    public IReadOnlyList<string> AccentOptions => AccentPalette.Options;

    public IReadOnlyList<string> BackdropOptions => WindowBackdropOptions.All;

    public void Reload()
    {
        PerformanceMode = _settings.Features.PerformanceMode;
        EnableListThumbnails = _settings.Features.EnableListThumbnails;
        EnableImageProbeColumns = _settings.Features.EnableImageProbeColumns;
        EnableFolderRecursiveSize = _settings.Features.EnableFolderRecursiveSize;
        CopyMappedDriveAsUnc = _settings.Features.CopyMappedDriveAsUnc;
        AutoRevealInTree = _settings.Features.AutoRevealInTree;
        Theme = string.IsNullOrWhiteSpace(_settings.Features.Theme) ? "Default" : _settings.Features.Theme;
        AccentColor = string.IsNullOrWhiteSpace(_settings.Features.AccentColor) ? "Default" : _settings.Features.AccentColor;
        WindowBackdrop = WindowBackdropOptions.IsKnown(_settings.Features.WindowBackdrop)
            ? _settings.Features.WindowBackdrop
            : WindowBackdropOptions.Mica;

        Extensions.Clear();
        foreach (var manifest in _extensionHost.Discover())
        {
            Extensions.Add(new ExtensionToggleItem(
                manifest.Id,
                manifest.DisplayName,
                _settings.IsExtensionEnabled(manifest.Id)));
        }

        FileColors.Clear();
        foreach (var color in FileColorPalette.MergeWithDefaults(_settings.FileColors))
        {
            FileColors.Add(new FileColorEditItem(color.Key, color.DisplayName, color.Hex, color.Description));
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        _settings.Features.PerformanceMode = PerformanceMode;
        if (PerformanceMode)
        {
            // 性能模式：强制关闭重操作
            EnableListThumbnails = false;
            EnableImageProbeColumns = false;
            EnableFolderRecursiveSize = false;
        }

        _settings.Features.EnableListThumbnails = EnableListThumbnails;
        _settings.Features.EnableImageProbeColumns = EnableImageProbeColumns;
        _settings.Features.EnableFolderRecursiveSize = EnableFolderRecursiveSize;
        _settings.Features.CopyMappedDriveAsUnc = CopyMappedDriveAsUnc;
        _settings.Features.AutoRevealInTree = AutoRevealInTree;
        _settings.Features.Theme = Theme;
        _settings.Features.AccentColor = AccentColor;
        _settings.Features.WindowBackdrop = WindowBackdrop;

        foreach (var ext in Extensions)
        {
            _settings.SetExtensionEnabled(ext.Id, ext.Enabled);
        }

        _settings.FileColors.Clear();
        foreach (var row in FileColors)
        {
            _settings.FileColors.Add(new FileColorDefinition
            {
                Key = row.Key,
                DisplayName = row.DisplayName,
                Hex = row.Hex,
                Description = row.Description,
            });
        }

        await _settings.SaveAsync();
        ApplyTheme(Theme);
        ApplyAccent(AccentColor);
        ApplyBackdrop(WindowBackdrop);
    }

    public static void ApplyTheme(string theme)
    {
        if (App.Window?.Content is not FrameworkElement root)
        {
            return;
        }

        root.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    public static void ApplyAccent(string accent)
    {
        var resources = Application.Current.Resources;
        var rgb = AccentPalette.ResolveRgb(accent);
        if (rgb is null)
        {
            // 恢复应用级强调刷为系统强调色近似（浅蓝）
            resources["AppAccentBrush"] = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 212));
            return;
        }

        var (r, g, b) = rgb.Value;
        var color = Windows.UI.Color.FromArgb(255, r, g, b);
        resources["AppAccentBrush"] = new SolidColorBrush(color);
        // 覆盖部分系统强调色资源，使按钮/链接等一并变色
        resources["SystemAccentColor"] = color;
        resources["SystemAccentColorLight2"] = Windows.UI.Color.FromArgb(255,
            (byte)Math.Min(255, r + 40),
            (byte)Math.Min(255, g + 40),
            (byte)Math.Min(255, b + 40));
        resources["SystemAccentColorDark1"] = Windows.UI.Color.FromArgb(255,
            (byte)Math.Max(0, r - 30),
            (byte)Math.Max(0, g - 30),
            (byte)Math.Max(0, b - 30));
    }

    public static void ApplyBackdrop(string backdrop)
    {
        if (App.Window is MainWindow main)
        {
            main.ApplyBackdrop(backdrop);
        }
    }
}
