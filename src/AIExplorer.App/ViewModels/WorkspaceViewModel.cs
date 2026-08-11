using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AIExplorer.Core.Files;
using AIExplorer.Core.Metadata;
using AIExplorer.Core.Settings;
using AIExplorer.Core.Shell;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIExplorer_App.ViewModels;

/// <summary>
/// 线性 N 栏工作区：Slot0=Primary（左 BrowserTabs），Slot1…=标准 PaneGroup。
/// 方向只影响外层排布；每栏 UI 同构（PaneTabHost）。
/// </summary>
public partial class WorkspaceViewModel : ObservableObject
{
    private readonly IFileListSource _fileListSource;
    private readonly IFileSystemService _fileSystemService;
    private readonly IShellIconService _shellIconService;
    private readonly IFileMetadataStore _metadataStore;
    private readonly ISettingsService _settings;
    private readonly FileColumnLayout _columns;

    public WorkspaceViewModel(
        IFileListSource fileListSource,
        IFileSystemService fileSystemService,
        IShellIconService shellIconService,
        IFileMetadataStore metadataStore,
        ISettingsService settings,
        FileColumnLayout columns,
        PaneGroupViewModel firstSecondary)
    {
        _fileListSource = fileListSource;
        _fileSystemService = fileSystemService;
        _shellIconService = shellIconService;
        _metadataStore = metadataStore;
        _settings = settings;
        _columns = columns;

        // 兼容：原 ShellRightGroup 作为第一副栏的 Group 实例
        FirstSecondaryGroup = firstSecondary;

        Slots.Add(PaneSlotViewModel.CreatePrimary());
        Slots.CollectionChanged += OnSlotsChanged;
    }

    public ObservableCollection<PaneSlotViewModel> Slots { get; } = [];

    /// <summary>历史 ShellRightGroup；始终是「第一副栏」的数据源（即使当前未挂入 Slots）。</summary>
    public PaneGroupViewModel FirstSecondaryGroup { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSplit))]
    private DualPaneOrientation orientation = DualPaneOrientation.Horizontal;

    [ObservableProperty]
    private int activeSlotIndex;

    public bool IsSplit => SecondarySlotCount > 0;

    public int SecondarySlotCount => Math.Max(0, Slots.Count - 1);

    public event Action? LayoutChanged;

    /// <summary>开启/切换方向；若尚无副栏则挂上 FirstSecondaryGroup。</summary>
    public void SetSplit(DualPaneOrientation orientation, string pathForNewPane)
    {
        // 与旧 SetShellSplit：同方向再点且只有一副栏 → 关闭
        if (IsSplit && Orientation == orientation && SecondarySlotCount == 1)
        {
            CloseAllSecondary();
            return;
        }

        Orientation = orientation;
        if (!IsSplit)
        {
            FirstSecondaryGroup.ResetToSinglePane(pathForNewPane);
            Slots.Add(new PaneSlotViewModel(FirstSecondaryGroup));
            _ = FirstSecondaryGroup.InitializeAsync();
            ActiveSlotIndex = 1;
        }

        RaiseLayoutChanged();
    }

    /// <summary>再分一栏（第 3、4…）。</summary>
    public PaneGroupViewModel AddSlot(string path)
    {
        if (!IsSplit)
        {
            // 无副栏时先确保至少有第一副栏
            SetSplit(Orientation, path);
            return FirstSecondaryGroup;
        }

        var group = CreateGroup(path);
        Slots.Add(new PaneSlotViewModel(group));
        ActiveSlotIndex = Slots.Count - 1;
        _ = group.InitializeAsync();
        RaiseLayoutChanged();
        return group;
    }

    public void CloseAllSecondary()
    {
        while (Slots.Count > 1)
        {
            Slots.RemoveAt(Slots.Count - 1);
        }

        ActiveSlotIndex = 0;
        RaiseLayoutChanged();
    }

    /// <summary>会话恢复：若应分栏但 Slots 尚无副栏，挂上 FirstSecondaryGroup。</summary>
    public void EnsureSecondaryAttached()
    {
        if (Slots.Count > 1)
        {
            return;
        }

        Slots.Add(new PaneSlotViewModel(FirstSecondaryGroup));
        if (ActiveSlotIndex < 1)
        {
            ActiveSlotIndex = 1;
        }

        RaiseLayoutChanged();
    }

    public void RemoveSlotAt(int index)
    {
        if (index <= 0 || index >= Slots.Count)
        {
            return;
        }

        Slots.RemoveAt(index);
        if (Slots.Count == 1)
        {
            ActiveSlotIndex = 0;
        }
        else
        {
            ActiveSlotIndex = Math.Clamp(ActiveSlotIndex, 0, Slots.Count - 1);
        }

        RaiseLayoutChanged();
    }

    public void RemoveSlot(PaneGroupViewModel group)
    {
        for (var i = 1; i < Slots.Count; i++)
        {
            if (ReferenceEquals(Slots[i].Group, group))
            {
                RemoveSlotAt(i);
                return;
            }
        }
    }

    /// <summary>根据 FilePane 定位栏索引；Primary 左窗格返回 0。</summary>
    public int FindSlotIndex(FilePaneViewModel? pane)
    {
        if (pane is null)
        {
            return 0;
        }

        for (var i = 1; i < Slots.Count; i++)
        {
            var g = Slots[i].Group;
            if (g is not null && g.Panes.Contains(pane))
            {
                return i;
            }
        }

        return 0;
    }

    public PaneGroupViewModel? GetGroupAt(int slotIndex) =>
        slotIndex >= 0 && slotIndex < Slots.Count ? Slots[slotIndex].Group : null;

    private PaneGroupViewModel CreateGroup(string path) =>
        new(_fileListSource, _fileSystemService, _shellIconService, _metadataStore, _settings, _columns, path);

    private void OnSlotsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(IsSplit));

    private void RaiseLayoutChanged()
    {
        OnPropertyChanged(nameof(IsSplit));
        OnPropertyChanged(nameof(SecondarySlotCount));
        LayoutChanged?.Invoke();
    }
}
