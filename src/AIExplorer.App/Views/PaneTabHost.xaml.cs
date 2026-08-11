using System.Diagnostics;
using AIExplorer_App.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AIExplorer_App.Views;

/// <summary>
/// 标准分栏 Host：一份完整的文件 Tab 条 + 内容区。
/// 水平/垂直/第 N 栏都实例化本控件，不再走 ShellRight / RightGroupTabs 分叉。
/// </summary>
public sealed partial class PaneTabHost : UserControl
{
    private readonly Dictionary<FilePaneViewModel, FilePaneControl> _views = new();
    private PaneGroupViewModel? _group;
    private bool _syncingTabs;
    private bool _suppressUiSync;
    private bool _isFocusedSlot;
    private TabViewItem? _chromeActive;

    public PaneTabHost()
    {
        InitializeComponent();
        Tabs.Loaded += (_, _) =>
        {
            TabChromeHelper.NormalizeTabStrip(Tabs);
            TabChromeHelper.KillTabStripTransitions(Tabs);
        };
        Tabs.SizeChanged += (_, _) =>
        {
            TabChromeHelper.NormalizeTabStrip(Tabs);
            TabChromeHelper.KillTabStripTransitions(Tabs);
        };
        Tabs.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnTabsPointerPressed),
            handledEventsToo: true);
    }

    /// <summary>壳层 VM（必须用 MainPage 同一实例；DI 是 Transient，禁止 GetRequiredService 另取）。</summary>
    public MainPageViewModel? ShellViewModel { get; set; }

    public PaneGroupViewModel? Group
    {
        get => _group;
        set
        {
            if (ReferenceEquals(_group, value))
            {
                return;
            }

            UnwireGroup(_group);
            ClearViews();
            _group = value;
            WireGroup(_group);
            SyncTabs();
            SyncContent();
        }
    }

    public bool IsFocusedSlot
    {
        get => _isFocusedSlot;
        set
        {
            if (_isFocusedSlot == value)
            {
                // 值未变也可能残留蓝头（左栏激活捷径曾漏清），失焦时强制刷一次
                if (!value)
                {
                    ClearFocusVisuals();
                }

                return;
            }

            _isFocusedSlot = value;
            RefreshActiveVisuals();
        }
    }

    /// <summary>清除本栏全部 Tab 蓝头（不改 ActiveIndex）。</summary>
    public void ClearFocusVisuals()
    {
        foreach (var obj in Tabs.TabItems)
        {
            if (obj is TabViewItem item)
            {
                TabChromeHelper.SetActiveVisual(item, active: false);
            }
        }

        _chromeActive = null;
    }

    public event Action<FilePaneViewModel>? PaneActivated;
    public event Action<DualPaneOrientation>? RequestSetSplit;
    public event Action? RequestCloseAllSplits;
    public event Action? RequestAddSlot;
    public event Action<PaneGroupViewModel>? RequestRemoveThisSlot;

    public FilePaneViewModel? ActivePane => _group?.ActivePane;

    public void SyncFromGroup()
    {
        SyncTabs();
        SyncContent();
    }

    private void WireGroup(PaneGroupViewModel? group)
    {
        if (group is null)
        {
            return;
        }

        group.Panes.CollectionChanged += OnPanesCollectionChanged;
        group.PropertyChanged += OnGroupPropertyChanged;
        foreach (var pane in group.Panes)
        {
            pane.PropertyChanged += OnPanePropertyChanged;
        }
    }

    private void UnwireGroup(PaneGroupViewModel? group)
    {
        if (group is null)
        {
            return;
        }

        group.Panes.CollectionChanged -= OnPanesCollectionChanged;
        group.PropertyChanged -= OnGroupPropertyChanged;
        foreach (var pane in group.Panes)
        {
            pane.PropertyChanged -= OnPanePropertyChanged;
        }
    }

    private void OnPanesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (FilePaneViewModel pane in e.OldItems)
            {
                pane.PropertyChanged -= OnPanePropertyChanged;
                RemoveView(pane);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (FilePaneViewModel pane in e.NewItems)
            {
                pane.PropertyChanged += OnPanePropertyChanged;
            }
        }

        if (!_suppressUiSync)
        {
            SyncTabs();
            SyncContent();
        }
    }

    private void OnGroupPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_suppressUiSync)
        {
            return;
        }

        if (e.PropertyName is nameof(PaneGroupViewModel.ActiveIndex) or nameof(PaneGroupViewModel.ActivePane))
        {
            _syncingTabs = true;
            try
            {
                if (_group is not null && Tabs.TabItems.Count > 0)
                {
                    Tabs.SelectedIndex = Math.Clamp(_group.ActiveIndex, 0, Tabs.TabItems.Count - 1);
                }
            }
            finally
            {
                _syncingTabs = false;
            }

            SyncContent();
            RefreshActiveVisuals();
        }
    }

    private void OnPanePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FilePaneViewModel.CurrentPath) or nameof(FilePaneViewModel.EditablePath))
        {
            RefreshHeadersIncremental();
        }
    }

    private void SyncTabs()
    {
        if (_group is null)
        {
            Tabs.TabItems.Clear();
            return;
        }

        _syncingTabs = true;
        try
        {
            var group = _group;
            while (Tabs.TabItems.Count > group.Panes.Count)
            {
                Tabs.TabItems.RemoveAt(Tabs.TabItems.Count - 1);
            }

            var canClose = group.Panes.Count > 1;
            while (Tabs.TabItems.Count < group.Panes.Count)
            {
                var pane = group.Panes[Tabs.TabItems.Count];
                Tabs.TabItems.Add(CreateTabItem(pane, canClose));
            }

            for (var i = 0; i < Tabs.TabItems.Count && i < group.Panes.Count; i++)
            {
                if (Tabs.TabItems[i] is not TabViewItem item)
                {
                    continue;
                }

                var pane = group.Panes[i];
                item.Tag = pane;
                EnsureHeader(item, pane, canClose);
            }

            if (group.Panes.Count > 0)
            {
                Tabs.SelectedIndex = Math.Clamp(group.ActiveIndex, 0, group.Panes.Count - 1);
            }

            TabChromeHelper.NormalizeTabStrip(Tabs);
        }
        finally
        {
            _syncingTabs = false;
        }

        RefreshActiveVisuals();
    }

    private void RefreshHeadersIncremental()
    {
        if (_group is null)
        {
            return;
        }

        var canClose = _group.Panes.Count > 1;
        for (var i = 0; i < Tabs.TabItems.Count && i < _group.Panes.Count; i++)
        {
            if (Tabs.TabItems[i] is TabViewItem item)
            {
                EnsureHeader(item, _group.Panes[i], canClose);
            }
        }
    }

    private FrameworkElement BuildHeader(FilePaneViewModel pane, bool canClose) =>
        TabChromeHelper.CreateFilePaneHeader(
            PaneGroupViewModel.GetShortName(pane.CurrentPath),
            canClose,
            () => ClosePane(pane));

    private void EnsureHeader(TabViewItem item, FilePaneViewModel pane, bool canClose)
    {
        var want = PaneGroupViewModel.GetShortName(pane.CurrentPath);
        var title = TabChromeHelper.FindTitleText(item.Header)?.Text;
        var close = TabChromeHelper.FindCloseButton(item.Header);
        var closeVisible = close is { Opacity: > 0.5, IsHitTestVisible: true };
        if (item.Header is Border { Tag: "TabHeaderRoot" } &&
            string.Equals(title, want, StringComparison.Ordinal) &&
            closeVisible == canClose)
        {
            return;
        }

        item.Header = BuildHeader(pane, canClose);
    }

    private TabViewItem CreateTabItem(FilePaneViewModel pane, bool closable)
    {
        var item = new TabViewItem
        {
            Tag = pane,
            Header = BuildHeader(pane, closable),
            Content = new Border { Width = 0, Height = 0, Visibility = Visibility.Collapsed },
            ContextFlyout = null,
            IsClosable = false,
        };
        TabChromeHelper.SetActiveVisual(item, active: false);
        item.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnTabItemPointerPressed),
            handledEventsToo: true);
        return item;
    }

    private void OnTabItemPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not TabViewItem item)
        {
            return;
        }

        if (TabChromeHelper.IsUnderCloseButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        ActivateFromItem(item);

        if (!e.GetCurrentPoint(item).Properties.IsRightButtonPressed)
        {
            return;
        }

        if (item.Tag is not FilePaneViewModel pane)
        {
            return;
        }

        BuildContextFlyout(pane).ShowAt(item, e.GetCurrentPoint(item).Position);
        e.Handled = true;
    }

    private void OnTabsPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_group?.ActivePane is { } pane)
        {
            PaneActivated?.Invoke(pane);
        }
    }

    private void ActivateFromItem(TabViewItem item)
    {
        if (_group is null)
        {
            return;
        }

        var idx = Tabs.TabItems.IndexOf(item);
        if (idx < 0 || idx >= _group.Panes.Count)
        {
            return;
        }

        if (_group.ActiveIndex != idx)
        {
            _group.ActiveIndex = idx;
        }

        PaneActivated?.Invoke(_group.Panes[idx]);
        RefreshActiveVisuals();
        SyncContent();
    }

    private void RefreshActiveVisuals()
    {
        if (!_isFocusedSlot)
        {
            foreach (var obj in Tabs.TabItems)
            {
                if (obj is TabViewItem item)
                {
                    TabChromeHelper.SetActiveVisual(item, false);
                }
            }

            _chromeActive = null;
            return;
        }

        TabViewItem? active = null;
        if (Tabs.SelectedIndex >= 0 &&
            Tabs.SelectedIndex < Tabs.TabItems.Count &&
            Tabs.TabItems[Tabs.SelectedIndex] is TabViewItem selected)
        {
            active = selected;
        }

        if (!ReferenceEquals(_chromeActive, active))
        {
            if (_chromeActive is not null && Tabs.TabItems.Contains(_chromeActive))
            {
                TabChromeHelper.SetActiveVisual(_chromeActive, false);
            }

            if (active is not null)
            {
                TabChromeHelper.SetActiveVisual(active, true);
            }

            _chromeActive = active;
        }
        else if (active is not null)
        {
            TabChromeHelper.SetActiveVisual(active, true);
        }
    }

    private MenuFlyout BuildContextFlyout(FilePaneViewModel pane)
    {
        var flyout = new MenuFlyout();
        if (_group is null)
        {
            return flyout;
        }

        var group = _group;

        void Refresh()
        {
            SyncTabs();
            SyncContent();
        }

        var close = new MenuFlyoutItem { Text = "关闭" };
        close.Click += (_, _) =>
        {
            ClosePane(pane);
            Refresh();
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

            Refresh();
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

            Refresh();
        };
        flyout.Items.Add(closeRight);

        var closeOthers = new MenuFlyoutItem { Text = "关闭其他标签" };
        closeOthers.Click += (_, _) =>
        {
            foreach (var other in group.Panes.Where(p => !ReferenceEquals(p, pane)).ToList())
            {
                group.ClosePane(other);
            }

            Refresh();
        };
        flyout.Items.Add(closeOthers);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var splitH = new MenuFlyoutItem { Text = "左右分栏" };
        splitH.Click += (_, _) => RequestSetSplit?.Invoke(DualPaneOrientation.Horizontal);
        flyout.Items.Add(splitH);

        var splitV = new MenuFlyoutItem { Text = "上下分栏" };
        splitV.Click += (_, _) => RequestSetSplit?.Invoke(DualPaneOrientation.Vertical);
        flyout.Items.Add(splitV);

        var addSlot = new MenuFlyoutItem { Text = "再分一栏" };
        addSlot.Click += (_, _) => RequestAddSlot?.Invoke();
        flyout.Items.Add(addSlot);

        var closeSplit = new MenuFlyoutItem { Text = "关闭此栏" };
        closeSplit.Click += (_, _) => RequestRemoveThisSlot?.Invoke(group);
        flyout.Items.Add(closeSplit);

        var closeAll = new MenuFlyoutItem { Text = "关闭全部分栏" };
        closeAll.Click += (_, _) => RequestCloseAllSplits?.Invoke();
        flyout.Items.Add(closeAll);

        flyout.Opening += (_, _) =>
        {
            var canClose = group.Panes.Count > 1;
            close.IsEnabled = canClose;
            closeLeft.IsEnabled = canClose && group.Panes.IndexOf(pane) > 0;
            closeRight.IsEnabled = canClose && group.Panes.IndexOf(pane) < group.Panes.Count - 1;
            closeOthers.IsEnabled = canClose;
        };

        return flyout;
    }

    private async void OnAddTabClick(TabView sender, object args)
    {
        if (_group is null)
        {
            return;
        }

        // 与 MainPage.OnAddTabClick 同序：压制 ActiveIndex 副作用 → 蓝头 → 一帧 → Normal Seed/换绑
        var sw = Stopwatch.StartNew();
        PerfLog.Write("PaneHostAddTab/click");
        var group = _group;
        var seedFrom = group.ActivePane;
        var pageVm = ShellViewModel;
        if (pageVm is null)
        {
            return;
        }

        FilePaneViewModel pane;
        TabViewItem? newItem;
        _suppressUiSync = true;
        _syncingTabs = true;
        pageVm.SuppressShellRightSideEffects = true;
        try
        {
            pane = group.AddPane(loadContent: false);
            var canClose = group.Panes.Count > 1;
            if (canClose && Tabs.TabItems.Count == 1 && Tabs.TabItems[0] is TabViewItem only)
            {
                EnsureHeader(only, group.Panes[0], canClose: true);
            }

            newItem = CreateTabItem(pane, canClose);
            // 先上蓝再插入：避免 TabView 入场动画先画空白槽再填内容
            TabChromeHelper.SetActiveVisual(newItem, true);
            if (_chromeActive is not null && !ReferenceEquals(_chromeActive, newItem))
            {
                TabChromeHelper.SetActiveVisual(_chromeActive, false);
            }

            _isFocusedSlot = true;
            _chromeActive = newItem;

            TabChromeHelper.KillTabStripTransitions(Tabs);
            Tabs.TabItems.Add(newItem);
            Tabs.SelectedIndex = Tabs.TabItems.Count - 1;
            TabChromeHelper.NormalizeTabStrip(Tabs);
            TabChromeHelper.KillTabStripTransitions(Tabs);
            Tabs.UpdateLayout();

            // 热路径只上蓝；导航同步放到 deferred（与左侧 ScheduleNavigationSync 同效，避免 ActivePaneSide 连环 Refresh）
            PerfLog.Write($"PaneHostAddTab/headerReady: {sw.Elapsed.TotalMilliseconds:F0}ms");
            await WaitForNextRenderAsync();
            PerfLog.Write($"PaneHostAddTab/firstPaint: {sw.Elapsed.TotalMilliseconds:F0}ms");
        }
        finally
        {
            _syncingTabs = false;
            _suppressUiSync = false;
            pageVm.SuppressShellRightSideEffects = false;
        }

        var capturedPane = pane;
        var capturedSeed = seedFrom;
        var capturedItem = newItem;
        _ = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, async () =>
        {
            try
            {
                if (capturedSeed is not null && await capturedPane.TrySeedFromAsync(capturedSeed))
                {
                    PerfLog.Write($"PaneHostAddTab/seededFromCache: {sw.Elapsed.TotalMilliseconds:F0}ms");
                }
                else
                {
                    await capturedPane.LoadAsync();
                    PerfLog.Write($"PaneHostAddTab/initDone: {sw.Elapsed.TotalMilliseconds:F0}ms");
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write("PaneHostAddTab.Load", ex);
            }

            if (!ReferenceEquals(_group?.ActivePane, capturedPane))
            {
                return;
            }

            ShowPane(capturedPane, createIfMissing: true);
            if (capturedItem is not null && ReferenceEquals(Tabs.SelectedItem, capturedItem))
            {
                TabChromeHelper.SetActiveVisual(capturedItem, _isFocusedSlot);
            }

            PaneActivated?.Invoke(capturedPane);
            PerfLog.Write($"PaneHostAddTab/bodyVisible: {sw.Elapsed.TotalMilliseconds:F0}ms");
        });
    }

    private void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab.Tag is FilePaneViewModel pane)
        {
            ClosePane(pane);
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTabs || _group is null)
        {
            return;
        }

        var idx = Tabs.SelectedIndex;
        if (idx < 0 || idx >= _group.Panes.Count)
        {
            return;
        }

        _group.ActiveIndex = idx;
        PaneActivated?.Invoke(_group.Panes[idx]);
        SyncContent();
        RefreshActiveVisuals();
    }

    private void ClosePane(FilePaneViewModel pane)
    {
        if (_group is null)
        {
            return;
        }

        _group.ClosePane(pane);
        RemoveView(pane);
        SyncTabs();
        SyncContent();
    }

    private void SyncContent()
    {
        if (_group?.ActivePane is { } pane)
        {
            ShowPane(pane, createIfMissing: true);
            if (pane.VisibleItems.Count == 0 &&
                !string.IsNullOrWhiteSpace(pane.CurrentPath) &&
                !pane.IsLoading)
            {
                _ = LoadPaneAsync(pane);
            }
        }
        else
        {
            ContentHost.Children.Clear();
        }
    }

    private async Task LoadPaneAsync(FilePaneViewModel pane)
    {
        await pane.LoadAsync();
        if (ReferenceEquals(_group?.ActivePane, pane))
        {
            _views.GetValueOrDefault(pane)?.EnsureListBoundAfterAttach();
        }
    }

    private FilePaneControl GetOrCreateView(FilePaneViewModel pane)
    {
        if (_views.TryGetValue(pane, out var existing))
        {
            return existing;
        }

        var view = new FilePaneControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ViewModel = pane,
        };
        _views[pane] = view;
        return view;
    }

    private void ShowPane(FilePaneViewModel pane, bool createIfMissing)
    {
        FilePaneControl? target = null;
        if (_views.TryGetValue(pane, out var existing))
        {
            target = existing;
        }
        else if (createIfMissing)
        {
            target = GetOrCreateView(pane);
        }

        if (target is null)
        {
            return;
        }

        for (var i = ContentHost.Children.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(ContentHost.Children[i], target))
            {
                ContentHost.Children.RemoveAt(i);
            }
        }

        if (!ContentHost.Children.Contains(target))
        {
            if (target.Parent is Panel p)
            {
                p.Children.Remove(target);
            }

            target.Visibility = Visibility.Visible;
            target.Opacity = 1;
            target.IsHitTestVisible = true;
            ContentHost.Children.Add(target);
            target.EnsureListBoundAfterAttach();
        }
    }

    private void RemoveView(FilePaneViewModel pane)
    {
        if (!_views.Remove(pane, out var view))
        {
            return;
        }

        ContentHost.Children.Remove(view);
        view.ViewModel = null;
    }

    private void ClearViews()
    {
        foreach (var pane in _views.Keys.ToList())
        {
            RemoveView(pane);
        }
    }

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
}
