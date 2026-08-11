using AIExplorer.Core.Files;
using AIExplorer.Core.Metadata;
using AIExplorer.Core.Navigation;
using AIExplorer.Core.Settings;
using AIExplorer.Core.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIExplorer_App.ViewModels;

public enum DualPaneOrientation
{
    Horizontal,
    Vertical,
}

public partial class BrowserTabViewModel : ObservableObject, IDisposable
{
    private readonly bool _ownsRightGroup;

    public BrowserTabViewModel(
        IFileListSource fileListSource,
        IFileSystemService fileSystemService,
        IShellIconService shellIconService,
        IFileMetadataStore metadataStore,
        ISettingsService settings,
        FileColumnLayout columns,
        string initialPath,
        PaneGroupViewModel? sharedRightGroup = null)
    {
        LeftGroup = new PaneGroupViewModel(fileListSource, fileSystemService, shellIconService, metadataStore, settings, columns, initialPath);
        if (sharedRightGroup is null)
        {
            RightGroup = new PaneGroupViewModel(fileListSource, fileSystemService, shellIconService, metadataStore, settings, columns, initialPath);
            _ownsRightGroup = true;
        }
        else
        {
            RightGroup = sharedRightGroup;
            _ownsRightGroup = false;
        }

        LeftGroup.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(Title)); OnPaneNavigation(LeftGroup.ActivePane?.CurrentPath ?? string.Empty); };
        if (_ownsRightGroup)
        {
            RightGroup.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(Title)); };
        }

        // 锁定改道注入到每个 Pane
        void WireGroup(PaneGroupViewModel g)
        {
            g.Panes.CollectionChanged += (_, _) =>
            {
                foreach (var p in g.Panes)
                {
                    p.TryRedirectNavigationAsync = RedirectIfLockedAsync;
                }
            };
            foreach (var p in g.Panes)
            {
                p.TryRedirectNavigationAsync = RedirectIfLockedAsync;
            }
        }

        WireGroup(LeftGroup);
        if (_ownsRightGroup)
        {
            WireGroup(RightGroup);
        }

        title = GetTitle(initialPath);
    }

    public PaneGroupViewModel LeftGroup { get; }
    public PaneGroupViewModel RightGroup { get; }

    /// <summary>向后兼容快捷访问：当前左侧活跃 Pane。</summary>
    public FilePaneViewModel LeftPane => LeftGroup.ActivePane!;

    /// <summary>向后兼容快捷访问：当前右侧活跃 Pane。</summary>
    public FilePaneViewModel RightPane => RightGroup.ActivePane!;

    /// <summary>锁定标签改道：由 MainPageViewModel 注入，打开新 tab。</summary>
    public Func<string, Task>? OpenPathInNewTabAsync { get; set; }

    [ObservableProperty]
    private string title = "新标签";

    [ObservableProperty]
    private bool isDualPane;

    [ObservableProperty]
    private bool isLocked;

    [ObservableProperty]
    private DualPaneOrientation orientation = DualPaneOrientation.Horizontal;

    public bool IsHorizontalSplit => Orientation == DualPaneOrientation.Horizontal;
    public int LeftPaneColumnSpan => IsDualPane ? 1 : 3;

    private async Task<bool> RedirectIfLockedAsync(string path)
    {
        if (!TabNavigationPolicy.ShouldOpenNewTab(IsLocked))
        {
            return false;
        }

        if (OpenPathInNewTabAsync is not null)
        {
            await OpenPathInNewTabAsync(path);
        }

        return true;
    }

    public async Task InitializeAsync()
    {
        await LeftGroup.InitializeAsync();
        if (IsDualPane)
        {
            await RightGroup.InitializeAsync();
        }
    }

    /// <summary>由壳层同步全局分栏状态（不在此重置右侧组）。</summary>
    public void ApplyShellSplit(bool isDualPane, DualPaneOrientation orientation)
    {
        Orientation = orientation;
        IsDualPane = isDualPane;
        OnPropertyChanged(nameof(LeftPaneColumnSpan));
        OnPropertyChanged(nameof(IsHorizontalSplit));
    }

    public async Task NavigateLeftPaneAsync(string path)
    {
        if (LeftGroup.ActivePane is { } pane)
        {
            await pane.NavigateToPathCommand.ExecuteAsync(path);
        }
    }

    public async Task NavigatePaneAsync(PaneSide side, string path)
    {
        var pane = side == PaneSide.Right ? RightGroup.ActivePane : LeftGroup.ActivePane;
        if (pane is not null)
        {
            await pane.NavigateToPathCommand.ExecuteAsync(path);
        }
    }

    partial void OnIsDualPaneChanged(bool value) => OnPropertyChanged(nameof(LeftPaneColumnSpan));
    partial void OnOrientationChanged(DualPaneOrientation value) => OnPropertyChanged(nameof(IsHorizontalSplit));

    private void OnPaneNavigation(string path) => Title = GetTitle(path);

    private static string GetTitle(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "新标签";
        }

        try
        {
            return Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } name ? name : path;
        }
        catch
        {
            return path;
        }
    }

    public void Dispose()
    {
        LeftGroup.Dispose();
        if (_ownsRightGroup)
        {
            RightGroup.Dispose();
        }
    }
}
