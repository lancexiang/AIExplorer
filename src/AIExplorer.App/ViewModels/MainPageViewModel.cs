using System.Collections.ObjectModel;
using System.Diagnostics;
using AIExplorer.Core.Extensions;
using AIExplorer.Core.Favorites;
using AIExplorer.Core.Files;
using AIExplorer.Core.Metadata;
using AIExplorer.Core.Navigation;
using AIExplorer.Core.Session;
using AIExplorer.Core.Settings;
using AIExplorer.Core.Shell;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windows.Storage.Pickers;

namespace AIExplorer_App.ViewModels;

public partial class MainPageViewModel : ObservableObject
{
    private readonly IFavoritesRepository _favoritesRepository;
    private readonly IFileListSource _fileListSource;
    private readonly IFileSystemService _fileSystemService;
    private readonly IShellIconService _shellIconService;
    private readonly IExtensionHost _extensionHost;
    private readonly ISessionStore _sessionStore;
    private readonly IFileMetadataStore _metadataStore;
    private readonly ISettingsService _settings;
    private readonly FileColumnLayout _columns;
    private CancellationTokenSource? _persistCts;
    private bool _restoringSession;
    private bool _isSecondaryWindow;
    private bool _suppressTabReorder;
    /// <summary>右侧 + 建头期间：禁止 ActiveIndex 联动 TrackPath/历史树/落盘抖动。</summary>
    public bool SuppressShellRightSideEffects { get; set; }

    public MainPageViewModel(
        IFavoritesRepository favoritesRepository,
        IFileListSource fileListSource,
        IFileSystemService fileSystemService,
        IShellIconService shellIconService,
        IExtensionHost extensionHost,
        ISessionStore sessionStore,
        IFileMetadataStore metadataStore,
        ISettingsService settings,
        FileColumnLayout columns)
    {
        _favoritesRepository = favoritesRepository;
        _fileListSource = fileListSource;
        _fileSystemService = fileSystemService;
        _shellIconService = shellIconService;
        _extensionHost = extensionHost;
        _sessionStore = sessionStore;
        _metadataStore = metadataStore;
        _settings = settings;
        _columns = columns;
        Tabs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasTabs));
            SchedulePersistSession();
        };
        NavigationPane = new NavigationPaneViewModel();

        // 全局右侧标签组：所有左 Tab 共享，分栏开启后切换左 Tab 不关掉右栏
        ShellRightGroup = new PaneGroupViewModel(
            fileListSource,
            fileSystemService,
            shellIconService,
            metadataStore,
            settings,
            columns,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Workspace = new WorkspaceViewModel(
            fileListSource,
            fileSystemService,
            shellIconService,
            metadataStore,
            settings,
            columns,
            ShellRightGroup);
        Workspace.LayoutChanged += () =>
        {
            IsDualPane = Workspace.IsSplit;
            Orientation = Workspace.Orientation;
        };
        WireSharedRightGroup();
    }

    public ObservableCollection<BrowserTabViewModel> Tabs { get; } = [];
    public NavigationPaneViewModel NavigationPane { get; }

    /// <summary>壳层共享的右侧独立标签组（= Workspace 第一副栏）。</summary>
    public PaneGroupViewModel ShellRightGroup { get; }

    /// <summary>线性 N 栏工作区。</summary>
    public WorkspaceViewModel Workspace { get; }

    /// <summary>书签栏顶层项（分组 + 叶子），Chrome 式水平展示。</summary>
    public ObservableCollection<FavoriteNodeViewModel> FavoriteBarItems { get; } = [];

    [ObservableProperty]
    private BrowserTabViewModel? selectedTab;

    /// <summary>全局分栏：与具体左 Tab 解耦，开启后切左 Tab 仍保持分栏。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHorizontalSplit))]
    private bool isDualPane;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHorizontalSplit))]
    private DualPaneOrientation orientation = DualPaneOrientation.Horizontal;

    public bool IsHorizontalSplit => IsDualPane && Orientation == DualPaneOrientation.Horizontal;

    [ObservableProperty]
    private PaneSide activePaneSide = PaneSide.Left;

    /// <summary>侧栏/书签/终端应落到的当前激活窗格。</summary>
    public FilePaneViewModel? ActiveFilePane
    {
        get
        {
            if (!IsDualPane || ActivePaneSide != PaneSide.Right)
            {
                return SelectedTab?.LeftPane;
            }

            var slot = Workspace.ActiveSlotIndex;
            if (slot > 0)
            {
                return Workspace.GetGroupAt(slot)?.ActivePane ?? ShellRightGroup.ActivePane;
            }

            return ShellRightGroup.ActivePane;
        }
    }

    [ObservableProperty]
    private FavoriteNodeViewModel? favoriteRoot;

    [ObservableProperty]
    private FavoriteNodeViewModel? selectedFavorite;

    [ObservableProperty]
    private string searchQuery = string.Empty;

    [ObservableProperty]
    private bool searchInCurrentFolder;

    /// <summary>Everything 可用时可切换「当前文件夹 / 全盘」；本地搜索时禁用。</summary>
    [ObservableProperty]
    private bool isSearchScopeEnabled = true;

    [ObservableProperty]
    private SearchResultsViewModel? activeSearch;

    [ObservableProperty]
    private string searchPlaceholder = "搜索文件…";

    [ObservableProperty]
    private string searchProviderHint = string.Empty;

    public bool HasTabs => Tabs.Count > 0;

    public ISearchProvider? ActiveSearchProvider
    {
        get
        {
            var providers = _extensionHost.GetCapabilities<ISearchProvider>();
            return providers.FirstOrDefault(p => p.ProviderId == "everything" && p.IsAvailable)
                ?? providers.FirstOrDefault(p => p.IsAvailable);
        }
    }

    public ObservableCollection<string> SearchSuggestions { get; } = [];

    public event Action? SearchSessionChanged;
    public event Action? NavigationPathTracked;
    /// <summary>ViewModel 切换了 SelectedTab 后，通知 UI 选中对应 TabViewItem（含从搜索跳转）。</summary>
    public event Action? RequestShowSelectedTab;

    private readonly List<string> _searchHistory = [];
    private static readonly string SearchHistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AIExplorer",
        "search-history.json");

    private void LoadSearchHistory()
    {
        try
        {
            if (!File.Exists(SearchHistoryPath))
            {
                return;
            }

            var json = File.ReadAllText(SearchHistoryPath);
            var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
            if (list is null)
            {
                return;
            }

            _searchHistory.Clear();
            _searchHistory.AddRange(list.Where(s => !string.IsNullOrWhiteSpace(s)).Take(30));
            RefreshSearchSuggestions(SearchQuery);
        }
        catch
        {
        }
    }

    private void RememberSearchQuery(string query)
    {
        _searchHistory.RemoveAll(s => string.Equals(s, query, StringComparison.OrdinalIgnoreCase));
        _searchHistory.Insert(0, query);
        while (_searchHistory.Count > 30)
        {
            _searchHistory.RemoveAt(_searchHistory.Count - 1);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SearchHistoryPath)!);
            File.WriteAllText(
                SearchHistoryPath,
                System.Text.Json.JsonSerializer.Serialize(_searchHistory, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }

        RefreshSearchSuggestions(query);
    }

    public void RefreshSearchSuggestions(string? prefix)
    {
        SearchSuggestions.Clear();
        IEnumerable<string> source = _searchHistory;
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            source = _searchHistory.Where(s => s.Contains(prefix, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in source.Take(10))
        {
            SearchSuggestions.Add(item);
        }
    }

    partial void OnSearchQueryChanged(string value) => RefreshSearchSuggestions(value);

    public async Task InitializeAsync()
    {
        await InitializeShellAsync();

        var session = await _sessionStore.LoadAsync();
        if (session?.Tabs is { Count: > 0 })
        {
            await RestoreSessionAsync(session);
        }
        else
        {
            await NewTabCommand.ExecuteAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }

        RefreshSearchProviderHint();
    }

    /// <summary>撕裂标签后的副窗口：加载导航壳，不恢复主会话，由调用方 Attach 撕裂的标签。</summary>
    public async Task InitializeSecondaryAsync()
    {
        _isSecondaryWindow = true;
        await InitializeShellAsync();
        RefreshSearchProviderHint();
    }

    private async Task InitializeShellAsync()
    {
        await _extensionHost.InitializeEnabledAsync();
        NavigationPane.Refresh();
        NavigationPane.LoadRecentPaths();
        NavigationPane.IsNavDrawerExpanded = _settings.Features.NavDrawerExpanded;
        var root = await _favoritesRepository.LoadAsync();
        FavoriteRoot = new FavoriteNodeViewModel(root);
        RebuildFavoriteBar();
        LoadSearchHistory();
    }

    public async Task PersistNavDrawerExpandedAsync()
    {
        _settings.Features.NavDrawerExpanded = NavigationPane.IsNavDrawerExpanded;
        await _settings.SaveAsync();
    }

    public async Task PersistSessionAsync()
    {
        // 副窗口不写主会话，避免覆盖主窗标签布局
        if (_restoringSession || _isSecondaryWindow)
        {
            return;
        }

        var state = new SessionState
        {
            SelectedIndex = SelectedTab is null ? 0 : Math.Max(0, Tabs.IndexOf(SelectedTab)),
            IsDualPane = IsDualPane,
            Orientation = Orientation.ToString(),
            ActivePaneSide = ActivePaneSide.ToString(),
            RightPanePaths = ShellRightGroup.Panes
                .Select(p => p.CurrentPath)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList(),
            RightSelectedIndex = ShellRightGroup.ActiveIndex,
            Tabs = Tabs.Select(t => new SessionTabState
            {
                LeftPath = t.LeftPane.CurrentPath,
                RightPath = ShellRightGroup.ActivePane?.CurrentPath,
                IsDualPane = IsDualPane,
                IsLocked = t.IsLocked,
                Orientation = Orientation.ToString(),
            }).ToList(),
        };

        await _sessionStore.SaveAsync(state);
    }

    private void SchedulePersistSession()
    {
        if (_restoringSession)
        {
            return;
        }

        _persistCts?.Cancel();
        _persistCts = new CancellationTokenSource();
        var token = _persistCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, token);
                await PersistSessionAsync();
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private async Task RestoreSessionAsync(SessionState session)
    {
        _restoringSession = true;
        try
        {
            // 兼容旧会话：任一 Tab 曾开分栏则视为全局开
            IsDualPane = session.IsDualPane || session.Tabs.Any(t => t.IsDualPane);
            if (Enum.TryParse<DualPaneOrientation>(session.Orientation, out var shellOrientation))
            {
                Orientation = shellOrientation;
            }
            else if (session.Tabs.FirstOrDefault(t => t.IsDualPane) is { } dualTab &&
                     Enum.TryParse<DualPaneOrientation>(dualTab.Orientation, out var fromTab))
            {
                Orientation = fromTab;
            }

            if (Enum.TryParse<PaneSide>(session.ActivePaneSide, out var side))
            {
                ActivePaneSide = side;
            }

            await RestoreRightGroupAsync(session);

            Workspace.Orientation = Orientation;
            if (IsDualPane)
            {
                Workspace.EnsureSecondaryAttached();
            }
            else
            {
                Workspace.CloseAllSecondary();
            }

            BrowserTabViewModel? restoreSelected = null;
            _suppressLockedTabSort = true;
            try
            {
                var tabIndex = 0;
                foreach (var tabState in session.Tabs)
                {
                    var left = Directory.Exists(tabState.LeftPath)
                        ? tabState.LeftPath
                        : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (!Directory.Exists(left))
                    {
                        continue;
                    }

                    var tab = CreateTab(left);
                    Tabs.Add(tab);
                    tab.IsLocked = tabState.IsLocked;
                    tab.ApplyShellSplit(false, Orientation);
                    if (tabIndex == session.SelectedIndex)
                    {
                        restoreSelected = tab;
                    }

                    tabIndex++;
                    await tab.InitializeAsync();
                }
            }
            finally
            {
                _suppressLockedTabSort = false;
            }

            SortLockedTabsToLeft();

            if (Tabs.Count == 0)
            {
                await NewTabCommand.ExecuteAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                return;
            }

            SelectedTab = restoreSelected is not null && Tabs.Contains(restoreSelected)
                ? restoreSelected
                : Tabs[Math.Clamp(session.SelectedIndex, 0, Tabs.Count - 1)];
            // 先显示选中 Tab，再强制重载一次，避免恢复阶段离屏 Load 导致空白列表
            RequestShowSelectedTab?.Invoke();
            await SelectedTab.InitializeAsync();
            if (IsDualPane)
            {
                await ShellRightGroup.InitializeAsync();
            }
        }
        finally
        {
            _restoringSession = false;
        }
    }

    private async Task RestoreRightGroupAsync(SessionState session)
    {
        var paths = session.RightPanePaths
            .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
            .ToList();

        if (paths.Count == 0)
        {
            // 旧会话：从首个双栏 Tab 的 RightPath 回填
            var legacy = session.Tabs.FirstOrDefault(t => t.IsDualPane && !string.IsNullOrWhiteSpace(t.RightPath));
            if (legacy?.RightPath is { } rp && Directory.Exists(rp))
            {
                paths.Add(rp);
            }
        }

        if (paths.Count == 0)
        {
            return;
        }

        ShellRightGroup.ResetToSinglePane(paths[0]);
        for (var i = 1; i < paths.Count; i++)
        {
            ShellRightGroup.AddPane(paths[i]);
        }

        ShellRightGroup.ActiveIndex = Math.Clamp(session.RightSelectedIndex, 0, ShellRightGroup.Panes.Count - 1);
        await ShellRightGroup.InitializeAsync();
    }

    private void WireSharedRightGroup()
    {
        void WirePane(FilePaneViewModel pane)
        {
            pane.NavigationRequested -= OnRightPanePathChanged;
            pane.NavigationRequested += OnRightPanePathChanged;
        }

        foreach (var pane in ShellRightGroup.Panes)
        {
            WirePane(pane);
        }

        ShellRightGroup.Panes.CollectionChanged += (_, args) =>
        {
            if (args.NewItems is not null)
            {
                foreach (FilePaneViewModel pane in args.NewItems)
                {
                    WirePane(pane);
                }
            }

            SchedulePersistSession();
        };

        ShellRightGroup.PropertyChanged += (_, args) =>
        {
            if (SuppressShellRightSideEffects)
            {
                return;
            }

            if (args.PropertyName is nameof(PaneGroupViewModel.ActiveIndex)
                or nameof(PaneGroupViewModel.ActivePane))
            {
                SchedulePersistSession();
                if (ActivePaneSide == PaneSide.Right)
                {
                    SyncNavigationToSelectedTab();
                }
            }
        };
    }

    private void OnRightPanePathChanged(string path)
    {
        if (ActivePaneSide == PaneSide.Right)
        {
            NavigationPane.TrackPath(path, _settings.Features.AutoRevealInTree);
            if (!_restoringSession)
            {
                NavigationPane.PushRecent(path);
            }

            NavigationPathTracked?.Invoke();
        }

        SchedulePersistSession();
    }

    private BrowserTabViewModel CreateTab(string path)
    {
        var tab = new BrowserTabViewModel(
            _fileListSource,
            _fileSystemService,
            _shellIconService,
            _metadataStore,
            _settings,
            _columns,
            path,
            ShellRightGroup);
        tab.OpenPathInNewTabAsync = async target => await NewTabCommand.ExecuteAsync(target);
        tab.ApplyShellSplit(IsDualPane, Orientation);
        tab.LeftPane.NavigationRequested += OnBrowserPathChanged;
        tab.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(BrowserTabViewModel.IsLocked))
            {
                if (!_suppressLockedTabSort)
                {
                    SortLockedTabsToLeft();
                }

                SchedulePersistSession();
            }
        };
        return tab;
    }

    private bool _suppressLockedTabSort;

    /// <summary>锁定的标签统一排到最左侧（稳定排序，尽量保持相对顺序）。</summary>
    public void SortLockedTabsToLeft()
    {
        if (_suppressLockedTabSort || Tabs.Count <= 1)
        {
            return;
        }

        var selected = SelectedTab;
        var ordered = Tabs
            .Select((tab, index) => (tab, index))
            .OrderByDescending(x => x.tab.IsLocked)
            .ThenBy(x => x.index)
            .Select(x => x.tab)
            .ToList();

        var changed = false;
        for (var desired = 0; desired < ordered.Count; desired++)
        {
            var actual = Tabs.IndexOf(ordered[desired]);
            if (actual != desired && actual >= 0)
            {
                Tabs.Move(actual, desired);
                changed = true;
            }
        }

        if (changed && selected is not null && Tabs.Contains(selected))
        {
            SelectedTab = selected;
        }
    }

    /// <summary>开启/切换全局分栏方向；再次点同方向且仅一副栏 = 关闭。</summary>
    public void SetShellSplit(DualPaneOrientation orientation)
    {
        var path = SelectedTab?.LeftPane.CurrentPath
                   ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Workspace.SetSplit(orientation, path);
        IsDualPane = Workspace.IsSplit;
        Orientation = Workspace.Orientation;
        if (!IsDualPane)
        {
            ActivePaneSide = PaneSide.Left;
            Workspace.ActiveSlotIndex = 0;
        }

        ApplyShellSplitToAllTabs();
        SchedulePersistSession();
    }

    /// <summary>再分一栏（第 3、4…）。</summary>
    public void AddWorkspaceSlot()
    {
        var path = ActiveFilePane?.CurrentPath
                   ?? SelectedTab?.LeftPane.CurrentPath
                   ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Workspace.IsSplit)
        {
            SetShellSplit(Orientation);
            return;
        }

        Workspace.AddSlot(path);
        IsDualPane = true;
        ActivePaneSide = PaneSide.Right;
        ApplyShellSplitToAllTabs();
        SchedulePersistSession();
    }

    public void CloseShellSplit()
    {
        Workspace.CloseAllSecondary();
        IsDualPane = false;
        ActivePaneSide = PaneSide.Left;
        Workspace.ActiveSlotIndex = 0;
        ApplyShellSplitToAllTabs();
        SchedulePersistSession();
    }

    public void RemoveWorkspaceSlot(PaneGroupViewModel group)
    {
        Workspace.RemoveSlot(group);
        IsDualPane = Workspace.IsSplit;
        if (!IsDualPane)
        {
            ActivePaneSide = PaneSide.Left;
            Workspace.ActiveSlotIndex = 0;
        }

        ApplyShellSplitToAllTabs();
        SchedulePersistSession();
    }

    private void ApplyShellSplitToAllTabs()
    {
        // 分栏改由 Workspace + PaneTabHost 托管；BrowserTabView 内不再嵌右栏
        foreach (var tab in Tabs)
        {
            tab.ApplyShellSplit(false, Orientation);
        }
    }

    public void NotifyPaneActivated(FilePaneViewModel? pane)
    {
        if (pane is null)
        {
            return;
        }

        var slot = Workspace.FindSlotIndex(pane);
        Workspace.ActiveSlotIndex = slot;
        var side = slot > 0 ? PaneSide.Right : PaneSide.Left;

        if (ActivePaneSide != side)
        {
            ActivePaneSide = side;
        }

        SyncNavigationToSelectedTab();
    }

    private void OnBrowserPathChanged(string path)
    {
        if (ActivePaneSide == PaneSide.Left)
        {
            NavigationPane.TrackPath(path, _settings.Features.AutoRevealInTree);
            if (!_restoringSession)
            {
                NavigationPane.PushRecent(path);
            }

            NavigationPathTracked?.Invoke();
        }

        SchedulePersistSession();
    }

    partial void OnSelectedTabChanged(BrowserTabViewModel? value)
    {
        value?.ApplyShellSplit(IsDualPane, Orientation);
        // 导航树同步由 UI 侧 ScheduleNavigationSync 统一做，避免与切 Tab 路径双重 RevealInTree
        SchedulePersistSession();
    }

    partial void OnIsDualPaneChanged(bool value) => SchedulePersistSession();
    partial void OnOrientationChanged(DualPaneOrientation value) => SchedulePersistSession();
    partial void OnActivePaneSideChanged(PaneSide value) => SchedulePersistSession();

    /// <summary>侧栏目录树跟随当前激活窗格路径；切 Tab 也记入历史访问。</summary>
    public void SyncNavigationToSelectedTab()
    {
        using var _ = AIExplorer_App.PerfLog.Measure("SyncNavigationToSelectedTab");
        var path = ActiveFilePane?.CurrentPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        NavigationPane.TrackPath(path, _settings.Features.AutoRevealInTree);
        if (!_restoringSession)
        {
            NavigationPane.PushRecent(path);
        }

        NavigationPathTracked?.Invoke();
    }

    public async Task NavigateActivePaneAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (IsDualPane && ActivePaneSide == PaneSide.Right)
        {
            if (ShellRightGroup.ActivePane is { } right)
            {
                await right.NavigateToPathCommand.ExecuteAsync(path);
            }

            return;
        }

        if (SelectedTab is not null)
        {
            await SelectedTab.NavigateLeftPaneAsync(path);
        }
    }

    public void RefreshSearchProviderHint()
    {
        var provider = ActiveSearchProvider;
        if (provider is null)
        {
            SearchPlaceholder = "搜索不可用";
            SearchProviderHint = string.Empty;
            IsSearchScopeEnabled = false;
            return;
        }

        if (provider.ProviderId == "everything")
        {
            SearchPlaceholder = SearchInCurrentFolder ? "在当前文件夹搜索（Everything）" : "全盘搜索（Everything）";
            SearchProviderHint = "Everything";
            IsSearchScopeEnabled = true;
        }
        else
        {
            // 本地搜索仅支持当前文件夹：禁用开关，避免「勾上却取消不了」的错觉
            SearchPlaceholder = "在当前文件夹搜索（本地）";
            SearchProviderHint = "本地";
            IsSearchScopeEnabled = false;
            if (!SearchInCurrentFolder)
            {
                SearchInCurrentFolder = true;
            }
        }
    }

    partial void OnSearchInCurrentFolderChanged(bool value)
    {
        // 只刷新文案，勿在 Everything 模式下回写 SearchInCurrentFolder（否则会顶掉用户取消勾选）
        var provider = ActiveSearchProvider;
        if (provider?.ProviderId == "everything")
        {
            SearchPlaceholder = value ? "在当前文件夹搜索（Everything）" : "全盘搜索（Everything）";
            SearchProviderHint = "Everything";
            IsSearchScopeEnabled = true;
            return;
        }

        RefreshSearchProviderHint();
    }

    public void RebuildFavoriteBar()
    {
        FavoriteBarItems.Clear();
        if (FavoriteRoot is null)
        {
            return;
        }

        foreach (var child in FavoriteRoot.Children)
        {
            FavoriteBarItems.Add(child);
        }
    }

    /// <summary>
    /// 仅创建并选中标签头（不 Initialize / 不枚举目录）。
    /// + 号路径用这个先出蓝头，再异步 Initialize。
    /// </summary>
    public BrowserTabViewModel? CreateAndSelectTab(string? path)
    {
        var sw = Stopwatch.StartNew();
        void Mark(string step) => AIExplorer_App.PerfLog.Write($"NewTab/{step}: {sw.Elapsed.TotalMilliseconds:F0}ms");

        // + 号：默认复制当前激活标签目录（避免每次落到用户主目录导致卡顿）
        var target = string.IsNullOrWhiteSpace(path)
            ? (SelectedTab?.LeftPane.CurrentPath
               ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            : path;

        // UNC 上 Directory.Exists 同步很贵；路径非空则信任，失败留给 Load/Seed
        if (string.IsNullOrWhiteSpace(target))
        {
            target = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        else if (!target.StartsWith(@"\\", StringComparison.Ordinal) && !Directory.Exists(target))
        {
            target = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!Directory.Exists(target))
            {
                return null;
            }
        }

        Mark("begin");
        var tab = CreateTab(target);
        Mark("CreateTab(vm)");
        SelectedTab = tab;
        Tabs.Add(tab);
        Mark("Tabs.Add+SyncTabs(header only)");
        RequestShowSelectedTab?.Invoke();
        Mark("RequestShowSelectedTab");
        return tab;
    }

    [RelayCommand]
    private async Task NewTabAsync(string? path)
    {
        var sw = Stopwatch.StartNew();
        void Mark(string step) => AIExplorer_App.PerfLog.Write($"NewTab/{step}: {sw.Elapsed.TotalMilliseconds:F0}ms");

        var tab = CreateAndSelectTab(path);
        if (tab is null)
        {
            return;
        }

        // 等一帧让标签头先布局/绘制，再枚举目录（其它入口仍走完整 NewTab）
        await YieldUiFrameAsync();
        Mark("YieldUiFrame");
        await tab.InitializeAsync();
        Mark("InitializeAsync(done)");
    }

    private static async Task YieldUiFrameAsync()
    {
        var dq = App.DispatcherQueue;
        if (dq is null)
        {
            await Task.Yield();
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Normal：尽快跑完布局/绘制标签头，再进入目录枚举
        if (!dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () => tcs.TrySetResult()))
        {
            tcs.TrySetResult();
        }

        await tcs.Task;
    }

    /// <summary>
    /// 搜索/主页等入口：已有同目录 Tab 则激活，否则新开 Tab。不覆盖当前 Tab 路径。
    /// </summary>
    public async Task OpenFolderInTabAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch
        {
            return;
        }

        if (File.Exists(normalized))
        {
            normalized = Path.GetDirectoryName(normalized) ?? normalized;
        }

        if (!Directory.Exists(normalized))
        {
            return;
        }

        static bool SameDir(string? a, string b)
        {
            if (string.IsNullOrWhiteSpace(a))
            {
                return false;
            }

            try
            {
                return string.Equals(
                    Path.GetFullPath(a).TrimEnd('\\'),
                    Path.GetFullPath(b).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        var existing = Tabs.FirstOrDefault(t => SameDir(t.LeftPane.CurrentPath, normalized));
        if (existing is not null)
        {
            SelectedTab = existing;
            RequestShowSelectedTab?.Invoke();
            return;
        }

        await NewTabCommand.ExecuteAsync(normalized);
    }


    /// <summary>撕裂到其它窗口：从本窗移除但不 Dispose。</summary>
    public void DetachTab(BrowserTabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        Tabs.RemoveAt(index);
        if (SelectedTab == tab)
        {
            SelectedTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        }

        if (!_isSecondaryWindow)
        {
            SchedulePersistSession();
        }
    }

    /// <summary>从其它窗口拖入 / 撕裂接收。</summary>
    public void AttachTab(BrowserTabViewModel tab, int insertIndex = -1)
    {
        if (Tabs.Contains(tab))
        {
            SelectedTab = tab;
            return;
        }

        if (insertIndex < 0 || insertIndex > Tabs.Count)
        {
            Tabs.Add(tab);
        }
        else
        {
            Tabs.Insert(insertIndex, tab);
        }

        SelectedTab = tab;
        if (!_isSecondaryWindow)
        {
            SchedulePersistSession();
        }
    }

    /// <summary>按标签条 UI 顺序写回业务 Tabs（主页/搜索不在集合内）。</summary>
    public void ApplyTabOrder(IReadOnlyList<BrowserTabViewModel> order)
    {
        if (_suppressTabReorder || order.Count == 0)
        {
            return;
        }

        if (Tabs.Count != order.Count || order.Any(t => !Tabs.Contains(t)))
        {
            return;
        }

        var same = true;
        for (var i = 0; i < order.Count; i++)
        {
            if (!ReferenceEquals(Tabs[i], order[i]))
            {
                same = false;
                break;
            }
        }

        if (same)
        {
            return;
        }

        _suppressTabReorder = true;
        try
        {
            for (var i = 0; i < order.Count; i++)
            {
                var current = Tabs.IndexOf(order[i]);
                if (current >= 0 && current != i)
                {
                    Tabs.Move(current, i);
                }
            }
        }
        finally
        {
            _suppressTabReorder = false;
        }

        if (!_isSecondaryWindow)
        {
            SchedulePersistSession();
        }
    }

    [RelayCommand]
    private void CloseTab(BrowserTabViewModel? tab)
    {
        if (tab is null || tab.IsLocked)
        {
            return;
        }

        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        tab.Dispose();
        Tabs.RemoveAt(index);

        if (Tabs.Count == 0)
        {
            SelectedTab = null;
            SchedulePersistSession();
            return;
        }

        SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        SchedulePersistSession();
    }

    [RelayCommand]
    private void CloseOtherTabs(BrowserTabViewModel? keep)
    {
        if (keep is null)
        {
            return;
        }

        foreach (var tab in Tabs.Where(t => t != keep && !t.IsLocked).ToList())
        {
            tab.Dispose();
            Tabs.Remove(tab);
        }

        SelectedTab = keep;
        SchedulePersistSession();
    }

    [RelayCommand]
    private void CloseTabsToTheRight(BrowserTabViewModel? pivot)
    {
        if (pivot is null)
        {
            return;
        }

        var index = Tabs.IndexOf(pivot);
        if (index < 0)
        {
            return;
        }

        for (var i = Tabs.Count - 1; i > index; i--)
        {
            if (Tabs[i].IsLocked)
            {
                continue;
            }

            Tabs[i].Dispose();
            Tabs.RemoveAt(i);
        }

        SelectedTab = pivot;
        SchedulePersistSession();
    }

    [RelayCommand]
    private void CloseTabsToTheLeft(BrowserTabViewModel? pivot)
    {
        if (pivot is null)
        {
            return;
        }

        var index = Tabs.IndexOf(pivot);
        if (index < 0)
        {
            return;
        }

        for (var i = index - 1; i >= 0; i--)
        {
            if (Tabs[i].IsLocked)
            {
                continue;
            }

            Tabs[i].Dispose();
            Tabs.RemoveAt(i);
        }

        SelectedTab = pivot;
        SchedulePersistSession();
    }

    [RelayCommand]
    private void ToggleDualPane()
    {
        if (IsDualPane)
        {
            CloseShellSplit();
        }
        else
        {
            SetShellSplit(Orientation);
        }
    }

    [RelayCommand]
    private void ToggleOrientation()
    {
        SetShellSplit(Orientation == DualPaneOrientation.Horizontal
            ? DualPaneOrientation.Vertical
            : DualPaneOrientation.Horizontal);
    }

    [RelayCommand]
    private async Task RunSearchAsync()
    {
        var query = SearchQuery.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        RefreshSearchProviderHint();
        var provider = ActiveSearchProvider;
        if (provider is null)
        {
            return;
        }

        var scopePath = SelectedTab?.LeftPane.CurrentPath;
        var needScope = provider.ProviderId != "everything" || SearchInCurrentFolder;
        if (needScope && string.IsNullOrWhiteSpace(scopePath))
        {
            return;
        }

        ActiveSearch?.Dispose();
        RememberSearchQuery(query);
        var label = provider.ProviderId == "everything" ? "Everything" : "本地文件名";
        var session = new SearchResultsViewModel(
            query,
            label,
            _fileSystemService,
            OpenFolderInTabAsync);

        ActiveSearch = session;
        SearchSessionChanged?.Invoke();

        var request = new SearchRequest
        {
            Query = query,
            ScopePath = needScope ? scopePath : null,
            MaxResults = 300,
        };
        await session.RunAsync(provider, request);
    }

    [RelayCommand]
    private void CloseSearch()
    {
        ActiveSearch?.Dispose();
        ActiveSearch = null;
        SearchSessionChanged?.Invoke();
    }

    [RelayCommand]
    private async Task OpenFavoriteAsync(FavoriteNodeViewModel? node)
    {
        if (node is null || node.IsGroup || string.IsNullOrWhiteSpace(node.Path))
        {
            return;
        }

        if (File.Exists(node.Path))
        {
            await _fileSystemService.OpenPathAsync(node.Path);
            return;
        }

        if (!Directory.Exists(node.Path))
        {
            return;
        }

        // 书签：跟当前激活栏（左或右）
        if (SelectedTab is not null || (IsDualPane && ActivePaneSide == PaneSide.Right))
        {
            await NavigateActivePaneAsync(node.Path);
            return;
        }

        await NewTabCommand.ExecuteAsync(node.Path);
    }

    [RelayCommand]
    private async Task NavigateFromSidebarAsync(NavigationItemViewModel? item)
    {
        if (item is null || !item.IsSelectable || string.IsNullOrWhiteSpace(item.Path))
        {
            return;
        }

        if (item.Kind == NavItemKind.Network)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "shell:NetworkPlacesFolder",
                UseShellExecute = true,
            });
            return;
        }

        if (SelectedTab is not null || (IsDualPane && ActivePaneSide == PaneSide.Right))
        {
            await NavigateActivePaneAsync(item.Path);
            return;
        }

        await NewTabCommand.ExecuteAsync(item.Path);
    }

    [RelayCommand]
    private async Task OpenAllInFavoriteAsync(FavoriteNodeViewModel? node)
    {
        if (node is null)
        {
            return;
        }

        foreach (var leaf in node.GetFolderLeaves())
        {
            if (!string.IsNullOrWhiteSpace(leaf.Path))
            {
                await NewTabCommand.ExecuteAsync(leaf.Path);
            }
        }
    }

    [RelayCommand]
    private async Task AddFavoriteFolderAsync(FavoriteNodeViewModel? parent)
    {
        parent ??= FavoriteRoot;
        if (parent is null)
        {
            return;
        }

        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        var path = folder.Path;
        parent.AddFolder(Path.GetFileName(path), path);
        await SaveFavoritesAsync();
        RebuildFavoriteBar();
    }

    [RelayCommand]
    private async Task AddFavoriteGroupAsync(FavoriteNodeViewModel? parent)
    {
        await CreateFavoriteGroupAsync(parent, "新建分组");
    }

    /// <summary>在指定父分组下创建命名子分组；空名回退默认名。</summary>
    public async Task<FavoriteNodeViewModel?> CreateFavoriteGroupAsync(FavoriteNodeViewModel? parent, string name)
    {
        parent ??= FavoriteRoot;
        if (parent is null)
        {
            return null;
        }

        if (!parent.IsGroup)
        {
            parent = parent.Parent ?? FavoriteRoot;
        }

        var groupName = string.IsNullOrWhiteSpace(name) ? "新建分组" : name.Trim();
        var group = parent!.AddGroup(groupName);
        await SaveFavoritesAsync();
        RebuildFavoriteBar();
        return group;
    }

    [RelayCommand]
    private async Task RenameFavoriteAsync(FavoriteNodeViewModel? node)
    {
        if (node is null || node == FavoriteRoot)
        {
            return;
        }

        SelectedFavorite = node;
        RenameDialogRequested?.Invoke(node);
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RemoveFavoriteAsync(FavoriteNodeViewModel? node)
    {
        if (node is null || node == FavoriteRoot)
        {
            return;
        }

        node.Remove();
        await SaveFavoritesAsync();
        RebuildFavoriteBar();
    }

    public async Task ApplyFavoriteRenameAsync(FavoriteNodeViewModel node, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        node.DisplayName = newName.Trim();
        await SaveFavoritesAsync();
        RebuildFavoriteBar();
    }

    public async Task SaveFavoritesAsync()
    {
        if (FavoriteRoot is null)
        {
            return;
        }

        await _favoritesRepository.SaveAsync(FavoriteRoot.Model);
    }

    public IReadOnlyList<FavoriteNodeViewModel> GetFavoriteGroups()
    {
        if (FavoriteRoot is null)
        {
            return [];
        }

        return FavoriteRoot.EnumerateGroups().ToList();
    }

    /// <summary>
    /// 收藏当前路径：弹窗确认名称与目标分组；同路径已存在时编辑已有项。
    /// </summary>
    public async Task<bool> BookmarkPathAsync(string path, string displayName, FavoriteNodeViewModel? targetGroup)
    {
        if (FavoriteRoot is null || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        if (!Directory.Exists(normalized) && !File.Exists(normalized))
        {
            return false;
        }

        var name = string.IsNullOrWhiteSpace(displayName)
            ? (Path.GetFileName(normalized.TrimEnd('\\')) is { Length: > 0 } leaf ? leaf : normalized)
            : displayName.Trim();

        targetGroup ??= FavoriteRoot;
        if (!targetGroup.IsGroup)
        {
            targetGroup = targetGroup.Parent ?? FavoriteRoot;
        }

        var existing = FavoriteRoot.Model.FindByPath(normalized);
        if (existing is not null)
        {
            existing.DisplayName = name;
            if (!string.Equals(existing.Path, normalized, StringComparison.OrdinalIgnoreCase))
            {
                existing.Path = normalized;
            }

            // 若目标分组不同，移动已有收藏
            if (targetGroup is not null &&
                FavoriteRoot.Model.FindParentOf(existing.Id) is { } currentParent &&
                !string.Equals(currentParent.Id, targetGroup.Model.Id, StringComparison.Ordinal))
            {
                FavoriteRoot.Model.MoveTo(existing.Id, targetGroup.Model.Id);
            }

            ReloadFavoriteTree();
            await SaveFavoritesAsync();
            return true;
        }

        targetGroup!.AddFolder(name, normalized);
        await SaveFavoritesAsync();
        RebuildFavoriteBar();
        return true;
    }

    public async Task MoveFavoriteToGroupAsync(FavoriteNodeViewModel item, FavoriteNodeViewModel targetGroup)
    {
        if (FavoriteRoot is null || item == FavoriteRoot || !targetGroup.IsGroup)
        {
            return;
        }

        if (!FavoriteRoot.Model.MoveTo(item.Model.Id, targetGroup.Model.Id))
        {
            return;
        }

        ReloadFavoriteTree();
        await SaveFavoritesAsync();
    }

    /// <summary>
    /// 收藏栏拖放：目标是组则移入；目标是收藏项则新建组并把两者放入。
    /// </summary>
    public async Task DropFavoriteOntoAsync(FavoriteNodeViewModel source, FavoriteNodeViewModel target)
    {
        if (FavoriteRoot is null || source == FavoriteRoot || target == FavoriteRoot || ReferenceEquals(source, target))
        {
            return;
        }

        // 不能把分组拖进自己的后代
        if (source.IsGroup && source.Model.FindById(target.Model.Id) is not null)
        {
            return;
        }

        if (target.IsGroup)
        {
            await MoveFavoriteToGroupAsync(source, target);
            return;
        }

        var parentModel = FavoriteRoot.Model.FindParentOf(target.Model.Id) ?? FavoriteRoot.Model;
        var groupName = BuildNestGroupName(source.DisplayName, target.DisplayName);
        var group = new FavoriteItem { DisplayName = groupName };
        parentModel.Children.Add(group);

        // 把目标叶子移入新组
        parentModel.Children.Remove(target.Model);
        group.Children.Add(target.Model);

        // 把源项移入新组（可能来自其它分组）
        FavoriteRoot.Model.MoveTo(source.Model.Id, group.Id);

        // 若源与目标原同父且 MoveTo 因边界失败，再尝试直接挂入
        if (group.FindById(source.Model.Id) is null)
        {
            var sourceParent = FavoriteRoot.Model.FindParentOf(source.Model.Id);
            sourceParent?.Children.Remove(source.Model);
            if (group.FindById(source.Model.Id) is null)
            {
                group.Children.Add(source.Model);
            }
        }

        ReloadFavoriteTree();
        await SaveFavoritesAsync();
    }

    private static string BuildNestGroupName(string a, string b)
    {
        static string Short(string s) =>
            string.IsNullOrWhiteSpace(s) ? "收藏" : (s.Length <= 10 ? s : s[..10] + "…");

        return $"{Short(b)} 等";
    }

    private void ReloadFavoriteTree()
    {
        if (FavoriteRoot is null)
        {
            return;
        }

        FavoriteRoot = new FavoriteNodeViewModel(FavoriteRoot.Model);
        RebuildFavoriteBar();
    }

    public async Task AddFavoriteFoldersFromPathsAsync(IEnumerable<string> paths)
    {
        if (FavoriteRoot is null)
        {
            return;
        }

        var changed = false;
        foreach (var rawPath in paths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            string path;
            try
            {
                path = Path.GetFullPath(rawPath.Trim().Trim('"'));
            }
            catch
            {
                continue;
            }

            if (!Directory.Exists(path) && !File.Exists(path))
            {
                continue;
            }

            var name = Path.GetFileName(path.TrimEnd('\\'));
            if (string.IsNullOrWhiteSpace(name))
            {
                name = path;
            }

            FavoriteRoot.AddFolder(name, path);
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        await SaveFavoritesAsync();
        RebuildFavoriteBar();
    }

    public event Action<FavoriteNodeViewModel>? RenameDialogRequested;
}
