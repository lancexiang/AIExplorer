using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using AIExplorer.Core.Files;
using AIExplorer.Core.Settings;
using AIExplorer.Core.Shell;
using AIExplorer_App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.System;

namespace AIExplorer_App.Views;

public sealed partial class FilePaneControl : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(FilePaneViewModel),
            typeof(FilePaneControl),
            new PropertyMetadata(null, OnViewModelChanged));

    private ScrollViewer? _itemsScrollViewer;
    /// <summary>点击展开按钮后短暂屏蔽列表双击进目录。</summary>
    private DateTime _suppressItemActivateUntilUtc;

    public FilePaneControl()
    {
        InitializeComponent();

        // F5：仅本窗格有焦点时刷新。
        // 必须 Hidden：默认 Auto 会在整个窗格悬停时弹出漂浮的「F5」气泡（跟鼠标跑）。
        KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        var f5 = new KeyboardAccelerator { Key = VirtualKey.F5 };
        f5.Invoked += async (_, e) =>
        {
            if (ViewModel is null || !OwnsKeyboardFocus())
            {
                return;
            }

            e.Handled = true;
            await ViewModel.RefreshCommand.ExecuteAsync(null);
        };
        KeyboardAccelerators.Add(f5);

        // Ctrl+C/X/V 改由 MainPage 全局快捷键管理，避免焦点不在列表时跨目录粘贴失效

        Loaded += (_, _) =>
        {
            HookItemsScroll();
            SyncItemsSources();
        };
        SizeChanged += (_, _) =>
        {
            SyncNameTextMaxWidth();
            UpdateQuickActionsBar();
        };
        FileList.Loaded += (_, _) =>
        {
            HookItemsScroll();
            SyncItemsSources();
            SyncNameTextMaxWidth();
        };
        if (NameHeaderButton is not null)
        {
            NameHeaderButton.SizeChanged += (_, _) => SyncNameTextMaxWidth();
        }
        FileIcons.Loaded += (_, _) => HookItemsScroll();
        FileCards.Loaded += (_, _) => HookItemsScroll();

        PointerPressed += (_, _) => ReportActivated();
        GotFocus += (_, _) => ReportActivated();
        // ListView 会把 PointerPressed 标为 Handled，必须额外监听才能在点列表/空白时激活本窗格
        AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnPanePointerPressed),
            handledEventsToo: true);
    }

    private Point? _quickActionAnchorInLayer;
    private bool _syncingSelectionFromUi;
    private IReadOnlyList<FileListItemViewModel> _dragItems = [];
    private BitmapImage? _dragPreviewImage;

    /// <summary>供全局快捷键调用：复制/剪切当前列表选中项。</summary>
    public async Task CopyOrCutSelectionFromUiAsync(bool cut)
    {
        if (ViewModel is null)
        {
            return;
        }

        ReportActivated();

        var selection = ViewModel.GetSelectedItems(ActiveItemsView.SelectedItems.ToList());
        if (selection.Count == 0)
        {
            selection = ViewModel.SelectionSnapshot;
        }

        if (selection.Count == 0 && ViewModel.SelectedItem is { } single)
        {
            selection = [single];
        }

        if (selection.Count == 0)
        {
            RequestActionToast(cut ? "未选中可剪切项" : "未选中可复制项");
            return;
        }

        // 立刻固化快照，避免后续 SelectionChanged 空脉冲清掉选中
        ViewModel.SetSelectionSnapshot(selection);
        await RunOpAsync(cut ? "剪切" : "复制", () => ViewModel.CopySelectionAsync(selection, cut));
    }

    /// <summary>供全局快捷键调用：粘贴到当前窗格。</summary>
    public async Task PasteFromUiAsync()
    {
        if (ViewModel is null)
        {
            return;
        }

        ReportActivated();

        try
        {
            var result = await ViewModel.PasteAsync();
            RequestActionToast(result switch
            {
                true => "粘贴成功",
                false => "没有可粘贴的内容",
                _ => "已取消粘贴",
            });
        }
        catch (Exception ex)
        {
            RequestActionToast("粘贴失败");
            await ShowMessageAsync("粘贴失败", ex.Message);
        }
    }

    private async Task CreateNewFolderFromUiAsync()
    {
        if (ViewModel is null)
        {
            return;
        }

        try
        {
            var created = await ViewModel.CreateNewFolderAsync();
            RequestActionToast("已新建文件夹");
            if (created is not null)
            {
                ActiveItemsView.SelectedItem = created;
                ViewModel.SetSelectionSnapshot([created]);
                BeginInlineRename(created);
            }
        }
        catch (Exception ex)
        {
            RequestActionToast("新建文件夹失败");
            await ShowMessageAsync("新建文件夹失败", ex.Message);
        }
    }

    private void RequestActionToast(string message)
    {
        for (DependencyObject? p = this; p is not null; p = VisualTreeHelper.GetParent(p))
        {
            if (p is MainPage page)
            {
                page.ShowActionToast(message);
                return;
            }
        }
    }

    /// <summary>由宿主标记本窗格是否为当前激活栏（侧栏导航目标）。不再改透明度，避免闪烁。</summary>
    public void NotifyHostActive(bool active)
    {
    }

    private void ReportActivated()
    {
        if (ViewModel is null)
        {
            return;
        }

        try
        {
            App.Services.GetRequiredService<MainPageViewModel>().NotifyPaneActivated(ViewModel);
        }
        catch
        {
        }
    }

    /// <summary>焦点在本窗格可见子树内时才响应，避免双栏/折叠栏抢快捷键。</summary>
    private bool OwnsKeyboardFocus()
    {
        if (!IsLoaded || Visibility != Visibility.Visible || XamlRoot is null)
        {
            return false;
        }

        for (DependencyObject? p = this; p is not null; p = VisualTreeHelper.GetParent(p))
        {
            if (p is UIElement { Visibility: Visibility.Collapsed })
            {
                return false;
            }
        }

        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (focused is not null)
        {
            if (ReferenceEquals(focused, this))
            {
                return true;
            }

            focused = VisualTreeHelper.GetParent(focused);
        }

        return false;
    }

    public FilePaneViewModel? ViewModel
    {
        get => (FilePaneViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    /// <summary>当前可见的文件视图（详情 / 图标 / 卡片）。</summary>
    private ListViewBase ActiveItemsView => ViewModel?.ViewMode switch
    {
        FilePaneViewMode.Icons => FileIcons,
        FilePaneViewMode.Cards => FileCards,
        _ => FileList,
    };

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FilePaneControl control)
        {
            control.DataContext = e.NewValue;
            if (e.OldValue is FilePaneViewModel oldVm)
            {
                oldVm.PropertyChanged -= control.OnViewModelPropertyChanged;
                oldVm.ListContentChanged -= control.OnListContentChanged;
                oldVm.AskFileConflictAsync = null;
            }

            if (e.OldValue is FilePaneViewModel oldPane)
            {
                oldPane.Columns.PropertyChanged -= control.OnColumnsChanged;
            }

            if (e.NewValue is FilePaneViewModel newVm)
            {
                newVm.PropertyChanged += control.OnViewModelPropertyChanged;
                newVm.ListContentChanged += control.OnListContentChanged;
                newVm.AskFileConflictAsync = control.AskFileConflictAsync;
                newVm.Columns.PropertyChanged += control.OnColumnsChanged;
                control.ApplyHeaderColumnWidths(newVm.Columns);
                control.SyncItemsSources();
            }
        }
    }

    private void OnListContentChanged()
    {
        // VisibleItems 的 CollectionChanged 已足够驱动 ListView；
        // 仅在尚未绑定 ItemsSource 时补一次（首次 / 换 VM），禁止 null 重绑闪白。
        if (ViewModel is null)
        {
            return;
        }

        if (!ReferenceEquals(FileList.ItemsSource, ViewModel.VisibleItems))
        {
            SyncItemsSources(force: false);
        }
    }

    /// <summary>
    /// 仅在未绑定或强制时设置 ItemsSource。切 Tab / watcher 增量更新禁止走这里的 null 重绑。
    /// </summary>
    internal void SyncItemsSources(bool force = false)
    {
        using var _ = AIExplorer_App.PerfLog.Measure(force ? "SyncItemsSources(force)" : "SyncItemsSources");
        if (ViewModel is null)
        {
            FileList.ItemsSource = null;
            FileIcons.ItemsSource = null;
            FileCards.ItemsSource = null;
            return;
        }

        var items = ViewModel.VisibleItems;
        var alreadyBound =
            ReferenceEquals(FileList.ItemsSource, items) &&
            ReferenceEquals(FileIcons.ItemsSource, items) &&
            ReferenceEquals(FileCards.ItemsSource, items);

        if (!force && alreadyBound)
        {
            return;
        }

        // 已绑过再 force：直接赋同引用，尽量避免 null→再赋 的清空闪帧
        if (alreadyBound)
        {
            return;
        }

        FileList.ItemsSource = items;
        FileIcons.ItemsSource = items;
        FileCards.ItemsSource = items;
    }

    /// <summary>首次挂到内容区时绑定；已绑定则 noop。</summary>
    internal void EnsureListBoundAfterAttach() => SyncItemsSources(force: false);

    private void OnColumnsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ApplyHeaderColumnWidths(ViewModel.Columns);
        }
    }

    // 表头列宽无法用 {Binding} 绑到 ColumnDefinition，这里在列显隐/拖动变化时同步。
    private void ApplyHeaderColumnWidths(FileColumnLayout columns)
    {
        HeaderSizeCol.Width = columns.SizeWidth;
        HeaderTypeCol.Width = columns.TypeWidth;
        HeaderModifiedCol.Width = columns.ModifiedWidth;
    }

    /// <summary>当前拖动的分割线边界：name-size / size-type / type-modified。</summary>
    private string? _resizingBoundary;
    private double _resizeStartX;
    private double _resizeStartLeft;
    private double _resizeStartRight;
    private Border? _highlightedSplitter;

    private void OnSplitterPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border)
        {
            return;
        }

        // 同时只允许一条分割线高亮（避免残留导致“双选”）
        if (_highlightedSplitter is not null && !ReferenceEquals(_highlightedSplitter, border))
        {
            ResetSplitterVisual(_highlightedSplitter);
        }

        _highlightedSplitter = border;
        // 每次新建 cursor：静态复用 + ProtectedCursor=null 释放后会在 Microsoft.UI.Input.dll 里 0xc0000409 闪退
        TrySetResizeCursor();
        if (border.Child is Microsoft.UI.Xaml.Shapes.Rectangle line)
        {
            line.Fill = (Brush)Application.Current.Resources["AppAccentBrush"];
            line.Width = 2;
        }
    }

    private void OnSplitterPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_resizingBoundary is not null)
        {
            return;
        }

        if (sender is Border border)
        {
            ResetSplitterVisual(border);
            if (ReferenceEquals(_highlightedSplitter, border))
            {
                _highlightedSplitter = null;
            }
        }

        TryClearResizeCursor();
    }

    private void TrySetResizeCursor()
    {
        try
        {
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(
                Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
        }
        catch
        {
        }
    }

    private void TryClearResizeCursor()
    {
        try
        {
            ProtectedCursor = null;
        }
        catch
        {
        }
    }

    private static void ResetSplitterVisual(Border? border)
    {
        if (border?.Child is not Microsoft.UI.Xaml.Shapes.Rectangle line)
        {
            return;
        }

        if (Application.Current.Resources.TryGetValue("DividerStrokeColorDefaultBrush", out var brush) &&
            brush is Brush divider)
        {
            line.Fill = divider;
        }
        else
        {
            line.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0x80, 0x80, 0x80));
        }

        line.Width = 1;
    }

    private void OnSortByNameClick(object sender, RoutedEventArgs e) =>
        ViewModel?.SortByNameCommand.Execute(null);

    private void OnSortBySizeClick(object sender, RoutedEventArgs e) =>
        ViewModel?.SortBySizeCommand.Execute(null);

    private void OnSortByTypeClick(object sender, RoutedEventArgs e) =>
        ViewModel?.SortByTypeCommand.Execute(null);

    private void OnSortByModifiedClick(object sender, RoutedEventArgs e) =>
        ViewModel?.SortByModifiedCommand.Execute(null);

    private void OnSplitterPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel is null || sender is not Border border || border.Tag is not string boundary)
        {
            return;
        }

        var columns = ViewModel.Columns;
        _resizingBoundary = boundary;
        _resizeStartX = e.GetCurrentPoint(DetailsHeader).Position.X;
        (_resizeStartLeft, _resizeStartRight) = boundary switch
        {
            "name-size" => (0, columns.SizePixels), // 左列名称是 *，只记右列
            "size-type" => (columns.SizePixels, columns.TypePixels),
            "type-modified" => (columns.TypePixels, columns.ModifiedPixels),
            _ => (0, 0),
        };

        if (_highlightedSplitter is not null && !ReferenceEquals(_highlightedSplitter, border))
        {
            ResetSplitterVisual(_highlightedSplitter);
        }

        _highlightedSplitter = border;
        border.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnColumnSplitterMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_resizingBoundary is null || ViewModel is null)
        {
            return;
        }

        // 左移 dx<0：左列变窄、右列变宽；右移相反。例 100|100 左移 20 → 80|120
        var dx = e.GetCurrentPoint(DetailsHeader).Position.X - _resizeStartX;
        ApplyBoundaryDelta(ViewModel.Columns, _resizingBoundary, _resizeStartLeft, _resizeStartRight, dx, persist: false);
        ApplyHeaderColumnWidths(ViewModel.Columns);
        e.Handled = true;
    }

    private void OnColumnSplitterReleased(object sender, PointerRoutedEventArgs e) =>
        EndColumnResize(sender, e, persist: true);

    private void OnColumnSplitterCaptureLost(object sender, PointerRoutedEventArgs e) =>
        EndColumnResize(sender, e, persist: true);

    private void EndColumnResize(object sender, PointerRoutedEventArgs e, bool persist)
    {
        if (_resizingBoundary is null || ViewModel is null)
        {
            return;
        }

        if (persist)
        {
            // 触发一次落盘（当前像素值已在 Move 中写好）
            ViewModel.Columns.SetSizePixels(ViewModel.Columns.SizePixels, persist: true);
            ViewModel.Columns.SetTypePixels(ViewModel.Columns.TypePixels, persist: false);
            ViewModel.Columns.SetModifiedPixels(ViewModel.Columns.ModifiedPixels, persist: false);
        }

        if (sender is Border border)
        {
            try
            {
                border.ReleasePointerCapture(e.Pointer);
            }
            catch
            {
                // capture 可能已丢失
            }

            ResetSplitterVisual(border);
        }

        if (ReferenceEquals(_highlightedSplitter, sender))
        {
            _highlightedSplitter = null;
        }

        TryClearResizeCursor();
        _resizingBoundary = null;
        e.Handled = true;
    }

    /// <summary>
    /// 按分割线左右分摊：left += dx，right -= dx，并夹紧到列最小/最大宽。
    /// name-size 时左列为弹性名称列，只调整右侧「大小」。
    /// </summary>
    private static void ApplyBoundaryDelta(
        FileColumnLayout columns,
        string boundary,
        double leftStart,
        double rightStart,
        double dx,
        bool persist)
    {
        switch (boundary)
        {
            case "name-size":
            {
                // 左=名称(*)，右=大小：左移让大小变宽 → size = rightStart - dx
                var minDx = rightStart - FileColumnLayout.SizeMax;
                var maxDx = rightStart - FileColumnLayout.SizeMin;
                dx = Math.Clamp(dx, minDx, maxDx);
                columns.SetSizePixels(rightStart - dx, persist);
                break;
            }
            case "size-type":
            {
                ClampPairDelta(
                    leftStart, rightStart, dx,
                    FileColumnLayout.SizeMin, FileColumnLayout.SizeMax,
                    FileColumnLayout.TypeMin, FileColumnLayout.TypeMax,
                    out var left, out var right);
                columns.SetSizePixels(left, persist);
                columns.SetTypePixels(right, persist);
                break;
            }
            case "type-modified":
            {
                ClampPairDelta(
                    leftStart, rightStart, dx,
                    FileColumnLayout.TypeMin, FileColumnLayout.TypeMax,
                    FileColumnLayout.ModifiedMin, FileColumnLayout.ModifiedMax,
                    out var left, out var right);
                columns.SetTypePixels(left, persist);
                columns.SetModifiedPixels(right, persist);
                break;
            }
        }
    }

    private static void ClampPairDelta(
        double leftStart,
        double rightStart,
        double dx,
        double leftMin,
        double leftMax,
        double rightMin,
        double rightMax,
        out double left,
        out double right)
    {
        var minDx = Math.Max(leftMin - leftStart, rightStart - rightMax);
        var maxDx = Math.Min(leftMax - leftStart, rightStart - rightMin);
        if (minDx > maxDx)
        {
            left = leftStart;
            right = rightStart;
            return;
        }

        dx = Math.Clamp(dx, minDx, maxDx);
        left = leftStart + dx;
        right = rightStart - dx;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilePaneViewModel.IsEditingPath) &&
            ViewModel?.IsEditingPath == true)
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                AddressBox.Focus(FocusState.Programmatic);
                AddressBox.SelectAll();
            });
        }

        if (e.PropertyName is nameof(FilePaneViewModel.ViewMode)
            or nameof(FilePaneViewModel.IsIconsView)
            or nameof(FilePaneViewModel.IsCardsView)
            or nameof(FilePaneViewModel.IsDetailsView))
        {
            _ = DispatcherQueue.TryEnqueue(() => ActiveItemsView.Focus(FocusState.Programmatic));
        }

        if (e.PropertyName == nameof(FilePaneViewModel.IsPreviewPaneVisible))
        {
            _ = RefreshPreviewPaneAsync(ViewModel?.SelectedItem);
        }

        if (e.PropertyName == nameof(FilePaneViewModel.SelectedItem) &&
            !_syncingSelectionFromUi &&
            ViewModel?.SelectedItem is { } selected)
        {
            _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                RevealSelectedItem(selected);
                // 列表容器可能尚未生成，再补一次滚入
                _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                    RevealSelectedItem(selected));
            });
        }
    }

    private void RevealSelectedItem(FileListItemViewModel item)
    {
        try
        {
            var view = ActiveItemsView;
            if (!ReferenceEquals(view.SelectedItem, item))
            {
                view.SelectedItem = item;
            }

            view.ScrollIntoView(item);
            UpdateQuickActionsBar();
        }
        catch
        {
        }
    }

    private void OnToggleEditPathClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (ViewModel.IsEditingPath)
        {
            ViewModel.CancelEditPathCommand.Execute(null);
        }
        else
        {
            ViewModel.BeginEditPathCommand.Execute(null);
        }
    }

    private void OnAddressDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ViewModel?.BeginEditPathCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>点路径段跳转；点空白区域进入编辑/复制（原双击行为）。</summary>
    private void OnAddressBarTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (IsUnderBreadcrumbSegmentButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        // 点刷新钮不进编辑
        if (IsUnderNamedOrTaggedButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        ViewModel.BeginEditPathCommand.Execute(null);
        e.Handled = true;
    }

    private static bool IsUnderBreadcrumbSegmentButton(DependencyObject? start)
    {
        for (var cur = start; cur is not null; cur = VisualTreeHelper.GetParent(cur))
        {
            if (cur is Button { Tag: string })
            {
                return true;
            }

            if (cur is Border { Name: "BreadcrumbHost" })
            {
                break;
            }
        }

        return false;
    }

    private static bool IsUnderNamedOrTaggedButton(DependencyObject? start)
    {
        for (var cur = start; cur is not null; cur = VisualTreeHelper.GetParent(cur))
        {
            if (cur is Button button && button.Command is not null)
            {
                return true;
            }

            if (cur is Border { Name: "BreadcrumbHost" })
            {
                break;
            }
        }

        return false;
    }

    private void OnCopyAddressPathClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.CopyCurrentPathCommand.Execute(null);
    }

    private static Brush ResolveColorBrush(string? hex)
    {
        if (FileColorPalette.TryParseRgb(hex, out var r, out var g, out var b))
        {
            return new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
        }

        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128));
    }

    private async void OnAddressKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            await ViewModel.CommitEditPathCommand.ExecuteAsync(null);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            ViewModel.CancelEditPathCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnAddressLostFocus(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.IsEditingPath == true)
        {
            ViewModel.CancelEditPathCommand.Execute(null);
        }
    }

    private async void OnBreadcrumbClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ViewModel is null || sender is not Button button)
            {
                return;
            }

            var path = button.Tag as string
                       ?? (button.DataContext as PathSegmentViewModel)?.FullPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            await ViewModel.NavigateToPathCommand.ExecuteAsync(path);
        }
        catch
        {
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null || _syncingSelectionFromUi)
        {
            return;
        }

        // 仅处理当前可见视图，避免三视图 SelectionChanged 互相清空快照
        if (!ReferenceEquals(sender, ActiveItemsView))
        {
            return;
        }

        ReportActivated();

        var list = (ListViewBase)sender;
        var selected = ViewModel.GetSelectedItems(list.SelectedItems.ToList());
        if (selected.Count == 0 && list.SelectedItem is FileListItemViewModel one)
        {
            selected = [one];
        }

        _syncingSelectionFromUi = true;
        try
        {
            ViewModel.SelectedItem = selected.FirstOrDefault()
                ?? list.SelectedItem as FileListItemViewModel;
            // 空脉冲（TwoWay 回写残留）不覆盖已有快照；仅在有明确选中或用户清空时更新
            if (selected.Count > 0)
            {
                ViewModel.SetSelectionSnapshot(selected);
                ViewModel.SetSelectedCount(selected.Count);
            }
            else if (e.AddedItems.Count == 0 && e.RemovedItems.Count > 0 && list.SelectedItems.Count == 0)
            {
                ViewModel.SetSelectionSnapshot([]);
                ViewModel.SetSelectedCount(0);
            }
        }
        finally
        {
            _syncingSelectionFromUi = false;
        }

        _ = RefreshPreviewPaneAsync(ViewModel.SelectedItem);
        HookItemsScroll();
        // 容器可能尚未生成，下一帧再定位悬浮条
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdateQuickActionsBar);
    }

    private void HookItemsScroll()
    {
        var sv = FindDescendantScrollViewer(ActiveItemsView);
        if (ReferenceEquals(sv, _itemsScrollViewer))
        {
            return;
        }

        if (_itemsScrollViewer is not null)
        {
            _itemsScrollViewer.ViewChanged -= OnItemsScrollChanged;
        }

        _itemsScrollViewer = sv;
        if (_itemsScrollViewer is not null)
        {
            _itemsScrollViewer.ViewChanged += OnItemsScrollChanged;
        }
    }

    private void OnPanePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ReportActivated();
        if (QuickActionsLayer is null)
        {
            return;
        }

        try
        {
            var pt = e.GetCurrentPoint(QuickActionsLayer).Position;
            if (pt.X >= 0 && pt.Y >= 0 &&
                pt.X <= QuickActionsLayer.ActualWidth &&
                pt.Y <= QuickActionsLayer.ActualHeight)
            {
                _quickActionAnchorInLayer = pt;
            }
        }
        catch
        {
        }
    }

    /// <summary>名称最多占名称列一半；窗格变宽后自适应。</summary>
    private void SyncNameTextMaxWidth()
    {
        if (ViewModel is null)
        {
            return;
        }

        double nameColW;
        if (ViewModel.IsDetailsView && NameHeaderButton is { ActualWidth: > 1 } header)
        {
            nameColW = header.ActualWidth;
        }
        else if (QuickActionsLayer is { ActualWidth: > 1 } layer)
        {
            nameColW = Math.Max(160, layer.ActualWidth * 0.45);
        }
        else
        {
            return;
        }

        var max = Math.Max(72, nameColW * 0.5);
        if (Math.Abs(ViewModel.Columns.NameTextMaxWidth - max) > 0.5)
        {
            ViewModel.Columns.NameTextMaxWidth = max;
        }
    }

    private void OnItemsScrollChanged(object? sender, ScrollViewerViewChangedEventArgs e) =>
        UpdateQuickActionsBar();

    private void UpdateQuickActionsBar()
    {
        SyncNameTextMaxWidth();

        if (ViewModel is null ||
            QuickActionsBar is null ||
            QuickActionsLayer is null ||
            _renamingItem is not null ||
            ActiveItemsView.SelectedItems.Count == 0)
        {
            if (QuickActionsBar is not null)
            {
                QuickActionsBar.Visibility = Visibility.Collapsed;
            }

            return;
        }

        var item = ActiveItemsView.SelectedItems[^1] as FileListItemViewModel
                   ?? ViewModel.SelectedItem;
        if (item is null)
        {
            QuickActionsBar.Visibility = Visibility.Collapsed;
            return;
        }

        if (ActiveItemsView.ContainerFromItem(item) is not FrameworkElement container ||
            container.ActualHeight <= 0)
        {
            QuickActionsBar.Visibility = Visibility.Collapsed;
            return;
        }

        var layerW = QuickActionsLayer.ActualWidth;
        var layerH = QuickActionsLayer.ActualHeight;
        if (layerW <= 0 || layerH <= 0)
        {
            return;
        }

        QuickActionsBar.Visibility = Visibility.Visible;
        QuickActionsBar.UpdateLayout();

        // 目标高度 ≈ 行高 × 1.3
        var rowH = container.ActualHeight > 1 ? container.ActualHeight : 26;
        var targetBarH = Math.Clamp(rowH * 1.3, 28, 36);
        if (Math.Abs(QuickActionsBar.MaxHeight - targetBarH) > 0.5)
        {
            QuickActionsBar.MaxHeight = targetBarH;
            QuickActionsBar.UpdateLayout();
        }

        var barW = QuickActionsBar.ActualWidth > 1 ? QuickActionsBar.ActualWidth : 168;
        var barH = QuickActionsBar.ActualHeight > 1 ? QuickActionsBar.ActualHeight : targetBarH;

        // 宽度不够放下整条才隐藏（不再用「名称列要有 2×条宽」的保守策略）
        if (layerW < barW + 12)
        {
            QuickActionsBar.Visibility = Visibility.Collapsed;
            return;
        }

        var bounds = container.TransformToVisual(QuickActionsLayer)
            .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));

        if (bounds.Bottom < 0 || bounds.Top > layerH)
        {
            QuickActionsBar.Visibility = Visibility.Collapsed;
            return;
        }

        const double gap = 3;
        var nameRight = MeasureTaggedNameRight(container, QuickActionsLayer) ?? (bounds.Left + Math.Min(bounds.Width * 0.5, ViewModel.Columns.NameTextMaxWidth + 56));
        var prevRight = MeasurePreviousItemContentRight(item, QuickActionsLayer);
        var minLeft = Math.Max(nameRight, prevRight) + gap;

        var preferredX = minLeft;
        if (_quickActionAnchorInLayer is { } anchor &&
            anchor.Y >= bounds.Top - 8 &&
            anchor.Y <= bounds.Bottom + 8)
        {
            preferredX = Math.Max(minLeft, anchor.X + 6);
        }

        // 原则：不与选中行同一行——优先整条在行顶之上；上方不够则整条在行底之下
        var yAbove = bounds.Top - barH - gap;
        var yBelow = bounds.Bottom + gap;
        double preferredY;
        if (yAbove >= 2)
        {
            preferredY = yAbove;
        }
        else if (yBelow + barH <= layerH - 2)
        {
            preferredY = yBelow;
        }
        else
        {
            // 上下都挤：仍尽量避开行带，优先上方贴顶
            preferredY = Math.Max(2, Math.Min(yAbove, layerH - barH - 2));
        }

        var x = preferredX;
        if (x + barW > layerW - 6)
        {
            x = Math.Max(minLeft, layerW - barW - 6);
        }

        if (x + barW > layerW - 4)
        {
            x = Math.Max(4, layerW - barW - 4);
            if (x < minLeft - 40)
            {
                QuickActionsBar.Visibility = Visibility.Collapsed;
                return;
            }
        }

        var y = Math.Clamp(preferredY, 2, Math.Max(2, layerH - barH - 2));
        // 最终校验：禁止与选中行垂直重叠
        if (y < bounds.Bottom && y + barH > bounds.Top)
        {
            y = yAbove >= 2 ? yAbove : yBelow;
            y = Math.Clamp(y, 2, Math.Max(2, layerH - barH - 2));
        }

        Canvas.SetLeft(QuickActionsBar, x);
        Canvas.SetTop(QuickActionsBar, y);
        SyncQuickPinButton(ActiveItemsView.SelectedItems.Count == 1 ? item : null);
    }

    private double MeasurePreviousItemContentRight(FileListItemViewModel item, UIElement layer)
    {
        try
        {
            var list = ViewModel?.VisibleItems;
            if (list is null)
            {
                return 0;
            }

            var idx = list.IndexOf(item);
            if (idx <= 0)
            {
                return 0;
            }

            var prev = list[idx - 1];
            if (ActiveItemsView.ContainerFromItem(prev) is not FrameworkElement prevContainer)
            {
                return 0;
            }

            return MeasureTaggedNameRight(prevContainer, layer)
                   ?? prevContainer.TransformToVisual(layer)
                       .TransformBounds(new Rect(0, 0, prevContainer.ActualWidth, prevContainer.ActualHeight)).Right;
        }
        catch
        {
            return 0;
        }
    }

    private static double? MeasureTaggedNameRight(FrameworkElement container, UIElement layer)
    {
        var nameEl = FindDescendantByTag(container, "ItemName");
        if (nameEl is null)
        {
            return null;
        }

        try
        {
            var r = nameEl.TransformToVisual(layer)
                .TransformBounds(new Rect(0, 0, nameEl.ActualWidth, nameEl.ActualHeight));
            return r.Right;
        }
        catch
        {
            return null;
        }
    }

    private static FrameworkElement? FindDescendantByTag(DependencyObject root, string tag)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && Equals(fe.Tag as string, tag))
            {
                return fe;
            }

            var nested = FindDescendantByTag(child, tag);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void SuppressItemActivateFromExpand() =>
        _suppressItemActivateUntilUtc = DateTime.UtcNow.AddMilliseconds(650);

    private bool ShouldSuppressItemActivate(DependencyObject? source)
    {
        if (DateTime.UtcNow < _suppressItemActivateUntilUtc)
        {
            return true;
        }

        return IsUnderExpandButton(source);
    }

    private static bool IsUnderExpandButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement fe &&
                string.Equals(fe.Name, "ExpandFolderButton", StringComparison.Ordinal))
            {
                return true;
            }

            // 无 Name 时：透明小按钮 + FontIcon 常见结构
            if (source is Button btn &&
                btn.ActualWidth <= 28 &&
                btn.ActualHeight <= 28 &&
                Equals(btn.Tag, "ExpandFolder"))
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void OnExpandButtonPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        SuppressItemActivateFromExpand();
        // 不 Handled：留给 Button 走 Click；仅抢先屏蔽随后的双击进目录
    }

    private void OnExpandButtonTapped(object sender, TappedRoutedEventArgs e)
    {
        SuppressItemActivateFromExpand();
        e.Handled = true;
    }

    private void OnExpandButtonDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        SuppressItemActivateFromExpand();
        e.Handled = true;
    }

    private async void OnExpandFolderClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null ||
            sender is not FrameworkElement { DataContext: FileListItemViewModel item } ||
            !item.IsDirectory)
        {
            return;
        }

        SuppressItemActivateFromExpand();

        // 保持滚动偏移：就地插入/删除子项，禁止 SyncItemsSources 重绑导致跳回顶部
        var offset = _itemsScrollViewer?.VerticalOffset;
        await ViewModel.ToggleExpandAsync(item);
        if (_itemsScrollViewer is not null && offset is not null)
        {
            _itemsScrollViewer.ChangeView(null, offset, null, disableAnimation: true);
        }

        UpdateQuickActionsBar();
    }

    private void OnQuickCutClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var selection = ViewModel.GetSelectedItems(ActiveItemsView.SelectedItems.ToList());
        if (selection.Count == 0)
        {
            return;
        }

        _ = RunOpAsync("剪切", () => ViewModel.CopySelectionAsync(selection, cut: true));
    }

    private void OnQuickCopyClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var selection = ViewModel.GetSelectedItems(ActiveItemsView.SelectedItems.ToList());
        if (selection.Count == 0)
        {
            return;
        }

        _ = RunOpAsync("复制", () => ViewModel.CopySelectionAsync(selection, cut: false));
    }

    private void OnQuickCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var selection = ViewModel.GetSelectedItems(ActiveItemsView.SelectedItems.ToList());
        if (selection.Count == 0 && ViewModel.SelectedItem is { } one)
        {
            selection = [one];
        }

        if (selection.Count == 0)
        {
            return;
        }

        CopyPathsToClipboard(selection);
        RequestActionToast(selection.Count == 1 ? "已复制路径" : $"已复制 {selection.Count} 条路径");
    }

    private void OnQuickPinClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var selection = ViewModel.GetSelectedItems(ActiveItemsView.SelectedItems.ToList());
        if (selection.Count == 0 && ViewModel.SelectedItem is { } one)
        {
            selection = [one];
        }

        if (selection.Count != 1)
        {
            return;
        }

        var item = selection[0];
        var next = !item.IsPinned;
        _ = TogglePinAsync(item, next);
    }

    private async Task TogglePinAsync(FileListItemViewModel item, bool pinned)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.UpsertMetadataAsync(item, r => r.IsPinned = pinned);
        SyncQuickPinButton(item);
        UpdateQuickActionsBar();
        RequestActionToast(pinned ? "已置顶" : "已取消置顶");
    }

    private void SyncQuickPinButton(FileListItemViewModel? item)
    {
        if (QuickPinButton is null || QuickPinIcon is null)
        {
            return;
        }

        var pinned = item?.IsPinned == true;
        QuickPinIcon.Glyph = pinned ? "\uE77A" : "\uE840";
        QuickPinIcon.Foreground = new SolidColorBrush(
            pinned
                ? Windows.UI.Color.FromArgb(255, 0, 120, 212)
                : Windows.UI.Color.FromArgb(255, 202, 80, 16));
        ToolTipService.SetToolTip(QuickPinButton, pinned ? "取消置顶" : "置顶");
        QuickPinButton.IsEnabled = item is not null;
    }

    private void OnQuickPropertiesClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var selection = ViewModel.GetSelectedItems(ActiveItemsView.SelectedItems.ToList());
        if (selection.Count == 0 && ViewModel.SelectedItem is { } one)
        {
            selection = [one];
        }

        if (selection.Count == 0)
        {
            return;
        }

        _ = ShowPropertiesAsync(selection);
    }

    private void OnQuickRenameClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var selection = ViewModel.GetSelectedItems(ActiveItemsView.SelectedItems.ToList());
        if (selection.Count == 0)
        {
            return;
        }

        BeginInlineRename(selection[0]);
    }

    private void OnQuickDeleteClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var selection = ViewModel.GetSelectedItems(ActiveItemsView.SelectedItems.ToList());
        if (selection.Count == 0)
        {
            return;
        }

        _ = ConfirmAndDeleteAsync(selection);
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv)
            {
                return sv;
            }

            var nested = FindDescendantScrollViewer(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private void OnFileContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is ListViewItem container)
        {
            container.DragStarting -= OnListItemDragStarting;
            if (!args.InRecycleQueue)
            {
                container.DragStarting += OnListItemDragStarting;
            }
        }

        if (args.InRecycleQueue || ViewModel is null || args.Item is not FileListItemViewModel item)
        {
            return;
        }

        // 可见行再加载壳图标，上千项时避免一次性打满 IO/CPU。
        if (!item.HasBitmapIcon)
        {
            _ = ViewModel.RequestIconAsync(item);
        }
    }

    private void OnListRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var view = ActiveItemsView;
        var hit = FindListItem(e.OriginalSource as DependencyObject);
        if (hit is not null)
        {
            if (!view.SelectedItems.Contains(hit))
            {
                view.SelectedItem = hit;
            }
        }
        else
        {
            // 右键空白处：清空选中，避免菜单仍针对旧选中项
            view.SelectedItems.Clear();
        }

        var selection = ViewModel.GetSelectedItems(view.SelectedItems.ToList());

        // Shift+右键：直接显示系统 Shell 菜单；普通右键保持 AIExplorer 的清晰基础菜单。
        var shift = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (shift && selection.Count > 0 &&
            TryShowShellFlyout(selection, view, e.GetPosition(view)))
        {
            e.Handled = true;
            return;
        }

        ShowAppContextFlyout(selection, view, e.GetPosition(view));
        e.Handled = true;
    }

    private bool TryShowShellFlyout(
        IReadOnlyList<FileListItemViewModel> selection,
        FrameworkElement target,
        Windows.Foundation.Point position)
    {
        try
        {
            var shell = App.Services.GetService<IShellContextMenuService>();
            if (shell is null)
            {
                return false;
            }

            var session = shell.Create(selection.Select(s => s.FullPath).ToList());
            if (session is null || session.Items.Count == 0)
            {
                session?.Dispose();
                return false;
            }

            var flyout = new MenuFlyout();
            // Closed 可能先于 Click 触发，立即 Dispose 会让点中的命令失效，必须延迟释放。
            flyout.Closed += (_, _) => _ = DelayedDisposeAsync(session);

            foreach (var item in session.Items)
            {
                flyout.Items.Add(CreateShellFlyoutItem(item, session, flyout));
            }

            flyout.Items.Add(new MenuFlyoutSeparator());
            var more = new MenuFlyoutItem { Text = "AIExplorer 工具…" };
            more.Click += (_, _) => ShowAppContextFlyout(selection, target, position);
            flyout.Items.Add(more);

            flyout.ShowAt(target, position);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task DelayedDisposeAsync(IShellContextMenuSession session)
    {
        await Task.Delay(TimeSpan.FromSeconds(15));
        try
        {
            session.Dispose();
        }
        catch
        {
        }
    }

    /// <summary>
    /// 用 ShellExecuteEx 调用 verb。properties 必须带 SEE_MASK_INVOKEIDLIST，
    /// Process.Start(Verb=properties) 在 WinUI 进程里会静默失败。
    /// </summary>
    private void InvokeShellVerb(IReadOnlyList<FileListItemViewModel> selection, string verb)
    {
        if (selection.Count == 0)
        {
            return;
        }

        if (string.Equals(verb, "properties", StringComparison.OrdinalIgnoreCase))
        {
            _ = ShowPropertiesAsync(selection);
            return;
        }

        foreach (var item in selection)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = item.FullPath,
                    Verb = verb,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(item.FullPath) is { Length: > 0 } dir
                        ? dir
                        : Environment.CurrentDirectory,
                });
            }
            catch (Exception ex)
            {
                _ = ShowMessageAsync("操作失败", ex.Message);
            }
        }
    }

    private async Task ShowPropertiesAsync(IReadOnlyList<FileListItemViewModel> selection)
    {
        if (selection.Count == 0)
        {
            return;
        }

        var paths = selection.Select(x => x.FullPath).ToList();
        if (!ShellVerb.ShowProperties(paths))
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            await ShowMessageAsync(
                "属性",
                selection.Count == 1
                    ? $"无法打开属性对话框（错误码 {err}）：\n{paths[0]}"
                    : $"无法打开多选属性对话框（错误码 {err}）。");
        }
    }

    private async Task<FileConflictDecision> AskFileConflictAsync(FileConflictPrompt prompt)
    {
        var kind = prompt.IsDirectory ? "文件夹" : "文件";
        var glyph = prompt.IsDirectory ? "\uE8B7" : "\uE7C3";
        var progress = prompt.ConflictTotal > 1
            ? $"冲突 {prompt.ConflictIndex} / {prompt.ConflictTotal}"
            : "目标位置已有同名项";

        var applyAll = new CheckBox
        {
            Content = "对全部冲突使用相同操作",
            IsChecked = false,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var nameRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 4, 0, 0),
        };
        nameRow.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 22,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["AppAccentBrush"],
        });
        nameRow.Children.Add(new TextBlock
        {
            Text = prompt.DisplayName,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 360,
        });

        var hint = new TextBlock
        {
            Text = $"此{kind}将如何处理？替换会覆盖目标；跳过保留目标；保留两者将自动重命名。",
            Opacity = 0.72,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 8),
        };

        FileConflictDecision? chosen = null;
        var dialog = new ContentDialog
        {
            Title = "粘贴冲突",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        Button MakeAction(string label, FileConflictAction action, bool primary = false)
        {
            var btn = new Button
            {
                Content = label,
                MinWidth = 88,
                Padding = new Thickness(14, 8, 14, 8),
                CornerRadius = new CornerRadius(6),
            };
            if (primary &&
                Application.Current.Resources.TryGetValue("AccentButtonStyle", out var styleObj) &&
                styleObj is Style accentStyle)
            {
                btn.Style = accentStyle;
            }

            btn.Click += (_, _) =>
            {
                chosen = new FileConflictDecision
                {
                    Action = action,
                    ApplyToAll = applyAll.IsChecked == true,
                };
                dialog.Hide();
            };
            return btn;
        }

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        actions.Children.Add(MakeAction("替换", FileConflictAction.Replace, primary: true));
        actions.Children.Add(MakeAction("跳过", FileConflictAction.Skip));
        actions.Children.Add(MakeAction("保留两者", FileConflictAction.Rename));

        var panel = new StackPanel { Spacing = 6, MinWidth = 420 };
        panel.Children.Add(new TextBlock
        {
            Text = progress,
            Opacity = 0.65,
            FontSize = 12,
        });
        panel.Children.Add(nameRow);
        panel.Children.Add(hint);
        panel.Children.Add(applyAll);
        panel.Children.Add(actions);
        dialog.Content = panel;

        await dialog.ShowAsync();
        return chosen ?? new FileConflictDecision { Action = FileConflictAction.CancelAll };
    }

    private static void CreateShortcutBeside(IReadOnlyList<FileListItemViewModel> selection)
    {
        foreach (var item in selection)
        {
            try
            {
                var dir = Path.GetDirectoryName(item.FullPath);
                if (string.IsNullOrEmpty(dir))
                {
                    continue;
                }

                var baseName = Path.GetFileNameWithoutExtension(item.FullPath);
                if (string.IsNullOrEmpty(baseName))
                {
                    baseName = item.Name;
                }

                var linkPath = Path.Combine(dir, $"{baseName} - 快捷方式.lnk");
                for (var i = 2; File.Exists(linkPath); i++)
                {
                    linkPath = Path.Combine(dir, $"{baseName} - 快捷方式 ({i}).lnk");
                }

                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType is null)
                {
                    continue;
                }

                dynamic wsh = Activator.CreateInstance(shellType)!;
                var shortcut = wsh.CreateShortcut(linkPath);
                shortcut.TargetPath = item.FullPath;
                shortcut.WorkingDirectory = dir;
                shortcut.Save();
            }
            catch
            {
            }
        }
    }

    /// <summary>统一执行文件操作：失败时弹窗提示，而不是静默失败。</summary>
    private async Task RunOpAsync(string opName, Func<Task> op)
    {
        try
        {
            await op();
            var toast = opName switch
            {
                "复制" => "已复制",
                "剪切" => "已剪切",
                _ => null,
            };
            if (toast is not null)
            {
                RequestActionToast(toast);
            }
        }
        catch (Exception ex)
        {
            RequestActionToast($"{opName}失败");
            await ShowMessageAsync($"{opName}失败", ex.Message);
        }
    }

    private async Task ConfirmAndDeleteAsync(IReadOnlyList<FileListItemViewModel> selection)
    {
        if (ViewModel is null || selection.Count == 0)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = BuildDeleteDialogTitle(selection),
            Content = BuildDeleteDialogContent(selection),
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        // 危险操作：主按钮用强调红，贴近快捷菜单配色
        dialog.PrimaryButtonStyle = CreateDeletePrimaryButtonStyle();

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await RunOpAsync("删除", () => ViewModel.DeleteSelectionAsync(selection));
            ActiveItemsView.Focus(FocusState.Programmatic);
        }
    }

    private static string BuildDeleteDialogTitle(IReadOnlyList<FileListItemViewModel> selection)
    {
        if (selection.Count == 1)
        {
            return selection[0].IsDirectory ? "删除文件夹" : "删除文件";
        }

        return "删除多个项目";
    }

    private static UIElement BuildDeleteDialogContent(IReadOnlyList<FileListItemViewModel> selection)
    {
        var root = new StackPanel { Spacing = 14, MinWidth = 320, MaxWidth = 420 };

        var prompt = new TextBlock
        {
            Text = selection.Count == 1
                ? (selection[0].IsDirectory
                    ? "确实要删除此文件夹吗？"
                    : "确实要删除此文件吗？")
                : $"确实要删除这 {selection.Count} 个项目吗？",
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        };
        root.Children.Add(prompt);

        if (selection.Count == 1)
        {
            root.Children.Add(BuildSingleDeleteCard(selection[0]));
        }
        else
        {
            root.Children.Add(BuildMultiDeleteList(selection));
        }

        return root;
    }

    private static Border BuildSingleDeleteCard(FileListItemViewModel item)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconGrid = new Grid();
        iconGrid.Children.Add(new FontIcon
        {
            Glyph = item.IconGlyph,
            FontSize = 22,
            Foreground = item.GlyphBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = item.HasBitmapIcon ? Visibility.Collapsed : Visibility.Visible,
        });
        iconGrid.Children.Add(new Image
        {
            Source = item.IconImage,
            Width = 24,
            Height = 24,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = item.HasBitmapIcon ? Visibility.Visible : Visibility.Collapsed,
        });

        var iconHost = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            Child = iconGrid,
        };
        Grid.SetColumn(iconHost, 0);
        row.Children.Add(iconHost);

        var meta = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        meta.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
            TextWrapping = TextWrapping.Wrap,
        });
        meta.Children.Add(MakeMetaLine($"类型: {item.TypeText}"));
        if (!string.IsNullOrWhiteSpace(item.SizeText))
        {
            meta.Children.Add(MakeMetaLine($"大小: {item.SizeText}"));
        }

        meta.Children.Add(MakeMetaLine($"修改日期: {item.ModifiedText}"));
        Grid.SetColumn(meta, 1);
        row.Children.Add(meta);

        return new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorSecondaryBrush"],
            Child = row,
        };
    }

    private static Border BuildMultiDeleteList(IReadOnlyList<FileListItemViewModel> selection)
    {
        var list = new StackPanel { Spacing = 6 };
        foreach (var item in selection.Take(6))
        {
            var line = new Grid { ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.Children.Add(new FontIcon
            {
                Glyph = item.IconGlyph,
                FontSize = 14,
                Foreground = item.GlyphBrush,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var name = new TextBlock
            {
                Text = item.Name,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 1);
            line.Children.Add(name);
            list.Children.Add(line);
        }

        if (selection.Count > 6)
        {
            list.Children.Add(MakeMetaLine($"…以及其他 {selection.Count - 6} 项"));
        }

        return new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorSecondaryBrush"],
            Child = list,
        };
    }

    private static TextBlock MakeMetaLine(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Opacity = 0.72,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static Style CreateDeletePrimaryButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28))));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Microsoft.UI.Colors.White)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28))));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(4)));
        return style;
    }

    private static MenuFlyoutItemBase CreateShellFlyoutItem(
        ShellMenuItemDto item,
        IShellContextMenuSession session,
        MenuFlyout? ownerFlyout)
    {
        if (item.IsSeparator)
        {
            return new MenuFlyoutSeparator();
        }

        if (item.Children.Count > 0)
        {
            var sub = new MenuFlyoutSubItem
            {
                Text = item.Text,
                IsEnabled = item.IsEnabled,
            };
            foreach (var child in item.Children)
            {
                sub.Items.Add(CreateShellFlyoutItem(child, session, ownerFlyout));
            }

            return sub;
        }

        var menuItem = new MenuFlyoutItem
        {
            Text = item.Text,
            IsEnabled = item.IsEnabled && (item.Id >= 0 || !string.IsNullOrWhiteSpace(item.Verb)),
        };
        var id = item.Id;
        var verb = item.Verb;
        menuItem.Click += (_, _) =>
        {
            // 先关菜单再 Invoke：避免 Popup 未关时 Shell 扩展拿不到正确 owner / 被挡住
            try
            {
                ownerFlyout?.Hide();
            }
            catch
            {
            }

            void Run()
            {
                try
                {
                    var hwnd = App.WindowHandle;
                    if (!string.IsNullOrWhiteSpace(verb))
                    {
                        session.InvokeVerb(verb, hwnd);
                    }
                    else if (id >= 0)
                    {
                        session.Invoke(id, hwnd);
                    }
                }
                catch
                {
                }
            }

            var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dq is null || !dq.TryEnqueue(Run))
            {
                Run();
            }
        };
        return menuItem;
    }

    private void ShowAppContextFlyout(
        IReadOnlyList<FileListItemViewModel> selection,
        FrameworkElement target,
        Windows.Foundation.Point position)
    {
        if (ViewModel is null)
        {
            return;
        }

        var flyout = new MenuFlyout();

        // text 带 (X) 仅作展示；AccessKey + AccessKeyInvoked 才是菜单打开后按字母触发
        static MenuFlyoutItem Item(string text, string glyph, bool enabled = true, string? accel = null, string? accessKey = null)
        {
            var item = new MenuFlyoutItem
            {
                Text = text,
                Icon = new FontIcon { Glyph = glyph },
                IsEnabled = enabled,
            };
            if (!string.IsNullOrEmpty(accel))
            {
                item.KeyboardAcceleratorTextOverride = accel;
            }

            if (!string.IsNullOrEmpty(accessKey))
            {
                item.AccessKey = accessKey;
            }

            return item;
        }

        static void OnInvoke(MenuFlyoutItem item, Action action)
        {
            item.Tag = action;
            item.Click += (_, _) => action();
            // AccessKey（Alt+字母）按下时走 AccessKeyInvoked，不能只靠 Click
            item.AccessKeyInvoked += (_, e) =>
            {
                e.Handled = true;
                action();
            };
        }

        // —— 布局对齐系统资源管理器：打开 | 剪切/复制/粘贴 | 快捷方式/删除/重命名 | 扩展 | 属性 ——

        var refresh = Item("刷新(E)", "\uE72C", true, "F5", "E");
        OnInvoke(refresh, () =>
        {
            if (ViewModel is not null)
            {
                _ = ViewModel.RefreshCommand.ExecuteAsync(null);
            }
        });
        flyout.Items.Add(refresh);

        var undoDelete = Item("撤销删除(U)", "\uE7A7", ViewModel.CanUndoDelete, "Ctrl+Z", "U");
        OnInvoke(undoDelete, () => _ = RunOpAsync("撤销删除", () => ViewModel!.UndoLastDeleteAsync()));
        flyout.Items.Add(undoDelete);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var open = Item("打开(O)", "\uE8E5", selection.Count == 1, "Enter", "O");
        OnInvoke(open, () =>
        {
            if (selection.Count == 1)
            {
                _ = RunOpAsync("打开", () => ViewModel!.HandleItemActivatedAsync(selection[0]));
            }
        });
        flyout.Items.Add(open);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var cut = Item("剪切(T)", "\uE8C6", selection.Count > 0, "Ctrl+X", "T");
        OnInvoke(cut, () => _ = RunOpAsync("剪切", () => ViewModel!.CopySelectionAsync(selection, cut: true)));
        flyout.Items.Add(cut);

        var copy = Item("复制(C)", "\uE8C8", selection.Count > 0, "Ctrl+C", "C");
        OnInvoke(copy, () => _ = RunOpAsync("复制", () => ViewModel!.CopySelectionAsync(selection, cut: false)));
        flyout.Items.Add(copy);

        var paste = Item("粘贴(P)", "\uE77F", true, "Ctrl+V", "P");
        OnInvoke(paste, () => _ = PasteFromUiAsync());
        flyout.Items.Add(paste);

        var newFolder = Item("新建文件夹(N)", "\uE8B7", true, accessKey: "N");
        OnInvoke(newFolder, () => _ = CreateNewFolderFromUiAsync());
        flyout.Items.Add(newFolder);

        var copyPath = Item("复制路径(A)", "\uE71B", selection.Count > 0, "Ctrl+Shift+C", "A");
        OnInvoke(copyPath, () =>
        {
            CopyPathsToClipboard(selection);
            RequestActionToast(selection.Count == 1 ? "已复制路径" : $"已复制 {selection.Count} 条路径");
        });
        flyout.Items.Add(copyPath);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var shortcut = Item("创建快捷方式(S)", "\uE71B", selection.Count > 0, accessKey: "S");
        OnInvoke(shortcut, () =>
        {
            CreateShortcutBeside(selection);
            _ = ViewModel!.LoadAsync();
        });
        flyout.Items.Add(shortcut);

        var del = Item("删除(D)", "\uE74D", selection.Count > 0, "Delete", "D");
        OnInvoke(del, () => _ = ConfirmAndDeleteAsync(selection));
        flyout.Items.Add(del);

        var rename = Item("重命名(M)", "\uE8AC", selection.Count == 1, "F2", "M");
        OnInvoke(rename, () =>
        {
            if (selection.Count == 1)
            {
                _ = RunOpAsync("重命名", () => RenameItemAsync(selection[0]));
            }
        });
        flyout.Items.Add(rename);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var organize = new MenuFlyoutSubItem
        {
            Text = "整理与标记",
            Icon = new FontIcon { Glyph = "\uE8EC" },
            IsEnabled = selection.Count == 1,
        };
        if (selection.Count == 1)
        {
            var selected = selection[0];
            var pin = Item(selected.IsPinned ? "取消置顶" : "置顶", "\uE840");
            pin.Click += async (_, _) =>
            {
                var next = !selected.IsPinned;
                await ViewModel.UpsertMetadataAsync(selected, r => r.IsPinned = next);
            };
            organize.Items.Add(pin);

            var colors = new MenuFlyoutSubItem { Text = "颜色标记" };
            var noneItem = new MenuFlyoutItem
            {
                Text = "无",
                Icon = new FontIcon { Glyph = "\uECCA", FontSize = 12 },
            };
            noneItem.Click += async (_, _) =>
                await ViewModel.UpsertMetadataAsync(selected, r => r.ColorKey = null);
            colors.Items.Add(noneItem);

            var palette = App.Services.GetRequiredService<ISettingsService>().FileColors;
            foreach (var def in palette)
            {
                var brush = ResolveColorBrush(def.Hex);
                var colorItem = new MenuFlyoutItem
                {
                    Text = def.DisplayName,
                    Icon = new FontIcon
                    {
                        Glyph = "\uEA3A",
                        FontSize = 14,
                        Foreground = brush,
                    },
                };
                ToolTipService.SetToolTip(colorItem, FileColorPalette.Tooltip(def));
                var colorKey = def.Key;
                colorItem.Click += async (_, _) =>
                    await ViewModel.UpsertMetadataAsync(selected, r => r.ColorKey = colorKey);
                colors.Items.Add(colorItem);
            }

            organize.Items.Add(colors);

            var note = Item("备注…", "\uE70B");
            note.Click += async (_, _) => await EditNoteAsync(selected);
            organize.Items.Add(note);

            if (selected.IsDirectory)
            {
                var calc = Item(
                    selected.IsComputingFolderSize ? "正在计算大小…" : "计算文件夹大小",
                    "\uE9D9",
                    enabled: !selected.IsComputingFolderSize);
                calc.Click += (_, _) => ViewModel.RequestComputeFolderSizes([selected]);
                organize.Items.Add(calc);
            }
        }
        flyout.Items.Add(organize);

        var tools = new MenuFlyoutSubItem
        {
            Text = "工具",
            Icon = new FontIcon { Glyph = "\uE90F" },
        };
        if (selection.Count == 1 && !selection[0].IsDirectory)
        {
            var hash = Item("计算 SHA256", "\uE950");
            hash.Click += async (_, _) => await ShowHashAsync(selection[0].FullPath);
            tools.Items.Add(hash);
        }

        if (selection.Count == 2 && selection.All(s => !s.IsDirectory))
        {
            var cmp = Item("比对这两个文件", "\uE8AB");
            cmp.Click += async (_, _) =>
            {
                var result = await FilePaneViewModel.CompareFilesAsync(selection[0].FullPath, selection[1].FullPath);
                await ShowMessageAsync("文件比对", result);
            };
            tools.Items.Add(cmp);
        }

        var empty = Item("清理空文件夹…", "\uE74D");
        empty.Click += async (_, _) => await CleanupEmptyFoldersAsync();
        tools.Items.Add(empty);
        flyout.Items.Add(tools);

        // 系统菜单：必须在 ShowAt 前填好 SubItem.Items。
        // WinUI 在 SubItem 已展开后再 Clear/Add 不会刷新（第一次只见「加载中」），点击也会点到失效项。
        if (selection.Count > 0)
        {
            flyout.Items.Add(BuildShellSubMenu(selection, flyout));
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        var props = Item("属性(R)", "\uE946", selection.Count > 0, "Alt+Enter", "R");
        OnInvoke(props, () =>
        {
            // 先关菜单再弹属性，避免 Popup 尚未关闭时属性窗被挡住/创建失败
            flyout.Hide();
            var paths = selection.ToList();
            DispatcherQueue.TryEnqueue(() => InvokeShellVerb(paths, "properties"));
        });
        flyout.Items.Add(props);

        // 资源管理器风格：菜单打开后直接按字母（不必先 Alt）
        var mnemonics = new Dictionary<VirtualKey, MenuFlyoutItem>
        {
            [VirtualKey.E] = refresh,
            [VirtualKey.U] = undoDelete,
            [VirtualKey.O] = open,
            [VirtualKey.T] = cut,
            [VirtualKey.C] = copy,
            [VirtualKey.P] = paste,
            [VirtualKey.A] = copyPath,
            [VirtualKey.S] = shortcut,
            [VirtualKey.D] = del,
            [VirtualKey.M] = rename,
            [VirtualKey.R] = props,
        };
        AttachMenuMnemonics(flyout, mnemonics);

        flyout.ShowAt(target, position);
    }

    /// <summary>
    /// 菜单打开后，把字母键路由到对应项（对齐系统资源管理器助记键，无需 Alt）。
    /// Popup 内 KeyDown 不可靠，改用各菜单项的 CharacterReceived。
    /// </summary>
    private static void AttachMenuMnemonics(MenuFlyout flyout, IReadOnlyDictionary<VirtualKey, MenuFlyoutItem> map)
    {
        var byChar = new Dictionary<char, MenuFlyoutItem>();
        foreach (var (key, item) in map)
        {
            if (key is >= VirtualKey.A and <= VirtualKey.Z)
            {
                byChar[(char)('A' + (key - VirtualKey.A))] = item;
            }
        }

        TypedEventHandler<UIElement, CharacterReceivedRoutedEventArgs> handler = (_, e) =>
        {
            var ch = char.ToUpperInvariant(e.Character);
            if (!byChar.TryGetValue(ch, out var item) || !item.IsEnabled || item.Tag is not Action action)
            {
                return;
            }

            e.Handled = true;
            flyout.Hide();
            action();
        };

        void AttachTo(MenuFlyoutItemBase entry)
        {
            if (entry is MenuFlyoutItem item)
            {
                item.CharacterReceived += handler;
            }
            else if (entry is MenuFlyoutSubItem sub)
            {
                foreach (var child in sub.Items)
                {
                    AttachTo(child);
                }
            }
        }

        foreach (var entry in flyout.Items)
        {
            AttachTo(entry);
        }
    }

    /// <summary>
    /// 构建「系统菜单」二级项：在 ShowAt 前同步 QueryContextMenu 并填满 Items。
    /// </summary>
    private MenuFlyoutSubItem BuildShellSubMenu(
        IReadOnlyList<FileListItemViewModel> selection,
        MenuFlyout ownerFlyout)
    {
        var sub = new MenuFlyoutSubItem
        {
            Text = "系统菜单",
            Icon = new FontIcon { Glyph = "\uE770" },
        };

        try
        {
            var shell = App.Services.GetService<IShellContextMenuService>();
            var session = shell?.Create(selection.Select(s => s.FullPath).ToList());
            if (session is null || session.Items.Count == 0)
            {
                session?.Dispose();
                sub.Items.Add(new MenuFlyoutItem { Text = "（无可用项）", IsEnabled = false });
                return sub;
            }

            // Closed 可能先于 Click，延迟 Dispose，否则 Invoke 会打到已释放的 IContextMenu
            ownerFlyout.Closed += (_, _) => _ = DelayedDisposeAsync(session);
            foreach (var item in session.Items)
            {
                sub.Items.Add(CreateShellFlyoutItem(item, session, ownerFlyout));
            }
        }
        catch
        {
            sub.Items.Add(new MenuFlyoutItem { Text = "加载失败", IsEnabled = false });
        }

        return sub;
    }

    private void CopyPathsToClipboard(IReadOnlyList<FileListItemViewModel> selection)
    {
        if (ViewModel is null || selection.Count == 0)
        {
            return;
        }

        var paths = selection
            .Select(s => ViewModel.GetPathForClipboard(s.FullPath))
            .ToList();
        ViewModel.CopyPathsAsText(paths);
    }

    private TextBox? _inlineRenameBox;
    private FileListItemViewModel? _renamingItem;
    private bool _renameCommitGuard;
    private FrameworkElement? _hiddenNameElement;

    private void BeginInlineRename(FileListItemViewModel item)
    {
        if (ViewModel is null)
        {
            return;
        }

        EndInlineRename(commit: false);

        // 确保项已选中且容器已生成
        if (!ActiveItemsView.SelectedItems.Contains(item))
        {
            ActiveItemsView.SelectedItem = item;
        }

        QuickActionsBar.Visibility = Visibility.Collapsed;

        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            if (ActiveItemsView.ContainerFromItem(item) is not FrameworkElement container)
            {
                // 回退：容器未就绪时仍用对话框
                _ = RenameItemWithDialogAsync(item);
                return;
            }

            var nameEl = FindNameTextElement(container, item.Name);
            if (nameEl is null)
            {
                _ = RenameItemWithDialogAsync(item);
                return;
            }

            _renamingItem = item;
            _hiddenNameElement = nameEl;
            nameEl.Opacity = 0;

            var box = new TextBox
            {
                Text = item.Name,
                FontSize = nameEl is TextBlock tb ? tb.FontSize : 13,
                MinHeight = Math.Max(24, nameEl.ActualHeight),
                MinWidth = Math.Max(140, Math.Min(360, nameEl.ActualWidth + 24)),
                MaxWidth = Math.Max(200, QuickActionsLayer.ActualWidth - 24),
                Padding = new Thickness(4, 2, 4, 2),
                AcceptsReturn = false,
            };

            // 选中主文件名（不含扩展名），对齐资源管理器
            var extLen = item.IsDirectory ? 0 : Path.GetExtension(item.Name).Length;
            box.SelectionStart = 0;
            box.SelectionLength = Math.Max(0, item.Name.Length - extLen);

            box.KeyDown += OnInlineRenameKeyDown;
            box.LostFocus += OnInlineRenameLostFocus;

            _inlineRenameBox = box;
            QuickActionsLayer.Children.Add(box);

            var bounds = nameEl.TransformToVisual(QuickActionsLayer)
                .TransformBounds(new Rect(0, 0, Math.Max(nameEl.ActualWidth, 80), Math.Max(nameEl.ActualHeight, 20)));
            var x = Math.Clamp(bounds.Left - 2, 4, Math.Max(4, QuickActionsLayer.ActualWidth - box.MinWidth - 4));
            var y = Math.Clamp(bounds.Top - 2, 2, Math.Max(2, QuickActionsLayer.ActualHeight - 28));
            Canvas.SetLeft(box, x);
            Canvas.SetTop(box, y);

            box.Focus(FocusState.Programmatic);
        });
    }

    private void OnInlineRenameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            EndInlineRename(commit: true);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            EndInlineRename(commit: false);
        }
    }

    private void OnInlineRenameLostFocus(object sender, RoutedEventArgs e)
    {
        // 下一拍提交，避免 Enter 触发的失焦与 KeyDown 双重提交
        _ = DispatcherQueue.TryEnqueue(() => EndInlineRename(commit: true));
    }

    private void EndInlineRename(bool commit)
    {
        if (_renameCommitGuard)
        {
            return;
        }

        var box = _inlineRenameBox;
        var item = _renamingItem;
        if (box is null && item is null)
        {
            return;
        }

        _renameCommitGuard = true;
        try
        {
            if (box is not null)
            {
                box.KeyDown -= OnInlineRenameKeyDown;
                box.LostFocus -= OnInlineRenameLostFocus;
                QuickActionsLayer.Children.Remove(box);
            }

            if (_hiddenNameElement is not null)
            {
                _hiddenNameElement.Opacity = 1;
                _hiddenNameElement = null;
            }

            _inlineRenameBox = null;
            _renamingItem = null;

            if (commit && item is not null && box is not null)
            {
                var name = (box.Text ?? string.Empty).Trim();
                _ = ApplyRenameAsync(item, name);
            }
            else
            {
                UpdateQuickActionsBar();
            }
        }
        finally
        {
            _renameCommitGuard = false;
        }
    }

    private async Task ApplyRenameAsync(FileListItemViewModel item, string name)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(name) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            string.Equals(name, item.Name, StringComparison.Ordinal))
        {
            UpdateQuickActionsBar();
            return;
        }

        var parent = Path.GetDirectoryName(item.FullPath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return;
        }

        var destination = Path.Combine(parent, name);
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            await ShowMessageAsync("重命名", "目标名称已经存在。");
            UpdateQuickActionsBar();
            return;
        }

        try
        {
            // 先抑制 watcher，再改名，最后就地更新列表（避免 LoadAsync + watcher 双刷）
            ViewModel.SuppressWatcherBriefly();
            if (item.IsDirectory)
            {
                Directory.Move(item.FullPath, destination);
            }
            else
            {
                File.Move(item.FullPath, destination);
            }

            ViewModel.ApplyLocalRename(item, destination);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("重命名失败", ex.Message);
        }
        finally
        {
            UpdateQuickActionsBar();
        }
    }

    private static FrameworkElement? FindNameTextElement(DependencyObject root, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock { Text: { } text } tb &&
                string.Equals(text, name, StringComparison.Ordinal))
            {
                return tb;
            }

            var nested = FindNameTextElement(child, name);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private async Task RenameItemAsync(FileListItemViewModel item)
    {
        // 优先行内编辑；仅在无法定位名称控件时回退弹窗
        BeginInlineRename(item);
        await Task.CompletedTask;
    }

    private async Task RenameItemWithDialogAsync(FileListItemViewModel item)
    {
        if (ViewModel is null)
        {
            return;
        }

        var box = new TextBox
        {
            Header = "新名称",
            Text = item.Name,
            SelectionStart = 0,
            SelectionLength = item.IsDirectory
                ? item.Name.Length
                : Math.Max(0, item.Name.Length - Path.GetExtension(item.Name).Length),
        };
        var dialog = new ContentDialog
        {
            Title = "重命名",
            Content = box,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ApplyRenameAsync(item, box.Text.Trim());
    }

    private async Task EditNoteAsync(FileListItemViewModel item)
    {
        if (ViewModel is null)
        {
            return;
        }

        var box = new TextBox { Text = item.Note ?? string.Empty, PlaceholderText = "备注" };
        var dialog = new ContentDialog
        {
            Title = "编辑备注",
            Content = box,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.UpsertMetadataAsync(item, r => r.Note = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim());
        }
    }

    private async Task ShowHashAsync(string path)
    {
        try
        {
            var hash = await FilePaneViewModel.ComputeFileHashAsync(path);
            await ShowMessageAsync("SHA256", $"{Path.GetFileName(path)}\n{hash}");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("SHA256", $"计算失败：{ex.Message}");
        }
    }

    private async Task CleanupEmptyFoldersAsync()
    {
        if (ViewModel is null)
        {
            return;
        }

        var empty = await ViewModel.FindEmptyFoldersAsync();
        if (empty.Count == 0)
        {
            await ShowMessageAsync("清理空文件夹", "当前目录下没有空文件夹。");
            return;
        }

        var preview = string.Join(Environment.NewLine, empty.Take(20));
        if (empty.Count > 20)
        {
            preview += $"\n…共 {empty.Count} 个";
        }

        var dialog = new ContentDialog
        {
            Title = $"删除 {empty.Count} 个空文件夹？",
            Content = new ScrollViewer
            {
                Content = new TextBlock { Text = preview, TextWrapping = TextWrapping.Wrap },
                MaxHeight = 320,
            },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        foreach (var dir in empty.OrderByDescending(d => d.Length))
        {
            try
            {
                Directory.Delete(dir);
            }
            catch
            {
            }
        }

        await ViewModel.RefreshCommand.ExecuteAsync(null);
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                MaxHeight = 360,
            },
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private async void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (ctrl && e.Key == VirtualKey.A)
        {
            ActiveItemsView.SelectAll();
            e.Handled = true;
            return;
        }

        var alt = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        var selection = ViewModel.GetSelectedItems(ActiveItemsView.SelectedItems.ToList());
        if (ctrl && shift && e.Key == VirtualKey.C)
        {
            CopyPathsToClipboard(selection);
            e.Handled = true;
        }
        // Ctrl+C / X / V 由控件级 KeyboardAccelerator 统一处理，避免与 KeyDown 双触发粘贴两次
        else if (e.Key == VirtualKey.Delete)
        {
            await ConfirmAndDeleteAsync(selection);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.F2 && selection.Count == 1)
        {
            await RenameItemAsync(selection[0]);
            e.Handled = true;
        }
        else if (alt && e.Key == VirtualKey.Enter && selection.Count > 0)
        {
            InvokeShellVerb(selection, "properties");
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter && selection.Count > 0)
        {
            await ViewModel.HandleItemActivatedAsync(selection[0]);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Space && selection.Count == 1)
        {
            await ShowQuickPreviewAsync(selection[0]);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Back && string.IsNullOrEmpty(ViewModel.FilterText))
        {
            await ViewModel.GoUpCommand.ExecuteAsync(null);
            e.Handled = true;
        }
        else if (!ctrl && e.Key >= VirtualKey.A && e.Key <= VirtualKey.Z)
        {
            // type-to-locate：字母键追加到筛选框
            var ch = (char)('a' + (e.Key - VirtualKey.A));
            ViewModel.FilterText += ch;
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape && !string.IsNullOrEmpty(ViewModel.FilterText))
        {
            ViewModel.FilterText = string.Empty;
            e.Handled = true;
        }
    }

    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var items = e.Items.OfType<FileListItemViewModel>().ToList();
        if (items.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        _dragItems = items;
        e.Data.SetText(string.Join(Environment.NewLine, items.Select(i => i.FullPath)));
        e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        if (QuickActionsBar is not null)
        {
            QuickActionsBar.Visibility = Visibility.Collapsed;
        }

        // DragStarting 未挂上时的兜底：异步生成精简预览，供后续 DragOver 使用
        if (_dragPreviewImage is null)
        {
            _ = EnsureDragPreviewAsync(items);
        }
    }

    private async Task EnsureDragPreviewAsync(IReadOnlyList<FileListItemViewModel> items)
    {
        try
        {
            _dragPreviewImage ??= await CreateDragPreviewBitmapAsync(items);
        }
        catch
        {
        }
    }

    private async void OnListItemDragStarting(UIElement sender, DragStartingEventArgs e)
    {
        var items = ActiveItemsView.SelectedItems.OfType<FileListItemViewModel>().ToList();
        if (items.Count == 0 &&
            sender is ListViewItem { Content: FileListItemViewModel one })
        {
            items = [one];
        }

        if (items.Count == 0)
        {
            return;
        }

        _dragItems = items;
        if (QuickActionsBar is not null)
        {
            QuickActionsBar.Visibility = Visibility.Collapsed;
        }

        var deferral = e.GetDeferral();
        try
        {
            e.AllowedOperations = DataPackageOperation.Copy | DataPackageOperation.Move;
            if (!e.Data.GetView().Contains(StandardDataFormats.Text))
            {
                e.Data.SetText(string.Join(Environment.NewLine, items.Select(i => i.FullPath)));
            }

            _dragPreviewImage = await CreateDragPreviewBitmapAsync(items);
            if (_dragPreviewImage is not null)
            {
                e.DragUI.SetContentFromBitmapImage(_dragPreviewImage);
            }
        }
        catch
        {
            _dragPreviewImage = null;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnDragItemsCompleted(object sender, DragItemsCompletedEventArgs e)
    {
        _dragItems = [];
        _dragPreviewImage = null;
        UpdateQuickActionsBar();
    }

    private async Task<BitmapImage?> CreateDragPreviewBitmapAsync(IReadOnlyList<FileListItemViewModel> items)
    {
        if (QuickActionsLayer is null || items.Count == 0)
        {
            return null;
        }

        Border? card = null;
        try
        {
            var primary = items[0];
            var label = items.Count == 1
                ? primary.Name
                : $"{primary.Name} 等 {items.Count} 项";

            var iconHost = new Grid { Width = 18, Height = 18 };
            iconHost.Children.Add(new FontIcon
            {
                Glyph = primary.IconGlyph,
                FontSize = 14,
                Foreground = primary.GlyphBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = primary.HasBitmapIcon ? Visibility.Collapsed : Visibility.Visible,
            });
            if (primary.HasBitmapIcon && primary.IconImage is not null)
            {
                iconHost.Children.Add(new Image
                {
                    Source = primary.IconImage,
                    Width = 16,
                    Height = 16,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(iconHost);
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 13,
                MaxWidth = 220,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });

            card = new Border
            {
                Padding = new Thickness(10, 6, 12, 6),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
                Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorSecondaryBrush"],
                Child = row,
                IsHitTestVisible = false,
            };

            // 离屏渲染：放到画布外，避免闪一下
            Canvas.SetLeft(card, -4000);
            Canvas.SetTop(card, -4000);
            QuickActionsLayer.Children.Add(card);
            card.Measure(new Size(280, 48));
            var w = Math.Max(120, card.DesiredSize.Width);
            var h = Math.Max(28, card.DesiredSize.Height);
            card.Arrange(new Rect(-4000, -4000, w, h));
            card.Width = w;
            card.Height = h;
            card.UpdateLayout();

            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(card, (int)Math.Ceiling(w), (int)Math.Ceiling(h));
            var pixels = await rtb.GetPixelsAsync();
            QuickActionsLayer.Children.Remove(card);
            card = null;

            var width = rtb.PixelWidth;
            var height = rtb.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            var stream = new InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
                stream);
            encoder.SetPixelData(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)width,
                (uint)height,
                96,
                96,
                pixels.ToArray());
            await encoder.FlushAsync();
            stream.Seek(0);

            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            return image;
        }
        catch
        {
            if (card is not null && QuickActionsLayer.Children.Contains(card))
            {
                QuickActionsLayer.Children.Remove(card);
            }

            return null;
        }
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var dropTarget = FindListItem(e.OriginalSource as DependencyObject);
        if (dropTarget is { IsDirectory: false } fileTarget &&
            IsOpenableDropTarget(fileTarget.Name) &&
            !IsDraggingItem(fileTarget))
        {
            // 拖到可执行文件上 → 「打开」
            e.AcceptedOperation = DataPackageOperation.Copy;
            ApplyDragUi(e, "打开");
            e.Handled = true;
            return;
        }

        if (dropTarget is { IsDirectory: false })
        {
            e.AcceptedOperation = DataPackageOperation.None;
            ApplyDragUi(e, "拖动");
            e.Handled = true;
            return;
        }

        var destination = dropTarget is { IsDirectory: true }
            ? dropTarget.FullPath
            : ViewModel.CurrentPath;
        if (string.IsNullOrWhiteSpace(destination) || !Directory.Exists(destination))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            ApplyDragUi(e, "拖动");
            e.Handled = true;
            return;
        }

        if (!(e.DataView.Contains(StandardDataFormats.Text) ||
              e.DataView.Contains(StandardDataFormats.StorageItems)))
        {
            ApplyDragUi(e, "拖动");
            return;
        }

        // 同源目录：只显示「拖动」，不接受放下
        if (IsDragFromSameDirectory(destination))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            ApplyDragUi(e, "拖动");
            e.Handled = true;
            return;
        }

        var move = e.Modifiers.HasFlag(Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Shift);
        e.AcceptedOperation = move ? DataPackageOperation.Move : DataPackageOperation.Copy;
        ApplyDragUi(e, move ? "移动" : "复制");
        e.Handled = true;
    }

    private void ApplyDragUi(DragEventArgs e, string caption)
    {
        try
        {
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = false;
            e.DragUIOverride.Caption = caption;
            if (_dragPreviewImage is not null)
            {
                e.DragUIOverride.IsContentVisible = true;
                e.DragUIOverride.SetContentFromBitmapImage(_dragPreviewImage);
            }
            else
            {
                // 无精简预览时隐藏整行幽灵，避免拖着大小/类型/日期
                e.DragUIOverride.IsContentVisible = false;
            }
        }
        catch
        {
        }
    }

    private bool IsDraggingItem(FileListItemViewModel item)
    {
        return _dragItems.Any(i =>
            string.Equals(i.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsDragFromSameDirectory(string destination)
    {
        if (_dragItems.Count == 0)
        {
            return false;
        }

        var dest = destination.TrimEnd('\\');
        return _dragItems.All(i =>
            string.Equals(
                Path.GetDirectoryName(i.FullPath.TrimEnd('\\'))?.TrimEnd('\\'),
                dest,
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsOpenableDropTarget(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".exe" or ".bat" or ".cmd" or ".msi" or ".com" or ".lnk";
    }

    private async void OnListDrop(object sender, DragEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var paths = new List<string>();
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            paths.AddRange(items.Select(i => i.Path).Where(p => !string.IsNullOrWhiteSpace(p))!);
        }
        else if (e.DataView.Contains(StandardDataFormats.Text))
        {
            var text = await e.DataView.GetTextAsync();
            paths.AddRange(text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        paths = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (paths.Count == 0)
        {
            return;
        }

        var dropTarget = FindListItem(e.OriginalSource as DependencyObject);
        if (dropTarget is { IsDirectory: false } fileTarget &&
            IsOpenableDropTarget(fileTarget.Name) &&
            !IsDraggingItem(fileTarget))
        {
            try
            {
                var args = string.Join(' ', paths.Select(p => $"\"{p}\""));
                Process.Start(new ProcessStartInfo
                {
                    FileName = fileTarget.FullPath,
                    Arguments = args,
                    UseShellExecute = true,
                });
            }
            catch
            {
            }

            e.Handled = true;
            return;
        }

        if (dropTarget is { IsDirectory: false })
        {
            e.Handled = true;
            return;
        }

        var destination = dropTarget is { IsDirectory: true }
            ? dropTarget.FullPath
            : ViewModel.CurrentPath;
        if (string.IsNullOrWhiteSpace(destination) || !Directory.Exists(destination))
        {
            return;
        }

        // 同目录内误触拖放不应默认复制出“(1)”副本。
        if (paths.All(p =>
            string.Equals(
                Path.GetDirectoryName(p.TrimEnd('\\'))?.TrimEnd('\\'),
                destination.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase)))
        {
            e.Handled = true;
            return;
        }

        var move = e.Modifiers.HasFlag(Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Shift);
        await ViewModel.PastePathsToAsync(paths, destination, move);
        e.Handled = true;
    }

    private async void OnListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        // 展开/折叠按钮上的连点不得当作「双击进入目录」
        if (ShouldSuppressItemActivate(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            return;
        }

        var item = FindListItem(e.OriginalSource as DependencyObject);
        if (item is not null)
        {
            await ViewModel.HandleItemActivatedAsync(item);
            e.Handled = true;
            return;
        }

        await ViewModel.GoUpCommand.ExecuteAsync(null);
        e.Handled = true;
    }

    private async Task RefreshPreviewPaneAsync(FileListItemViewModel? item)
    {
        if (ViewModel is null || !ViewModel.IsPreviewPaneVisible)
        {
            return;
        }

        if (item is null)
        {
            PreviewTitle.Text = "预览";
            PreviewHost.Content = new TextBlock
            {
                Text = "选择文件以预览",
                Opacity = 0.55,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            };
            return;
        }

        PreviewTitle.Text = item.Name;
        long length = 0;
        try
        {
            if (!item.IsDirectory && File.Exists(item.FullPath))
            {
                length = new FileInfo(item.FullPath).Length;
            }
        }
        catch
        {
        }

        var kind = FilePreviewPolicy.Resolve(item.FullPath, item.IsDirectory, length);
        try
        {
            switch (kind)
            {
                case FilePreviewKind.Directory:
                    PreviewHost.Content = new TextBlock
                    {
                        Text = "文件夹\n双击打开",
                        Opacity = 0.65,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                    };
                    break;
                case FilePreviewKind.Image:
                {
                    var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(item.FullPath));
                    PreviewHost.Content = new Image
                    {
                        Source = bitmap,
                        Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                        MaxWidth = 250,
                    };
                    break;
                }
                case FilePreviewKind.Text:
                {
                    var text = await File.ReadAllTextAsync(item.FullPath);
                    if (text.Length > 12000)
                    {
                        text = text[..12000] + "\n…";
                    }

                    PreviewHost.Content = new TextBlock
                    {
                        Text = text,
                        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                    };
                    break;
                }
                default:
                    PreviewHost.Content = new TextBlock
                    {
                        Text = $"{item.TypeText}\n{item.SizeText}\n\n空格键可快速预览/打开",
                        Opacity = 0.65,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                    };
                    break;
            }
        }
        catch (Exception ex)
        {
            PreviewHost.Content = new TextBlock
            {
                Text = $"无法预览\n{ex.Message}",
                Opacity = 0.65,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            };
        }
    }

    private async Task ShowQuickPreviewAsync(FileListItemViewModel item)
    {
        long length = 0;
        try
        {
            if (!item.IsDirectory && File.Exists(item.FullPath))
            {
                length = new FileInfo(item.FullPath).Length;
            }
        }
        catch
        {
        }

        var kind = FilePreviewPolicy.Resolve(item.FullPath, item.IsDirectory, length);
        if (kind is FilePreviewKind.Directory or FilePreviewKind.Unsupported or FilePreviewKind.None)
        {
            await ViewModel!.HandleItemActivatedAsync(item);
            return;
        }

        try
        {
            if (kind == FilePreviewKind.Image)
            {
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(item.FullPath));
                var image = new Image
                {
                    Source = bitmap,
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                    MaxWidth = 900,
                    MaxHeight = 600,
                };
                var dialog = new ContentDialog
                {
                    Title = item.Name,
                    Content = new ScrollViewer { Content = image, MaxHeight = 640 },
                    CloseButtonText = "关闭",
                    PrimaryButtonText = "打开",
                    XamlRoot = XamlRoot,
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    await ViewModel!.HandleItemActivatedAsync(item);
                }

                return;
            }

            if (kind == FilePreviewKind.Text)
            {
                var text = await File.ReadAllTextAsync(item.FullPath);
                if (text.Length > 20000)
                {
                    text = text[..20000] + "\n…";
                }

                var dialog = new ContentDialog
                {
                    Title = item.Name,
                    Content = new ScrollViewer
                    {
                        Content = new TextBlock
                        {
                            Text = text,
                            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                            TextWrapping = TextWrapping.Wrap,
                            IsTextSelectionEnabled = true,
                        },
                        MaxHeight = 480,
                        MaxWidth = 720,
                    },
                    CloseButtonText = "关闭",
                    PrimaryButtonText = "打开",
                    XamlRoot = XamlRoot,
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    await ViewModel!.HandleItemActivatedAsync(item);
                }

                return;
            }
        }
        catch
        {
        }

        await ViewModel!.HandleItemActivatedAsync(item);
    }

    private static FileListItemViewModel? FindListItem(DependencyObject? start)
    {
        var current = start;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: FileListItemViewModel item })
            {
                return item;
            }

            if (current is ListView)
            {
                break;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
