using System.Collections.ObjectModel;
using AIExplorer.Core.Files;
using AIExplorer.Core.Metadata;
using AIExplorer.Core.Settings;
using AIExplorer.Core.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIExplorer_App.ViewModels;

/// <summary>
/// 分栏内的独立标签组（类似 VS Code 的 EditorGroup）。
/// 包含若干 FilePaneViewModel，有自己的活跃索引，彼此完全独立。
/// </summary>
public partial class PaneGroupViewModel : ObservableObject, IDisposable
{
    private readonly IFileListSource _fileListSource;
    private readonly IFileSystemService _fileSystemService;
    private readonly IShellIconService _shellIconService;
    private readonly IFileMetadataStore _metadataStore;
    private readonly ISettingsService _settings;
    private readonly FileColumnLayout _columns;

    public PaneGroupViewModel(
        IFileListSource fileListSource,
        IFileSystemService fileSystemService,
        IShellIconService shellIconService,
        IFileMetadataStore metadataStore,
        ISettingsService settings,
        FileColumnLayout columns,
        string initialPath)
    {
        _fileListSource = fileListSource;
        _fileSystemService = fileSystemService;
        _shellIconService = shellIconService;
        _metadataStore = metadataStore;
        _settings = settings;
        _columns = columns;

        var first = CreatePane(initialPath);
        Panes.Add(first);
        ActiveIndex = 0;
    }

    public ObservableCollection<FilePaneViewModel> Panes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivePane))]
    [NotifyPropertyChangedFor(nameof(Title))]
    private int activeIndex;

    public FilePaneViewModel? ActivePane =>
        ActiveIndex >= 0 && ActiveIndex < Panes.Count ? Panes[ActiveIndex] : null;

    public string Title => GetShortName(ActivePane?.CurrentPath);

    /// <summary>在组内新建一个 Pane，路径复制自当前活跃 Pane；由 UI 层调用。</summary>
    /// <param name="loadContent">false=只建 VM（右侧 + 先出蓝头），内容稍后 Seed/Load。</param>
    public FilePaneViewModel AddPane(string? path = null, bool loadContent = true)
    {
        var target = path ?? ActivePane?.CurrentPath ?? string.Empty;
        var pane = CreatePane(target);
        Panes.Add(pane);
        ActiveIndex = Panes.Count - 1;
        if (loadContent)
        {
            _ = pane.LoadAsync();
        }

        return pane;
    }

    /// <summary>关闭指定 Pane；若只剩一个则不关闭。</summary>
    public void ClosePane(FilePaneViewModel pane)
    {
        if (Panes.Count <= 1)
        {
            return;
        }

        var idx = Panes.IndexOf(pane);
        if (idx < 0)
        {
            return;
        }

        pane.NavigationRequested -= OnPaneNavigated;
        Panes.RemoveAt(idx);
        ActiveIndex = Math.Clamp(ActiveIndex, 0, Panes.Count - 1);
    }

    public async Task InitializeAsync()
    {
        if (ActivePane is { } p)
        {
            await p.LoadAsync();
        }
    }

    /// <summary>重置为仅含一个标签（分栏时右侧从左侧当前路径复制）。</summary>
    public void ResetToSinglePane(string path)
    {
        foreach (var p in Panes.ToList())
        {
            p.NavigationRequested -= OnPaneNavigated;
        }

        Panes.Clear();
        var pane = CreatePane(path);
        Panes.Add(pane);
        ActiveIndex = 0;
        _ = pane.LoadAsync();
    }

    private FilePaneViewModel CreatePane(string path)
    {
        var pane = new FilePaneViewModel(
            _fileListSource, _fileSystemService, _shellIconService,
            _metadataStore, _settings, _columns, path);
        pane.NavigationRequested += OnPaneNavigated;
        return pane;
    }

    private void OnPaneNavigated(string _)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ActivePane));
    }

    public static string GetShortName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "新标签";
        }

        try
        {
            var trimmed = path.TrimEnd('\\', '/');
            if (trimmed.Length >= 2 && trimmed[1] == ':' && trimmed.Length <= 2)
            {
                // H: / C: 盘符根
                return trimmed.ToUpperInvariant() + @"\";
            }

            return Path.GetFileName(trimmed) is { Length: > 0 } n ? n : path;
        }
        catch
        {
            return path;
        }
    }

    public void Dispose()
    {
        foreach (var p in Panes)
        {
            p.NavigationRequested -= OnPaneNavigated;
        }
    }
}
