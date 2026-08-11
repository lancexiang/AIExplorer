using AIExplorer_App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using System.Reflection;

namespace AIExplorer_App.Views;

public sealed partial class BrowserTabView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(BrowserTabViewModel),
            typeof(BrowserTabView),
            new PropertyMetadata(null, OnViewModelChanged));

    private bool _dragging;
    private double _splitRatio = 0.5;
    private bool _syncingRightTabs;
    private bool _rightTabsDetached;
    private bool _shellOwnsHorizontalRight;
    private Grid? _rightTabsOriginalParent;
    private int _rightTabsOriginalRow;
    private int _rightTabsOriginalColumn;

    public event Action<double>? SplitRatioChanged;

    /// <summary>供 MainPage 全局快捷键定位左侧文件窗格。</summary>
    public FilePaneControl LeftFilePane => LeftPaneControl;

    public BrowserTabView()
    {
        InitializeComponent();
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        SizeChanged += (_, _) =>
        {
            if (_dragging)
            {
                return;
            }

            ApplySplitRatio();
            CollapseGroupTabStrip(RightGroupTabs);
        };
        RightGroupTabs.Loaded += (_, _) => CollapseGroupTabStrip(RightGroupTabs);
    }

    public BrowserTabViewModel? ViewModel
    {
        get => (BrowserTabViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    /// <summary>切回本 Tab 或首次挂到内容区时：仅在列表失步时重绑，避免大目录整表重建。</summary>
    public void SyncPaneListBindings()
    {
        using var _ = AIExplorer_App.PerfLog.Measure("SyncPaneListBindings");
        LeftPaneControl.EnsureListBoundAfterAttach();
        if (!_shellOwnsHorizontalRight)
        {
            RightPaneControl.EnsureListBoundAfterAttach();
        }
    }

    /// <summary>
    /// 左右分栏时右侧由 MainPage 壳层托管，本控件只渲染左栏，避免双分隔线与空白占位。
    /// </summary>
    public void SetShellHorizontalDual(bool enabled)
    {
        if (_shellOwnsHorizontalRight == enabled)
        {
            UpdateLayoutState();
            return;
        }

        _shellOwnsHorizontalRight = enabled;
        if (enabled)
        {
            RestoreRightTabStripFromHost();
            RightPaneControl.ViewModel = null;
        }
        else if (ViewModel is not null)
        {
            RightPaneControl.ViewModel = ViewModel.RightPane;
        }

        UpdateLayoutState();
    }

    /// <summary>高亮当前激活栏（已改为标签蓝色，内容区不再画框）。</summary>
    public void ClearActivePaneBorders()
    {
        LeftPaneHost.BorderThickness = new Thickness(0);
        RightPaneHost.BorderThickness = new Thickness(0);
        LeftPaneControl.NotifyHostActive(false);
        RightPaneControl.NotifyHostActive(false);
    }

    public void SetActivePaneSide(PaneSide side, bool shellOwnsRight = false)
    {
        ClearActivePaneBorders();
    }

    public void SetLeftPaneHighlight(bool active)
    {
        ClearActivePaneBorders();
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is BrowserTabView view)
        {
            if (e.OldValue is BrowserTabViewModel oldModel)
            {
                oldModel.PropertyChanged -= view.OnViewModelPropertyChanged;
                oldModel.LeftGroup.PropertyChanged -= view.OnLeftGroupPropertyChanged;
                oldModel.RightGroup.PropertyChanged -= view.OnRightGroupPropertyChanged;
                oldModel.RightGroup.Panes.CollectionChanged -= view.OnRightGroupPanesChanged;
                foreach (var pane in oldModel.RightGroup.Panes)
                {
                    pane.PropertyChanged -= view.OnRightPanePropertyChanged;
                }
            }

            view.ApplyViewModel();
        }
    }

    private void ApplyViewModel()
    {
        if (ViewModel is null)
        {
            return;
        }

        DataContext = ViewModel;
        LeftPaneControl.ViewModel = ViewModel.LeftPane;
        RightPaneControl.ViewModel = _shellOwnsHorizontalRight ? null : ViewModel.RightPane;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.LeftGroup.PropertyChanged += OnLeftGroupPropertyChanged;
        ViewModel.RightGroup.PropertyChanged += OnRightGroupPropertyChanged;
        ViewModel.RightGroup.Panes.CollectionChanged += OnRightGroupPanesChanged;

        foreach (var pane in ViewModel.RightGroup.Panes)
        {
            pane.PropertyChanged += OnRightPanePropertyChanged;
        }

        if (!ShellOwnsHorizontalRight)
        {
            SyncGroupTabs(RightGroupTabs, ViewModel.RightGroup, ref _syncingRightTabs);
        }

        UpdateLayoutState();
    }

    /// <summary>左右分栏由壳层托管右侧时，禁止同步内部 RightGroupTabs，避免空白占位串到顶栏。</summary>
    private bool ShellOwnsHorizontalRight =>
        _shellOwnsHorizontalRight ||
        (ViewModel is { IsDualPane: true, IsHorizontalSplit: true });

    private void OnLeftGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PaneGroupViewModel.ActivePane))
        {
            LeftPaneControl.ViewModel = ViewModel?.LeftPane;
        }
    }

    private void OnRightPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ShellOwnsHorizontalRight)
        {
            return;
        }

        if (e.PropertyName is nameof(FilePaneViewModel.CurrentPath))
        {
            SyncGroupTabHeaders(RightGroupTabs, ViewModel?.RightGroup);
        }
    }

    private void OnRightGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ShellOwnsHorizontalRight)
        {
            return;
        }

        if (e.PropertyName is nameof(PaneGroupViewModel.ActiveIndex)
            or nameof(PaneGroupViewModel.ActivePane))
        {
            RightPaneControl.ViewModel = ViewModel?.RightPane;
            SyncGroupTabSelection(RightGroupTabs, ViewModel?.RightGroup, ref _syncingRightTabs);
        }
        else if (e.PropertyName == nameof(PaneGroupViewModel.Title))
        {
            SyncGroupTabHeaders(RightGroupTabs, ViewModel?.RightGroup);
        }
    }

    private void OnRightGroupPanesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (FilePaneViewModel pane in e.OldItems)
            {
                pane.PropertyChanged -= OnRightPanePropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (FilePaneViewModel pane in e.NewItems)
            {
                pane.PropertyChanged += OnRightPanePropertyChanged;
            }
        }

        if (!ShellOwnsHorizontalRight)
        {
            SyncGroupTabs(RightGroupTabs, ViewModel?.RightGroup, ref _syncingRightTabs);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BrowserTabViewModel.IsDualPane)
            or nameof(BrowserTabViewModel.LeftPaneColumnSpan)
            or nameof(BrowserTabViewModel.Orientation)
            or nameof(BrowserTabViewModel.IsHorizontalSplit))
        {
            UpdateLayoutState();
        }
    }

    private void SyncGroupTabs(TabView tabView, PaneGroupViewModel? group, ref bool syncing)
    {
        if (group is null || ShellOwnsHorizontalRight)
        {
            return;
        }

        syncing = true;
        try
        {
            CollapseGroupTabStrip(tabView);
            while (tabView.TabItems.Count > 0)
            {
                tabView.TabItems.RemoveAt(tabView.TabItems.Count - 1);
            }

            foreach (var pane in group.Panes)
            {
                tabView.TabItems.Add(CreateGroupTabItem(pane, group.Panes.Count > 1));
            }

            tabView.SelectedIndex = Math.Clamp(group.ActiveIndex, 0, Math.Max(0, group.Panes.Count - 1));
        }
        finally
        {
            syncing = false;
        }
    }

    private static void SyncGroupTabSelection(TabView tabView, PaneGroupViewModel? group, ref bool syncing)
    {
        if (group is null || tabView.TabItems.Count == 0)
        {
            return;
        }

        syncing = true;
        try
        {
            tabView.SelectedIndex = Math.Clamp(group.ActiveIndex, 0, tabView.TabItems.Count - 1);
        }
        finally
        {
            syncing = false;
        }
    }

    private static void SyncGroupTabHeaders(TabView tabView, PaneGroupViewModel? group)
    {
        if (group is null)
        {
            return;
        }

        for (var i = 0; i < tabView.TabItems.Count && i < group.Panes.Count; i++)
        {
            if (tabView.TabItems[i] is TabViewItem item)
            {
                item.Header = CreatePaneTabHeader(group.Panes[i]);
                item.IsClosable = group.Panes.Count > 1;
            }
        }
    }

    private static TabViewItem CreateGroupTabItem(FilePaneViewModel pane, bool closable) =>
        new()
        {
            Header = CreatePaneTabHeader(pane),
            IsClosable = closable,
            Content = new Border { Width = 0, Height = 0, Visibility = Visibility.Collapsed },
        };

    private static FrameworkElement CreatePaneTabHeader(FilePaneViewModel pane)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(new FontIcon
        {
            Glyph = "\uE8B7",
            FontSize = 12,
            Foreground = IconBrushes.Folder,
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = PaneGroupViewModel.GetShortName(pane.CurrentPath),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return panel;
    }

    private static void CollapseGroupTabStrip(TabView tabView)
    {
        try
        {
            var presenter = FindDescendant<ContentPresenter>(tabView, "TabContentPresenter");
            if (presenter is not null)
            {
                presenter.Visibility = Visibility.Collapsed;
                presenter.Height = 0;
                presenter.MaxHeight = 0;
                presenter.MinHeight = 0;
            }

            // 去掉默认内边距，避免挂到主页顶栏后比左侧 Tab 更高/下沉
            tabView.Padding = new Thickness(0);
            tabView.Margin = new Thickness(0);
            tabView.VerticalAlignment = VerticalAlignment.Top;

            var strip = FindDescendant<FrameworkElement>(tabView, "TabContainerGrid");
            var h = strip is { ActualHeight: > 1 } ? strip.ActualHeight : 36;
            tabView.Height = h;
            tabView.MaxHeight = h;
            tabView.MinHeight = 0;
        }
        catch
        {
        }
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

    private void OnRightGroupAddTab(TabView sender, object args) => ViewModel?.RightGroup.AddPane();

    private void OnRightGroupTabClose(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (ViewModel is null)
        {
            return;
        }

        var idx = sender.TabItems.IndexOf(args.Tab);
        if (idx >= 0 && idx < ViewModel.RightGroup.Panes.Count)
        {
            ViewModel.RightGroup.ClosePane(ViewModel.RightGroup.Panes[idx]);
        }
    }

    private void OnRightGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingRightTabs || ViewModel is null)
        {
            return;
        }

        var idx = RightGroupTabs.SelectedIndex;
        if (idx >= 0 && idx < ViewModel.RightGroup.Panes.Count)
        {
            ViewModel.RightGroup.ActiveIndex = idx;
            try
            {
                App.Services.GetRequiredService<MainPageViewModel>()
                    .NotifyPaneActivated(ViewModel.RightGroup.ActivePane);
            }
            catch
            {
            }
        }
    }

    private void UpdateLayoutState()
    {
        if (ViewModel is null)
        {
            return;
        }

        // 左右分栏由壳层统一托管右侧：本控件仅铺满左栏，不再画第二根分隔线
        if (ShellOwnsHorizontalRight)
        {
            _shellOwnsHorizontalRight = true;
            RightPaneControl.ViewModel = null;
            Splitter.Visibility = Visibility.Collapsed;
            RightPaneHost.Visibility = Visibility.Collapsed;
            RightGroupTabs.Visibility = Visibility.Collapsed;
            RightTabsRow.Height = new GridLength(0);
            RightTabsRow.MinHeight = 0;

            // 清空内部右侧标签，杜绝空白占位残留
            _syncingRightTabs = true;
            try
            {
                while (RightGroupTabs.TabItems.Count > 0)
                {
                    RightGroupTabs.TabItems.RemoveAt(RightGroupTabs.TabItems.Count - 1);
                }
            }
            finally
            {
                _syncingRightTabs = false;
            }

            Grid.SetRow(LeftPaneHost, 0);
            Grid.SetColumn(LeftPaneHost, 0);
            Grid.SetRowSpan(LeftPaneHost, 3);
            Grid.SetColumnSpan(LeftPaneHost, 3);

            SplitterCol.Width = new GridLength(0);
            SecondaryStar.Width = new GridLength(0);
            SecondaryStar.MinWidth = 0;
            SplitterRow.Height = new GridLength(0);
            SecondaryRow.Height = new GridLength(0);
            SecondaryRow.MinHeight = 0;
            PrimaryStar.Width = new GridLength(1, GridUnitType.Star);
            PrimaryRow.Height = new GridLength(1, GridUnitType.Star);
            return;
        }

        var dual = ViewModel.IsDualPane;
        var sideBySide = ViewModel.IsHorizontalSplit;

        Splitter.Visibility = dual ? Visibility.Visible : Visibility.Collapsed;
        RightPaneHost.Visibility = dual ? Visibility.Visible : Visibility.Collapsed;

        var showInternalRightTabs = dual && !_rightTabsDetached && !ViewModel.IsHorizontalSplit;
        RightGroupTabs.Visibility = showInternalRightTabs ? Visibility.Visible : Visibility.Collapsed;

        if (dual && _rightTabsDetached)
        {
            RightTabsRow.Height = new GridLength(0);
            RightTabsRow.MinHeight = 0;
            Grid.SetRow(RightPaneContentHost, 0);
            Grid.SetRowSpan(RightPaneContentHost, 2);
        }
        else
        {
            RightTabsRow.Height = new GridLength(1, GridUnitType.Auto);
            RightTabsRow.MinHeight = 0;
            Grid.SetRow(RightPaneContentHost, 1);
            Grid.SetRowSpan(RightPaneContentHost, 1);
        }

        if (showInternalRightTabs)
        {
            SyncGroupTabs(RightGroupTabs, ViewModel.RightGroup, ref _syncingRightTabs);
            CollapseGroupTabStrip(RightGroupTabs);
        }

        if (!dual)
        {
            Grid.SetRow(LeftPaneHost, 0);
            Grid.SetColumn(LeftPaneHost, 0);
            Grid.SetRowSpan(LeftPaneHost, 3);
            Grid.SetColumnSpan(LeftPaneHost, 3);

            SplitterCol.Width = new GridLength(0);
            SecondaryStar.Width = new GridLength(0);
            SplitterRow.Height = new GridLength(0);
            SecondaryRow.Height = new GridLength(0);
            SecondaryRow.MinHeight = 0;
            PrimaryStar.Width = new GridLength(1, GridUnitType.Star);
            PrimaryRow.Height = new GridLength(1, GridUnitType.Star);
            return;
        }

        _splitRatio = Math.Clamp(_splitRatio, 0.2, 0.8);

        if (sideBySide)
        {
            Grid.SetRow(LeftPaneHost, 0);
            Grid.SetColumn(LeftPaneHost, 0);
            Grid.SetRowSpan(LeftPaneHost, 3);
            Grid.SetColumnSpan(LeftPaneHost, 1);

            Grid.SetRow(Splitter, 0);
            Grid.SetColumn(Splitter, 1);
            Grid.SetRowSpan(Splitter, 3);
            Grid.SetColumnSpan(Splitter, 1);

            Grid.SetRow(RightPaneHost, 0);
            Grid.SetColumn(RightPaneHost, 2);
            Grid.SetRowSpan(RightPaneHost, 3);
            Grid.SetColumnSpan(RightPaneHost, 1);

            Splitter.Width = 6;
            Splitter.Height = double.NaN;
            SplitterVisual.Width = 1;
            SplitterVisual.Height = double.NaN;
            SplitterVisual.HorizontalAlignment = HorizontalAlignment.Center;
            TrySetSplitterCursor(InputSystemCursorShape.SizeWestEast);

            SplitterRow.Height = new GridLength(0);
            SecondaryRow.Height = new GridLength(0);
            SecondaryRow.MinHeight = 0;
            PrimaryRow.Height = new GridLength(1, GridUnitType.Star);
            PrimaryRow.MinHeight = 120;
            SplitterCol.Width = new GridLength(6);
            ApplySplitRatio();
        }
        else
        {
            Grid.SetRow(LeftPaneHost, 0);
            Grid.SetColumn(LeftPaneHost, 0);
            Grid.SetRowSpan(LeftPaneHost, 1);
            Grid.SetColumnSpan(LeftPaneHost, 3);

            Grid.SetRow(Splitter, 1);
            Grid.SetColumn(Splitter, 0);
            Grid.SetRowSpan(Splitter, 1);
            Grid.SetColumnSpan(Splitter, 3);

            Grid.SetRow(RightPaneHost, 2);
            Grid.SetColumn(RightPaneHost, 0);
            Grid.SetRowSpan(RightPaneHost, 1);
            Grid.SetColumnSpan(RightPaneHost, 3);

            Splitter.Height = 6;
            Splitter.Width = double.NaN;
            SplitterVisual.Height = 1;
            SplitterVisual.Width = double.NaN;
            TrySetSplitterCursor(InputSystemCursorShape.SizeNorthSouth);

            SplitterCol.Width = new GridLength(0);
            SecondaryStar.Width = new GridLength(0);
            PrimaryStar.Width = new GridLength(1, GridUnitType.Star);
            SplitterRow.Height = new GridLength(6);
            PrimaryRow.MinHeight = 80;
            SecondaryRow.MinHeight = 80;
            ApplySplitRatio();
        }
    }

    private void ApplySplitRatio()
    {
        if (ViewModel is null || !ViewModel.IsDualPane)
        {
            return;
        }

        _splitRatio = Math.Clamp(_splitRatio, 0.2, 0.8);
        var primary = _splitRatio;
        var secondary = 1.0 - _splitRatio;

        if (ViewModel.IsHorizontalSplit)
        {
            PrimaryStar.Width = new GridLength(primary, GridUnitType.Star);
            SecondaryStar.Width = new GridLength(secondary, GridUnitType.Star);
            PrimaryRow.Height = new GridLength(1, GridUnitType.Star);
            SecondaryRow.Height = new GridLength(0);
            SecondaryRow.MinHeight = 0;
            SplitRatioChanged?.Invoke(_splitRatio);
        }
        else
        {
            PrimaryRow.Height = new GridLength(primary, GridUnitType.Star);
            SecondaryRow.Height = new GridLength(secondary, GridUnitType.Star);
            PrimaryStar.Width = new GridLength(1, GridUnitType.Star);
            SecondaryStar.Width = new GridLength(0);
        }
    }

    private void TrySetSplitterCursor(InputSystemCursorShape shape)
    {
        try
        {
            var prop = typeof(UIElement).GetProperty("ProtectedCursor", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            prop?.SetValue(Splitter, InputSystemCursor.Create(shape));
        }
        catch
        {
        }
    }

    private void OnSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        Splitter.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || ViewModel is null || !ViewModel.IsDualPane)
        {
            return;
        }

        var pos = e.GetCurrentPoint(RootGrid).Position;
        if (ViewModel.IsHorizontalSplit)
        {
            if (ActualWidth > 16)
            {
                _splitRatio = Math.Clamp(pos.X / ActualWidth, 0.2, 0.8);
                ApplySplitRatio();
            }
        }
        else if (ActualHeight > 16)
        {
            _splitRatio = Math.Clamp(pos.Y / ActualHeight, 0.2, 0.8);
            ApplySplitRatio();
        }

        e.Handled = true;
    }

    private void OnSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndDrag(e.Pointer);
        e.Handled = true;
    }

    private void OnSplitterPointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndDrag(e.Pointer);

    private void EndDrag(Microsoft.UI.Xaml.Input.Pointer pointer)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Splitter.ReleasePointerCapture(pointer);
    }

    public TabView DetachRightTabStripForHost()
    {
        if (!_rightTabsDetached && RightGroupTabs.Parent is Grid grid)
        {
            _rightTabsOriginalParent = grid;
            _rightTabsOriginalRow = Grid.GetRow(RightGroupTabs);
            _rightTabsOriginalColumn = Grid.GetColumn(RightGroupTabs);
            grid.Children.Remove(RightGroupTabs);
            _rightTabsDetached = true;
            UpdateLayoutState();
        }

        RightGroupTabs.Visibility = Visibility.Visible;
        RightGroupTabs.HorizontalAlignment = HorizontalAlignment.Stretch;
        CollapseGroupTabStrip(RightGroupTabs);
        return RightGroupTabs;
    }

    public void RestoreRightTabStripFromHost()
    {
        if (!_rightTabsDetached || _rightTabsOriginalParent is null)
        {
            return;
        }

        if (RightGroupTabs.Parent is Panel host && !ReferenceEquals(host, _rightTabsOriginalParent))
        {
            host.Children.Remove(RightGroupTabs);
        }

        if (!_rightTabsOriginalParent.Children.Contains(RightGroupTabs))
        {
            Grid.SetRow(RightGroupTabs, _rightTabsOriginalRow);
            Grid.SetColumn(RightGroupTabs, _rightTabsOriginalColumn);
            _rightTabsOriginalParent.Children.Add(RightGroupTabs);
        }

        _rightTabsDetached = false;
        _rightTabsOriginalParent = null;
        UpdateLayoutState();
    }

    public void ApplyExternalSplitRatio(double ratio)
    {
        _splitRatio = Math.Clamp(ratio, 0.2, 0.8);
        ApplySplitRatio();
    }
}
