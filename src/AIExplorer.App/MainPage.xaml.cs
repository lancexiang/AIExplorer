using System.Diagnostics;
using System.Text;
using AIExplorer_App.ViewModels;
using AIExplorer_App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using WinRT.Interop;

namespace AIExplorer_App;

public sealed partial class MainPage : Page
{
    private const string TabDragMarker = "AIExplorer.BrowserTab";
    /// <summary>相对起点下移（或上移）达到此像素即可撕裂；不再要求 dy≫dx。</summary>
    private const int TabTearMinDyPx = 28;
    /// <summary>光标离开标签条垂直范围超过此像素也算撕裂。</summary>
    private const int TabTearOutsideStripPx = 16;
    private static WeakReference<MainPage>? _dragSourcePage;
    private static WeakReference<TabViewItem>? _draggedTabItem;
    private static int _tabDragSession;
    private static bool _tabDragMerged;
    private static TabViewItem? _pendingOutsideTab;

    private readonly Dictionary<BrowserTabViewModel, TabViewItem> _tabItems = new();
    private TabViewItem? _searchTabItem;
    private TabViewItem? _homeTabItem;
    private bool _isSecondary;
    private bool _suppressTabSync;
    private bool _isReorderingFromUi;
    private bool _isTabDragging;
    private bool _liveReorderBusy;
    private bool _suppressTabBodyMaterialize;
    private TabViewItem? _chromeActiveLeft;
    private TabViewItem? _chromeActiveRight;
    private int _lastLiveReorderIndex = -1;
    private NativeWindowHelper.POINT _tabDragStartCursor;
    private double _splitTabRatio = 0.5;
    private double _tabStripMeasuredHeight;
    private bool _shellSplitterDragging;
    private bool _syncingShellRightTabs;
    /// <summary>右侧 + 快速建头期间：禁止 ActiveIndex/CollectionChanged 连带整表刷 Header / LoadAsync。</summary>
    private bool _suppressShellRightUiSync;
    private Brush? _shellSplitterBrush;
    private DateTime _suppressFavoriteTapUntilUtc;
    /// <summary>右侧每个 Pane 保活自己的 FilePaneControl（对齐左侧 Tab 内容保活），禁止共用一个控件反复换 VM。</summary>
    private readonly Dictionary<FilePaneViewModel, FilePaneControl> _rightPaneViews = new();
    private FilePaneControl? _activeRightPaneView;
    private readonly List<PaneTabHost> _secondaryHosts = [];
    private readonly List<Border> _workspaceSplitters = [];

    /// <summary>当前可见的右栏文件窗格（可能为 null）。</summary>
    private FilePaneControl? RightShellPane =>
        _secondaryHosts.FirstOrDefault(h => h.IsFocusedSlot)?.ActivePane is { } p
            ? null // 内容在 PaneTabHost 内
            : _activeRightPaneView;

    public MainPageViewModel ViewModel { get; }

    public MainPage()
    {
        ViewModel = App.Services.GetRequiredService<MainPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.RenameDialogRequested += OnRenameDialogRequested;
        ViewModel.PropertyChanged += OnShellSplitPropertyChanged;
        ViewModel.Tabs.CollectionChanged += (_, _) =>
        {
            if (!_isReorderingFromUi)
            {
                SyncTabs();
            }

            TryCloseSecondaryWindowIfEmpty();
        };
        ViewModel.SearchSessionChanged += SyncSearchTab;
        ViewModel.NavigationPathTracked += SyncNavSelection;
        ViewModel.RequestShowSelectedTab += OnRequestShowSelectedTab;
        WireShellRightGroupUi();
        ViewModel.Workspace.LayoutChanged += () => UpdateSplitTabStripLayout();
        BrowserTabs.RegisterPropertyChangedCallback(TabView.SelectedItemProperty, OnSelectedTabItemChanged);
        BrowserTabs.Loaded += (_, _) =>
        {
            CollapseTabViewInternalContent();
            ShowActiveTabContent();
        };
        BrowserTabs.SizeChanged += (_, _) => CollapseTabViewInternalContent();
        BrowserTabs.AllowDrop = true;
        // handledEventsToo：右键点在 TabViewItem 上时也能收到，并按命中项激活（勿用 SelectedItem）
        BrowserTabs.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnBrowserTabsPointerPressed),
            handledEventsToo: true);
        ShellRightTabs.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnShellRightTabsPointerPressed),
            handledEventsToo: true);
        RightShellContentHost.PointerPressed += (_, _) =>
        {
            if (ViewModel.ShellRightGroup.ActivePane is { } right)
            {
                ViewModel.NotifyPaneActivated(right);
            }
        };
        TabContentHost.PointerPressed += (_, _) =>
        {
            if (ViewModel.SelectedTab?.LeftPane is { } left)
            {
                ViewModel.NotifyPaneActivated(left);
            }
        };
        Loaded += OnLoaded;
        TerminalPaneHost.ActiveDirectoryProvider = GetActiveFolderPath;
        TerminalPaneHost.CollapseRequested += OnTerminalCollapseRequested;
        WireTabTearDragFeedback();
    }

    /// <summary>内容区接受标签拖放为 Move，避免出现 stop 图标，并提示「在新窗口打开」。</summary>
    private void WireTabTearDragFeedback()
    {
        BrowserArea.AllowDrop = true;
        BrowserArea.DragOver += OnContentTabDragOver;
        BrowserArea.Drop += OnContentTabDrop;
        TabContentHost.AllowDrop = true;
        TabContentHost.DragOver += OnContentTabDragOver;
        TabContentHost.Drop += OnContentTabDrop;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _isSecondary = e.Parameter is true;
        base.OnNavigatedTo(e);
    }

    private void OnRequestShowSelectedTab()
    {
        // CollectionChanged 通常已 Sync；仅缺项时再补，避免新建 Tab 时二次整表同步
        if (ViewModel.SelectedTab is { } tab &&
            (!_tabItems.TryGetValue(tab, out var existing) || !BrowserTabs.TabItems.Contains(existing)))
        {
            SyncTabs();
        }

        if (ViewModel.SelectedTab is not null &&
            _tabItems.TryGetValue(ViewModel.SelectedTab, out var item))
        {
            BrowserTabs.SelectedItem = item;
            if (!_suppressTabBodyMaterialize)
            {
                ShowActiveTabContent();
            }
            else
            {
                // 快速新建：只切蓝头，不物化 BrowserTabView / 不碰其它 tab
                SetActiveLeftTabVisual(item);
            }
        }
    }

    private void OnSelectedTabItemChanged(DependencyObject sender, DependencyProperty dp)
    {
        using var _ = PerfLog.Measure("OnSelectedTabItemChanged");
        if (BrowserTabs.SelectedItem is not TabViewItem item)
        {
            if (_isTabDragging || _suppressTabBodyMaterialize)
            {
                return;
            }

            ShowActiveTabContent();
            ScheduleNavigationSync();
            return;
        }

        if (IsSpecialTab(item))
        {
            if (_isTabDragging || _suppressTabBodyMaterialize)
            {
                return;
            }

            ShowActiveTabContent();
            ScheduleNavigationSync();
            return;
        }

        if (TryGetBrowserTabVm(item) is { } tabVm)
        {
            ViewModel.SelectedTab = tabVm;
        }

        // + 新建路径：禁止在这里 EnsureBrowserTabView（日志里曾到 175ms，整条标签空白）
        if (_suppressTabBodyMaterialize || _isTabDragging)
        {
            SetActiveLeftTabVisual(item);
            return;
        }

        var view = EnsureBrowserTabView(item);
        if (view?.ViewModel is not null)
        {
            ViewModel.NotifyPaneActivated(view.ViewModel.LeftPane);
        }

        ShowActiveTabContent();
        ScheduleNavigationSync();
    }

    private int _navSyncGeneration;

    /// <summary>切 Tab 后低优先级同步侧栏，不阻塞内容切换。</summary>
    private void ScheduleNavigationSync()
    {
        var gen = ++_navSyncGeneration;
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (gen != _navSyncGeneration)
            {
                return;
            }

            using var _ = PerfLog.Measure("DeferredNavigationSync");
            ViewModel.SyncNavigationToSelectedTab();
        });
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isSecondary)
        {
            await ViewModel.InitializeSecondaryAsync();
        }
        else
        {
            await ViewModel.InitializeAsync();
            App.Window.Closed += OnWindowClosed;
        }

        CollapseTabViewInternalContent();
        DisableTabStripTransitions();
        ShowActiveTabContent();
        RefreshActivePaneChrome();
    }

    /// <summary>
    /// 关掉标签条入场/重排动画（TabView 本身无 ItemContainerTransitions API）。
    /// 否则 + 新建日志很短，但视觉上会空白很久才上蓝。
    /// </summary>
    private void DisableTabStripTransitions()
    {
        // WinUI TabView 内部是 ListView / ItemsRepeater 一类宿主
        if (FindDescendant<ListView>(BrowserTabs) is { } listView)
        {
            listView.ItemContainerTransitions = [];
        }

        if (FindDescendant<ItemsControl>(BrowserTabs) is { } items &&
            !ReferenceEquals(items, BrowserTabs))
        {
            items.ItemContainerTransitions = [];
        }

        BrowserTabs.Transitions = [];
        TabContentHost.Transitions = [];
    }

    /// <summary>
    /// 隐藏 TabView 内置内容区，并把 TabView 高度锁成仅标签条，
    /// 避免 Auto 行仍按内部 * 行抢高度、挤扁 TabContentHost。
    /// </summary>
    private void CollapseTabViewInternalContent()
    {
        NormalizeTabStrip(BrowserTabs, 44);
        _tabStripMeasuredHeight = 44;
    }

    private void OnHostedRightTabStripSizeChanged(object sender, SizeChangedEventArgs e) =>
        SyncShellRightTabStripHeight();

    /// <summary>
    /// 左右标签条共用：去内边距、锁 44px、Stretch。
    /// 左侧曾漏清 Padding → 相对右侧多出一圈顶/左 margin。
    /// </summary>
    private static void NormalizeTabStrip(TabView tabView, double height)
    {
        var presenter = FindDescendant<ContentPresenter>(tabView, "TabContentPresenter");
        if (presenter is not null)
        {
            presenter.Visibility = Visibility.Collapsed;
            presenter.Height = 0;
            presenter.MaxHeight = 0;
            presenter.MinHeight = 0;
        }

        tabView.Padding = new Thickness(0);
        tabView.Margin = new Thickness(0);
        tabView.Height = height;
        tabView.MaxHeight = height;
        tabView.MinHeight = height;
        tabView.VerticalAlignment = VerticalAlignment.Stretch;

        if (FindDescendant<FrameworkElement>(tabView, "TabContainerGrid") is { } strip)
        {
            strip.Margin = new Thickness(0);
            strip.VerticalAlignment = VerticalAlignment.Stretch;
            if (strip is Control stripControl)
            {
                stripControl.Padding = new Thickness(0);
            }
        }

        // 内部 ListView 默认常带左右缩进，左右分栏会看起来错位
        if (FindDescendant<ListView>(tabView) is { } list)
        {
            list.Padding = new Thickness(0);
            list.Margin = new Thickness(0);
            list.VerticalAlignment = VerticalAlignment.Stretch;
        }
    }

    private static void CollapseHostedTabStrip(TabView tabView, double? matchHeight = null)
    {
        var h = matchHeight is > 1 ? matchHeight.Value : 44;
        NormalizeTabStrip(tabView, h);
    }

    private void SyncShellRightTabStripHeight()
    {
        if (ShellRightTabs.Visibility != Visibility.Visible)
        {
            return;
        }

        NormalizeTabStrip(ShellRightTabs, 44);
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (ShellRightTabs.Visibility == Visibility.Visible)
            {
                NormalizeTabStrip(ShellRightTabs, 44);
            }
        });
    }

    private void ShowActiveTabContent()
    {
        using var _ = PerfLog.Measure("ShowActiveTabContent");

        if (BrowserTabs.SelectedItem is null && BrowserTabs.TabItems.Count > 0)
        {
            BrowserTabs.SelectedItem = BrowserTabs.TabItems[0];
        }

        FrameworkElement? body = null;
        if (BrowserTabs.SelectedItem is TabViewItem selected)
        {
            body = IsSpecialTab(selected)
                ? GetTabBody(selected)
                : EnsureBrowserTabView(selected);
            if (body is BrowserTabView { ViewModel: { } tab })
            {
                ViewModel.SelectedTab = tab;
                tab.PropertyChanged -= OnSelectedBrowserTabPropertyChanged;
                tab.PropertyChanged += OnSelectedBrowserTabPropertyChanged;
            }
        }

        var firstAttach = body is not null && !TabContentHost.Children.Contains(body);
        ShowTabBodyInHost(body);

        // 仅首次挂入时绑 ItemsSource；之后 watcher 静默增量，切 Tab 绝不 SyncItemsSources
        if (firstAttach && body is BrowserTabView browserTab)
        {
            browserTab.SyncPaneListBindings();
        }

        if (BrowserTabs.SelectedItem is TabViewItem selectedItem && !IsSpecialTab(selectedItem))
        {
            // 切左栏 Tab：必须清副栏蓝头（PaneTabHost.IsFocusedSlot），不能只清遗留 ShellRightTabs
            if (TryGetBrowserTabVm(selectedItem) is { } tab)
            {
                ViewModel.SelectedTab = tab;
                ViewModel.NotifyPaneActivated(tab.LeftPane);
            }
            else
            {
                ViewModel.NotifyPaneActivated(ViewModel.SelectedTab?.LeftPane);
            }

            RefreshActivePaneChrome();
        }
        else
        {
            RefreshActivePaneChrome();
        }
    }

    private UIElement? GetVisibleTabBody()
    {
        foreach (var child in TabContentHost.Children)
        {
            // Opacity 保活：非激活仍 Visibility=Visible，用 Opacity/HitTest 区分
            if (child is UIElement { Opacity: > 0.5 } el)
            {
                return el;
            }
        }

        return null;
    }

    /// <summary>
    /// 标签内容永久保活：不 Parent=null、不用 Collapsed（Collapsed 一段时间后 ListView 会丢项）。
    /// 非激活：Opacity=0 + 不接收点击，仍在可视树里接收 CollectionChanged，切回零闪烁。
    /// </summary>
    private void ShowTabBodyInHost(FrameworkElement? body)
    {
        if (body is null)
        {
            foreach (var child in TabContentHost.Children.OfType<UIElement>())
            {
                SetTabBodyLayerActive(child, false);
            }

            return;
        }

        body.HorizontalAlignment = HorizontalAlignment.Stretch;
        body.VerticalAlignment = VerticalAlignment.Stretch;
        body.Width = double.NaN;
        body.Height = double.NaN;

        if (!TabContentHost.Children.Contains(body))
        {
            DetachFromVisualParent(body);
            body.Visibility = Visibility.Visible;
            TabContentHost.Children.Add(body);
        }

        foreach (var child in TabContentHost.Children.OfType<UIElement>())
        {
            SetTabBodyLayerActive(child, ReferenceEquals(child, body));
        }
    }

    private static void SetTabBodyLayerActive(UIElement child, bool active)
    {
        child.Visibility = Visibility.Visible;
        child.Opacity = active ? 1 : 0;
        child.IsHitTestVisible = active;
        Canvas.SetZIndex(child, active ? 1 : 0);
    }

    private void RemoveTabBodyFromHost(UIElement? body)
    {
        if (body is null)
        {
            return;
        }

        if (TabContentHost.Children.Contains(body))
        {
            TabContentHost.Children.Remove(body);
        }
    }

    private BrowserTabView? GetActiveBrowserTabView() =>
        GetVisibleTabBody() as BrowserTabView;

    private void OnSelectedBrowserTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BrowserTabViewModel.IsDualPane)
            or nameof(BrowserTabViewModel.IsHorizontalSplit)
            or nameof(BrowserTabViewModel.Orientation))
        {
            UpdateSplitTabStripLayout(syncRightContent: true);
        }
    }

    private void OnShellSplitPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainPageViewModel.ActivePaneSide))
        {
            if (_suppressShellRightUiSync || ViewModel.SuppressShellRightSideEffects)
            {
                return;
            }

            // 仅更新激活样式，禁止整页布局/右栏重绑
            RefreshActivePaneChrome();
            return;
        }

        if (e.PropertyName is nameof(MainPageViewModel.IsDualPane)
            or nameof(MainPageViewModel.Orientation)
            or nameof(MainPageViewModel.IsHorizontalSplit))
        {
            UpdateSplitTabStripLayout(syncRightContent: true);
            RefreshActivePaneChrome();
        }
    }

    private void WireShellRightGroupUi()
    {
        // 副栏 UI 由 PaneTabHost 自管；+ 建头期间看 SuppressShellRightSideEffects
        ViewModel.ShellRightGroup.PropertyChanged += (_, args) =>
        {
            if (_suppressShellRightUiSync || ViewModel.SuppressShellRightSideEffects)
            {
                return;
            }

            if (args.PropertyName is nameof(PaneGroupViewModel.ActiveIndex)
                or nameof(PaneGroupViewModel.ActivePane)
                or nameof(PaneGroupViewModel.Title))
            {
                RefreshActivePaneChrome();
            }
        };
    }

    private void OnShellRightPanePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // no-op：PaneTabHost 自行刷新 Header
    }

    private void ApplyBrowseColumnRatio(double ratio)
    {
        ratio = Math.Clamp(ratio, 0.2, 0.8);
        _splitTabRatio = ratio;
        var ws = ViewModel.Workspace;
        if (ws.Slots.Count == 2)
        {
            ws.Slots[0].Ratio = ratio;
            ws.Slots[1].Ratio = 1.0 - ratio;
            ApplyWorkspaceSlotRatios();
        }
    }

    private void ApplyBrowseColumnPixels(double ratio) => ApplyBrowseColumnRatio(ratio);

    private void ApplyWorkspaceSlotRatios()
    {
        var ws = ViewModel.Workspace;
        var horizontal = ws.Orientation == DualPaneOrientation.Horizontal;
        var slotCount = ws.Slots.Count;
        if (slotCount <= 1)
        {
            return;
        }

        for (var i = 0; i < slotCount; i++)
        {
            var gridIndex = i * 2;
            var r = Math.Max(0.05, ws.Slots[i].Ratio);
            if (horizontal && gridIndex < BrowserArea.ColumnDefinitions.Count)
            {
                BrowserArea.ColumnDefinitions[gridIndex].Width = new GridLength(r, GridUnitType.Star);
            }
            else if (!horizontal && gridIndex < BrowserArea.RowDefinitions.Count)
            {
                BrowserArea.RowDefinitions[gridIndex].Height = new GridLength(r, GridUnitType.Star);
            }
        }
    }

    private void EnsureShellSplitterBrush()
    {
        _shellSplitterBrush ??= new SolidColorBrush(Windows.UI.Color.FromArgb(255, 158, 158, 158));
    }

    private void PositionShellSplitterOverlay()
    {
        ShellSplitter.Visibility = Visibility.Collapsed;
    }

    private void UpdateSplitTabStripLayout(bool syncRightContent = true)
    {
        RebuildWorkspaceLayout();
        GetActiveBrowserTabView()?.SetShellHorizontalDual(false);
        CollapseTabViewInternalContent();
        RefreshActivePaneChrome();
    }

    private void RebuildWorkspaceLayout()
    {
        var ws = ViewModel.Workspace;
        var horizontal = ws.Orientation == DualPaneOrientation.Horizontal;
        var slotCount = Math.Max(1, ws.Slots.Count);

        ShellRightTabs.Visibility = Visibility.Collapsed;
        RightShellContentHost.Visibility = Visibility.Collapsed;
        ShellSplitter.Visibility = Visibility.Collapsed;

        foreach (var host in _secondaryHosts)
        {
            BrowserArea.Children.Remove(host);
        }

        foreach (var sp in _workspaceSplitters)
        {
            BrowserArea.Children.Remove(sp);
        }

        BrowserArea.ColumnDefinitions.Clear();
        BrowserArea.RowDefinitions.Clear();

        if (slotCount == 1)
        {
            BrowserArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 160 });
            BrowserArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 80 });
            Grid.SetColumn(PrimarySlotHost, 0);
            Grid.SetRow(PrimarySlotHost, 0);
            Grid.SetColumnSpan(PrimarySlotHost, 1);
            Grid.SetRowSpan(PrimarySlotHost, 1);
            foreach (var host in _secondaryHosts)
            {
                host.Group = null;
                host.IsFocusedSlot = false;
            }

            return;
        }

        for (var i = 0; i < slotCount; i++)
        {
            var ratio = Math.Max(0.05, ws.Slots[i].Ratio);
            if (horizontal)
            {
                BrowserArea.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(ratio, GridUnitType.Star),
                    MinWidth = 160,
                });
                if (i < slotCount - 1)
                {
                    BrowserArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
                }
            }
            else
            {
                BrowserArea.RowDefinitions.Add(new RowDefinition
                {
                    Height = new GridLength(ratio, GridUnitType.Star),
                    MinHeight = 80,
                });
                if (i < slotCount - 1)
                {
                    BrowserArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
                }
            }
        }

        if (horizontal)
        {
            BrowserArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }
        else
        {
            BrowserArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        PlaceWorkspaceChild(PrimarySlotHost, slotIndex: 0, horizontal);

        EnsureSecondaryHostCount(slotCount - 1);
        for (var i = 1; i < slotCount; i++)
        {
            var host = _secondaryHosts[i - 1];
            host.ShellViewModel = ViewModel;
            host.Group = ws.Slots[i].Group;
            host.IsFocusedSlot = ws.ActiveSlotIndex == i;
            host.SyncFromGroup();
            PlaceWorkspaceChild(host, slotIndex: i, horizontal);
            if (!BrowserArea.Children.Contains(host))
            {
                BrowserArea.Children.Add(host);
            }
        }

        for (var i = slotCount - 1; i < _secondaryHosts.Count; i++)
        {
            _secondaryHosts[i].Group = null;
            _secondaryHosts[i].IsFocusedSlot = false;
        }

        EnsureWorkspaceSplitterCount(slotCount - 1);
        for (var i = 0; i < slotCount - 1; i++)
        {
            var sp = _workspaceSplitters[i];
            PlaceWorkspaceSplitter(sp, afterSlotIndex: i, horizontal);
            if (!BrowserArea.Children.Contains(sp))
            {
                BrowserArea.Children.Add(sp);
            }
        }
    }

    private static void PlaceWorkspaceChild(FrameworkElement el, int slotIndex, bool horizontal)
    {
        var gridIndex = slotIndex * 2;
        if (horizontal)
        {
            Grid.SetColumn(el, gridIndex);
            Grid.SetRow(el, 0);
            Grid.SetColumnSpan(el, 1);
            Grid.SetRowSpan(el, 1);
        }
        else
        {
            Grid.SetRow(el, gridIndex);
            Grid.SetColumn(el, 0);
            Grid.SetColumnSpan(el, 1);
            Grid.SetRowSpan(el, 1);
        }
    }

    private void PlaceWorkspaceSplitter(Border sp, int afterSlotIndex, bool horizontal)
    {
        var gridIndex = afterSlotIndex * 2 + 1;
        EnsureShellSplitterBrush();
        sp.Background = _shellSplitterBrush;
        if (horizontal)
        {
            sp.Width = 5;
            sp.Height = double.NaN;
            sp.HorizontalAlignment = HorizontalAlignment.Stretch;
            sp.VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetColumn(sp, gridIndex);
            Grid.SetRow(sp, 0);
        }
        else
        {
            sp.Height = 5;
            sp.Width = double.NaN;
            sp.HorizontalAlignment = HorizontalAlignment.Stretch;
            sp.VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetRow(sp, gridIndex);
            Grid.SetColumn(sp, 0);
        }
    }

    private void EnsureSecondaryHostCount(int count)
    {
        while (_secondaryHosts.Count < count)
        {
            var host = new PaneTabHost
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                ShellViewModel = ViewModel,
            };
            host.PaneActivated += OnSecondaryPaneActivated;
            host.RequestSetSplit += orient =>
            {
                ViewModel.SetShellSplit(orient);
                UpdateSplitTabStripLayout();
            };
            host.RequestCloseAllSplits += () =>
            {
                ViewModel.CloseShellSplit();
                UpdateSplitTabStripLayout();
            };
            host.RequestAddSlot += () =>
            {
                ViewModel.AddWorkspaceSlot();
                UpdateSplitTabStripLayout();
            };
            host.RequestRemoveThisSlot += group =>
            {
                ViewModel.RemoveWorkspaceSlot(group);
                UpdateSplitTabStripLayout();
            };
            _secondaryHosts.Add(host);
        }
    }

    private void EnsureWorkspaceSplitterCount(int count)
    {
        while (_workspaceSplitters.Count < count)
        {
            var sp = new Border { IsHitTestVisible = true, Tag = _workspaceSplitters.Count };
            sp.PointerPressed += OnWorkspaceSplitterPressed;
            sp.PointerMoved += OnWorkspaceSplitterMoved;
            sp.PointerReleased += OnWorkspaceSplitterReleased;
            sp.PointerCaptureLost += OnWorkspaceSplitterCaptureLost;
            _workspaceSplitters.Add(sp);
        }

        for (var i = 0; i < _workspaceSplitters.Count; i++)
        {
            _workspaceSplitters[i].Tag = i;
            _workspaceSplitters[i].Visibility = i < count ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private int _workspaceSplitterIndex = -1;

    private void OnWorkspaceSplitterPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border sp || sp.Tag is not int idx)
        {
            return;
        }

        _workspaceSplitterIndex = idx;
        _shellSplitterDragging = true;
        sp.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnWorkspaceSplitterMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_shellSplitterDragging || _workspaceSplitterIndex < 0)
        {
            return;
        }

        var ws = ViewModel.Workspace;
        var a = _workspaceSplitterIndex;
        var b = a + 1;
        if (b >= ws.Slots.Count)
        {
            return;
        }

        var horizontal = ws.Orientation == DualPaneOrientation.Horizontal;
        var pos = e.GetCurrentPoint(BrowserArea).Position;
        double total = horizontal ? BrowserArea.ActualWidth : BrowserArea.ActualHeight;
        if (total < 32)
        {
            return;
        }

        var t = horizontal ? pos.X / total : pos.Y / total;
        t = Math.Clamp(t, 0.15, 0.85);
        var sum = ws.Slots[a].Ratio + ws.Slots[b].Ratio;
        ws.Slots[a].Ratio = sum * t;
        ws.Slots[b].Ratio = sum * (1 - t);
        ApplyWorkspaceSlotRatios();
        e.Handled = true;
    }

    private void OnWorkspaceSplitterReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border sp)
        {
            sp.ReleasePointerCapture(e.Pointer);
        }

        _shellSplitterDragging = false;
        _workspaceSplitterIndex = -1;
    }

    private void OnWorkspaceSplitterCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _shellSplitterDragging = false;
        _workspaceSplitterIndex = -1;
    }

    private void OnSecondaryPaneActivated(FilePaneViewModel pane)
    {
        ViewModel.NotifyPaneActivated(pane);
        var active = ViewModel.Workspace.ActiveSlotIndex;
        for (var i = 0; i < _secondaryHosts.Count; i++)
        {
            _secondaryHosts[i].IsFocusedSlot = (i + 1) == active;
        }

        // + 建头期间：只改蓝头，禁止整页 RefreshActivePaneChrome（与左侧 suppress 同效）
        if (ViewModel.SuppressShellRightSideEffects)
        {
            SetActiveLeftTabVisual(null);
            return;
        }

        SetActiveLeftTabVisual(null);
        RefreshActivePaneChrome();
    }

    private void EnsureRightShellBoundOnly()
    {
        foreach (var host in _secondaryHosts.Where(h => h.Group is not null))
        {
            host.SyncFromGroup();
        }
    }

    private void SyncShellRightPane()
    {
        EnsureRightShellBoundOnly();
    }

    private async Task EnsureRightShellContentAsync()
    {
        EnsureRightShellBoundOnly();
        await Task.CompletedTask;
    }

    private FilePaneControl GetOrCreateRightPaneView(FilePaneViewModel pane)
    {
        if (_rightPaneViews.TryGetValue(pane, out var existing))
        {
            return existing;
        }

        // 先不入可视树：多 Tab 时若全部 Opacity=0 叠在 Grid 里，布局/虚拟化仍会随 N 变卡
        var view = new FilePaneControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ViewModel = pane,
        };
        _rightPaneViews[pane] = view;
        return view;
    }

    /// <summary>
    /// 右栏只挂当前激活的 FilePaneControl；其它控件保留在字典（VM/列表不丢），但不参与布局。
    /// 网络盘多开 + 时，避免 N 份完整列表同时压在可视树上。
    /// </summary>
    private void ShowRightPaneContent(FilePaneViewModel? pane, bool createIfMissing, bool loadIfEmpty)
    {
        if (pane is null)
        {
            RightShellContentHost.Children.Clear();
            _activeRightPaneView = null;
            return;
        }

        FilePaneControl? target = null;
        if (_rightPaneViews.TryGetValue(pane, out var existing))
        {
            target = existing;
        }
        else if (createIfMissing)
        {
            target = GetOrCreateRightPaneView(pane);
        }

        if (target is null)
        {
            return;
        }

        // 只保留激活项在 Children；非激活从树上摘下（不置 ViewModel=null）
        for (var i = RightShellContentHost.Children.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(RightShellContentHost.Children[i], target))
            {
                RightShellContentHost.Children.RemoveAt(i);
            }
        }

        if (!RightShellContentHost.Children.Contains(target))
        {
            DetachFromVisualParent(target);
            target.Visibility = Visibility.Visible;
            target.Opacity = 1;
            target.IsHitTestVisible = true;
            RightShellContentHost.Children.Add(target);
            target.EnsureListBoundAfterAttach();
        }
        else
        {
            target.Visibility = Visibility.Visible;
            target.Opacity = 1;
            target.IsHitTestVisible = true;
        }

        _activeRightPaneView = target;

        if (loadIfEmpty &&
            pane.VisibleItems.Count == 0 &&
            !pane.IsLoading &&
            !string.IsNullOrWhiteSpace(pane.CurrentPath))
        {
            // 不在 UI 线程 Directory.Exists（UNC 易卡）；交给 LoadAsync 自己处理失败
            _ = pane.LoadAsync();
        }
    }

    private void RemoveRightPaneView(FilePaneViewModel pane)
    {
        if (!_rightPaneViews.Remove(pane, out var view))
        {
            return;
        }

        if (ReferenceEquals(_activeRightPaneView, view))
        {
            _activeRightPaneView = null;
        }

        RightShellContentHost.Children.Remove(view);
        view.ViewModel = null;
    }

    private void PruneRightPaneViews()
    {
        var live = ViewModel.ShellRightGroup.Panes;
        foreach (var stale in _rightPaneViews.Keys.Where(p => !live.Contains(p)).ToList())
        {
            RemoveRightPaneView(stale);
        }
    }

    private void ClearRightPaneViews()
    {
        foreach (var pane in _rightPaneViews.Keys.ToList())
        {
            RemoveRightPaneView(pane);
        }

        _activeRightPaneView = null;
    }

    private void SyncShellRightTabs()
    {
        // 副栏已迁入 PaneTabHost；保留调用点兼容
        EnsureRightShellBoundOnly();
    }

    private static void EnsureShellRightTabHeader(TabViewItem item, FilePaneViewModel pane, bool canClose)
    {
        var wantTitle = PaneGroupViewModel.GetShortName(pane.CurrentPath);
        var title = FindHeaderTitleText(item.Header)?.Text;
        var close = FindHeaderCloseButton(item.Header);
        var closeVisible = close is { Opacity: > 0.5, IsHitTestVisible: true };
        if (item.Header is Border { Tag: "TabHeaderRoot" } &&
            string.Equals(title, wantTitle, StringComparison.Ordinal) &&
            closeVisible == canClose)
        {
            return;
        }

        item.Header = CreateShellPaneTabHeader(pane, canClose);
    }

    private static Button? FindHeaderCloseButton(object? header)
    {
        switch (header)
        {
            case Button { Tag: "TabClose" } direct:
                return direct;
            case Border { Child: { } child }:
                return FindHeaderCloseButton(child);
            case Panel panel:
                foreach (var child in panel.Children)
                {
                    if (FindHeaderCloseButton(child) is { } found)
                    {
                        return found;
                    }
                }

                break;
        }

        return null;
    }

    private void RefreshShellRightTabHeaders()
    {
        EnsureRightShellBoundOnly();
    }

    private TabViewItem CreateShellRightTabItem(FilePaneViewModel pane, bool closable)
    {
        var item = new TabViewItem
        {
            Tag = pane,
            Header = CreateShellPaneTabHeader(pane, closable),
            Content = new Border { Width = 0, Height = 0, Visibility = Visibility.Collapsed },
            ContextFlyout = null,
            IsClosable = false,
        };
        SetTabItemActiveVisual(item, active: false);
        item.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnShellRightTabItemPointerPressed),
            handledEventsToo: true);
        return item;
    }

    private void OnShellRightTabItemPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not TabViewItem item)
        {
            return;
        }

        if (IsUnderTabCloseButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        ActivateRightTabFromItem(item);

        if (!e.GetCurrentPoint(item).Properties.IsRightButtonPressed)
        {
            return;
        }

        var pane = item.Tag as FilePaneViewModel
                   ?? (ShellRightTabs.TabItems.IndexOf(item) is int idx and >= 0
                       && idx < ViewModel.ShellRightGroup.Panes.Count
                       ? ViewModel.ShellRightGroup.Panes[idx]
                       : null);
        if (pane is null)
        {
            return;
        }

        var flyout = BuildShellRightTabContextFlyout(pane);
        flyout.ShowAt(item, e.GetCurrentPoint(item).Position);
        e.Handled = true;
    }

    private MenuFlyout BuildShellRightTabContextFlyout(FilePaneViewModel pane)
    {
        var flyout = new MenuFlyout();
        var group = ViewModel.ShellRightGroup;

        var close = new MenuFlyoutItem { Text = "关闭" };
        close.Click += (_, _) =>
        {
            group.ClosePane(pane);
            SyncShellRightTabs();
            SyncShellRightPane();
            RefreshActivePaneChrome();
        };
        flyout.Items.Add(close);

        var closeLeft = new MenuFlyoutItem { Text = "关闭左侧标签" };
        closeLeft.Click += (_, _) =>
        {
            var idx = group.Panes.IndexOf(pane);
            foreach (var other in group.Panes.Take(Math.Max(0, idx)).ToList())
            {
                group.ClosePane(other);
            }

            SyncShellRightTabs();
            SyncShellRightPane();
            RefreshActivePaneChrome();
        };
        flyout.Items.Add(closeLeft);

        var closeRight = new MenuFlyoutItem { Text = "关闭右侧标签" };
        closeRight.Click += (_, _) =>
        {
            var idx = group.Panes.IndexOf(pane);
            foreach (var other in group.Panes.Skip(idx + 1).ToList())
            {
                group.ClosePane(other);
            }

            SyncShellRightTabs();
            SyncShellRightPane();
            RefreshActivePaneChrome();
        };
        flyout.Items.Add(closeRight);

        var closeOthers = new MenuFlyoutItem { Text = "关闭其他标签" };
        closeOthers.Click += (_, _) =>
        {
            foreach (var other in group.Panes.Where(p => !ReferenceEquals(p, pane)).ToList())
            {
                group.ClosePane(other);
            }

            SyncShellRightTabs();
            SyncShellRightPane();
            RefreshActivePaneChrome();
        };
        flyout.Items.Add(closeOthers);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var splitSideBySide = new MenuFlyoutItem { Text = "左右分栏" };
        splitSideBySide.Click += (_, _) =>
        {
            ViewModel.SetShellSplit(DualPaneOrientation.Horizontal);
            UpdateSplitTabStripLayout();
        };
        flyout.Items.Add(splitSideBySide);

        var splitTopBottom = new MenuFlyoutItem { Text = "上下分栏" };
        splitTopBottom.Click += (_, _) =>
        {
            ViewModel.SetShellSplit(DualPaneOrientation.Vertical);
            UpdateSplitTabStripLayout();
        };
        flyout.Items.Add(splitTopBottom);

        var addSlot = new MenuFlyoutItem { Text = "再分一栏" };
        addSlot.Click += (_, _) =>
        {
            ViewModel.AddWorkspaceSlot();
            UpdateSplitTabStripLayout();
        };
        flyout.Items.Add(addSlot);

        var closeSplit = new MenuFlyoutItem { Text = "关闭全部分栏" };
        closeSplit.Click += (_, _) =>
        {
            ViewModel.CloseShellSplit();
            UpdateSplitTabStripLayout();
        };
        flyout.Items.Add(closeSplit);

        flyout.Opening += (_, _) =>
        {
            // 右键打开前切换到该右栏标签并激活右栏
            var idx = group.Panes.IndexOf(pane);
            if (idx >= 0)
            {
                group.ActiveIndex = idx;
                _syncingShellRightTabs = true;
                try
                {
                    ShellRightTabs.SelectedIndex = idx;
                }
                finally
                {
                    _syncingShellRightTabs = false;
                }

                SyncShellRightPane();
                ViewModel.NotifyPaneActivated(pane);
                RefreshActivePaneChrome();
            }

            var canClose = group.Panes.Count > 1;
            close.IsEnabled = canClose;
            closeLeft.IsEnabled = canClose && group.Panes.IndexOf(pane) > 0;
            closeRight.IsEnabled = canClose && group.Panes.IndexOf(pane) < group.Panes.Count - 1;
            closeOthers.IsEnabled = canClose;
            closeSplit.IsEnabled = ViewModel.IsDualPane;
        };

        return flyout;
    }

    private async void OnShellRightAddTab(TabView sender, object args)
    {
        // 必须与左侧 OnAddTabClick 同序：压住联动 → 只加一项 → 上蓝 → 等一帧 → 再 Seed/Load。
        // 旧坑：AddPane 改 ActiveIndex 会同步 RefreshShellRightTabHeaders(整表) + SyncShellRightPane(LoadAsync)。
        var sw = Stopwatch.StartNew();
        PerfLog.Write("RightAddTab/click");
        var group = ViewModel.ShellRightGroup;
        var seedFrom = group.ActivePane;

        FilePaneViewModel pane;
        TabViewItem? newItem;
        _suppressShellRightUiSync = true;
        _syncingShellRightTabs = true;
        ViewModel.SuppressShellRightSideEffects = true;
        try
        {
            pane = group.AddPane(loadContent: false);
            PerfLog.Write($"RightAddTab/paneVm: {sw.Elapsed.TotalMilliseconds:F0}ms");

            var canClose = group.Panes.Count > 1;
            if (canClose && ShellRightTabs.TabItems.Count == 1 &&
                ShellRightTabs.TabItems[0] is TabViewItem only)
            {
                EnsureShellRightTabHeader(only, group.Panes[0], canClose: true);
            }

            newItem = CreateShellRightTabItem(pane, canClose);
            ShellRightTabs.TabItems.Add(newItem);
            ShellRightTabs.SelectedIndex = ShellRightTabs.TabItems.Count - 1;

            // 上蓝在 suppress 内完成，避免 ActivePaneSide 变化触发整页 Refresh
            ViewModel.NotifyPaneActivated(pane);
            SetActiveLeftTabVisual(null);
            SetActiveRightTabVisual(newItem);
            PerfLog.Write($"RightAddTab/headerReady: {sw.Elapsed.TotalMilliseconds:F0}ms");
            await WaitForNextRenderAsync();
            PerfLog.Write($"RightAddTab/firstPaint: {sw.Elapsed.TotalMilliseconds:F0}ms");
        }
        finally
        {
            _syncingShellRightTabs = false;
            _suppressShellRightUiSync = false;
            ViewModel.SuppressShellRightSideEffects = false;
        }

        var capturedPane = pane;
        var capturedSeed = seedFrom;
        var capturedItem = newItem;
        // Low：让蓝头先稳住。先 Seed（控件未挂树，避免逐条 CollectionChanged），再只挂当前控件。
        _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            using var _ = PerfLog.Measure("RightAddTab.InitAndBody");
            try
            {
                if (capturedSeed is not null && await capturedPane.TrySeedFromAsync(capturedSeed))
                {
                    PerfLog.Write($"RightAddTab/seededFromCache: {sw.Elapsed.TotalMilliseconds:F0}ms");
                }
                else
                {
                    await capturedPane.LoadAsync();
                    PerfLog.Write($"RightAddTab/initDone: {sw.Elapsed.TotalMilliseconds:F0}ms");
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write("RightAddTab.Load", ex);
            }

            if (!ReferenceEquals(ViewModel.ShellRightGroup.ActivePane, capturedPane))
            {
                return;
            }

            // 旧右栏控件从树上摘下（仍留在字典保活）；只挂当前 Tab，避免 N 份列表叠布局
            var view = GetOrCreateRightPaneView(capturedPane);
            ShowRightPaneContent(capturedPane, createIfMissing: false, loadIfEmpty: false);
            view.EnsureListBoundAfterAttach();

            if (capturedItem is not null &&
                ReferenceEquals(ShellRightTabs.SelectedItem, capturedItem))
            {
                SetActiveRightTabVisual(capturedItem);
            }

            PerfLog.Write($"RightAddTab/bodyVisible: {sw.Elapsed.TotalMilliseconds:F0}ms");
        });
    }

    private void OnShellRightTabClose(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        var idx = sender.TabItems.IndexOf(args.Tab);
        if (idx < 0 || idx >= ViewModel.ShellRightGroup.Panes.Count)
        {
            return;
        }

        ViewModel.ShellRightGroup.ClosePane(ViewModel.ShellRightGroup.Panes[idx]);
        SyncShellRightTabs();
        SyncShellRightPane();
        RefreshActivePaneChrome();
    }

    private void OnShellRightSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingShellRightTabs)
        {
            return;
        }

        var idx = ShellRightTabs.SelectedIndex;
        if (idx < 0 || idx >= ViewModel.ShellRightGroup.Panes.Count)
        {
            return;
        }

        ViewModel.ShellRightGroup.ActiveIndex = idx;
        SyncShellRightPane();
        ViewModel.NotifyPaneActivated(ViewModel.ShellRightGroup.ActivePane);
        ScheduleNavigationSync();
        RefreshActivePaneChrome();
    }

    private void OnBrowserTabsPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 左栏标签项自身已挂 PointerPressed；这里只兜底点到条空白时激活左栏
        if (FindTabViewItemAtPointer(BrowserTabs, e) is not null ||
            FindAncestorTabViewItem(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (ViewModel.SelectedTab?.LeftPane is { } left)
        {
            ViewModel.NotifyPaneActivated(left);
        }
    }

    private void OnShellRightTabsPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (FindTabViewItemAtPointer(ShellRightTabs, e) is not null ||
            FindAncestorTabViewItem(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (ViewModel.ShellRightGroup.ActivePane is { } right)
        {
            ViewModel.NotifyPaneActivated(right);
        }
    }

    private static TabViewItem? FindAncestorTabViewItem(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TabViewItem item)
            {
                return item;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static TabViewItem? FindTabViewItemAtPointer(UIElement root, PointerRoutedEventArgs e)
    {
        try
        {
            var local = e.GetCurrentPoint(root).Position;
            var windowPoint = root.TransformToVisual(null).TransformPoint(local);
            foreach (var el in VisualTreeHelper.FindElementsInHostCoordinates(windowPoint, root))
            {
                if (el is TabViewItem direct)
                {
                    return direct;
                }

                if (FindAncestorTabViewItem(el) is { } ancestor)
                {
                    return ancestor;
                }
            }
        }
        catch
        {
            // ignore hit-test failures
        }

        return null;
    }

    private void ActivateLeftTabFromItem(TabViewItem item)
    {
        if (!ReferenceEquals(BrowserTabs.SelectedItem, item))
        {
            BrowserTabs.SelectedItem = item;
            // OnSelectedTabItemChanged → ShowActiveTabContent + ScheduleNavigationSync
            return;
        }

        ShowActiveTabContent();
        if (TryGetBrowserTabVm(item) is { } tab)
        {
            ViewModel.SelectedTab = tab;
            ViewModel.NotifyPaneActivated(tab.LeftPane);
        }
        else if (ViewModel.SelectedTab?.LeftPane is { } left)
        {
            ViewModel.NotifyPaneActivated(left);
        }

        ScheduleNavigationSync();
        RefreshActivePaneChrome();
    }

    private void ActivateRightTabFromItem(TabViewItem item)
    {
        var idx = ShellRightTabs.TabItems.IndexOf(item);
        if (idx < 0)
        {
            // IndexOf 偶发对包装项失败时，按 Tag / 引用扫描
            for (var i = 0; i < ShellRightTabs.TabItems.Count; i++)
            {
                if (ReferenceEquals(ShellRightTabs.TabItems[i], item))
                {
                    idx = i;
                    break;
                }
            }
        }

        if (idx < 0 || idx >= ViewModel.ShellRightGroup.Panes.Count)
        {
            return;
        }

        var pane = item.Tag as FilePaneViewModel ?? ViewModel.ShellRightGroup.Panes[idx];
        ViewModel.ShellRightGroup.ActiveIndex = idx;

        if (ShellRightTabs.SelectedIndex != idx)
        {
            _syncingShellRightTabs = true;
            try
            {
                ShellRightTabs.SelectedIndex = idx;
            }
            finally
            {
                _syncingShellRightTabs = false;
            }
        }

        SyncShellRightPane();
        ViewModel.NotifyPaneActivated(pane);
        ScheduleNavigationSync();
        RefreshActivePaneChrome();
    }

    private void OnShellSplitterPressed(object sender, PointerRoutedEventArgs e)
    {
        _shellSplitterDragging = true;
        ShellSplitter.CapturePointer(e.Pointer);
        TrySetShellSplitterCursor();
        e.Handled = true;
    }

    private void OnBrowserAreaSizeChangedForSplitter(object sender, SizeChangedEventArgs e)
    {
        if (_shellSplitterDragging)
        {
            return;
        }

        PositionShellSplitterOverlay();
    }

    private void OnShellSplitterMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_shellSplitterDragging || !e.Pointer.IsInContact)
        {
            return;
        }

        var pos = e.GetCurrentPoint(BrowserArea).Position;
        if (BrowserArea.ActualWidth > 16)
        {
            ApplyBrowseColumnPixels(pos.X / BrowserArea.ActualWidth);
        }

        e.Handled = true;
    }

    private void TrySetShellSplitterCursor()
    {
        try
        {
            var prop = typeof(UIElement).GetProperty(
                "ProtectedCursor",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            prop?.SetValue(ShellSplitter, Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast));
        }
        catch
        {
        }
    }

    private void OnShellSplitterReleased(object sender, PointerRoutedEventArgs e)
    {
        EndShellSplitterDrag(e.Pointer);
        e.Handled = true;
    }

    private void OnShellSplitterCaptureLost(object sender, PointerRoutedEventArgs e) =>
        EndShellSplitterDrag(e.Pointer);

    private void EndShellSplitterDrag(Microsoft.UI.Xaml.Input.Pointer pointer)
    {
        if (!_shellSplitterDragging)
        {
            return;
        }

        _shellSplitterDragging = false;
        try
        {
            ShellSplitter.ReleasePointerCapture(pointer);
        }
        catch
        {
        }

        // 松手后回到 Star 比例，窗口缩放时仍按比例分配
        ApplyBrowseColumnRatio(_splitTabRatio);
    }

    private void RefreshActivePaneChrome()
    {
        if (_isTabDragging || _suppressTabBodyMaterialize)
        {
            return;
        }

        using var _ = PerfLog.Measure("RefreshActivePaneChrome");
        var dual = ViewModel.IsDualPane;
        var side = dual ? ViewModel.ActivePaneSide : PaneSide.Left;
        var horizontal = dual && ViewModel.IsHorizontalSplit;

        // 不再用内容区蓝框；激活态只体现在「唯一」蓝色标签上
        if (GetActiveBrowserTabView() is { } body)
        {
            body.ClearActivePaneBorders();
        }

        ApplyActiveTabHeaders(side, horizontal);
    }

    private void ApplyActiveTabHeaders(PaneSide side, bool horizontal)
    {
        // N 栏：Left=Primary；Right=任一副栏焦点（不再依赖已废弃的 ShellRightTabs）
        var leftActive = side == PaneSide.Left;
        var secondaryActive = side == PaneSide.Right;

        TabViewItem? activeLeft = null;
        if (leftActive &&
            BrowserTabs.SelectedItem is TabViewItem leftItem &&
            !IsSpecialTab(leftItem) &&
            TryGetBrowserTabVm(leftItem) is not null)
        {
            activeLeft = leftItem;
        }

        SetActiveLeftTabVisual(activeLeft);

        var activeSlot = ViewModel.Workspace.ActiveSlotIndex;
        for (var i = 0; i < _secondaryHosts.Count; i++)
        {
            var focused = secondaryActive && (i + 1) == activeSlot;
            _secondaryHosts[i].IsFocusedSlot = focused;
            if (!focused)
            {
                // 即使原先已是 false，也强制清蓝头，避免与左栏同路径时「串台」残留
                _secondaryHosts[i].ClearFocusVisuals();
            }
        }

        _chromeActiveRight = null;
        _pendingBringIntoViewLeft = activeLeft;
        _pendingBringIntoViewRight = null;
        BringTabIntoView(activeLeft);
    }

    /// <summary>右侧业务标签最多一个实心蓝底。</summary>
    /// <summary>右侧业务标签最多一个实心蓝底（只改前后两项，避免 N 个 tab 全量刷）。</summary>
    private void SetActiveRightTabVisual(TabViewItem? active)
    {
        if (ReferenceEquals(_chromeActiveRight, active))
        {
            if (active is not null)
            {
                SetTabItemActiveVisual(active, true);
            }

            return;
        }

        if (_chromeActiveRight is not null && ShellRightTabs.TabItems.Contains(_chromeActiveRight))
        {
            SetTabItemActiveVisual(_chromeActiveRight, false);
        }

        if (active is not null)
        {
            SetTabItemActiveVisual(active, true);
        }

        _chromeActiveRight = active;
    }

    /// <summary>
    /// 保证整条业务标签里最多一个蓝底。撕裂/合并后旧 tab 可能残留蓝底，
    /// 不能只清 _chromeActiveLeft 引用。
    /// </summary>
    private void SetActiveLeftTabVisual(TabViewItem? active)
    {
        foreach (var obj in BrowserTabs.TabItems)
        {
            if (obj is not TabViewItem item || IsSpecialTab(item))
            {
                continue;
            }

            SetTabItemActiveVisual(item, ReferenceEquals(item, active));
        }

        _chromeActiveLeft = active;
    }

    /// <summary>撕裂后：激活剩余第一个业务 tab。</summary>
    private void ActivateFirstBusinessTab()
    {
        foreach (var obj in BrowserTabs.TabItems)
        {
            if (obj is not TabViewItem item || IsSpecialTab(item))
            {
                continue;
            }

            if (TryGetBrowserTabVm(item) is not { } tab)
            {
                continue;
            }

            ViewModel.SelectedTab = tab;
            BrowserTabs.SelectedItem = item;
            SetActiveLeftTabVisual(item);
            if (!_suppressTabBodyMaterialize && !_isTabDragging)
            {
                ShowActiveTabContent();
            }

            return;
        }

        ViewModel.SelectedTab = null;
        SetActiveLeftTabVisual(null);
        ShowTabBodyInHost(null);
    }

    private static Brush? _tabAccentBrush;
    private static Brush? _tabActiveFg;
    private static Brush? _tabInactiveFg;
    private static Brush? _tabInactiveIconFg;
    private static Brush? _tabTransparentBrush;

    private static void EnsureTabBrushes()
    {
        if (_tabAccentBrush is not null)
        {
            return;
        }

        _tabAccentBrush = Application.Current.Resources.TryGetValue("AppAccentBrush", out var a) && a is Brush ab
            ? ab
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 212));
        _tabActiveFg = new SolidColorBrush(Microsoft.UI.Colors.White);
        // 固定深色字：勿用主题 brush（拖拽态下可能变成浅色 → 看不清）
        _tabInactiveFg = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32));
        _tabInactiveIconFg = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 96, 96, 96));
        _tabTransparentBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    /// <summary>激活态：Header 铺满蓝底（含关闭钮）；激活/非激活同一 Margin，避免字体被撑宽。</summary>
    private static void SetTabItemActiveVisual(TabViewItem item, bool active)
    {
        EnsureTabBrushes();
        item.IsClosable = false;

        // 压住 WinUI 原生 Selected 底边线/浅描边，只保留我们的实心蓝头
        item.Background = _tabTransparentBrush;

        if (item.Header is not Border root || !Equals(root.Tag, "TabHeaderRoot"))
        {
            return;
        }

        root.Background = active ? _tabAccentBrush : _tabTransparentBrush;
        ApplyForegroundToHeader(
            root.Child as DependencyObject,
            active ? _tabActiveFg! : _tabInactiveFg!,
            active ? _tabActiveFg! : _tabInactiveIconFg!);
    }

    private static void ApplyForegroundToHeader(DependencyObject? node, Brush fg, Brush iconFg)
    {
        switch (node)
        {
            case null:
                return;
            case TextBlock text:
                text.Foreground = fg;
                return;
            case FontIcon icon:
                icon.Foreground = iconFg;
                return;
            case Button { Tag: "TabClose" } closeBtn:
                ApplyForegroundToHeader(closeBtn.Content as DependencyObject, fg, iconFg);
                return;
            case Panel panel:
                foreach (var child in panel.Children)
                {
                    ApplyForegroundToHeader(child as DependencyObject, fg, iconFg);
                }

                return;
            case Border { Child: DependencyObject borderChild }:
                ApplyForegroundToHeader(borderChild, fg, iconFg);
                return;
            case ContentControl { Content: DependencyObject content }:
                ApplyForegroundToHeader(content, fg, iconFg);
                return;
        }
    }

    private static FrameworkElement CreateShellPaneTabHeader(FilePaneViewModel pane, bool canClose = false)
    {
        EnsureTabBrushes();
        var title = PaneGroupViewModel.GetShortName(pane.CurrentPath);
        // 与左侧 CreateTabHeader 同结构（含关闭槽），避免分栏左右 tab 高低错位
        var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(new FontIcon
        {
            Glyph = "\uE8B7",
            FontSize = 12,
            Foreground = _tabInactiveIconFg,
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Tag = "Title",
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = _tabInactiveFg,
        });
        Grid.SetColumn(panel, 0);
        row.Children.Add(panel);

        // 始终占位关闭列，单 pane 时透明不可点，保证与左侧同高
        var close = CreateTabCloseButton(() =>
        {
            FindPageOwningPane(pane)?.CloseShellRightPane(pane);
        });
        if (!canClose)
        {
            close.Opacity = 0;
            close.IsHitTestVisible = false;
            close.IsEnabled = false;
        }

        Grid.SetColumn(close, 1);
        row.Children.Add(close);

        return WrapTabHeader(row);
    }

    private static Button CreateTabCloseButton(Action onClose)
    {
        EnsureTabBrushes();
        var close = new Button
        {
            Tag = "TabClose",
            Content = new FontIcon
            {
                Glyph = "\uE711",
                FontSize = 10,
                Foreground = _tabInactiveFg,
            },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2, 4, 2),
            MinWidth = 22,
            MinHeight = 22,
            Width = 22,
            Height = 22,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(4),
        };
        ToolTipService.SetToolTip(close, "关闭");
        close.Click += (_, _) => onClose();
        return close;
    }

    private static MainPage? FindPageOwningPane(FilePaneViewModel pane)
    {
        foreach (var window in App.ActiveWindows)
        {
            if (window is MainWindow { Page: MainPage page } &&
                page.ViewModel.ShellRightGroup.Panes.Contains(pane))
            {
                return page;
            }
        }

        return null;
    }

    private void CloseShellRightPane(FilePaneViewModel pane)
    {
        if (ViewModel.ShellRightGroup.Panes.Count <= 1)
        {
            return;
        }

        ViewModel.ShellRightGroup.ClosePane(pane);
        RemoveRightPaneView(pane);
        SyncShellRightTabs();
        _ = EnsureRightShellContentAsync();
        RefreshActivePaneChrome();
        ScheduleNavigationSync();
    }

    private static Border WrapTabHeader(UIElement content)
    {
        EnsureTabBrushes();
        return new Border
        {
            Tag = "TabHeaderRoot",
            Background = _tabTransparentBrush,
            // 负 margin 铺满 TabViewItem 槽位，盖住原生 Selected 底线
            Padding = new Thickness(12, 5, 10, 5),
            Margin = new Thickness(-8, -4, -8, -4),
            CornerRadius = new CornerRadius(4),
            MinHeight = 30,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = content,
        };
    }

    private TabViewItem? _pendingBringIntoViewLeft;
    private TabViewItem? _pendingBringIntoViewRight;

    /// <summary>激活标签始终滚入可视区（启动多标签时尤其容易滚出视野）。</summary>
    private void EnsureActiveTabsVisible()
    {
        BringTabIntoView(_pendingBringIntoViewLeft ?? BrowserTabs.SelectedItem as TabViewItem);
        if (ViewModel.IsDualPane && ViewModel.IsHorizontalSplit)
        {
            BringTabIntoView(_pendingBringIntoViewRight ?? ShellRightTabs.SelectedItem as TabViewItem);
        }

        // 布局未完成时再补一次
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            BringTabIntoView(_pendingBringIntoViewLeft ?? BrowserTabs.SelectedItem as TabViewItem);
            if (ViewModel.IsDualPane && ViewModel.IsHorizontalSplit)
            {
                BringTabIntoView(_pendingBringIntoViewRight ?? ShellRightTabs.SelectedItem as TabViewItem);
            }
        });
    }

    private static void BringTabIntoView(TabViewItem? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            item.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = false,
                HorizontalAlignmentRatio = 0.5,
            });
        }
        catch
        {
        }
    }

    private static FrameworkElement? GetTabBody(TabViewItem item) =>
        item.Tag as FrameworkElement ?? item.Content as FrameworkElement;

    /// <summary>把元素从其当前视觉父节点摘下，避免二次 Parent 赋值 E_INVALIDARG。</summary>
    private static void DetachFromVisualParent(UIElement? element)
    {
        if (element is not FrameworkElement fe)
        {
            return;
        }

        switch (fe.Parent)
        {
            case Border border:
                border.Child = null;
                break;
            case Panel panel:
                panel.Children.Remove(fe);
                break;
            case ContentControl contentControl when ReferenceEquals(contentControl.Content, fe):
                contentControl.Content = null;
                break;
            case ContentPresenter presenter when ReferenceEquals(presenter.Content, fe):
                presenter.Content = null;
                break;
        }
    }

    /// <summary>合并前确保 TabViewItem 不在任何窗口的 TabItems 里，并清空仍托管其 body 的 ContentHost。</summary>
    private static void DetachTabItemFromAllWindows(TabViewItem item)
    {
        FrameworkElement? body = item.Tag as FrameworkElement;

        foreach (var window in App.ActiveWindows)
        {
            if (window is not MainWindow { Page: MainPage page })
            {
                continue;
            }

            if (body is not null && page.TabContentHost.Children.Contains(body))
            {
                page.RemoveTabBodyFromHost(body);
            }

            if (page.BrowserTabs.TabItems.Contains(item))
            {
                try
                {
                    page.BrowserTabs.TabItems.Remove(item);
                }
                catch (ArgumentException)
                {
                    // 已不在集合中
                }
            }
        }

        DetachFromVisualParent(body);
    }

    /// <summary>取业务 VM；Tag 可能是尚未物化的 BrowserTabViewModel。</summary>
    private static BrowserTabViewModel? TryGetBrowserTabVm(TabViewItem item) =>
        item.Tag switch
        {
            BrowserTabView { ViewModel: { } vm } => vm,
            BrowserTabViewModel vm => vm,
            _ => null,
        };

    /// <summary>首次需要内容时才创建 BrowserTabView（+ 新建只画标签头）。</summary>
    private BrowserTabView? EnsureBrowserTabView(TabViewItem item)
    {
        if (item.Tag is BrowserTabView existing)
        {
            return existing;
        }

        if (item.Tag is not BrowserTabViewModel tab)
        {
            return null;
        }

        using var _ = PerfLog.Measure("EnsureBrowserTabView");
        var view = new BrowserTabView
        {
            ViewModel = tab,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        item.Tag = view;
        return view;
    }

    private static T? FindDescendant<T>(DependencyObject root, string? name = null)
        where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && (name is null || match.Name == name))
            {
                return match;
            }

            var nested = FindDescendant<T>(child, name);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        App.Window.Closed -= OnWindowClosed;
        await ViewModel.PersistSessionAsync();
    }

    private void SyncTabs()
    {
        if (_suppressTabSync)
        {
            return;
        }

        using var _ = PerfLog.Measure("SyncTabs");
        _suppressTabSync = true;
        try
        {
            var tabs = ViewModel.Tabs;

            foreach (var tab in _tabItems.Keys.Where(k => !tabs.Contains(k)).ToList())
            {
                var dead = _tabItems[tab];
                if (dead.Tag is UIElement body)
                {
                    RemoveTabBodyFromHost(body);
                }

                BrowserTabs.TabItems.Remove(dead);
                _tabItems.Remove(tab);
            }

            foreach (var tab in tabs)
            {
                if (!_tabItems.TryGetValue(tab, out var item))
                {
                    item = CreateTabItem(tab);
                    _tabItems[tab] = item;
                }
            }

            // 去掉 UI 里已无对应 VM 的业务 Tab（不含主页/搜索等特殊页）
            foreach (var orphan in BrowserTabs.TabItems.OfType<TabViewItem>()
                         .Where(i => !IsSpecialTab(i) && !_tabItems.ContainsValue(i))
                         .ToList())
            {
                if (orphan.Tag is UIElement body)
                {
                    RemoveTabBodyFromHost(body);
                }

                BrowserTabs.TabItems.Remove(orphan);
            }

            // 增量对齐：只 Move 错位项，禁止 Clear；新 tab 只 Insert 一次
            for (var i = 0; i < tabs.Count; i++)
            {
                var item = _tabItems[tabs[i]];
                var rawActual = IndexOfRawTabItem(item);
                var rawDesired = BusinessIndexToRawInsertIndex(i);
                if (rawActual < 0)
                {
                    BrowserTabs.TabItems.Insert(Math.Min(rawDesired, BrowserTabs.TabItems.Count), item);
                    continue;
                }

                if (rawActual == rawDesired)
                {
                    continue;
                }

                BrowserTabs.TabItems.Remove(item);
                rawDesired = BusinessIndexToRawInsertIndex(i);
                BrowserTabs.TabItems.Insert(Math.Min(rawDesired, BrowserTabs.TabItems.Count), item);
            }

            // 特殊 Tab 仅在不在末尾时才挪动（避免每次 Add 都 Remove/Add 闪空白）
            EnsureSpecialTabsAtEnd();

            PruneOrphanBrowserTabsCore();

            if (ViewModel.ActiveSearch is null &&
                ViewModel.SelectedTab is not null &&
                _tabItems.TryGetValue(ViewModel.SelectedTab, out var selectedItem) &&
                !ReferenceEquals(BrowserTabs.SelectedItem, selectedItem))
            {
                BrowserTabs.SelectedItem = selectedItem;
            }
        }
        finally
        {
            _suppressTabSync = false;
        }
    }

    private void EnsureSpecialTabsAtEnd()
    {
        var specials = BrowserTabs.TabItems.OfType<TabViewItem>().Where(IsSpecialTab).ToList();
        if (specials.Count == 0)
        {
            return;
        }

        var businessCount = BrowserTabs.TabItems.Count - specials.Count;
        var alreadyOk = true;
        for (var i = 0; i < specials.Count; i++)
        {
            if (!ReferenceEquals(BrowserTabs.TabItems[businessCount + i], specials[i]))
            {
                alreadyOk = false;
                break;
            }
        }

        if (alreadyOk)
        {
            return;
        }

        foreach (var special in specials)
        {
            BrowserTabs.TabItems.Remove(special);
            BrowserTabs.TabItems.Add(special);
        }
    }

    /// <summary>业务槽位 i 在 TabItems 中的插入下标（跳过特殊 Tab）。</summary>
    private int BusinessIndexToRawInsertIndex(int businessIndex)
    {
        var seen = 0;
        for (var i = 0; i < BrowserTabs.TabItems.Count; i++)
        {
            if (BrowserTabs.TabItems[i] is not TabViewItem ti || IsSpecialTab(ti))
            {
                continue;
            }

            if (seen == businessIndex)
            {
                return i;
            }

            seen++;
        }

        for (var i = 0; i < BrowserTabs.TabItems.Count; i++)
        {
            if (BrowserTabs.TabItems[i] is TabViewItem ti && IsSpecialTab(ti))
            {
                return i;
            }
        }

        return BrowserTabs.TabItems.Count;
    }

    private int IndexOfRawTabItem(TabViewItem item)
    {
        for (var i = 0; i < BrowserTabs.TabItems.Count; i++)
        {
            if (ReferenceEquals(BrowserTabs.TabItems[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    private int IndexOfBusinessTab(TabViewItem item)
    {
        var idx = 0;
        foreach (var obj in BrowserTabs.TabItems)
        {
            if (obj is not TabViewItem tabItem || IsSpecialTab(tabItem))
            {
                continue;
            }

            if (ReferenceEquals(tabItem, item))
            {
                return idx;
            }

            idx++;
        }

        return -1;
    }

    private void PruneOrphanBrowserTabs()
    {
        if (_suppressTabSync)
        {
            return;
        }

        _suppressTabSync = true;
        try
        {
            PruneOrphanBrowserTabsCore();
        }
        finally
        {
            _suppressTabSync = false;
        }
    }

    private void PruneOrphanBrowserTabsCore()
    {
        for (var i = BrowserTabs.TabItems.Count - 1; i >= 0; i--)
        {
            if (BrowserTabs.TabItems[i] is not TabViewItem item || IsSpecialTab(item))
            {
                continue;
            }

            if (!_tabItems.ContainsValue(item))
            {
                BrowserTabs.TabItems.RemoveAt(i);
            }
        }
    }

    private bool IsSpecialTab(TabViewItem item) =>
        ReferenceEquals(item, _homeTabItem) || ReferenceEquals(item, _searchTabItem);

    /// <summary>仅在拖拽结束时写回顺序，避免 TabItemsChanged 与 SyncTabs 互相拉扯。</summary>
    private void SyncTabOrderFromUi()
    {
        if (_suppressTabSync)
        {
            return;
        }

        var order = new List<BrowserTabViewModel>();
        foreach (var obj in BrowserTabs.TabItems)
        {
            if (obj is TabViewItem item &&
                TryGetBrowserTabVm(item) is { } tab &&
                _tabItems.ContainsKey(tab))
            {
                order.Add(tab);
            }
        }

        if (order.Count == 0)
        {
            return;
        }

        _isReorderingFromUi = true;
        try
        {
            ViewModel.ApplyTabOrder(order);
        }
        finally
        {
            _isReorderingFromUi = false;
        }
    }

    private void SyncSearchTab()
    {
        if (ViewModel.ActiveSearch is null)
        {
            if (_searchTabItem is not null)
            {
                BrowserTabs.TabItems.Remove(_searchTabItem);
                _searchTabItem = null;
            }

            ShowActiveTabContent();
            return;
        }

        if (_searchTabItem is null)
        {
            var searchView = new SearchResultsView { ViewModel = ViewModel.ActiveSearch };
            _searchTabItem = new TabViewItem
            {
                Header = CreateSearchTabHeader(ViewModel.ActiveSearch.Title),
                Content = new Border { Width = 0, Height = 0, Visibility = Visibility.Collapsed },
                Tag = searchView,
                CanDrag = false,
                IsClosable = false,
            };
            SetTabItemActiveVisual(_searchTabItem, active: false);
            BrowserTabs.TabItems.Add(_searchTabItem);
        }
        else
        {
            if (_searchTabItem.Header is Border { Tag: "TabHeaderRoot" } &&
                TabChromeHelper.FindTitleText(_searchTabItem.Header) is { } titleText)
            {
                titleText.Text = ViewModel.ActiveSearch.Title;
            }
            else
            {
                _searchTabItem.Header = CreateSearchTabHeader(ViewModel.ActiveSearch.Title);
            }

            if (GetTabBody(_searchTabItem) is SearchResultsView view)
            {
                view.ViewModel = ViewModel.ActiveSearch;
            }
        }

        BrowserTabs.SelectedItem = _searchTabItem;
        ShowActiveTabContent();
    }

    /// <summary>搜索 Tab 与普通文件 Tab 同构 Header，保证文字垂直居中。</summary>
    private FrameworkElement CreateSearchTabHeader(string title)
    {
        EnsureTabBrushes();
        var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(new FontIcon
        {
            Glyph = "\uE721",
            FontSize = 12,
            Foreground = _tabInactiveIconFg,
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Tag = "Title",
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = _tabInactiveFg,
        });
        Grid.SetColumn(panel, 0);
        row.Children.Add(panel);

        var close = CreateTabCloseButton(() => ViewModel.CloseSearchCommand.Execute(null));
        Grid.SetColumn(close, 1);
        row.Children.Add(close);

        return WrapTabHeader(row);
    }

    private TabViewItem CreateTabItem(BrowserTabViewModel tab)
    {
        // 仅建轻量 TabViewItem；BrowserTabView 延后到首次显示，避免 + 新建时整页控件 Instantiation 卡顿
        var item = new TabViewItem
        {
            Header = CreateTabHeader(tab),
            Content = new Border { Width = 0, Height = 0, Visibility = Visibility.Collapsed },
            Tag = tab,
            AllowDrop = true,
            IsClosable = false,
        };
        SetTabItemActiveVisual(item, false);

        tab.PropertyChanged += OnTornTabPropertyChanged;

        item.DragOver += OnTabItemDragOver;
        item.Drop += OnTabItemDrop;
        item.ContextFlyout = null;
        item.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnBrowserTabItemPointerPressed),
            handledEventsToo: true);
        return item;
    }

    private static void OnTornTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (sender is not BrowserTabViewModel tab)
        {
            return;
        }

        var page = FindPageOwningTab(tab);
        if (page is null || !page._tabItems.TryGetValue(tab, out var item))
        {
            return;
        }

        var active = page.ViewModel.ActivePaneSide == PaneSide.Left &&
                     ReferenceEquals(page.BrowserTabs.SelectedItem, item);

        if (args.PropertyName == nameof(BrowserTabViewModel.Title))
        {
            if (FindHeaderTitleText(item.Header) is { } title)
            {
                title.Text = tab.Title;
            }

            return;
        }

        if (args.PropertyName == nameof(BrowserTabViewModel.IsLocked))
        {
            item.Header = CreateTabHeader(tab);
            SetTabItemActiveVisual(item, active);
        }
    }

    private static TextBlock? FindHeaderTitleText(object? header)
    {
        switch (header)
        {
            case TextBlock { Tag: "Title" } direct:
                return direct;
            case Border { Child: { } child }:
                return FindHeaderTitleText(child);
            case Panel panel:
                foreach (var child in panel.Children)
                {
                    if (FindHeaderTitleText(child) is { } found)
                    {
                        return found;
                    }
                }

                break;
        }

        return null;
    }

    private static void CloseTabFromAnyWindow(BrowserTabViewModel tab)
    {
        FindPageOwningTab(tab)?.ViewModel.CloseTabCommand.Execute(tab);
    }

    private static MainPage? FindPageOwningTab(BrowserTabViewModel tab)
    {
        foreach (var window in App.ActiveWindows)
        {
            if (window is MainWindow { Page: MainPage page } && page._tabItems.ContainsKey(tab))
            {
                return page;
            }
        }

        return null;
    }

    private void OnBrowserTabItemPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not TabViewItem item)
        {
            return;
        }

        // 标签可能已合并到其它窗口，事件仍挂在创建页 — 转发给当前宿主
        if (TryGetBrowserTabVm(item) is { } owned &&
            FindPageOwningTab(owned) is { } owner &&
            !ReferenceEquals(owner, this))
        {
            owner.OnBrowserTabItemPointerPressed(sender, e);
            return;
        }

        // 点关闭钮时不要抢激活/右键菜单，让 Button.Click 正常触发
        if (IsUnderTabCloseButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        ActivateLeftTabFromItem(item);

        if (!e.GetCurrentPoint(item).Properties.IsRightButtonPressed)
        {
            return;
        }

        if (TryGetBrowserTabVm(item) is not { } tab)
        {
            return;
        }

        var flyout = BuildTabContextFlyout(tab);
        flyout.ShowAt(item, e.GetCurrentPoint(item).Position);
        e.Handled = true;
    }

    private static bool IsUnderTabCloseButton(DependencyObject? source)
    {
        for (var current = source; current is not null; current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is Button button &&
                (button.Name == "CloseButton" || button.Tag as string == "TabClose"))
            {
                return true;
            }

            if (current is TabViewItem)
            {
                break;
            }
        }

        return false;
    }

    private static FrameworkElement CreateTabHeader(BrowserTabViewModel tab)
    {
        EnsureTabBrushes();
        var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (tab.IsLocked)
        {
            panel.Children.Add(new FontIcon
            {
                Glyph = "\uE72E",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = _tabInactiveFg,
            });
        }

        panel.Children.Add(new FontIcon
        {
            Glyph = "\uE8B7",
            FontSize = 12,
            Foreground = _tabInactiveIconFg,
            VerticalAlignment = VerticalAlignment.Center,
        });

        panel.Children.Add(new TextBlock
        {
            Text = tab.Title,
            Tag = "Title",
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = _tabInactiveFg,
        });
        Grid.SetColumn(panel, 0);
        row.Children.Add(panel);

        if (!tab.IsLocked)
        {
            var close = CreateTabCloseButton(() => CloseTabFromAnyWindow(tab));
            Grid.SetColumn(close, 1);
            row.Children.Add(close);
        }
        else
        {
            // 锁定 tab 也占位，避免与可关闭 tab 高低宽窄不一致
            var spacer = CreateTabCloseButton(() => { });
            spacer.Opacity = 0;
            spacer.IsHitTestVisible = false;
            spacer.IsEnabled = false;
            Grid.SetColumn(spacer, 1);
            row.Children.Add(spacer);
        }

        return WrapTabHeader(row);
    }

    private void OnTabItemDragOver(object sender, DragEventArgs e)
    {
        if (sender is TabViewItem item &&
            TryGetBrowserTabVm(item) is { } tab &&
            FindPageOwningTab(tab) is { } owner &&
            !ReferenceEquals(owner, this))
        {
            owner.OnTabItemDragOver(sender, e);
            return;
        }

        // 标签拖入须优先接受，否则停在某个 Tab 上会被当成拒绝 → 无法合并 / 误触发新建窗口
        if (e.DataView.Properties.ContainsKey(TabDragMarker))
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            e.Handled = true;
            HandleTabDragOver(e);
            return;
        }

        // 只接受资源管理器文件拖入；不要接 Text，以免标签重排/撕裂时被当成粘贴目标
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
        e.Handled = true;
    }

    private async void OnTabItemDrop(object sender, DragEventArgs e)
    {
        if (sender is not TabViewItem item)
        {
            return;
        }

        if (TryGetBrowserTabVm(item) is { } ownedVm &&
            FindPageOwningTab(ownedVm) is { } owner &&
            !ReferenceEquals(owner, this))
        {
            owner.OnTabItemDrop(sender, e);
            return;
        }

        if (e.DataView.Properties.ContainsKey(TabDragMarker))
        {
            HandleTabDrop(e);
            return;
        }

        // 文件拖入需要已物化的窗格
        if (EnsureBrowserTabView(item) is not { ViewModel: { } tab })
        {
            return;
        }

        var paths = await ExtractAnyPathsFromDataViewAsync(e.DataView);
        if (paths.Count == 0)
        {
            return;
        }

        await tab.LeftPane.PastePathsAsync(paths, move: false);
        ViewModel.SelectedTab = tab;
        if (_tabItems.TryGetValue(tab, out var tabItem))
        {
            BrowserTabs.SelectedItem = tabItem;
        }

        e.Handled = true;
    }

    private MenuFlyout BuildTabContextFlyout(BrowserTabViewModel tab)
    {
        var flyout = new MenuFlyout();

        var lockTab = new MenuFlyoutItem();
        lockTab.Click += (_, _) => tab.IsLocked = !tab.IsLocked;
        flyout.Items.Add(lockTab);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var close = new MenuFlyoutItem { Text = "关闭" };
        close.Click += (_, _) => ViewModel.CloseTabCommand.Execute(tab);
        flyout.Items.Add(close);

        var closeLeft = new MenuFlyoutItem { Text = "关闭左侧标签" };
        closeLeft.Click += (_, _) => ViewModel.CloseTabsToTheLeftCommand.Execute(tab);
        flyout.Items.Add(closeLeft);

        var closeRight = new MenuFlyoutItem { Text = "关闭右侧标签" };
        closeRight.Click += (_, _) => ViewModel.CloseTabsToTheRightCommand.Execute(tab);
        flyout.Items.Add(closeRight);

        var closeOthers = new MenuFlyoutItem { Text = "关闭其他标签" };
        closeOthers.Click += (_, _) => ViewModel.CloseOtherTabsCommand.Execute(tab);
        flyout.Items.Add(closeOthers);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var splitSideBySide = new MenuFlyoutItem { Text = "左右分栏" };
        splitSideBySide.Click += (_, _) =>
        {
            ViewModel.SetShellSplit(DualPaneOrientation.Horizontal);
            UpdateSplitTabStripLayout();
        };
        flyout.Items.Add(splitSideBySide);

        var splitTopBottom = new MenuFlyoutItem { Text = "上下分栏" };
        splitTopBottom.Click += (_, _) =>
        {
            ViewModel.SetShellSplit(DualPaneOrientation.Vertical);
            UpdateSplitTabStripLayout();
        };
        flyout.Items.Add(splitTopBottom);

        var addSlot = new MenuFlyoutItem { Text = "再分一栏" };
        addSlot.Click += (_, _) =>
        {
            ViewModel.AddWorkspaceSlot();
            UpdateSplitTabStripLayout();
        };
        flyout.Items.Add(addSlot);

        var closeSplit = new MenuFlyoutItem { Text = "关闭全部分栏" };
        closeSplit.Click += (_, _) =>
        {
            ViewModel.CloseShellSplit();
            UpdateSplitTabStripLayout();
        };
        flyout.Items.Add(closeSplit);

        flyout.Opening += (_, _) =>
        {
            // 右键打开菜单前先选中并激活该标签，避免跨栏时要点两次
            if (_tabItems.TryGetValue(tab, out var tabItem))
            {
                BrowserTabs.SelectedItem = tabItem;
            }

            ViewModel.SelectedTab = tab;
            ViewModel.NotifyPaneActivated(tab.LeftPane);
            RefreshActivePaneChrome();

            lockTab.Text = tab.IsLocked ? "解锁标签" : "锁定标签";
            close.IsEnabled = !tab.IsLocked;
            closeSplit.IsEnabled = ViewModel.IsDualPane;
        };

        return flyout;
    }

    private CancellationTokenSource? _navSelectCts;

    private async void SyncNavSelection()
    {
        using var _ = PerfLog.Measure("SyncNavSelection");
        var node = ViewModel.NavigationPane.SelectedTreeNode;
        if (node is null)
        {
            return;
        }

        // 固定访问 / 盘符快捷：在左侧竖条高亮
        if (ViewModel.NavigationPane.RailItems.Contains(node))
        {
            try
            {
                NavRailList.SelectedItem = node;
                node.IsSelected = true;
                NavTree.SelectedItem = null;
                FindDescendant<ScrollViewer>(NavTree)?.ChangeView(null, 0, null, disableAnimation: true);
            }
            catch
            {
            }

            return;
        }

        try
        {
            NavRailList.SelectedItem = null;
        }
        catch
        {
        }

        if (!ViewModel.NavigationPane.IsNavDrawerExpanded)
        {
            return;
        }

        _navSelectCts?.Cancel();
        _navSelectCts = new CancellationTokenSource();
        var token = _navSelectCts.Token;

        // 最多约 300ms，勿再 30×50ms 拖住 UI（切 Tab 体感卡 1s 的主因之一）
        for (var i = 0; i < 6; i++)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                NavTree.SelectedItem = node;
                node.IsSelected = true;

                if (NavTree.ContainerFromItem(node) is FrameworkElement fe)
                {
                    fe.StartBringIntoView(new BringIntoViewOptions
                    {
                        AnimationDesired = false,
                        VerticalAlignmentRatio = 0.35,
                    });
                    return;
                }

                ScrollNavTreeToNode(node);
                if (i >= 2)
                {
                    return;
                }
            }
            catch
            {
            }

            try
            {
                await Task.Delay(40, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void OnCollapseNavTreeClick(object sender, RoutedEventArgs e)
    {
        ViewModel.NavigationPane.CollapseAll();
        try
        {
            FindDescendant<ScrollViewer>(NavTree)?.ChangeView(null, 0, null, disableAnimation: true);
        }
        catch
        {
        }
    }

    private async void OnNavRailItemClick(object sender, ItemClickEventArgs e)
    {
        try
        {
            if (e.ClickedItem is not FolderTreeNode entry || !entry.IsNavigable)
            {
                return;
            }

            ViewModel.NavigationPane.SelectedTreeNode = entry;
            await NavigateTreePathAsync(entry);
        }
        catch
        {
        }
    }

    private async void OnToggleNavDrawerClick(object sender, RoutedEventArgs e)
    {
        ViewModel.NavigationPane.IsNavDrawerExpanded = !ViewModel.NavigationPane.IsNavDrawerExpanded;
        await ViewModel.PersistNavDrawerExpandedAsync();
    }

    private void OnNavTreeRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        try
        {
            var node = FindFolderTreeNode(e.OriginalSource as DependencyObject);
            if (node is null || !node.IsRecentHistory || !node.IsNavigable)
            {
                return;
            }

            var flyout = BuildRecentHistoryFlyout(node);
            if (e.OriginalSource is FrameworkElement anchor)
            {
                flyout.ShowAt(anchor, e.GetPosition(anchor));
            }
            else
            {
                flyout.ShowAt(NavTree, e.GetPosition(NavTree));
            }

            e.Handled = true;
        }
        catch
        {
        }
    }

    private MenuFlyout BuildRecentHistoryFlyout(FolderTreeNode node)
    {
        var flyout = new MenuFlyout();

        var open = new MenuFlyoutItem { Text = "打开", Icon = new FontIcon { Glyph = "\uE8E5" } };
        open.Click += async (_, _) => await NavigateTreePathAsync(node);
        flyout.Items.Add(open);

        var openTab = new MenuFlyoutItem { Text = "在新标签中打开", Icon = new FontIcon { Glyph = "\uE8A7" } };
        openTab.Click += async (_, _) => await ViewModel.NewTabCommand.ExecuteAsync(node.Path);
        flyout.Items.Add(openTab);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var remove = new MenuFlyoutItem { Text = "从历史移除", Icon = new FontIcon { Glyph = "\uE74D" } };
        remove.Click += (_, _) => ViewModel.NavigationPane.RemoveRecent(node.Path);
        flyout.Items.Add(remove);

        var clear = new MenuFlyoutItem { Text = "清空历史" };
        clear.Click += (_, _) => ViewModel.NavigationPane.ClearRecent();
        flyout.Items.Add(clear);

        return flyout;
    }

    private static FolderTreeNode? FindFolderTreeNode(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: FolderTreeNode fromContext })
            {
                return fromContext;
            }

            if (source is TreeViewItem { Content: FolderTreeNode fromContent })
            {
                return fromContent;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    /// <summary>
    /// 目标节点在视野外时容器不会被虚拟化生成，ContainerFromItem 始终为 null；
    /// 按可见顺序算扁平索引，滚动内部 ScrollViewer 把目标滚到大约 1/3 处。
    /// </summary>
    private void ScrollNavTreeToNode(FolderTreeNode target)
    {
        var index = GetVisibleFlatIndex(ViewModel.NavigationPane.TreeRoots, target);
        if (index < 0)
        {
            return;
        }

        var scrollViewer = FindDescendant<ScrollViewer>(NavTree);
        if (scrollViewer is null)
        {
            return;
        }

        const double rowHeight = 28.0;
        var viewport = scrollViewer.ViewportHeight;
        var targetOffset = Math.Max(0, (index * rowHeight) - (viewport * 0.3));
        scrollViewer.ChangeView(null, targetOffset, null, disableAnimation: true);
    }

    private static int GetVisibleFlatIndex(IEnumerable<FolderTreeNode> roots, FolderTreeNode target)
    {
        var index = 0;

        bool Walk(IEnumerable<FolderTreeNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.IsPlaceholder)
                {
                    continue;
                }

                if (ReferenceEquals(node, target))
                {
                    return true;
                }

                index++;
                if (node.IsExpanded && node.Children.Count > 0 && Walk(node.Children))
                {
                    return true;
                }
            }

            return false;
        }

        return Walk(roots) ? index : -1;
    }

    private void OnNavTreeExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        try
        {
            if (args.Item is FolderTreeNode node)
            {
                node.EnsureChildrenLoaded();
                return;
            }

            if (args.Node?.Content is FolderTreeNode content)
            {
                content.EnsureChildrenLoaded();
            }
        }
        catch
        {
        }
    }

    private async void OnNavTreeItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        try
        {
            FolderTreeNode? entry = args.InvokedItem as FolderTreeNode;
            if (entry is null && args.InvokedItem is TreeViewNode { Content: FolderTreeNode content })
            {
                entry = content;
            }

            if (entry is null || !entry.IsNavigable)
            {
                return;
            }

            try
            {
                NavRailList.SelectedItem = null;
            }
            catch
            {
            }

            await NavigateTreePathAsync(entry);
        }
        catch
        {
        }
    }

    private async Task NavigateTreePathAsync(FolderTreeNode entry)
    {
        var kind = entry.Path == @"\\"
            ? NavItemKind.Network
            : entry.IsDrive
                ? NavItemKind.Drive
                : NavItemKind.Location;
        var item = new NavigationItemViewModel(entry.Name, entry.Glyph, kind, entry.Path);
        await ViewModel.NavigateFromSidebarCommand.ExecuteAsync(item);
    }

    private async void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        ViewModel.SearchQuery = args.QueryText ?? sender.Text;
        await ViewModel.RunSearchCommand.ExecuteAsync(null);
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ViewModel.RefreshSearchSuggestions(sender.Text);
        }
    }

    private async void OnTabOverviewClick(object sender, RoutedEventArgs e)
    {
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 360,
            ItemsSource = ViewModel.Tabs.Select(t => t.Title).ToList(),
        };

        var dialog = new ContentDialog
        {
            Title = "标签总览",
            Content = list,
            PrimaryButtonText = "切换到选中",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary &&
            list.SelectedIndex >= 0 &&
            list.SelectedIndex < ViewModel.Tabs.Count)
        {
            ViewModel.SelectedTab = ViewModel.Tabs[list.SelectedIndex];
        }
    }

    private async void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        if (sender is AutoSuggestBox box)
        {
            ViewModel.SearchQuery = box.Text;
        }

        await ViewModel.RunSearchCommand.ExecuteAsync(null);
        e.Handled = true;
    }

    private async void OnFavoriteBarListItemClick(object sender, ItemClickEventArgs e)
    {
        if (DateTime.UtcNow < _suppressFavoriteTapUntilUtc)
        {
            return;
        }

        if (e.ClickedItem is not FavoriteNodeViewModel node)
        {
            return;
        }

        ViewModel.SelectedFavorite = node;
        var container = FavoriteBar.ContainerFromItem(node) as FrameworkElement;

        if (node.IsGroup)
        {
            await ShowFavoriteGroupFlyoutAsync(container ?? FavoriteBar, node);
            return;
        }

        await ViewModel.OpenFavoriteCommand.ExecuteAsync(node);
    }

    private void OnFavoriteBarDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.Count == 0 || e.Items[0] is not FavoriteNodeViewModel node)
        {
            e.Cancel = true;
            return;
        }

        e.Data.SetText($"{FavoriteDragFormat}:{node.Model.Id}");
        e.Data.RequestedOperation = DataPackageOperation.Move;
        _suppressFavoriteTapUntilUtc = DateTime.UtcNow.AddMilliseconds(500);
    }

    private void OnFavoriteBarContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is null)
        {
            return;
        }

        // 拖放目标挂在容器上：模板内 Border 在 CanDragItems 场景下经常收不到 Drop
        args.ItemContainer.AllowDrop = true;
        args.ItemContainer.DragOver -= OnFavoriteItemDragOver;
        args.ItemContainer.Drop -= OnFavoriteItemDrop;
        if (!args.InRecycleQueue)
        {
            args.ItemContainer.DragOver += OnFavoriteItemDragOver;
            args.ItemContainer.Drop += OnFavoriteItemDrop;
        }
    }

    private void OnFavoriteBarItemPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        }
    }

    private void OnFavoriteBarItemPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border)
        {
            border.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    private async void OnFavoriteBarItemClick(object sender, RoutedEventArgs e)
    {
        // 旧 Button 入口保留，避免热重载/旧生成代码残留引用
        if (sender is not FrameworkElement { Tag: FavoriteNodeViewModel node } anchor)
        {
            return;
        }

        ViewModel.SelectedFavorite = node;
        if (node.IsGroup)
        {
            await ShowFavoriteGroupFlyoutAsync(anchor, node);
            return;
        }

        await ViewModel.OpenFavoriteCommand.ExecuteAsync(node);
    }

    private async Task ShowFavoriteGroupFlyoutAsync(FrameworkElement? anchor, FavoriteNodeViewModel group)
    {
        if (anchor is null)
        {
            return;
        }

        var flyout = new MenuFlyout
        {
            // 紧贴按钮下方左对齐展开；限高避免长列表超出窗口
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft,
        };

        if (Application.Current.Resources.TryGetValue("AppFavoriteFlyoutPresenterStyle", out var styleObj) && styleObj is Style presenterStyle)
        {
            flyout.MenuFlyoutPresenterStyle = presenterStyle;
        }

        FillFavoriteMenuItems(flyout.Items, group, flyout);

        if (group.Children.Count > 0)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        var openAll = new MenuFlyoutItem { Text = "打开此分组下全部文件夹", Icon = new FontIcon { Glyph = "\uE8E5" } };
        openAll.Click += async (_, _) => await ViewModel.OpenAllInFavoriteCommand.ExecuteAsync(group);
        flyout.Items.Add(openAll);

        flyout.ShowAt(anchor);
        await Task.CompletedTask;
    }

    /// <summary>
    /// 递归构建分组菜单：子分组=悬浮向右级联展开的子菜单，叶子=点击打开；
    /// 管理操作（重命名/删除等）统一放在右键菜单里。
    /// </summary>
    private void FillFavoriteMenuItems(IList<MenuFlyoutItemBase> items, FavoriteNodeViewModel group, MenuFlyout rootFlyout)
    {
        foreach (var child in group.Children)
        {
            if (child.IsGroup)
            {
                var sub = new MenuFlyoutSubItem
                {
                    Text = child.DisplayName,
                    Icon = new FontIcon { Glyph = child.IconGlyph, Foreground = child.GlyphBrush },
                    Tag = child,
                };

                if (child.Children.Count == 0)
                {
                    sub.Items.Add(new MenuFlyoutItem { Text = "（空分组）", IsEnabled = false });
                }
                else
                {
                    FillFavoriteMenuItems(sub.Items, child, rootFlyout);
                }

                AttachFavoriteMenuContextMenu(sub, child, rootFlyout);
                items.Add(sub);
            }
            else
            {
                var item = new MenuFlyoutItem
                {
                    Text = child.DisplayName,
                    Icon = new FontIcon { Glyph = child.IconGlyph, Foreground = child.GlyphBrush },
                    Tag = child,
                };
                item.Click += async (_, _) =>
                {
                    ViewModel.SelectedFavorite = child;
                    await ViewModel.OpenFavoriteCommand.ExecuteAsync(child);
                };
                AttachFavoriteMenuContextMenu(item, child, rootFlyout);
                items.Add(item);
            }
        }
    }

    /// <summary>菜单项右键：关掉当前级联菜单，在原位置弹出管理菜单。</summary>
    private void AttachFavoriteMenuContextMenu(MenuFlyoutItemBase element, FavoriteNodeViewModel node, MenuFlyout rootFlyout)
    {
        element.RightTapped += (_, e) =>
        {
            e.Handled = true;
            var position = e.GetPosition(this);
            rootFlyout.Hide();

            var context = BuildFavoriteContextFlyout(node);
            DispatcherQueue.TryEnqueue(() => context.ShowAt(this, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            {
                Position = position,
            }));
        };
    }

    private async void OnAddTabClick(TabView sender, object args)
    {
        // + 号端到端：先切到左栏激活 + 出蓝头 → 记 firstPaint → 再后台 Initialize/物化 body
        var sw = Stopwatch.StartNew();
        PerfLog.Write("AddTab/click");
        _suppressTabBodyMaterialize = true;
        BrowserTabViewModel? tab = null;
        TabViewItem? selected = null;
        // 新建前记下当前窗格，供同路径 Seed（避免再扫盘）
        var seedFrom = ViewModel.SelectedTab?.LeftPane;
        try
        {
            tab = ViewModel.CreateAndSelectTab(null);
            if (tab is null)
            {
                return;
            }

            // 分栏时右栏可能仍是 ActivePaneSide：必须先切回左栏，否则 Refresh 会清掉新建蓝头
            ViewModel.NotifyPaneActivated(tab.LeftPane);

            if (_tabItems.TryGetValue(tab, out var item))
            {
                selected = item;
                BrowserTabs.SelectedItem = item;
                SetActiveLeftTabVisual(item);
                SetActiveRightTabVisual(null);
                BrowserTabs.UpdateLayout();
                NormalizeTabStrip(BrowserTabs, 44);
            }

            PerfLog.Write($"AddTab/headerReady: {sw.Elapsed.TotalMilliseconds:F0}ms");
            await WaitForNextRenderAsync();
            PerfLog.Write($"AddTab/firstPaint: {sw.Elapsed.TotalMilliseconds:F0}ms");
        }
        finally
        {
            _suppressTabBodyMaterialize = false;
        }

        if (tab is null || selected is null)
        {
            return;
        }

        var captured = selected;
        var capturedTab = tab;
        var seedPane = seedFrom;
        _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, async () =>
        {
            using var _ = PerfLog.Measure("AddTab.InitAndBody");
            try
            {
                if (seedPane is not null && await capturedTab.LeftPane.TrySeedFromAsync(seedPane))
                {
                    PerfLog.Write($"AddTab/seededFromCache: {sw.Elapsed.TotalMilliseconds:F0}ms");
                }
                else
                {
                    await capturedTab.InitializeAsync();
                    PerfLog.Write($"AddTab/initDone: {sw.Elapsed.TotalMilliseconds:F0}ms");
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write("AddTab.InitializeAsync", ex);
            }

            if (!IsSpecialTab(captured) && ReferenceEquals(BrowserTabs.SelectedItem, captured))
            {
                EnsureBrowserTabView(captured);
                ShowActiveTabContent();
                // 物化后可能触发 Refresh：再次钉死新建 tab 为唯一蓝头
                SetActiveLeftTabVisual(captured);
                SetActiveRightTabVisual(null);
                ScheduleNavigationSync();
            }

            PerfLog.Write($"AddTab/bodyVisible: {sw.Elapsed.TotalMilliseconds:F0}ms");
        });
    }

    /// <summary>等到下一帧 Composition 渲染回调（比 Dispatcher Low 更能反映“看见了”）。</summary>
    private static Task WaitForNextRenderAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRendering(object? sender, object args)
        {
            CompositionTarget.Rendering -= OnRendering;
            tcs.TrySetResult();
        }

        CompositionTarget.Rendering += OnRendering;
        return tcs.Task;
    }

    /// <summary>等到布局+低优先级队列空一轮，确保标签头已绘制。</summary>
    private static async Task WaitForUiPaintAsync()
    {
        var dq = App.DispatcherQueue;
        if (dq is null)
        {
            await Task.Yield();
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
            {
                if (!dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => tcs.TrySetResult()))
                {
                    tcs.TrySetResult();
                }
            }))
        {
            tcs.TrySetResult();
        }

        await tcs.Task;
    }

    private void OnOpenHomeClick(object sender, RoutedEventArgs e)
    {
        if (_homeTabItem is null)
        {
            var homeView = new HomeView();
            homeView.FolderActivated += ActivateOrOpenPathAsync;
            var homeHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            homeHeader.Children.Add(new FontIcon { Glyph = "\uE80F", FontSize = 12, Foreground = IconBrushes.Home, VerticalAlignment = VerticalAlignment.Center });
            homeHeader.Children.Add(new TextBlock { Text = "主页", VerticalAlignment = VerticalAlignment.Center });
            _homeTabItem = new TabViewItem
            {
                Header = homeHeader,
                Content = new Border { Width = 0, Height = 0, Visibility = Visibility.Collapsed },
                Tag = homeView,
                CanDrag = false,
                IsClosable = true,
            };
            BrowserTabs.TabItems.Add(_homeTabItem);
        }

        if (GetTabBody(_homeTabItem) is HomeView view)
        {
            view.Refresh(GetFavoriteFolderLeaves());
        }

        BrowserTabs.SelectedItem = _homeTabItem;
        ShowActiveTabContent();
    }

    /// <summary>
    /// 主页卡片优先激活已有标签；仅当绝对路径完全一致时复用（J:\lx ≠ J:\）。
    /// </summary>
    private async void ActivateOrOpenPathAsync(string path) =>
        await ViewModel.OpenFolderInTabAsync(path);

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(
            left.TrimEnd('\\', '/'),
            right.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<(string Name, string Path)> GetFavoriteFolderLeaves()
    {
        if (ViewModel.FavoriteRoot is null)
        {
            yield break;
        }

        foreach (var leaf in ViewModel.FavoriteRoot.GetFolderLeaves())
        {
            if (!string.IsNullOrWhiteSpace(leaf.Path))
            {
                yield return (leaf.DisplayName, leaf.Path);
            }
        }
    }

    private void OnSplitLeftRightClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SetShellSplit(DualPaneOrientation.Horizontal);
        UpdateSplitTabStripLayout();
    }

    private void OnSplitTopBottomClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SetShellSplit(DualPaneOrientation.Vertical);
        UpdateSplitTabStripLayout();
    }

    private void OnAddWorkspaceSlotClick(object sender, RoutedEventArgs e)
    {
        ViewModel.AddWorkspaceSlot();
        UpdateSplitTabStripLayout();
    }

    private void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab is not TabViewItem item)
        {
            return;
        }

        if (GetTabBody(item) is SearchResultsView)
        {
            ViewModel.CloseSearchCommand.Execute(null);
            return;
        }

        if (GetTabBody(item) is HomeView)
        {
            BrowserTabs.TabItems.Remove(item);
            _homeTabItem = null;
            ShowActiveTabContent();
            return;
        }

        if (TryGetBrowserTabVm(item) is { IsLocked: true })
        {
            return;
        }

        if (TryGetBrowserTabVm(item) is { } tabVm)
        {
            ViewModel.CloseTabCommand.Execute(tabVm);
            TryCloseSecondaryWindowIfEmpty();
        }
    }

    private void OnTabDragStarting(TabView sender, TabViewTabDragStartingEventArgs args)
    {
        if (IsSpecialTab(args.Tab) ||
            TryGetBrowserTabVm(args.Tab) is not { IsLocked: false })
        {
            args.Cancel = true;
            return;
        }

        _tabDragSession++;
        _tabDragMerged = false;
        _pendingOutsideTab = null;
        _lastLiveReorderIndex = -1;
        _isTabDragging = true;
        NativeWindowHelper.TryGetCursorPos(out _tabDragStartCursor);
        _dragSourcePage = new WeakReference<MainPage>(this);
        _draggedTabItem = new WeakReference<TabViewItem>(args.Tab);
        args.Data.Properties.Add(TabDragMarker, true);
        args.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void OnTabDragCompleted(TabView sender, TabViewTabDragCompletedEventArgs args)
    {
        var tab = args.Tab;
        var session = _tabDragSession;
        _pendingOutsideTab = null;

        // 不依赖 TabDroppedOutside（落到内容区时常被吃掉）；松手时若已垂直离开条则撕裂
        var shouldTear = !_tabDragMerged &&
                         tab is not null &&
                         !IsSpecialTab(tab) &&
                         ShouldTearOffVertically();

        if (shouldTear && tab is not null)
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                if (session != _tabDragSession || _tabDragMerged)
                {
                    return;
                }

                if (!BrowserTabs.TabItems.Contains(tab))
                {
                    return;
                }

                TearOffTabToNewWindow(tab);
            });
        }
        else
        {
            SyncTabOrderFromUi();
        }

        FinishTabDragUi();
        ClearTabDragState();
    }

    private void OnTabDroppedOutside(TabView sender, TabViewTabDroppedOutsideEventArgs args)
    {
        if (IsSpecialTab(args.Tab) ||
            TryGetBrowserTabVm(args.Tab) is not { IsLocked: false })
        {
            FinishTabDragUi();
            ClearTabDragState();
            return;
        }

        // 标记条外松手；真正撕裂在 TabDragCompleted 统一判定（避免与合并竞态）
        _pendingOutsideTab = args.Tab;
    }

    private void OnTabStripDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Properties.ContainsKey(TabDragMarker))
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        e.Handled = true;
        HandleTabDragOver(e);
    }

    private void OnTabStripDrop(object sender, DragEventArgs e) => HandleTabDrop(e);

    private void HandleTabDragOver(DragEventArgs e)
    {
        MainPage? source = null;
        _dragSourcePage?.TryGetTarget(out source);
        var crossWindow = source is not null && !ReferenceEquals(source, this);
        var x = e.GetPosition(BrowserTabs).X;

        // AllowDropTabs=False 后仍可能残留空壳；拖过时清掉
        PruneGhostBrowserTabs();
        UpdateTabInsertCaret(x);

        try
        {
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
            e.DragUIOverride.Caption = crossWindow ? "合并到此窗口" : "调整标签顺序";
        }
        catch
        {
        }

        // 同窗换序交给 CanReorderTabs；自定义 Remove/Insert 会造成空白占位约 1s
        _ = crossWindow;
    }

    private void UpdateTabInsertCaret(double xInTabs)
    {
        var insert = GetBusinessInsertRawIndex(xInTabs);
        double caretX;
        if (insert <= 0)
        {
            caretX = 4;
        }
        else if (insert >= BrowserTabs.TabItems.Count)
        {
            // 最后一个业务 tab 右侧
            caretX = 4;
            for (var i = BrowserTabs.TabItems.Count - 1; i >= 0; i--)
            {
                if (BrowserTabs.TabItems[i] is not TabViewItem item || IsSpecialTab(item))
                {
                    continue;
                }

                var b = item.TransformToVisual(BrowserTabs)
                    .TransformBounds(new Rect(0, 0, Math.Max(1, item.ActualWidth), 1));
                caretX = b.X + b.Width;
                break;
            }
        }
        else if (BrowserTabs.TabItems[insert] is TabViewItem at)
        {
            var b = at.TransformToVisual(BrowserTabs)
                .TransformBounds(new Rect(0, 0, Math.Max(1, at.ActualWidth), 1));
            caretX = b.X;
        }
        else
        {
            caretX = xInTabs;
        }

        TabInsertCaret.Margin = new Thickness(Math.Max(0, caretX - 1), 8, 0, 0);
        TabInsertCaret.Visibility = Visibility.Visible;
    }

    private void HideTabInsertCaret()
    {
        TabInsertCaret.Visibility = Visibility.Collapsed;
    }

    private void OnContentTabDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Properties.ContainsKey(TabDragMarker))
        {
            return;
        }

        // 落在标签条带内：交给 TabStrip / TabItem 处理（换序/合并）
        var y = e.GetPosition(BrowserTabs).Y;
        if (y is >= -8 and <= 52)
        {
            return;
        }

        MainPage? source = null;
        _dragSourcePage?.TryGetTarget(out source);
        if (source is not null && !ReferenceEquals(source, this))
        {
            // 其它窗口内容区：提示拖到标签条合并，勿显示 stop
            e.AcceptedOperation = DataPackageOperation.Move;
            try
            {
                e.DragUIOverride.Caption = "拖到标签条以合并";
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.IsGlyphVisible = true;
            }
            catch
            {
            }

            e.Handled = true;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        try
        {
            e.DragUIOverride.Caption = "在新窗口打开";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }
        catch
        {
        }

        e.Handled = true;
    }

    private void OnContentTabDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Properties.ContainsKey(TabDragMarker))
        {
            return;
        }

        // 真正撕裂在 TabDragCompleted；这里只消费事件，避免 stop / 误粘贴
        e.AcceptedOperation = DataPackageOperation.Move;
        e.Handled = true;
    }

    private void HandleTabDrop(DragEventArgs e)
    {
        if (!e.DataView.Properties.ContainsKey(TabDragMarker) ||
            _draggedTabItem is null ||
            !_draggedTabItem.TryGetTarget(out var item) ||
            IsSpecialTab(item))
        {
            FinishTabDragUi();
            ClearTabDragState();
            return;
        }

        MainPage? source = null;
        _dragSourcePage?.TryGetTarget(out source);
        var insertIndex = GetBusinessInsertRawIndex(e.GetPosition(BrowserTabs).X);

        try
        {
            if (source is not null && !ReferenceEquals(source, this))
            {
                _tabDragMerged = true;
                _pendingOutsideTab = null;
                _tabDragSession++; // 取消源窗口挂起的撕裂
                source.ReleaseTornTab(item);
                AcceptTornTab(item, insertIndex);
                source.FinishTabDragUi();
                source.TryCloseSecondaryWindowIfEmpty();
                PruneGhostBrowserTabs();
                RefreshActivePaneChrome();
            }
            else
            {
                // 同窗：WinUI CanReorderTabs 已排好序，只写回 VM
                SyncTabOrderFromUi();
            }

            e.Handled = true;
        }
        finally
        {
            FinishTabDragUi();
            ClearTabDragState();
        }
    }

    private void TearOffTabToNewWindow(TabViewItem tab)
    {
        if (IsSpecialTab(tab) ||
            TryGetBrowserTabVm(tab) is not { IsLocked: false })
        {
            return;
        }

        // 光标仍在任意窗口标签条上 → 合并/重排意图，不新建
        if (IsCursorOverAnyTabStrip())
        {
            SyncTabOrderFromUi();
            return;
        }

        ReleaseTornTab(tab);
        // 旧窗口默认激活剩余第一个 tab，并清掉残留多蓝底
        ActivateFirstBusinessTab();

        var window = new MainWindow(secondary: true);
        App.TrackWindow(window);
        window.Activate();

        void AttachWhenReady(object? s, RoutedEventArgs e)
        {
            if (window.Page is not MainPage page)
            {
                return;
            }

            page.Loaded -= AttachWhenReady;
            page.AcceptTornTab(tab);
        }

        if (window.Page is MainPage { IsLoaded: true } ready)
        {
            ready.AcceptTornTab(tab);
        }
        else if (window.Page is MainPage pending)
        {
            pending.Loaded += AttachWhenReady;
        }

        TryCloseSecondaryWindowIfEmpty();
    }

    /// <summary>
    /// 垂直离开标签条 → 新窗口；仍在条上（含其它窗口条）→ 重排/合并。
    /// 不要求 dy≫dx，避免轻微斜向拖就无法撕裂。
    /// </summary>
    private bool ShouldTearOffVertically()
    {
        if (!NativeWindowHelper.TryGetCursorPos(out var now))
        {
            return false;
        }

        // 仍在任一标签条上：绝不撕裂
        if (IsCursorOverAnyTabStrip())
        {
            return false;
        }

        var dy = now.Y - _tabDragStartCursor.Y;
        if (Math.Abs(dy) >= TabTearMinDyPx)
        {
            return true;
        }

        // 起点在条上、光标已离开本窗条一定距离（即使总位移略小）
        return IsCursorVerticallyAwayFromSourceStrip(now, TabTearOutsideStripPx);
    }

    private static bool IsCursorOverAnyTabStrip()
    {
        if (!NativeWindowHelper.TryGetCursorPos(out var pt))
        {
            return false;
        }

        foreach (var window in App.ActiveWindows)
        {
            if (window is MainWindow { Page: MainPage page } && page.HitTestTabStripScreen(pt, padY: 6))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsCursorVerticallyAwayFromSourceStrip(NativeWindowHelper.POINT screenPt, int minAwayPx)
    {
        if (!TryGetTabStripScreenBounds(out var left, out var top, out var right, out var bottom))
        {
            return false;
        }

        _ = left;
        _ = right;
        return screenPt.Y < top - minAwayPx || screenPt.Y > bottom + minAwayPx;
    }

    private bool HitTestTabStripScreen(NativeWindowHelper.POINT screenPt, int padY = 6)
    {
        if (!TryGetTabStripScreenBounds(out var left, out var top, out var right, out var bottom))
        {
            return false;
        }

        return screenPt.X >= left && screenPt.X < right &&
               screenPt.Y >= top - padY && screenPt.Y < bottom + padY;
    }

    /// <summary>标签条屏幕矩形；高度钳在约 44px，避免 TabView ActualHeight 异常时整窗被当成条。</summary>
    private bool TryGetTabStripScreenBounds(out int left, out int top, out int right, out int bottom)
    {
        left = top = right = bottom = 0;
        try
        {
            var window = FindHostWindow();
            if (window is null || BrowserTabs.XamlRoot is null)
            {
                return false;
            }

            var hwnd = WindowNative.GetWindowHandle(window);
            var scale = BrowserTabs.XamlRoot.RasterizationScale;
            // 强制用标签行高度，勿用可能未折叠的 TabView 内容区高度
            var heightDip = 44 /* Primary tab row */;
            if (heightDip < 24 || heightDip > 64)
            {
                heightDip = 44;
            }

            var widthDip = BrowserTabs.ActualWidth;
            if (widthDip < 8)
            {
                widthDip = ActualWidth;
            }

            var t = BrowserTabs.TransformToVisual(null);
            var tl = t.TransformPoint(new Point(0, 0));
            var br = t.TransformPoint(new Point(widthDip, heightDip));
            var p1 = new NativeWindowHelper.POINT
            {
                X = (int)Math.Round(tl.X * scale),
                Y = (int)Math.Round(tl.Y * scale),
            };
            var p2 = new NativeWindowHelper.POINT
            {
                X = (int)Math.Round(br.X * scale),
                Y = (int)Math.Round(br.Y * scale),
            };
            NativeWindowHelper.ClientToScreen(hwnd, ref p1);
            NativeWindowHelper.ClientToScreen(hwnd, ref p2);

            left = Math.Min(p1.X, p2.X);
            right = Math.Max(p1.X, p2.X);
            top = Math.Min(p1.Y, p2.Y);
            bottom = Math.Max(p1.Y, p2.Y);
            return right > left && bottom > top;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Chrome 式：光标越过目标标签宽度 50% 即插入到该侧。</summary>
    private int GetBusinessInsertRawIndex(double xInTabs)
    {
        var specialStart = BrowserTabs.TabItems.Count;
        for (var i = 0; i < BrowserTabs.TabItems.Count; i++)
        {
            if (BrowserTabs.TabItems[i] is TabViewItem ti && IsSpecialTab(ti))
            {
                specialStart = i;
                break;
            }
        }

        for (var i = 0; i < specialStart; i++)
        {
            if (BrowserTabs.TabItems[i] is not TabViewItem item)
            {
                continue;
            }

            var bounds = item.TransformToVisual(BrowserTabs)
                .TransformBounds(new Rect(0, 0, Math.Max(1, item.ActualWidth), Math.Max(1, item.ActualHeight)));
            if (xInTabs < bounds.X + bounds.Width * 0.5)
            {
                return i;
            }
        }

        return specialStart;
    }

    private void TryLiveReorderTabs(TabViewItem dragged, double xInTabs)
    {
        if (_liveReorderBusy || IsSpecialTab(dragged))
        {
            return;
        }

        var desiredInsert = GetBusinessInsertRawIndex(xInTabs);
        var current = IndexOfRawTabItem(dragged);
        if (current < 0)
        {
            return;
        }

        var newIndex = desiredInsert > current ? desiredInsert - 1 : desiredInsert;
        if (newIndex == current || newIndex == _lastLiveReorderIndex)
        {
            return;
        }

        // 不允许拖进特殊 Tab（主页/搜索）之后
        var maxBusiness = 0;
        for (var i = 0; i < BrowserTabs.TabItems.Count; i++)
        {
            if (BrowserTabs.TabItems[i] is TabViewItem ti && !IsSpecialTab(ti))
            {
                maxBusiness = i;
            }
        }

        newIndex = Math.Clamp(newIndex, 0, maxBusiness);

        _liveReorderBusy = true;
        _suppressTabSync = true;
        try
        {
            var selected = BrowserTabs.SelectedItem;
            BrowserTabs.TabItems.RemoveAt(current);
            if (newIndex > BrowserTabs.TabItems.Count)
            {
                newIndex = BrowserTabs.TabItems.Count;
            }

            BrowserTabs.TabItems.Insert(newIndex, dragged);
            _lastLiveReorderIndex = newIndex;
            if (selected is not null)
            {
                BrowserTabs.SelectedItem = selected;
            }
        }
        finally
        {
            _suppressTabSync = false;
            _liveReorderBusy = false;
        }
    }

    private void FinishTabDragUi()
    {
        var wasDragging = _isTabDragging;
        _isTabDragging = false;
        _lastLiveReorderIndex = -1;
        HideTabInsertCaret();
        // 拖拽中跳过了蓝头刷新，松手后立即补上（否则会灰占位约 1s）
        if (wasDragging)
        {
            PruneGhostBrowserTabs();
            if (BrowserTabs.SelectedItem is TabViewItem selected && !IsSpecialTab(selected))
            {
                SetActiveLeftTabVisual(selected);
            }
            else
            {
                RefreshActivePaneChrome();
            }
        }
    }

    private static void ClearTabDragState()
    {
        _dragSourcePage = null;
        _draggedTabItem = null;
        _pendingOutsideTab = null;
    }

    /// <summary>把撕裂出的标签并入本页（不重建 ViewModel）。</summary>
    public void AcceptTornTab(TabViewItem item, int insertIndex = -1)
    {
        var tab = TryGetBrowserTabVm(item);
        if (tab is null)
        {
            return;
        }

        // 合并过来的项通常已带 BrowserTabView；若只有 VM 则保持懒创建
        _ = EnsureBrowserTabView(item);

        _suppressTabSync = true;
        try
        {
            // 防止仍挂在源窗口 TabItems / ContentHost 上导致 Insert / Border.Child 崩溃
            DetachTabItemFromAllWindows(item);
            PruneGhostBrowserTabs();

            _tabItems[tab] = item;
            var businessIndex = insertIndex < 0
                ? ViewModel.Tabs.Count
                : CountBusinessTabsBeforeUiIndex(insertIndex);
            ViewModel.AttachTab(tab, businessIndex);

            if (!BrowserTabs.TabItems.Contains(item))
            {
                var index = insertIndex < 0
                    ? BrowserTabs.TabItems.Count
                    : Math.Clamp(insertIndex, 0, BrowserTabs.TabItems.Count);
                try
                {
                    if (index >= BrowserTabs.TabItems.Count)
                    {
                        BrowserTabs.TabItems.Add(item);
                    }
                    else
                    {
                        BrowserTabs.TabItems.Insert(index, item);
                    }
                }
                catch (ArgumentException)
                {
                    // Insert 偶发 E_INVALIDARG：回退到末尾 Add
                    if (!BrowserTabs.TabItems.Contains(item))
                    {
                        BrowserTabs.TabItems.Add(item);
                    }
                }
            }

            // 重新挂到本页事件
            item.DragOver -= OnTabItemDragOver;
            item.Drop -= OnTabItemDrop;
            item.DragOver += OnTabItemDragOver;
            item.Drop += OnTabItemDrop;
            item.IsClosable = false;
            if (item.Header is not Border { Tag: "TabHeaderRoot" })
            {
                item.Header = CreateTabHeader(tab);
            }

            BrowserTabs.SelectedItem = item;
            PruneGhostBrowserTabs();
            SetActiveLeftTabVisual(item);
            ShowActiveTabContent();
        }
        finally
        {
            _suppressTabSync = false;
        }
    }

    /// <summary>清掉合并/重排时 WinUI 可能留下的空壳 TabViewItem。</summary>
    private void PruneGhostBrowserTabs()
    {
        for (var i = BrowserTabs.TabItems.Count - 1; i >= 0; i--)
        {
            if (BrowserTabs.TabItems[i] is not TabViewItem item || IsSpecialTab(item))
            {
                continue;
            }

            if (TryGetBrowserTabVm(item) is { } tab &&
                _tabItems.TryGetValue(tab, out var mapped) &&
                ReferenceEquals(mapped, item))
            {
                continue;
            }

            BrowserTabs.TabItems.RemoveAt(i);
        }
    }

    /// <summary>从本页摘除标签（不 Dispose，供撕裂/合并）。</summary>
    public void ReleaseTornTab(TabViewItem item)
    {
        item.DragOver -= OnTabItemDragOver;
        item.Drop -= OnTabItemDrop;

        if (TryGetBrowserTabVm(item) is not { } tab)
        {
            BrowserTabs.TabItems.Remove(item);
            return;
        }

        _suppressTabSync = true;
        try
        {
            // 摘除前去掉蓝底，避免合并回其它窗口后残留多蓝 active
            SetTabItemActiveVisual(item, false);
            if (ReferenceEquals(_chromeActiveLeft, item))
            {
                _chromeActiveLeft = null;
            }

            // 先从内容宿主摘掉，否则目标窗挂入会因双重 Parent 崩溃
            if (item.Tag is FrameworkElement body)
            {
                RemoveTabBodyFromHost(body);
            }
            else if (GetTabBody(item) is FrameworkElement hosted)
            {
                RemoveTabBodyFromHost(hosted);
            }

            _tabItems.Remove(tab);
            if (BrowserTabs.TabItems.Contains(item))
            {
                BrowserTabs.TabItems.Remove(item);
            }

            ViewModel.DetachTab(tab);
            PruneGhostBrowserTabs();
        }
        finally
        {
            _suppressTabSync = false;
        }
    }

    private int CountBusinessTabsBeforeUiIndex(int uiIndex)
    {
        if (uiIndex < 0)
        {
            return ViewModel.Tabs.Count;
        }

        var count = 0;
        var limit = Math.Min(uiIndex, BrowserTabs.TabItems.Count);
        for (var i = 0; i < limit; i++)
        {
            if (BrowserTabs.TabItems[i] is TabViewItem item && !IsSpecialTab(item))
            {
                count++;
            }
        }

        return count;
    }

    private void TryCloseSecondaryWindowIfEmpty()
    {
        if (!_isSecondary || ViewModel.Tabs.Count > 0)
        {
            return;
        }

        var window = FindHostWindow();
        window?.Close();
    }

    private Window? FindHostWindow()
    {
        foreach (var window in App.ActiveWindows)
        {
            if (window is MainWindow main && ReferenceEquals(main.Page, this))
            {
                return window;
            }
        }

        return null;
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        Frame?.Navigate(typeof(SettingsPage));
    }

    private async void OnBookmarkCurrentClick(object sender, RoutedEventArgs e) =>
        await ShowBookmarkCurrentFolderDialogAsync();

    private async void OnBookmarkAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await ShowBookmarkCurrentFolderDialogAsync();
    }

    private void OnEditPathAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.SelectedTab?.LeftPane.BeginEditPathCommand.Execute(null);
    }

    private async void OnGlobalCopyAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ShouldDeferClipboardToFocusedEditor())
        {
            return;
        }

        args.Handled = true;
        var pane = GetActiveFilePaneControl();
        if (pane is null)
        {
            ShowActionToast("当前没有可复制的文件窗格");
            return;
        }

        await pane.CopyOrCutSelectionFromUiAsync(cut: false);
    }

    private async void OnGlobalCutAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ShouldDeferClipboardToFocusedEditor())
        {
            return;
        }

        args.Handled = true;
        var pane = GetActiveFilePaneControl();
        if (pane is null)
        {
            ShowActionToast("当前没有可剪切的文件窗格");
            return;
        }

        await pane.CopyOrCutSelectionFromUiAsync(cut: true);
    }

    private async void OnGlobalPasteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ShouldDeferClipboardToFocusedEditor())
        {
            return;
        }

        args.Handled = true;
        var pane = GetActiveFilePaneControl();
        if (pane is null)
        {
            ShowActionToast("当前没有可粘贴的文件窗格");
            return;
        }

        await pane.PasteFromUiAsync();
    }

    /// <summary>地址栏 / 终端等文本输入中时，不拦截系统剪贴板快捷键。</summary>
    private bool ShouldDeferClipboardToFocusedEditor()
    {
        try
        {
            var focused = FocusManager.GetFocusedElement(XamlRoot);
            if (focused is TextBox or RichEditBox or PasswordBox)
            {
                return true;
            }

            if (focused is DependencyObject node)
            {
                for (var p = node; p is not null; p = VisualTreeHelper.GetParent(p))
                {
                    if (p is TerminalDockHost or WebView2)
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private FilePaneControl? GetActiveFilePaneControl()
    {
        if (ViewModel.IsDualPane)
        {
            // 按焦点所在侧纠正激活栏，避免点空白后 ActivePaneSide 仍停在另一侧
            if (IsElementUnderFocus(RightShellContentHost) || IsElementUnderFocus(RightShellPane))
            {
                if (ViewModel.ShellRightGroup.ActivePane is { } right)
                {
                    ViewModel.NotifyPaneActivated(right);
                }

                return RightShellPane;
            }

            if (IsElementUnderFocus(TabContentHost))
            {
                if (ViewModel.SelectedTab?.LeftPane is { } left)
                {
                    ViewModel.NotifyPaneActivated(left);
                }

                return GetActiveBrowserTabView()?.LeftFilePane;
            }
        }

        if (ViewModel.IsDualPane && ViewModel.ActivePaneSide == PaneSide.Right)
        {
            return RightShellPane;
        }

        return GetActiveBrowserTabView()?.LeftFilePane;
    }

    private bool IsElementUnderFocus(DependencyObject? root)
    {
        if (root is null)
        {
            return false;
        }

        try
        {
            var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            while (focused is not null)
            {
                if (ReferenceEquals(focused, root))
                {
                    return true;
                }

                focused = VisualTreeHelper.GetParent(focused);
            }
        }
        catch
        {
        }

        return false;
    }

    private int _toastSerial;
    private DispatcherTimer? _toastHideTimer;

    /// <summary>界面下半部居中提示，约 1s 后缓缓淡出。</summary>
    public void ShowActionToast(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        ActionToastText.Text = message;
        var serial = ++_toastSerial;
        _toastHideTimer?.Stop();

        var show = new DoubleAnimation
        {
            To = 1,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(show, ActionToast);
        Storyboard.SetTargetProperty(show, "Opacity");
        var showBoard = new Storyboard();
        showBoard.Children.Add(show);
        showBoard.Begin();

        _toastHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _toastHideTimer.Tick += (_, _) =>
        {
            _toastHideTimer.Stop();
            if (serial != _toastSerial)
            {
                return;
            }

            var fade = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(420),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };
            Storyboard.SetTarget(fade, ActionToast);
            Storyboard.SetTargetProperty(fade, "Opacity");
            var fadeBoard = new Storyboard();
            fadeBoard.Children.Add(fade);
            fadeBoard.Begin();
        };
        _toastHideTimer.Start();
    }

    private async void OnUndoDeleteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        var tab = ViewModel.SelectedTab;
        if (tab is null || !tab.LeftPane.CanUndoDelete)
        {
            return;
        }

        try
        {
            await tab.LeftPane.UndoLastDeleteAsync();
            ShowActionToast("已撤销删除");
            if (ViewModel.IsDualPane)
            {
                await ViewModel.ShellRightGroup.ActivePane!.LoadAsync();
            }
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                Title = "撤销删除失败",
                Content = ex.Message,
                CloseButtonText = "关闭",
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();
        }
    }

    private async void OnAddFavoriteGroupClick(object sender, RoutedEventArgs e)
    {
        await CreateFavoriteGroupWithPromptAsync(ViewModel.FavoriteRoot);
    }

    /// <summary>弹窗自定义名称后创建子分组。</summary>
    private async Task CreateFavoriteGroupWithPromptAsync(FavoriteNodeViewModel? parent)
    {
        var name = await ShowNameInputDialogAsync("新建分组", "分组名称", "新建分组");
        if (name is null)
        {
            return;
        }

        await ViewModel.CreateFavoriteGroupAsync(parent, name);
    }

    /// <summary>通用单行文本输入弹窗；取消返回 null。</summary>
    private async Task<string?> ShowNameInputDialogAsync(string title, string header, string defaultValue)
    {
        var input = new TextBox
        {
            Header = header,
            Text = defaultValue,
            SelectionStart = 0,
            SelectionLength = defaultValue.Length,
        };

        var dialog = new ContentDialog
        {
            Title = title,
            Content = input,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(input.Text) ? defaultValue : input.Text.Trim();
    }

    private void OnRenameDialogRequested(FavoriteNodeViewModel node)
    {
        _ = ShowRenameDialogAsync(node);
    }

    private async Task ShowRenameDialogAsync(FavoriteNodeViewModel node)
    {
        var input = new TextBox
        {
            Text = node.DisplayName,
            PlaceholderText = "显示名称",
        };

        var dialog = new ContentDialog
        {
            Title = node.IsGroup ? "重命名分组" : "重命名收藏",
            Content = input,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.ApplyFavoriteRenameAsync(node, input.Text);
            ViewModel.RebuildFavoriteBar();
        }
    }

    private async Task ShowBookmarkCurrentFolderDialogAsync()
    {
        var path = ViewModel.ActiveFilePane?.CurrentPath
                   ?? ViewModel.SelectedTab?.LeftPane.CurrentPath;
        if (string.IsNullOrWhiteSpace(path) || ViewModel.FavoriteRoot is null)
        {
            return;
        }

        string normalized;
        try
        {
            normalized = System.IO.Path.GetFullPath(path);
        }
        catch
        {
            return;
        }

        var existing = ViewModel.FavoriteRoot.Model.FindByPath(normalized);
        var defaultName = existing?.DisplayName
            ?? (System.IO.Path.GetFileName(normalized.TrimEnd('\\')) is { Length: > 0 } leaf ? leaf : normalized);

        var groups = ViewModel.GetFavoriteGroups().ToList();
        var nameBox = new TextBox
        {
            Header = "收藏名称",
            Text = defaultName,
            Margin = new Thickness(0, 0, 0, 12),
        };
        var pathBox = new TextBox
        {
            Header = "路径",
            Text = normalized,
            IsReadOnly = true,
            Margin = new Thickness(0, 0, 0, 12),
        };
        var groupBox = new ComboBox
        {
            Header = "收藏到",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = groups,
            DisplayMemberPath = nameof(FavoriteNodeViewModel.DisplayName),
            SelectedItem = groups.FirstOrDefault(g =>
                existing is not null &&
                ViewModel.FavoriteRoot.Model.FindParentOf(existing.Id)?.Id == g.Model.Id)
                ?? groups.FirstOrDefault(g => g == ViewModel.FavoriteRoot)
                ?? groups.FirstOrDefault(),
        };

        var newGroupBtn = new Button
        {
            Content = "新建组",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(12, 6, 12, 6),
        };
        newGroupBtn.Click += async (_, _) =>
        {
            var parent = groupBox.SelectedItem as FavoriteNodeViewModel ?? ViewModel.FavoriteRoot;
            // 不弹第二层对话框（WinUI 同时只能一个 ContentDialog），直接建组并选中
            var created = await ViewModel.CreateFavoriteGroupAsync(parent, "新建分组");
            if (created is null)
            {
                return;
            }

            groups = ViewModel.GetFavoriteGroups().ToList();
            groupBox.ItemsSource = groups;
            groupBox.SelectedItem = groups.FirstOrDefault(g =>
                string.Equals(g.Model.Id, created.Model.Id, StringComparison.Ordinal));
        };

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(nameBox);
        panel.Children.Add(pathBox);
        panel.Children.Add(groupBox);
        panel.Children.Add(newGroupBtn);

        var dialog = new ContentDialog
        {
            Title = existing is null ? "添加到收藏夹" : "编辑收藏",
            Content = panel,
            PrimaryButtonText = existing is null ? "收藏" : "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.BookmarkPathAsync(
            normalized,
            nameBox.Text,
            groupBox.SelectedItem as FavoriteNodeViewModel ?? ViewModel.FavoriteRoot);
    }

    private void OnFavoriteItemRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FavoriteNodeViewModel node } anchor)
        {
            return;
        }

        ViewModel.SelectedFavorite = node;
        var flyout = BuildFavoriteContextFlyout(node);
        flyout.ShowAt(anchor, e.GetPosition(anchor));
        e.Handled = true;
    }

    private MenuFlyout BuildFavoriteContextFlyout(FavoriteNodeViewModel node)
    {
        var flyout = new MenuFlyout();

        if (!node.IsGroup)
        {
            var open = new MenuFlyoutItem { Text = "打开" };
            open.Click += async (_, _) => await ViewModel.OpenFavoriteCommand.ExecuteAsync(node);
            flyout.Items.Add(open);
        }

        var rename = new MenuFlyoutItem { Text = "重命名" };
        rename.Click += async (_, _) => await ShowRenameDialogAsync(node);
        flyout.Items.Add(rename);

        var addGroup = new MenuFlyoutItem { Text = "新建子分组" };
        addGroup.Click += async (_, _) =>
        {
            var parent = node.IsGroup ? node : node.Parent ?? ViewModel.FavoriteRoot;
            await CreateFavoriteGroupWithPromptAsync(parent);
        };
        flyout.Items.Add(addGroup);

        if (node.IsGroup)
        {
            var openAll = new MenuFlyoutItem { Text = "打开此分组下全部文件夹" };
            openAll.Click += async (_, _) => await ViewModel.OpenAllInFavoriteCommand.ExecuteAsync(node);
            flyout.Items.Add(openAll);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        var remove = new MenuFlyoutItem { Text = "删除" };
        remove.Click += async (_, _) =>
        {
            await ViewModel.RemoveFavoriteCommand.ExecuteAsync(node);
            ViewModel.RebuildFavoriteBar();
        };
        flyout.Items.Add(remove);

        return flyout;
    }

    private const string FavoriteDragFormat = "aiexplorer/favorite-id";

    private void OnFavoriteItemDragStarting(UIElement sender, DragStartingEventArgs e)
    {
        // 收藏栏已改用 ListView.DragItemsStarting；保留以防其它入口
        if (sender is not FrameworkElement { Tag: FavoriteNodeViewModel node })
        {
            e.Cancel = true;
            return;
        }

        e.Data.SetText($"{FavoriteDragFormat}:{node.Model.Id}");
        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.AllowedOperations = DataPackageOperation.Move;
        _suppressFavoriteTapUntilUtc = DateTime.UtcNow.AddMilliseconds(500);
    }

    private void OnFavoriteItemDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.None;
        var target = ResolveFavoriteNodeFromElement(sender as DependencyObject);
        if (target is null)
        {
            e.Handled = true;
            return;
        }

        if (e.DataView.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            try
            {
                e.DragUIOverride.IsGlyphVisible = true;
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.Caption = target.IsGroup ? "移入分组" : "合并为新分组";
            }
            catch
            {
            }
        }
        else if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }

        e.Handled = true;
    }

    private static FavoriteNodeViewModel? ResolveFavoriteNodeFromElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { Tag: FavoriteNodeViewModel tagged })
            {
                return tagged;
            }

            if (source is FrameworkElement { DataContext: FavoriteNodeViewModel ctx })
            {
                return ctx;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private async void OnFavoriteItemDrop(object sender, DragEventArgs e)
    {
        var target = ResolveFavoriteNodeFromElement(sender as DependencyObject);
        if (target is null)
        {
            return;
        }

        _suppressFavoriteTapUntilUtc = DateTime.UtcNow.AddMilliseconds(500);

        if (e.DataView.Contains(StandardDataFormats.Text))
        {
            var text = await e.DataView.GetTextAsync();
            if (text.StartsWith(FavoriteDragFormat + ":", StringComparison.Ordinal) &&
                ViewModel.FavoriteRoot is not null)
            {
                var id = text[(FavoriteDragFormat.Length + 1)..];
                var source = FindFavoriteById(ViewModel.FavoriteRoot, id);
                if (source is not null && !ReferenceEquals(source, target) &&
                    !string.Equals(source.Model.Id, target.Model.Id, StringComparison.Ordinal))
                {
                    await ViewModel.DropFavoriteOntoAsync(source, target);
                    ShowActionToast(target.IsGroup ? "已移入分组" : "已合并为新分组");
                    e.Handled = true;
                    return;
                }
            }
        }

        var paths = await ExtractAnyPathsFromDataViewAsync(e.DataView);
        if (paths.Count == 0)
        {
            return;
        }

        var parent = target.IsGroup ? target : ViewModel.FavoriteRoot;
        foreach (var path in paths)
        {
            await ViewModel.BookmarkPathAsync(
                path,
                System.IO.Path.GetFileName(path.TrimEnd('\\')) ?? path,
                parent);
        }

        e.Handled = true;
    }

    private static FavoriteNodeViewModel? FindFavoriteById(FavoriteNodeViewModel root, string id)
    {
        if (string.Equals(root.Model.Id, id, StringComparison.Ordinal))
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            var found = FindFavoriteById(child, id);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private void OnFavoriteBarDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.None;

        if (e.DataView.Contains(StandardDataFormats.Text))
        {
            // 收藏互拖：落到空白处=移回根；落到项上由项级 DragOver 覆盖为 Move+提示
            e.AcceptedOperation = DataPackageOperation.Move;
        }
        else if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }

        e.Handled = true;
    }

    private async void OnFavoriteBarDrop(object sender, DragEventArgs e)
    {
        // 若落点在某个收藏项上，优先做嵌套/合并（容器 Drop 未触发时的兜底）
        if (sender is UIElement root &&
            FindFavoriteNodeAtDragPoint(root, e) is { } hitTarget &&
            e.DataView.Contains(StandardDataFormats.Text) &&
            ViewModel.FavoriteRoot is not null)
        {
            var text = await e.DataView.GetTextAsync();
            if (text.StartsWith(FavoriteDragFormat + ":", StringComparison.Ordinal))
            {
                var id = text[(FavoriteDragFormat.Length + 1)..];
                var source = FindFavoriteById(ViewModel.FavoriteRoot, id);
                if (source is not null &&
                    !string.Equals(source.Model.Id, hitTarget.Model.Id, StringComparison.Ordinal))
                {
                    await ViewModel.DropFavoriteOntoAsync(source, hitTarget);
                    ShowActionToast(hitTarget.IsGroup ? "已移入分组" : "已合并为新分组");
                    _suppressFavoriteTapUntilUtc = DateTime.UtcNow.AddMilliseconds(500);
                    e.Handled = true;
                    return;
                }
            }
        }

        if (e.DataView.Contains(StandardDataFormats.Text))
        {
            var text = await e.DataView.GetTextAsync();
            if (text.StartsWith(FavoriteDragFormat + ":", StringComparison.Ordinal) &&
                ViewModel.FavoriteRoot is not null)
            {
                var id = text[(FavoriteDragFormat.Length + 1)..];
                var source = FindFavoriteById(ViewModel.FavoriteRoot, id);
                if (source is not null)
                {
                    await ViewModel.MoveFavoriteToGroupAsync(source, ViewModel.FavoriteRoot);
                    ShowActionToast("已移回收藏栏根级");
                    e.Handled = true;
                    return;
                }
            }
        }

        var paths = await ExtractAnyPathsFromDataViewAsync(e.DataView);
        if (paths.Count == 0)
        {
            return;
        }

        await ViewModel.AddFavoriteFoldersFromPathsAsync(paths);
        e.Handled = true;
    }

    private FavoriteNodeViewModel? FindFavoriteNodeAtDragPoint(UIElement root, DragEventArgs e)
    {
        try
        {
            var pos = e.GetPosition(root);
            var windowPoint = root.TransformToVisual(null).TransformPoint(pos);
            foreach (var el in VisualTreeHelper.FindElementsInHostCoordinates(windowPoint, root))
            {
                var node = ResolveFavoriteNodeFromElement(el);
                if (node is not null)
                {
                    return node;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static async Task<IReadOnlyList<string>> ExtractAnyPathsFromDataViewAsync(DataPackageView dataView)
    {
        var paths = new List<string>();

        if (dataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await dataView.GetStorageItemsAsync();
            foreach (var item in items)
            {
                if (!string.IsNullOrWhiteSpace(item.Path))
                {
                    paths.Add(item.Path);
                }
            }
        }

        if (paths.Count == 0 && dataView.Contains(StandardDataFormats.Text))
        {
            var text = await dataView.GetTextAsync();
            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim().Trim('"');
                if (Directory.Exists(trimmed) || File.Exists(trimmed))
                {
                    paths.Add(trimmed);
                }
            }
        }

        return paths;
    }

    private static async Task<IReadOnlyList<string>> ExtractFolderPathsFromDataViewAsync(DataPackageView dataView)
    {
        var all = await ExtractAnyPathsFromDataViewAsync(dataView);
        return all.Where(Directory.Exists).ToList();
    }

    private string? GetActiveFolderPath()
    {
        var path = ViewModel.ActiveFilePane?.CurrentPath;
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            return path;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private void OnOpenCmdHereClick(object sender, RoutedEventArgs e)
    {
        var path = GetActiveFolderPath();
        if (path is null)
        {
            return;
        }

        try
        {
            // 外部真控制台：保留用户 AutoRun（conda_hook）；不用 ConPTY
            var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            Process.Start(new ProcessStartInfo
            {
                FileName = cmd,
                Arguments = "/k",
                WorkingDirectory = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _ = new ContentDialog
            {
                Title = "打开 CMD 失败",
                Content = ex.Message,
                CloseButtonText = "确定",
                XamlRoot = XamlRoot,
            }.ShowAsync();
        }
    }

    private void OnOpenPowerShellHereClick(object sender, RoutedEventArgs e)
    {
        var path = GetActiveFolderPath();
        if (path is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoExit",
                WorkingDirectory = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _ = new ContentDialog
            {
                Title = "打开 PowerShell 失败",
                Content = ex.Message,
                CloseButtonText = "确定",
                XamlRoot = XamlRoot,
            }.ShowAsync();
        }
    }

    private async void OnToggleTerminalClick(object sender, RoutedEventArgs e)
    {
        var path = GetActiveFolderPath();
        TerminalPaneHost.SetPreferredDirectory(path);

        if (TerminalDock.Visibility != Visibility.Visible)
        {
            TerminalDock.Visibility = Visibility.Visible;
            await TerminalPaneHost.OpenOrFocusAsync(path);
            return;
        }

        // 已展开：新目录 → 新建；同目录且已是当前标签 → 收起；同目录其它标签 → 聚焦
        var active = TerminalPaneHost.ActiveWorkingDirectory;
        var sameAsActive = active is not null &&
                           string.Equals(
                               active.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                               (path ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                               StringComparison.OrdinalIgnoreCase);

        if (sameAsActive)
        {
            TerminalDock.Visibility = Visibility.Collapsed;
            return;
        }

        await TerminalPaneHost.OpenOrFocusAsync(path);
    }

    private void OnTerminalCollapseRequested()
    {
        // 收起保留会话，方便再开时回到多标签
        TerminalDock.Visibility = Visibility.Collapsed;
    }
}
