using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using AIExplorer.Core.Files;
using AIExplorer.Core.Metadata;
using AIExplorer.Core.Settings;
using AIExplorer.Core.Shell;
using AIExplorer_App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.ApplicationModel.DataTransfer;

namespace AIExplorer_App.ViewModels;

/// <summary>文件窗格显示模式（对齐资源管理器/Allen：详情列表 vs 图标）。</summary>
public enum FilePaneViewMode
{
    Details,
    Icons,
    Cards,
}

public partial class FileListItemViewModel : ObservableObject
{
    public FileListItemViewModel(FileEntrySnapshot entry, FileColumnLayout columns)
    {
        Entry = entry;
        Columns = columns;
    }

    /// <summary>与所属窗格共享的列布局，用于 DataTemplate 内 x:Bind 列宽/可见性。</summary>
    public FileColumnLayout Columns { get; }

    public FileEntrySnapshot Entry { get; private set; }
    public string Name => Entry.Name;
    public string FullPath => Entry.FullPath;
    public bool IsDirectory => Entry.IsDirectory;

    /// <summary>详情列表行内展开深度（0=当前目录项）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndentMargin))]
    [NotifyPropertyChangedFor(nameof(IndentWidth))]
    private int depth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandGlyph))]
    private bool isExpanded;

    public bool CanExpand => IsDirectory;
    public string ExpandGlyph => IsExpanded ? "\uE70D" : "\uE76C";
    /// <summary>与 XAML 展开列宽一致：子项 chevron 对齐父项文件夹图标左缘。</summary>
    public const double ExpandColumnWidth = 16;
    public double IndentWidth => Depth * ExpandColumnWidth;
    public Thickness IndentMargin => new(IndentWidth, 0, 0, 0);
    public Visibility ExpandButtonVisibility =>
        IsDirectory ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>就地改路径/名称，避免整表 LoadAsync 闪烁。</summary>
    public void ApplyRenamedPath(string newFullPath)
    {
        var name = Path.GetFileName(newFullPath);
        Entry = new FileEntrySnapshot
        {
            Stat = new FileStat
            {
                Name = name,
                FullPath = newFullPath,
                IsDirectory = Entry.IsDirectory,
                Size = Entry.Size,
                ModifiedTime = Entry.ModifiedTime,
            },
            UserTags = Entry.UserTags,
            Probe = Entry.Probe,
        };

        NotifyIdentityChanged();
    }

    /// <summary>覆盖粘贴后刷新大小/时间等 stat，不重建行。</summary>
    public void ApplySnapshotRefresh(FileEntrySnapshot snapshot)
    {
        Entry = new FileEntrySnapshot
        {
            Stat = snapshot.Stat,
            UserTags = Entry.UserTags ?? snapshot.UserTags,
            Probe = Entry.Probe ?? snapshot.Probe,
        };
        NotifyIdentityChanged();
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(ModifiedText));
        OnPropertyChanged(nameof(AgeText));
        OnPropertyChanged(nameof(AgeBrush));
    }

    private void NotifyIdentityChanged()
    {
        OnPropertyChanged(nameof(Entry));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(FullPath));
        OnPropertyChanged(nameof(TypeText));
        OnPropertyChanged(nameof(IconGlyph));
        OnPropertyChanged(nameof(GlyphBrush));
    }
    public string ModifiedText => Entry.ModifiedTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>寿命徽章（对齐 Allen）：按修改时间距今的跨度分档上色。</summary>
    public string AgeText => GetAgeBucket().Text;
    public Microsoft.UI.Xaml.Media.SolidColorBrush AgeBrush => GetAgeBucket().Brush;

    private (string Text, Microsoft.UI.Xaml.Media.SolidColorBrush Brush) GetAgeBucket()
    {
        var now = DateTimeOffset.Now;
        var modified = Entry.ModifiedTime.ToLocalTime();
        var span = now - modified;

        if (span.TotalHours < 24 && now.Date == modified.Date)
        {
            return span.TotalHours < 1 ? ($"{Math.Max(1, (int)span.TotalMinutes)}分钟", AgeBrushes.Hot)
                : span.TotalHours < 6 ? ($"{(int)span.TotalHours}小时", AgeBrushes.Hot)
                : ("今天", AgeBrushes.Hot);
        }

        if (modified.Date == now.Date.AddDays(-1))
        {
            return ("昨天", AgeBrushes.Warm);
        }

        if (span.TotalDays < 7)
        {
            return ("本周", AgeBrushes.Week);
        }

        if (modified.Year == now.Year && modified.Month == now.Month)
        {
            return ("本月", AgeBrushes.Month);
        }

        if (modified.Year == now.Year)
        {
            return ("今年", AgeBrushes.Year);
        }

        if (modified.Year == now.Year - 1)
        {
            return ("去年", AgeBrushes.LastYear);
        }

        return ("更早", AgeBrushes.Older);
    }
    public string TypeText => Entry.IsDirectory ? "文件夹" : (Path.GetExtension(Entry.Name).TrimStart('.') is { Length: > 0 } ext ? ext.ToUpperInvariant() : "文件");
    public string IconGlyph => IsDirectory ? "\uE8B7" : ExtensionGlyph(Path.GetExtension(Entry.Name));

    public Microsoft.UI.Xaml.Media.SolidColorBrush GlyphBrush => IsDirectory
        ? IconBrushes.Folder
        : BrushForExtension(Path.GetExtension(Entry.Name));

    public static Microsoft.UI.Xaml.Media.SolidColorBrush BrushForExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".svg" or ".ico" or ".tif" or ".tiff" => IconBrushes.Image,
        ".mp3" or ".wav" or ".flac" or ".m4a" => IconBrushes.Music,
        ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" => IconBrushes.Media,
        ".pdf" => IconBrushes.Pdf,
        ".doc" or ".docx" or ".rtf" => IconBrushes.Doc,
        ".xls" or ".xlsx" or ".csv" => IconBrushes.Sheet,
        ".ppt" or ".pptx" => IconBrushes.Slide,
        ".txt" or ".md" or ".log" => IconBrushes.File,
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => IconBrushes.Archive,
        ".exe" or ".msi" or ".bat" or ".cmd" => IconBrushes.Executable,
        ".cs" or ".py" or ".js" or ".ts" or ".json" or ".xml" or ".xaml" or ".html" or ".css" or ".cpp" or ".h" or ".java" => IconBrushes.Code,
        _ => IconBrushes.File,
    };

    public static string ExtensionGlyph(string ext) => ext.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".svg" or ".ico" or ".tif" or ".tiff" => "\uEB9F",
        ".mp3" or ".wav" or ".flac" or ".m4a" => "\uEC4F",
        ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" => "\uE714",
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "\uF012",
        ".exe" or ".msi" or ".bat" or ".cmd" => "\uE756",
        ".txt" or ".md" or ".log" => "\uE8A5",
        ".pdf" or ".doc" or ".docx" or ".rtf" or ".xls" or ".xlsx" or ".csv" or ".ppt" or ".pptx" => "\uE8A5",
        ".cs" or ".py" or ".js" or ".ts" or ".json" or ".xml" or ".xaml" or ".html" or ".css" or ".cpp" or ".h" or ".java" => "\uE943",
        _ => "\uE7C3",
    };

    [ObservableProperty]
    private ImageSource? iconImage;

    [ObservableProperty]
    private bool hasBitmapIcon;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    [NotifyPropertyChangedFor(nameof(SizeSortKey))]
    private long? folderSizeBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private bool isComputingFolderSize;

    /// <summary>排序用大小：文件用自身长度；文件夹用已算递归大小（未算视为 null）。</summary>
    public long? SizeSortKey => IsDirectory ? FolderSizeBytes : Entry.Size;

    public DateTime ModifiedUtc => Entry.ModifiedTime.UtcDateTime;

    public string SizeText
    {
        get
        {
            if (IsDirectory)
            {
                if (FolderSizeBytes is long bytes)
                {
                    return FormatSize(bytes);
                }

                return IsComputingFolderSize ? "…" : string.Empty;
            }

            return FormatSize(Entry.Size);
        }
    }

    [ObservableProperty]
    private bool isPinned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasColor))]
    [NotifyPropertyChangedFor(nameof(ColorTooltip))]
    private string? colorKey;

    [ObservableProperty]
    private string? note;

    [ObservableProperty]
    private string colorTooltip = string.Empty;

    public string NoteDisplay => string.IsNullOrWhiteSpace(Note) ? string.Empty : Note;
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
    public bool HasColor => !string.IsNullOrWhiteSpace(ColorKey);

    public void ApplyMetadata(FileMetadataRecord record, string? tooltip = null)
    {
        IsPinned = record.IsPinned;
        ColorKey = record.ColorKey;
        Note = record.Note;
        ColorTooltip = tooltip ?? string.Empty;
        OnPropertyChanged(nameof(NoteDisplay));
        OnPropertyChanged(nameof(HasNote));
        OnPropertyChanged(nameof(HasColor));
    }

    private int _iconLoadState; // 0=idle 1=loading/done

    /// <summary>
    /// 优先 Material 文件类型图标；其余（exe/lnk 等）限流加载 Shell 图标。
    /// 同一项只发起一次，避免大目录瞬间打爆线程池。
    /// </summary>
    public async Task EnsureIconAsync(
        IShellIconService icons,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        if (HasBitmapIcon || Interlocked.CompareExchange(ref _iconLoadState, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (FileTypeIconService.PreferMaterialOverShell(Entry.Name, Entry.IsDirectory))
            {
                var material = FileTypeIconService.GetImageSource(Entry.Name, Entry.IsDirectory);
                if (material is not null)
                {
                    IconImage = material;
                    HasBitmapIcon = true;
                    return;
                }
            }

            await gate.WaitAsync(cancellationToken).ConfigureAwait(true);
            try
            {
                if (HasBitmapIcon)
                {
                    return;
                }

                var data = await icons.GetSmallIconPngAsync(Entry.FullPath, Entry.IsDirectory, cancellationToken);
                if (data is null || data.Length < 8)
                {
                    // Shell 失败时再试 Material 兜底
                    var fallback = FileTypeIconService.GetImageSource(Entry.Name, Entry.IsDirectory);
                    if (fallback is not null)
                    {
                        IconImage = fallback;
                        HasBitmapIcon = true;
                        return;
                    }

                    Interlocked.Exchange(ref _iconLoadState, 0);
                    return;
                }

                var width = BitConverter.ToInt32(data, 0);
                var height = BitConverter.ToInt32(data, 4);
                if (width <= 0 || height <= 0 || data.Length < 8 + width * height * 4)
                {
                    Interlocked.Exchange(ref _iconLoadState, 0);
                    return;
                }

                var bitmap = new Windows.Graphics.Imaging.SoftwareBitmap(
                    Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    width,
                    height,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

                bitmap.CopyFromBuffer(data.AsBuffer(8, width * height * 4));
                var source = new SoftwareBitmapSource();
                await source.SetBitmapAsync(bitmap);
                IconImage = source;
                HasBitmapIcon = true;
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            Interlocked.Exchange(ref _iconLoadState, 0);
        }
        catch
        {
            Interlocked.Exchange(ref _iconLoadState, 0);
        }
    }

    internal static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{size:0.##} {units[unit]}";
    }
}

public partial class PathSegmentViewModel : ObservableObject
{
    public PathSegmentViewModel(string label, string fullPath, bool isBeyondCurrent = false)
    {
        Label = label;
        FullPath = fullPath;
        IsBeyondCurrent = isBeyondCurrent;
    }

    public string Label { get; }
    public string FullPath { get; }

    /// <summary>当前目录之后的尾迹段（灰色，仍可点击前进）。</summary>
    public bool IsBeyondCurrent { get; }

    public double SegmentOpacity => IsBeyondCurrent ? 0.42 : 1.0;
}

public partial class FilePaneViewModel : ObservableObject
{
    private const string InternalFilePathsFormat = "AIExplorer.FilePaths";

    // 同进程剪贴板：自定义 DataPackage 格式经 GetDataAsync 常变成流而非 string，
    // 用静态列表保证应用内剪切/复制/粘贴一定生效。
    private static List<string> _localClipboardPaths = [];
    private static bool _localClipboardIsCut;
    /// <summary>「复制路径」后下一次粘贴必须走纯文本，即使系统剪贴板仍残留 StorageItems。</summary>
    private static bool _forceTextPaste;

    private const int IconPrefetchCount = 64;
    private const int IconMaxConcurrency = 6;

    private readonly IFileListSource _fileListSource;
    private readonly IFileSystemService _fileSystemService;
    private readonly IShellIconService _shellIconService;
    private readonly IFileMetadataStore _metadataStore;
    private readonly ISettingsService _settings;
    private readonly SemaphoreSlim _iconGate = new(IconMaxConcurrency, IconMaxConcurrency);
    private readonly Stack<string> _backStack = new();
    private readonly Stack<string> _forwardStack = new();
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _watcherCts;
    private IDisposable? _watcherSubscription;
    private string? _pendingWatcherPath;
    private int _watcherAttachGeneration;
    private readonly object _watcherDeltaLock = new();
    private readonly List<(WatcherChangeTypes Kind, string FullPath)> _pendingWatcherDeltas = [];
    private bool _suppressHistory;
    private bool _clipboardIsCut;
    /// <summary>面包屑尾迹：可长于 CurrentPath，用于灰显后续仍可点的段。</summary>
    private string? _breadcrumbTrailPath;
    /// <summary>回退到上层后，选中刚离开的子目录。</summary>
    private string? _pendingSelectChildPath;

    public FilePaneViewModel(
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
        Columns = columns;
        currentPath = initialPath;
        editablePath = initialPath;
        sortColumn = ParseSortColumn(settings.Features.SortColumn);
        sortAscending = settings.Features.SortAscending;
        RebuildBreadcrumb();
        // 网络盘 AttachWatcher/Directory.Exists 同步很贵：延后到 Low，且同路径多窗格共用 Watcher
        ScheduleAttachWatcher(initialPath);
        RefreshSortHeaderLabels();
    }

    /// <summary>全局共享的列显隐布局，供表头与列表行绑定。</summary>
    public FileColumnLayout Columns { get; }

    private static readonly Dictionary<string, long> FolderSizeSessionCache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _folderSizeCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSortGlyph))]
    [NotifyPropertyChangedFor(nameof(SizeSortGlyph))]
    [NotifyPropertyChangedFor(nameof(TypeSortGlyph))]
    [NotifyPropertyChangedFor(nameof(ModifiedSortGlyph))]
    private FileSortColumn sortColumn = FileSortColumn.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSortGlyph))]
    [NotifyPropertyChangedFor(nameof(SizeSortGlyph))]
    [NotifyPropertyChangedFor(nameof(TypeSortGlyph))]
    [NotifyPropertyChangedFor(nameof(ModifiedSortGlyph))]
    private bool sortAscending = true;

    public string NameSortGlyph => SortGlyph(FileSortColumn.Name);
    public string SizeSortGlyph => SortGlyph(FileSortColumn.Size);
    public string TypeSortGlyph => SortGlyph(FileSortColumn.Type);
    public string ModifiedSortGlyph => SortGlyph(FileSortColumn.Modified);

    private string SortGlyph(FileSortColumn column) =>
        SortColumn != column ? string.Empty : (SortAscending ? " ↑" : " ↓");

    private void RefreshSortHeaderLabels()
    {
        OnPropertyChanged(nameof(NameSortGlyph));
        OnPropertyChanged(nameof(SizeSortGlyph));
        OnPropertyChanged(nameof(TypeSortGlyph));
        OnPropertyChanged(nameof(ModifiedSortGlyph));
    }

    private static FileSortColumn ParseSortColumn(string? name) => name switch
    {
        nameof(FileSortColumn.Size) => FileSortColumn.Size,
        nameof(FileSortColumn.Type) => FileSortColumn.Type,
        nameof(FileSortColumn.Modified) => FileSortColumn.Modified,
        _ => FileSortColumn.Name,
    };

    [ObservableProperty]
    private string currentPath = string.Empty;

    public ObservableCollection<FileListItemViewModel> Items { get; } = [];
    public ObservableCollection<FileListItemViewModel> VisibleItems { get; } = [];
    public ObservableCollection<PathSegmentViewModel> Breadcrumb { get; } = [];

    /// <summary>行内展开：父路径 → 子项（不进 Items，只进 VisibleItems）。</summary>
    private readonly Dictionary<string, List<FileListItemViewModel>> _childCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expandedPaths = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private FileListItemViewModel? selectedItem;

    [ObservableProperty]
    private bool canGoBack;

    [ObservableProperty]
    private bool canGoForward;

    [ObservableProperty]
    private bool isEditingPath;

    [ObservableProperty]
    private string editablePath = string.Empty;

    [ObservableProperty]
    private string filterText = string.Empty;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private int selectedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailsView))]
    [NotifyPropertyChangedFor(nameof(IsIconsView))]
    [NotifyPropertyChangedFor(nameof(IsCardsView))]
    private FilePaneViewMode viewMode = FilePaneViewMode.Details;

    public bool IsDetailsView => ViewMode == FilePaneViewMode.Details;
    public bool IsIconsView => ViewMode == FilePaneViewMode.Icons;
    public bool IsCardsView => ViewMode == FilePaneViewMode.Cards;

    public event Action<string>? NavigationRequested;

    [RelayCommand]
    private void SetDetailsView() => ViewMode = FilePaneViewMode.Details;

    [RelayCommand]
    private void SetIconsView() => ViewMode = FilePaneViewMode.Icons;

    [RelayCommand]
    private void SetCardsView() => ViewMode = FilePaneViewMode.Cards;

    [ObservableProperty]
    private bool isPreviewPaneVisible;

    [RelayCommand]
    private void TogglePreviewPane() => IsPreviewPaneVisible = !IsPreviewPaneVisible;

    partial void OnCurrentPathChanged(string value)
    {
        RebuildBreadcrumb();
        if (!IsEditingPath)
        {
            EditablePath = value;
        }

        ScheduleAttachWatcher(value);
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    public event Action? ListContentChanged;

    public async Task LoadAsync()
    {
        _folderSizeCts?.Cancel();
        var loadCts = new CancellationTokenSource();
        _loadCts?.Cancel();
        _loadCts = loadCts;
        var token = loadCts.Token;

        await RunOnUiAsync(() =>
        {
            IsLoading = true;
            Items.Clear();
            VisibleItems.Clear();
            _childCache.Clear();
            _expandedPaths.Clear();
            SelectedCount = 0;
            RefreshStatusText();
        });

        try
        {
            var pending = new List<FileListItemViewModel>(80);
            // 枚举可在后台；写入 ObservableCollection 必须回 UI 线程（否则 WinUI 首次绑定常出现空白列表）
            await foreach (var entry in _fileListSource.EnumerateIncrementalAsync(CurrentPath, token).ConfigureAwait(false))
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                var item = new FileListItemViewModel(entry, Columns);
                if (item.IsDirectory && FolderSizeSessionCache.TryGetValue(item.FullPath, out var cached))
                {
                    item.FolderSizeBytes = cached;
                }

                pending.Add(item);
                if (pending.Count < 80)
                {
                    continue;
                }

                var batch = pending;
                pending = new List<FileListItemViewModel>(80);
                await RunOnUiAsync(() =>
                {
                    if (token.IsCancellationRequested || !ReferenceEquals(_loadCts, loadCts))
                    {
                        return;
                    }

                    foreach (var i in batch)
                    {
                        Items.Add(i);
                    }

                    RebuildVisibleItems();
                });
            }

            if (!token.IsCancellationRequested && ReferenceEquals(_loadCts, loadCts))
            {
                await RunOnUiAsync(() =>
                {
                    if (token.IsCancellationRequested || !ReferenceEquals(_loadCts, loadCts))
                    {
                        return;
                    }

                    foreach (var i in pending)
                    {
                        Items.Add(i);
                    }

                    RebuildVisibleItems();
                });

                await ApplyMetadataAsync(token);
                _ = PrefetchIconsAsync(token);
                MaybeStartFolderSizeScan(autoOnly: true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // 仅最新一次加载负责复位，避免被取消的旧任务把 IsLoading 卡死或清掉新结果
            if (ReferenceEquals(_loadCts, loadCts))
            {
                await RunOnUiAsync(() =>
                {
                    IsLoading = false;
                    SelectedCount = 0;
                    RefreshStatusText();
                    // 通知视图强制重绑：ListView 在未挂到可视树时收到的 CollectionChanged 会被丢掉
                    ListContentChanged?.Invoke();
                });
            }
        }
    }

    private static Task RunOnUiAsync(Action action)
    {
        var dq = App.DispatcherQueue;
        if (dq is null || dq.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dq.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
        {
            tcs.TrySetException(new InvalidOperationException("DispatcherQueue.TryEnqueue failed."));
        }

        return tcs.Task;
    }

    /// <summary>预取首屏图标；其余由列表 ContainerContentChanging 按需加载。</summary>
    public Task RequestIconAsync(FileListItemViewModel item, CancellationToken cancellationToken = default) =>
        item.EnsureIconAsync(_shellIconService, _iconGate, cancellationToken);

    private async Task PrefetchIconsAsync(CancellationToken token)
    {
        try
        {
            var batch = VisibleItems.Take(IconPrefetchCount).ToList();
            foreach (var item in batch)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                _ = item.EnsureIconAsync(_shellIconService, _iconGate, token);
            }

            await Task.CompletedTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ApplyMetadataAsync(CancellationToken token)
    {
        List<string>? paths = null;
        await RunOnUiAsync(() =>
        {
            if (Items.Count == 0)
            {
                return;
            }

            paths = Items.Select(i => i.FullPath).ToList();
        });

        if (paths is null || paths.Count == 0 || token.IsCancellationRequested)
        {
            return;
        }

        try
        {
            var map = await _metadataStore.GetByPathsAsync(paths, token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                return;
            }

            await RunOnUiAsync(() =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                foreach (var item in Items)
                {
                    if (map.TryGetValue(item.FullPath, out var record))
                    {
                        var tip = FileColorPalette.Tooltip(FileColorPalette.Find(_settings.FileColors, record.ColorKey));
                        item.ApplyMetadata(record, tip);
                    }

                    if (item.IsDirectory && FolderSizeSessionCache.TryGetValue(item.FullPath, out var cached))
                    {
                        item.FolderSizeBytes = cached;
                    }
                }

                RebuildVisibleItems();
            });
        }
        catch
        {
        }
    }

    [RelayCommand]
    private void SortByName() => ToggleSort(FileSortColumn.Name);

    [RelayCommand]
    private void SortBySize() => ToggleSort(FileSortColumn.Size);

    [RelayCommand]
    private void SortByType() => ToggleSort(FileSortColumn.Type);

    [RelayCommand]
    private void SortByModified() => ToggleSort(FileSortColumn.Modified);

    private void ToggleSort(FileSortColumn column)
    {
        if (SortColumn == column)
        {
            SortAscending = !SortAscending;
        }
        else
        {
            SortColumn = column;
            SortAscending = true;
        }

        _settings.Features.SortColumn = SortColumn.ToString();
        _settings.Features.SortAscending = SortAscending;
        _ = _settings.SaveAsync();
        RebuildVisibleItems();
    }

    public void RebuildVisibleItems()
    {
        var ordered = FileListSort.OrderItems(
            Items.Where(MatchesFilter),
            SortColumn,
            SortAscending,
            x => x.IsPinned,
            x => x.IsDirectory,
            x => x.Name,
            x => x.SizeSortKey,
            x => x.TypeText,
            x => x.ModifiedUtc);

        VisibleItems.Clear();
        foreach (var item in ordered)
        {
            item.Depth = 0;
            AppendVisibleTree(item);
        }

        if (VisibleItems.Count == 1)
        {
            SelectedItem = VisibleItems[0];
        }

        RefreshStatusText();
        ListContentChanged?.Invoke();
    }

    /// <summary>
    /// + 新建同路径 Tab：复用源窗格已枚举结果，跳过磁盘 Enum举。
    /// 克隆在后台线程，UI 只做一次批量写入 + 一次 Rebuild（避免边 Yield 边卡顿）。
    /// </summary>
    public async Task<bool> TrySeedFromAsync(FilePaneViewModel source)
    {
        if (source.IsLoading || source.Items.Count == 0)
        {
            return false;
        }

        if (!PathsEqual(CurrentPath, source.CurrentPath))
        {
            return false;
        }

        // 小列表同步克隆（对齐左侧，避免 Task.Run 调度税）；大列表才后台
        var snapshot = new List<(FileEntrySnapshot Entry, long? FolderSizeBytes)>(source.Items.Count);
        foreach (var src in source.Items)
        {
            snapshot.Add((src.Entry, src.FolderSizeBytes));
        }

        var columns = Columns;
        List<FileListItemViewModel> clones;
        if (snapshot.Count <= 400)
        {
            clones = new List<FileListItemViewModel>(snapshot.Count);
            foreach (var (entry, size) in snapshot)
            {
                clones.Add(new FileListItemViewModel(entry, columns) { FolderSizeBytes = size });
            }
        }
        else
        {
            clones = await Task.Run(() =>
            {
                var list = new List<FileListItemViewModel>(snapshot.Count);
                foreach (var (entry, size) in snapshot)
                {
                    list.Add(new FileListItemViewModel(entry, columns) { FolderSizeBytes = size });
                }

                return list;
            }).ConfigureAwait(true);
        }

        Items.Clear();
        VisibleItems.Clear();
        _childCache.Clear();
        _expandedPaths.Clear();
        foreach (var clone in clones)
        {
            Items.Add(clone);
        }

        RebuildVisibleItems();
        IsLoading = false;
        return true;
    }

    private static bool PathsEqual(string a, string b)
    {
        static string Norm(string p)
        {
            var t = (p ?? string.Empty).Trim().TrimEnd('\\');
            if (t.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return t;
            }

            try
            {
                return Path.GetFullPath(t).TrimEnd('\\');
            }
            catch
            {
                return t;
            }
        }

        return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
    }

    private void AppendVisibleTree(FileListItemViewModel item)
    {
        VisibleItems.Add(item);
        if (!item.IsDirectory ||
            !item.IsExpanded ||
            !_childCache.TryGetValue(item.FullPath, out var children))
        {
            return;
        }

        var orderedKids = FileListSort.OrderItems(
            children.Where(MatchesFilter),
            SortColumn,
            SortAscending,
            x => x.IsPinned,
            x => x.IsDirectory,
            x => x.Name,
            x => x.SizeSortKey,
            x => x.TypeText,
            x => x.ModifiedUtc);

        foreach (var child in orderedKids)
        {
            child.Depth = item.Depth + 1;
            AppendVisibleTree(child);
        }
    }

    public async Task ToggleExpandAsync(FileListItemViewModel item)
    {
        if (!item.IsDirectory)
        {
            return;
        }

        if (item.IsExpanded)
        {
            CollapseExpanded(item.FullPath);
            item.IsExpanded = false;
            // 就地移除子树：后面的项往下/往上收，不 Clear 整表，避免滚动位置乱飞
            RemoveDescendantsFromVisible(item);
            RefreshStatusText();
            return;
        }

        await EnsureChildrenLoadedAsync(item);
        _expandedPaths.Add(item.FullPath);
        item.IsExpanded = true;
        InsertChildrenIntoVisible(item);
        RefreshStatusText();
    }

    private void CollapseExpanded(string path)
    {
        _expandedPaths.Remove(path);
        if (!_childCache.TryGetValue(path, out var children))
        {
            return;
        }

        foreach (var child in children.Where(c => c.IsDirectory))
        {
            if (child.IsExpanded)
            {
                child.IsExpanded = false;
                CollapseExpanded(child.FullPath);
            }
        }
    }

    /// <summary>在父项后方插入子树，已展开的深层一并插入；不清空 VisibleItems。</summary>
    private void InsertChildrenIntoVisible(FileListItemViewModel parent)
    {
        var parentIndex = VisibleItems.IndexOf(parent);
        if (parentIndex < 0 ||
            !_childCache.TryGetValue(parent.FullPath, out var children))
        {
            return;
        }

        var orderedKids = FileListSort.OrderItems(
            children.Where(MatchesFilter),
            SortColumn,
            SortAscending,
            x => x.IsPinned,
            x => x.IsDirectory,
            x => x.Name,
            x => x.SizeSortKey,
            x => x.TypeText,
            x => x.ModifiedUtc).ToList();

        var insertAt = parentIndex + 1;
        foreach (var child in orderedKids)
        {
            child.Depth = parent.Depth + 1;
            InsertVisibleSubtree(ref insertAt, child);
        }
    }

    private void InsertVisibleSubtree(ref int insertAt, FileListItemViewModel item)
    {
        VisibleItems.Insert(insertAt++, item);
        if (!item.IsDirectory ||
            !item.IsExpanded ||
            !_childCache.TryGetValue(item.FullPath, out var children))
        {
            return;
        }

        var orderedKids = FileListSort.OrderItems(
            children.Where(MatchesFilter),
            SortColumn,
            SortAscending,
            x => x.IsPinned,
            x => x.IsDirectory,
            x => x.Name,
            x => x.SizeSortKey,
            x => x.TypeText,
            x => x.ModifiedUtc);

        foreach (var child in orderedKids)
        {
            child.Depth = item.Depth + 1;
            InsertVisibleSubtree(ref insertAt, child);
        }
    }

    private void RemoveDescendantsFromVisible(FileListItemViewModel parent)
    {
        var idx = VisibleItems.IndexOf(parent);
        if (idx < 0)
        {
            return;
        }

        var depth = parent.Depth;
        var removedSelected = false;
        while (idx + 1 < VisibleItems.Count && VisibleItems[idx + 1].Depth > depth)
        {
            var victim = VisibleItems[idx + 1];
            if (ReferenceEquals(SelectedItem, victim))
            {
                removedSelected = true;
            }

            VisibleItems.RemoveAt(idx + 1);
        }

        if (removedSelected)
        {
            SelectedItem = parent;
        }
    }

    private async Task EnsureChildrenLoadedAsync(FileListItemViewModel parent)
    {
        if (_childCache.ContainsKey(parent.FullPath))
        {
            return;
        }

        var kids = new List<FileListItemViewModel>();
        try
        {
            await foreach (var entry in _fileListSource.EnumerateIncrementalAsync(parent.FullPath, CancellationToken.None))
            {
                var child = new FileListItemViewModel(entry, Columns)
                {
                    Depth = parent.Depth + 1,
                };
                if (child.IsDirectory && FolderSizeSessionCache.TryGetValue(child.FullPath, out var cached))
                {
                    child.FolderSizeBytes = cached;
                }

                kids.Add(child);
            }
        }
        catch
        {
            // 无权限等：当作空文件夹
        }

        _childCache[parent.FullPath] = kids;
    }

    /// <summary>右键手动计算选中文件夹大小（含网络盘）。</summary>
    public void RequestComputeFolderSizes(IReadOnlyList<FileListItemViewModel> folders)
    {
        var targets = folders.Where(f => f.IsDirectory).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        MaybeStartFolderSizeScan(autoOnly: false, targets);
    }

    private void MaybeStartFolderSizeScan(bool autoOnly, IReadOnlyList<FileListItemViewModel>? only = null)
    {
        if (autoOnly)
        {
            if (!_settings.Features.EnableFolderRecursiveSize ||
                _settings.Features.PerformanceMode ||
                !Columns.ShowSize ||
                !LocalPathPolicy.IsLocalFixedOrRemovableDrive(CurrentPath))
            {
                return;
            }
        }

        _folderSizeCts?.Cancel();
        _folderSizeCts = new CancellationTokenSource();
        var token = _folderSizeCts.Token;
        var folders = (only ?? Items.Where(i => i.IsDirectory).ToList())
            .Where(i => i.IsDirectory)
            .ToList();
        _ = ComputeFolderSizesAsync(folders, token);
    }

    private async Task ComputeFolderSizesAsync(IReadOnlyList<FileListItemViewModel> folders, CancellationToken token)
    {
        foreach (var folder in folders)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            if (FolderSizeSessionCache.TryGetValue(folder.FullPath, out var cached))
            {
                folder.FolderSizeBytes = cached;
                folder.IsComputingFolderSize = false;
                continue;
            }

            folder.IsComputingFolderSize = true;
            var path = folder.FullPath;
            long? size;
            try
            {
                size = await Task.Run(() => ComputeDirectorySizeSafe(path, token), token);
            }
            catch (OperationCanceledException)
            {
                folder.IsComputingFolderSize = false;
                return;
            }
            catch
            {
                folder.IsComputingFolderSize = false;
                continue;
            }

            if (token.IsCancellationRequested)
            {
                folder.IsComputingFolderSize = false;
                return;
            }

            var target = folder;
            var bytes = size;
            App.DispatcherQueue?.TryEnqueue(() =>
            {
                target.IsComputingFolderSize = false;
                if (bytes is long value)
                {
                    FolderSizeSessionCache[target.FullPath] = value;
                    target.FolderSizeBytes = value;
                }
            });
        }
    }

    private static long? ComputeDirectorySizeSafe(string path, CancellationToken token)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            DirectoryInfo info;
            try
            {
                info = new DirectoryInfo(dir);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 &&
                    !string.Equals(dir, path, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            try
            {
                foreach (var file in info.EnumerateFiles())
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }

                        total += file.Length;
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            try
            {
                foreach (var child in info.EnumerateDirectories())
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }

                        stack.Push(child.FullName);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        return total;
    }

    public async Task UpsertMetadataAsync(FileListItemViewModel item, Action<FileMetadataRecord> mutate)
    {
        var record = new FileMetadataRecord
        {
            Path = item.FullPath,
            IsPinned = item.IsPinned,
            ColorKey = item.ColorKey,
            Note = item.Note,
        };
        mutate(record);
        await _metadataStore.UpsertAsync(record);
        var tip = FileColorPalette.Tooltip(FileColorPalette.Find(_settings.FileColors, record.ColorKey));
        item.ApplyMetadata(record, tip);
        RebuildVisibleItems();
        await ApplyMetadataAsync(CancellationToken.None);
    }

    public static async Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
    }

    public static async Task<string> CompareFilesAsync(string pathA, string pathB, CancellationToken cancellationToken = default)
    {
        var infoA = new FileInfo(pathA);
        var infoB = new FileInfo(pathB);
        if (infoA.Length != infoB.Length)
        {
            return $"大小不同：{FileListItemViewModel.FormatSize(infoA.Length)} vs {FileListItemViewModel.FormatSize(infoB.Length)}";
        }

        var hashA = await ComputeFileHashAsync(pathA, cancellationToken);
        var hashB = await ComputeFileHashAsync(pathB, cancellationToken);
        return string.Equals(hashA, hashB, StringComparison.OrdinalIgnoreCase)
            ? $"内容相同（SHA256 一致）\n{hashA}"
            : $"内容不同\nA: {hashA}\nB: {hashB}";
    }

    public async Task<IReadOnlyList<string>> FindEmptyFoldersAsync(CancellationToken cancellationToken = default)
    {
        var root = CurrentPath;
        return await Task.Run(() =>
        {
            var result = new List<string>();
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        {
                            result.Add(dir);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            return (IReadOnlyList<string>)result;
        }, cancellationToken);
    }

    public void ApplyFilter() => RebuildVisibleItems();

    /// <summary>列表多选快照，供全局 Ctrl+C/X 使用（不依赖控件焦点）。</summary>
    public IReadOnlyList<FileListItemViewModel> SelectionSnapshot { get; private set; } = [];

    public void SetSelectionSnapshot(IReadOnlyList<FileListItemViewModel> items) =>
        SelectionSnapshot = items.Count == 0 ? [] : items.ToList();

    public void SetSelectedCount(int count)
    {
        SelectedCount = count;
        RefreshStatusText();
    }

    /// <summary>在当前目录新建文件夹并就地插入列表，返回新建项。</summary>
    public async Task<FileListItemViewModel?> CreateNewFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentPath) || !Directory.Exists(CurrentPath))
        {
            throw new DirectoryNotFoundException("当前目录不可用。");
        }

        var path = GetUniqueFolderPath(CurrentPath);
        Directory.CreateDirectory(path);
        await InsertPathsSmoothAsync([path]);
        return Items.FirstOrDefault(i =>
            string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetUniqueFolderPath(string directory)
    {
        var candidate = Path.Combine(directory, "新建文件夹");
        if (!Directory.Exists(candidate) && !File.Exists(candidate))
        {
            return candidate;
        }

        for (var index = 2; ; index++)
        {
            candidate = Path.Combine(directory, $"新建文件夹 ({index})");
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private void RefreshStatusText()
    {
        var total = VisibleItems.Count;
        var folders = VisibleItems.Count(i => i.IsDirectory);
        var files = total - folders;
        var baseText = $"共 {total} 个项目，包括 {folders} 个文件夹，{files} 个文件";
        StatusText = SelectedCount > 0 ? $"{baseText}  ·  已选中 {SelectedCount} 项" : baseText;
    }

    private bool MatchesFilter(FileListItemViewModel item)
    {
        if (string.IsNullOrWhiteSpace(FilterText))
        {
            return true;
        }

        return item.Name.Contains(FilterText.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>延后挂 Watcher，避免 + 新建时在 UI 线程上撞 UNC Exists/新建句柄。</summary>
    private void ScheduleAttachWatcher(string path)
    {
        _watcherSubscription?.Dispose();
        _watcherSubscription = null;
        _watcherCts?.Cancel();
        _pendingWatcherPath = path;
        var gen = ++_watcherAttachGeneration;

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var dq = App.DispatcherQueue;
        if (dq is null)
        {
            AttachWatcherCore(path, gen);
            return;
        }

        _ = dq.TryEnqueue(DispatcherQueuePriority.Low, () => AttachWatcherCore(path, gen));
    }

    private void AttachWatcherCore(string path, int generation)
    {
        if (generation != _watcherAttachGeneration ||
            !string.Equals(path, _pendingWatcherPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // 同路径多窗格共用一个 FileSystemWatcher（见 DirectoryWatcherHub）
        _watcherSubscription = DirectoryWatcherHub.TrySubscribe(
            path,
            onChange: (kind, fullPath) => ScheduleWatcherDelta(kind, fullPath),
            onRename: (oldPath, newPath) =>
            {
                var dq = App.DispatcherQueue;
                _ = dq?.TryEnqueue(() =>
                {
                    if (DateTime.UtcNow < _suppressWatcherUntilUtc)
                    {
                        return;
                    }

                    if (TryApplyExternalRename(oldPath, newPath))
                    {
                        SuppressWatcherBriefly();
                        return;
                    }

                    ScheduleWatcherDelta(WatcherChangeTypes.Deleted, oldPath);
                    // 改名后目标是否存在：本地可查；UNC 上 Exists 可能卡，交给后续 Created/失败即可
                    try
                    {
                        if (File.Exists(newPath) || Directory.Exists(newPath))
                        {
                            ScheduleWatcherDelta(WatcherChangeTypes.Created, newPath);
                        }
                    }
                    catch
                    {
                        ScheduleWatcherDelta(WatcherChangeTypes.Created, newPath);
                    }
                });
            });
    }

    private DateTime _suppressWatcherUntilUtc;

    /// <summary>本地操作（重命名等）后短暂忽略 watcher，避免紧接着再全量 Load。</summary>
    public void SuppressWatcherBriefly(int milliseconds = 1500)
    {
        _suppressWatcherUntilUtc = DateTime.UtcNow.AddMilliseconds(milliseconds);
        _watcherCts?.Cancel();
    }

    /// <summary>重命名后只把该项 Move 到新排序位置，其它行不动，避免整体闪烁。</summary>
    public void ApplyLocalRename(FileListItemViewModel item, string newFullPath)
    {
        SuppressWatcherBriefly(2500);
        item.ApplyRenamedPath(newFullPath);
        MoveItemToSortedIndex(item);
        if (!ReferenceEquals(SelectedItem, item))
        {
            SelectedItem = item;
        }

        RefreshStatusText();
    }

    /// <summary>其它窗格/外部改名：按旧路径找到项后就地更新。</summary>
    public bool TryApplyExternalRename(string oldFullPath, string newFullPath)
    {
        var item = Items.FirstOrDefault(i =>
            string.Equals(i.FullPath, oldFullPath, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return false;
        }

        SuppressWatcherBriefly(2500);
        item.ApplyRenamedPath(newFullPath);
        MoveItemToSortedIndex(item);
        RefreshStatusText();
        return true;
    }

    private void MoveItemToSortedIndex(FileListItemViewModel item)
    {
        var ordered = FileListSort.OrderItems(
            Items.Where(MatchesFilter),
            SortColumn,
            SortAscending,
            x => x.IsPinned,
            x => x.IsDirectory,
            x => x.Name,
            x => x.SizeSortKey,
            x => x.TypeText,
            x => x.ModifiedUtc).ToList();

        var newIndex = ordered.IndexOf(item);
        var oldIndex = VisibleItems.IndexOf(item);
        if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
        {
            VisibleItems.Move(oldIndex, newIndex);
        }
    }

    /// <summary>
    /// 目录变动增量合并：优先单条插入/删除，禁止动辄 LoadAsync 打乱顺序。
    /// </summary>
    private void ScheduleWatcherDelta(WatcherChangeTypes kind, string fullPath)
    {
        if (DateTime.UtcNow < _suppressWatcherUntilUtc)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return;
        }

        var name = Path.GetFileName(fullPath);
        // 忽略临时/隐藏噪声（部分杀软/索引会在目录里抖一下）
        if (!string.IsNullOrEmpty(name) &&
            (name.StartsWith('~') ||
             name.StartsWith('.') ||
             name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
             name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (IsLoading)
        {
            return;
        }

        try
        {
            fullPath = Path.GetFullPath(fullPath);
        }
        catch
        {
            return;
        }

        // 只处理当前目录顶层项（含子路径的留给展开缓存，避免误插顶层）
        var current = Path.GetFullPath(CurrentPath).TrimEnd('\\');
        var parent = Path.GetDirectoryName(fullPath)?.TrimEnd('\\');
        if (!string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_watcherDeltaLock)
        {
            _pendingWatcherDeltas.Add((kind, fullPath));
        }

        _watcherCts?.Cancel();
        _watcherCts = new CancellationTokenSource();
        var token = _watcherCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                // 合并抖动；后台静默增量插入/删除，与切 Tab 无关
                await Task.Delay(400, token);
                List<(WatcherChangeTypes Kind, string FullPath)> batch;
                lock (_watcherDeltaLock)
                {
                    batch = _pendingWatcherDeltas.ToList();
                    _pendingWatcherDeltas.Clear();
                }

                if (batch.Count == 0)
                {
                    return;
                }

                var dq = App.DispatcherQueue;
                dq?.TryEnqueue(() => _ = ApplyWatcherDeltasAsync(batch));
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private async Task ApplyWatcherDeltasAsync(IReadOnlyList<(WatcherChangeTypes Kind, string FullPath)> batch)
    {
        if (IsLoading || DateTime.UtcNow < _suppressWatcherUntilUtc)
        {
            return;
        }

        // 极端风暴（批量解压上千文件）才允许整表刷新
        if (batch.Count > 80)
        {
            await LoadAsync();
            return;
        }

        // 同一路径以最后一次事件为准
        var net = new Dictionary<string, WatcherChangeTypes>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, path) in batch)
        {
            net[path] = kind;
        }

        var deletes = net
            .Where(kv => kv.Value == WatcherChangeTypes.Deleted)
            .Select(kv => kv.Key)
            .ToList();
        var creates = net
            .Where(kv => kv.Value == WatcherChangeTypes.Created)
            .Select(kv => kv.Key)
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .ToList();

        if (deletes.Count == 0 && creates.Count == 0)
        {
            return;
        }

        // 吃掉紧随其后的重复 Created/Deleted，避免插入后又被全量逻辑碰到
        SuppressWatcherBriefly(2500);

        if (deletes.Count > 0)
        {
            await RunOnUiAsync(() => RemovePathsFromCurrentView(deletes));
        }

        if (creates.Count > 0)
        {
            // 外部新增（如系统菜单压缩出 zip）：按当前排序插入一条，单文件时选中便于看见
            await InsertPathsSmoothAsync(creates, selectLast: creates.Count == 1);
        }
    }

    public IReadOnlyList<FileListItemViewModel> GetSelectedItems(IList<object> selected)
    {
        return selected.OfType<FileListItemViewModel>().ToList();
    }

    public async Task CopySelectionAsync(IReadOnlyList<FileListItemViewModel> selection, bool cut)
    {
        if (selection.Count == 0)
        {
            return;
        }

        _clipboardIsCut = cut;
        _localClipboardIsCut = cut;
        _localClipboardPaths = selection.Select(s => s.FullPath).ToList();
        _forceTextPaste = false;

        var package = new Windows.ApplicationModel.DataTransfer.DataPackage
        {
            RequestedOperation = cut
                ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move
                : Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy,
        };
        var pathsText = string.Join(Environment.NewLine, _localClipboardPaths);
        // 文本方便粘贴到终端；StorageItems 方便粘贴到系统资源管理器。
        package.SetText(pathsText);

        // 自定义格式用 UTF-8 流写入，Paste 侧再按流读回（SetData(string) 读出不是 string）。
        try
        {
            var bytes = Encoding.UTF8.GetBytes(pathsText);
            var stream = new InMemoryRandomAccessStream();
            var writer = new DataWriter(stream);
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
            stream.Seek(0);
            package.SetData(InternalFilePathsFormat, stream);
        }
        catch
        {
        }

        try
        {
            var storageItems = new List<Windows.Storage.IStorageItem>();
            foreach (var entry in selection)
            {
                if (entry.IsDirectory)
                {
                    storageItems.Add(await Windows.Storage.StorageFolder.GetFolderFromPathAsync(entry.FullPath));
                }
                else
                {
                    storageItems.Add(await Windows.Storage.StorageFile.GetFileFromPathAsync(entry.FullPath));
                }
            }

            if (storageItems.Count > 0)
            {
                package.SetStorageItems(storageItems, readOnly: !cut);
            }
        }
        catch
        {
            // 网络盘等场景拿不到 StorageItems 时，应用内仍靠 _localClipboardPaths。
        }

        SetClipboardWithRetry(package);
    }

    /// <summary>剪贴板被其它进程占用时 SetContent 会抛 CLIPBRD_E_CANT_OPEN，重试几次再失败。</summary>
    private static void SetClipboardWithRetry(Windows.ApplicationModel.DataTransfer.DataPackage package)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                try
                {
                    Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
                }
                catch
                {
                    // Flush 失败不影响本进程存续期内的粘贴
                }

                return;
            }
            catch when (attempt < 3)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>清除进程内文件剪贴板（复制路径等纯文本场景必须调用）。</summary>
    public void ClearLocalFileClipboard()
    {
        _localClipboardPaths = [];
        _localClipboardIsCut = false;
        _clipboardIsCut = false;
    }

    /// <summary>复制路径为纯文本；后续 Ctrl+V 一律生成 txt，不复制文件/文件夹。</summary>
    public void CopyPathsAsText(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        ClearLocalFileClipboard();
        _forceTextPaste = true;

        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(string.Join(Environment.NewLine, paths));
        SetClipboardWithRetry(package);
    }

    /// <summary>true=已粘贴；false=无内容；null=用户取消冲突对话框。</summary>
    public async Task<bool?> PasteAsync()
    {
        // 加速键与残留 KeyDown 偶发双触发时，短窗内只执行一次
        var now = DateTime.UtcNow;
        if ((now - _lastPasteUtc).TotalMilliseconds < 350)
        {
            return false;
        }

        _lastPasteUtc = now;

        var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
        var hasText = content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text);

        // 「复制路径」优先：即使剪贴板因占用未替换、仍带 StorageItems，也只写 txt。
        if (_forceTextPaste && hasText)
        {
            var text = await content.GetTextAsync();
            _forceTextPaste = false;
            if (!string.IsNullOrEmpty(text))
            {
                var path = GetUniqueTextFilePath(CurrentPath);
                await File.WriteAllTextAsync(path, text);
                await InsertPathsSmoothAsync([path]);
                return true;
            }

            return false;
        }

        var kind = ClipboardPastePolicy.Resolve(
            content.Contains(InternalFilePathsFormat),
            content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems),
            hasText);

        // 纯文本（含外部复制的路径字符串）一律建 txt。
        if (kind == ClipboardPasteKind.Text)
        {
            var text = await content.GetTextAsync();
            if (!string.IsNullOrEmpty(text))
            {
                var path = GetUniqueTextFilePath(CurrentPath);
                await File.WriteAllTextAsync(path, text);
                await InsertPathsSmoothAsync([path]);
                return true;
            }

            return false;
        }

        if (kind == ClipboardPasteKind.None)
        {
            return false;
        }

        // 只有明确的内部文件格式 / StorageItems 才按文件处理。
        if (kind == ClipboardPasteKind.InternalFilePaths)
        {
            var paths = _localClipboardPaths
                .Where(p => File.Exists(p) || Directory.Exists(p))
                .ToList();
            if (paths.Count == 0)
            {
                paths = await ReadCustomPathFormatAsync(content);
            }

            if (paths.Count > 0)
            {
                var move = _localClipboardIsCut;
                var written = await TransferWithConflictAsync(paths, CurrentPath, move);
                if (written is null)
                {
                    return null;
                }

                if (move)
                {
                    ClearLocalFileClipboard();
                    RemovePathsFromCurrentView(paths);
                }

                await InsertPathsSmoothAsync(written);
                return written.Count > 0;
            }
        }

        if (kind is ClipboardPasteKind.InternalFilePaths or ClipboardPasteKind.StorageItems
            && content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            var storageItems = await content.GetStorageItemsAsync();
            var paths = storageItems
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList();
            if (paths.Count > 0)
            {
                var written = await TransferWithConflictAsync(paths, CurrentPath, move: false);
                if (written is null)
                {
                    return null;
                }

                await InsertPathsSmoothAsync(written);
                return written.Count > 0;
            }
        }

        return false;
    }

    private DateTime _lastPasteUtc;

    /// <summary>UI 注入：同名冲突时询问替换 / 跳过 / 保留两者。</summary>
    public Func<FileConflictPrompt, Task<FileConflictDecision>>? AskFileConflictAsync { get; set; }

    /// <summary>
    /// 解析冲突并执行传输。返回 null 表示用户取消整次粘贴。
    /// </summary>
    private async Task<IReadOnlyList<string>?> TransferWithConflictAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory,
        bool move)
    {
        var ops = await BuildTransferOperationsAsync(sourcePaths, destinationDirectory);
        if (ops is null)
        {
            return null;
        }

        if (ops.Count == 0)
        {
            return Array.Empty<string>();
        }

        return await _fileSystemService.ExecuteTransferAsync(ops, move);
    }

    private async Task<List<FileTransferOperation>?> BuildTransferOperationsAsync(
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory)
    {
        var destRoot = Path.GetFullPath(destinationDirectory).TrimEnd('\\');
        var planned = new List<(string Source, string Dest, bool IsDir)>();
        foreach (var source in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            var src = Path.GetFullPath(source);
            var isDir = Directory.Exists(src);
            if (!isDir && !File.Exists(src))
            {
                continue;
            }

            var name = Path.GetFileName(src.TrimEnd('\\'));
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            planned.Add((src, Path.Combine(destRoot, name), isDir));
        }

        static bool IsSameDirectoryPaste(string source, string destRoot)
        {
            var parent = Path.GetDirectoryName(source.TrimEnd('\\'));
            if (string.IsNullOrEmpty(parent))
            {
                return false;
            }

            return string.Equals(
                Path.GetFullPath(parent).TrimEnd('\\'),
                destRoot,
                StringComparison.OrdinalIgnoreCase);
        }

        // 跨目录同名才询问；同目录粘贴一律「保留两者」
        var askConflicts = planned
            .Where(p => FilePathConflict.TargetExists(p.Dest, p.IsDir) && !IsSameDirectoryPaste(p.Source, destRoot))
            .ToList();

        FileConflictAction? applyAll = null;
        var ops = new List<FileTransferOperation>();
        var conflictIndex = 0;

        foreach (var (source, dest, isDir) in planned)
        {
            var exists = FilePathConflict.TargetExists(dest, isDir);
            if (!exists)
            {
                ops.Add(new FileTransferOperation
                {
                    SourcePath = source,
                    DestinationPath = dest,
                    Overwrite = false,
                });
                continue;
            }

            if (IsSameDirectoryPaste(source, destRoot))
            {
                ops.Add(new FileTransferOperation
                {
                    SourcePath = source,
                    DestinationPath = FilePathConflict.EnsureUniquePath(dest, isDir),
                    Overwrite = false,
                });
                continue;
            }

            conflictIndex++;
            FileConflictAction action;
            if (applyAll is { } fixedAction)
            {
                action = fixedAction;
            }
            else if (AskFileConflictAsync is null)
            {
                action = FileConflictAction.Rename;
            }
            else
            {
                var decision = await AskFileConflictAsync(new FileConflictPrompt
                {
                    SourcePath = source,
                    DestinationPath = dest,
                    DisplayName = Path.GetFileName(dest.TrimEnd('\\')),
                    IsDirectory = isDir,
                    ConflictIndex = conflictIndex,
                    ConflictTotal = askConflicts.Count,
                });

                if (decision.Action == FileConflictAction.CancelAll)
                {
                    return null;
                }

                action = decision.Action;
                if (decision.ApplyToAll)
                {
                    applyAll = action;
                }
            }

            switch (action)
            {
                case FileConflictAction.Skip:
                    continue;
                case FileConflictAction.Replace:
                    ops.Add(new FileTransferOperation
                    {
                        SourcePath = source,
                        DestinationPath = dest,
                        Overwrite = true,
                    });
                    break;
                default:
                    ops.Add(new FileTransferOperation
                    {
                        SourcePath = source,
                        DestinationPath = FilePathConflict.EnsureUniquePath(dest, isDir),
                        Overwrite = false,
                    });
                    break;
            }
        }

        return ops;
    }

    private static async Task<List<string>> ReadCustomPathFormatAsync(
        Windows.ApplicationModel.DataTransfer.DataPackageView content)
    {
        try
        {
            var raw = await content.GetDataAsync(InternalFilePathsFormat);
            if (raw is string text)
            {
                return ParseExistingPaths(text);
            }

            if (raw is IRandomAccessStream stream)
            {
                using var reader = new DataReader(stream.GetInputStreamAt(0));
                var size = (uint)stream.Size;
                if (size == 0)
                {
                    return [];
                }

                await reader.LoadAsync(size);
                var bytes = new byte[size];
                reader.ReadBytes(bytes);
                return ParseExistingPaths(Encoding.UTF8.GetString(bytes));
            }
        }
        catch
        {
        }

        return [];
    }

    private static List<string> ParseExistingPaths(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(path => File.Exists(path) || Directory.Exists(path))
                .ToList();

    private static string GetUniqueTextFilePath(string directory)
    {
        var candidate = Path.Combine(directory, "新建文档.txt");
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        for (var index = 2; ; index++)
        {
            candidate = Path.Combine(directory, $"新建文档 ({index}).txt");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    public async Task DeleteSelectionAsync(IReadOnlyList<FileListItemViewModel> selection)
    {
        if (selection.Count == 0)
        {
            return;
        }

        var paths = selection
            .Select(s => s.FullPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Length)
            .ToList();
        if (paths.Count == 0)
        {
            return;
        }

        // 多选删除时：若已选父目录，则跳过其子项，避免“边删边遍历”导致子项报不存在。
        var normalized = new List<string>();
        foreach (var path in paths)
        {
            var coveredByParent = normalized.Any(parent =>
                path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (!coveredByParent)
            {
                normalized.Add(path);
            }
        }

        // 先就地移除，并压制 watcher，避免磁盘删除事件再触发一次整表 Load 闪烁
        SuppressWatcherBriefly(3500);
        RemovePathsFromCurrentView(normalized);
        try
        {
            await _fileSystemService.DeleteAsync(normalized);
            SuppressWatcherBriefly(2500);
        }
        catch
        {
            await LoadAsync();
            throw;
        }
    }

    public async Task UndoLastDeleteAsync()
    {
        if (!_fileSystemService.CanUndoDelete)
        {
            return;
        }

        await _fileSystemService.UndoLastDeleteAsync();
        await LoadAsync();
    }

    public bool CanUndoDelete => _fileSystemService.CanUndoDelete;

    public async Task PastePathsAsync(IReadOnlyList<string> paths, bool move)
    {
        if (paths.Count == 0)
        {
            return;
        }

        await PastePathsToAsync(paths, CurrentPath, move);
    }

    public async Task PastePathsToAsync(IReadOnlyList<string> paths, string destinationDirectory, bool move)
    {
        if (paths.Count == 0 || string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return;
        }

        var sourceList = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sourceList.Count == 0)
        {
            return;
        }

        var dest = Path.GetFullPath(destinationDirectory).TrimEnd('\\');
        var written = await TransferWithConflictAsync(sourceList, dest, move);
        if (written is null)
        {
            return;
        }

        var current = Path.GetFullPath(CurrentPath).TrimEnd('\\');
        if (move)
        {
            RemovePathsFromCurrentView(sourceList);
        }

        if (string.Equals(dest, current, StringComparison.OrdinalIgnoreCase))
        {
            await InsertPathsSmoothAsync(written);
        }
        else
        {
            // 粘贴到其它目录：只压住 watcher，避免本窗格无意义全量闪烁
            SuppressWatcherBriefly(2500);
        }
    }

    /// <summary>粘贴/新建/外部 Created 后就地插入，避免 Clear+Load 整表闪烁（同重命名丝滑策略）。</summary>
    private async Task InsertPathsSmoothAsync(IReadOnlyList<string> writtenPaths, bool selectLast = true)
    {
        SuppressWatcherBriefly(3000);
        if (writtenPaths.Count == 0)
        {
            return;
        }

        FileListItemViewModel? lastInserted = null;
        var inserted = new List<FileListItemViewModel>();
        await RunOnUiAsync(() =>
        {
            foreach (var path in writtenPaths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var full = Path.GetFullPath(path);
                var existing = Items.FirstOrDefault(i =>
                    string.Equals(i.FullPath, full, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    if (TryCreateSnapshot(full) is { } refreshed)
                    {
                        existing.ApplySnapshotRefresh(refreshed);
                    }

                    lastInserted = existing;
                    continue;
                }

                var snapshot = TryCreateSnapshot(full);
                if (snapshot is null)
                {
                    continue;
                }

                var item = new FileListItemViewModel(snapshot, Columns);
                if (item.IsDirectory && FolderSizeSessionCache.TryGetValue(item.FullPath, out var cached))
                {
                    item.FolderSizeBytes = cached;
                }

                Items.Add(item);
                if (MatchesFilter(item))
                {
                    InsertTopLevelIntoVisible(item);
                }

                inserted.Add(item);
                lastInserted = item;
            }

            if (selectLast && lastInserted is not null)
            {
                SelectedItem = lastInserted;
            }

            RefreshStatusText();
        });

        foreach (var item in inserted)
        {
            _ = item.EnsureIconAsync(_shellIconService, _iconGate, CancellationToken.None);
        }
    }

    private static FileEntrySnapshot? TryCreateSnapshot(string fullPath)
    {
        try
        {
            if (Directory.Exists(fullPath))
            {
                var dir = new DirectoryInfo(fullPath);
                return new FileEntrySnapshot
                {
                    Stat = new FileStat
                    {
                        Name = dir.Name,
                        FullPath = dir.FullName,
                        IsDirectory = true,
                        ModifiedTime = dir.LastWriteTimeUtc,
                    },
                };
            }

            if (File.Exists(fullPath))
            {
                var file = new FileInfo(fullPath);
                return new FileEntrySnapshot
                {
                    Stat = new FileStat
                    {
                        Name = file.Name,
                        FullPath = file.FullName,
                        IsDirectory = false,
                        Size = file.Length,
                        ModifiedTime = file.LastWriteTimeUtc,
                    },
                };
            }
        }
        catch
        {
        }

        return null;
    }

    /// <summary>按当前排序插入顶层项到 VisibleItems（保留已展开子树，不清空列表）。</summary>
    private void InsertTopLevelIntoVisible(FileListItemViewModel item)
    {
        item.Depth = 0;
        var ordered = FileListSort.OrderItems(
            Items.Where(MatchesFilter),
            SortColumn,
            SortAscending,
            x => x.IsPinned,
            x => x.IsDirectory,
            x => x.Name,
            x => x.SizeSortKey,
            x => x.TypeText,
            x => x.ModifiedUtc).ToList();

        var pos = ordered.IndexOf(item);
        if (pos < 0)
        {
            VisibleItems.Add(item);
            return;
        }

        if (pos == 0)
        {
            VisibleItems.Insert(0, item);
            return;
        }

        var prev = ordered[pos - 1];
        var prevIndex = VisibleItems.IndexOf(prev);
        if (prevIndex < 0)
        {
            VisibleItems.Add(item);
            return;
        }

        var insertAt = prevIndex + 1;
        while (insertAt < VisibleItems.Count && VisibleItems[insertAt].Depth > prev.Depth)
        {
            insertAt++;
        }

        if (insertAt >= VisibleItems.Count)
        {
            VisibleItems.Add(item);
        }
        else
        {
            VisibleItems.Insert(insertAt, item);
        }
    }

    private void RemovePathsFromCurrentView(IReadOnlyList<string> deletedPaths)
    {
        if (deletedPaths.Count == 0)
        {
            return;
        }

        static bool IsCoveredBy(string fullPath, string deletedPath)
        {
            var left = fullPath.TrimEnd('\\');
            var right = deletedPath.TrimEnd('\\');
            return left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
                   left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        var removed = Items
            .Where(item => deletedPaths.Any(path => IsCoveredBy(item.FullPath, path)))
            .ToList();
        if (removed.Count == 0)
        {
            return;
        }

        foreach (var item in removed)
        {
            Items.Remove(item);
            VisibleItems.Remove(item);
        }

        if (SelectedItem is not null && removed.Contains(SelectedItem))
        {
            SelectedItem = VisibleItems.FirstOrDefault();
        }

        SelectedCount = Math.Min(SelectedCount, VisibleItems.Count);
        RefreshStatusText();
    }

    /// <summary>
    /// 锁定标签导航闸门：返回 true 表示已改道（如新开 tab），本窗格勿改路径。
    /// </summary>
    public Func<string, Task<bool>>? TryRedirectNavigationAsync { get; set; }

    private async Task<bool> RedirectIfNeededAsync(string path)
    {
        if (TryRedirectNavigationAsync is null)
        {
            return false;
        }

        return await TryRedirectNavigationAsync(path);
    }

    /// <summary>内部同步用：忽略锁定改道（如开启双窗格时对齐右窗格）。</summary>
    public async Task NavigateInPlaceAsync(string path, bool resetBreadcrumbTrail = false)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        var previousPath = CurrentPath;
        PushHistoryIfNeeded(path);
        UpdateBreadcrumbTrail(path, resetBreadcrumbTrail);
        _pendingSelectChildPath = TryGetChildFolderToReveal(previousPath, path);
        CurrentPath = path;
        IsEditingPath = false;
        EditablePath = path;
        UpdateNavFlags();
        await LoadAsync();
        ApplyPendingChildSelection();
        NavigationRequested?.Invoke(CurrentPath);
    }

    /// <summary>
    /// 从 from 退到祖先 to 时，返回 to 下应选中的那一层子目录（朝 from 方向的第一级）。
    /// 例：from=…/2/3/4，to=…/2 → 返回 …/2/3。
    /// </summary>
    private static string? TryGetChildFolderToReveal(string? fromPath, string toPath)
    {
        if (string.IsNullOrWhiteSpace(fromPath) || string.IsNullOrWhiteSpace(toPath))
        {
            return null;
        }

        try
        {
            var from = Path.GetFullPath(fromPath);
            var to = Path.GetFullPath(toPath);
            if (PathsEqual(from, to) || !IsSameOrAncestor(to, from))
            {
                return null;
            }

            var toTrim = TrimPathEnd(to);
            var fromTrim = TrimPathEnd(from);
            var prefix = toTrim.EndsWith('\\') ? toTrim : toTrim + "\\";
            if (!fromTrim.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                // 盘符根 "C:\" 
                if (toTrim.Length == 3 && toTrim[1] == ':')
                {
                    prefix = toTrim;
                    if (!fromTrim.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }
                }
                else
                {
                    return null;
                }
            }

            var remainder = fromTrim[prefix.Length..].TrimStart('\\');
            if (string.IsNullOrEmpty(remainder))
            {
                return null;
            }

            var childName = remainder.Split('\\', StringSplitOptions.RemoveEmptyEntries)[0];
            return Path.GetFullPath(Path.Combine(to, childName));
        }
        catch
        {
            return null;
        }
    }

    private void ApplyPendingChildSelection()
    {
        var want = _pendingSelectChildPath;
        _pendingSelectChildPath = null;
        if (string.IsNullOrWhiteSpace(want))
        {
            return;
        }

        var match = Items.FirstOrDefault(i => PathsEqual(i.FullPath, want))
                    ?? VisibleItems.FirstOrDefault(i => PathsEqual(i.FullPath, want));
        if (match is null)
        {
            return;
        }

        SelectedItem = match;
        SelectedCount = 1;
        RefreshStatusText();
    }

    [RelayCommand]
    private async Task NavigateToPathAsync(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            // 面包屑盘符段可能是 "C:"，规范化后再判断
            var normalized = NormalizeNavigablePath(path);
            if (normalized is null || !Directory.Exists(normalized))
            {
                return;
            }

            if (await RedirectIfNeededAsync(normalized))
            {
                return;
            }

            // 面包屑点击：保留尾迹；地址栏提交等走 reset
            await NavigateInPlaceAsync(normalized, resetBreadcrumbTrail: false);
        }
        catch
        {
            // 地址栏点击不应拖垮进程
        }
    }

    private static string? NormalizeNavigablePath(string path)
    {
        try
        {
            var trimmed = path.Trim().Trim('"').Trim('\'');
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return null;
            }

            // 粘贴自 JSON / 日志的 \\ 双反斜杠；保留 UNC 前缀 \\server\share
            if (trimmed.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var body = trimmed[2..].Replace(@"\\", @"\", StringComparison.Ordinal);
                trimmed = @"\\" + body;
            }
            else
            {
                trimmed = trimmed.Replace(@"\\", @"\", StringComparison.Ordinal);
            }

            // file:///C:/... 
            if (trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = Uri.UnescapeDataString(trimmed);
                if (trimmed.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                {
                    trimmed = trimmed["file:///".Length..].Replace('/', '\\');
                }
                else if (trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    trimmed = @"\\" + trimmed["file://".Length..].Replace('/', '\\');
                }
            }

            if (trimmed.Length == 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':')
            {
                trimmed += "\\";
            }

            var full = Path.GetFullPath(trimmed);

            // 粘贴文件路径 → 导航到所在目录
            if (File.Exists(full))
            {
                return Path.GetDirectoryName(full);
            }

            if (Directory.Exists(full))
            {
                return full;
            }

            // 末尾缺盘符反斜杠等边缘情况再试一次
            if (full.EndsWith('\\') && Directory.Exists(full.TrimEnd('\\') + "\\"))
            {
                return Path.GetFullPath(full.TrimEnd('\\') + "\\");
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    [RelayCommand]
    private void BeginEditPath()
    {
        // 编辑/全选复制时给出 UNC，面包屑仍显示盘符短路径
        EditablePath = GetPathForClipboard(CurrentPath);
        IsEditingPath = true;
    }

    /// <summary>复制当前导航路径（映射盘按设置转为 \\ip\share）。</summary>
    [RelayCommand]
    private void CopyCurrentPath()
    {
        var path = GetPathForClipboard(CurrentPath);
        if (!string.IsNullOrWhiteSpace(path))
        {
            CopyPathsAsText([path]);
        }
    }

    public string GetPathForClipboard(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return _settings.Features.CopyMappedDriveAsUnc
            ? NetworkPathResolver.ToUncPath(path)
            : path;
    }

    [RelayCommand]
    private async Task CommitEditPathAsync()
    {
        var path = EditablePath.Trim();
        IsEditingPath = false;
        if (string.IsNullOrWhiteSpace(path))
        {
            EditablePath = CurrentPath;
            return;
        }

        var normalized = NormalizeNavigablePath(path);
        if (normalized is null || !Directory.Exists(normalized))
        {
            EditablePath = CurrentPath;
            return;
        }

        if (await RedirectIfNeededAsync(normalized))
        {
            return;
        }

        // 地址栏提交：重置尾迹为新路径
        await NavigateInPlaceAsync(normalized, resetBreadcrumbTrail: true);
    }

    [RelayCommand]
    private void CancelEditPath()
    {
        IsEditingPath = false;
        EditablePath = CurrentPath;
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        if (_backStack.Count == 0)
        {
            return;
        }

        var target = _backStack.Peek();
        if (await RedirectIfNeededAsync(target))
        {
            return;
        }

        _forwardStack.Push(CurrentPath);
        _suppressHistory = true;
        _backStack.Pop();
        _pendingSelectChildPath = TryGetChildFolderToReveal(CurrentPath, target);
        UpdateBreadcrumbTrail(target, reset: false);
        CurrentPath = target;
        IsEditingPath = false;
        EditablePath = target;
        UpdateNavFlags();
        await LoadAsync();
        ApplyPendingChildSelection();
        _suppressHistory = false;
        NavigationRequested?.Invoke(CurrentPath);
    }

    [RelayCommand]
    private async Task GoForwardAsync()
    {
        if (_forwardStack.Count == 0)
        {
            return;
        }

        var target = _forwardStack.Peek();
        if (await RedirectIfNeededAsync(target))
        {
            return;
        }

        _backStack.Push(CurrentPath);
        _suppressHistory = true;
        _forwardStack.Pop();
        _pendingSelectChildPath = TryGetChildFolderToReveal(CurrentPath, target);
        UpdateBreadcrumbTrail(target, reset: false);
        CurrentPath = target;
        IsEditingPath = false;
        EditablePath = target;
        UpdateNavFlags();
        await LoadAsync();
        ApplyPendingChildSelection();
        _suppressHistory = false;
        NavigationRequested?.Invoke(CurrentPath);
    }

    [RelayCommand]
    private async Task GoUpAsync()
    {
        var parent = _fileListSource.GetParentPath(CurrentPath);
        if (parent is null)
        {
            return;
        }

        await NavigateToPathCommand.ExecuteAsync(parent);
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private async Task OpenSelectedAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        if (SelectedItem.IsDirectory)
        {
            await NavigateToPathCommand.ExecuteAsync(SelectedItem.FullPath);
        }
        else
        {
            await _fileSystemService.OpenPathAsync(SelectedItem.FullPath);
        }
    }

    [RelayCommand]
    private async Task NavigateBreadcrumbAsync(PathSegmentViewModel? segment)
    {
        if (segment is null)
        {
            return;
        }

        await NavigateToPathCommand.ExecuteAsync(segment.FullPath);
    }

    public async Task HandleItemActivatedAsync(FileListItemViewModel item)
    {
        SelectedItem = item;
        await OpenSelectedCommand.ExecuteAsync(null);
    }

    private void PushHistoryIfNeeded(string newPath)
    {
        if (_suppressHistory)
        {
            return;
        }

        if (string.Equals(CurrentPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(CurrentPath))
        {
            _backStack.Push(CurrentPath);
        }

        _forwardStack.Clear();
    }

    private void UpdateNavFlags()
    {
        CanGoBack = _backStack.Count > 0;
        CanGoForward = _forwardStack.Count > 0;
    }

    private void RebuildBreadcrumb()
    {
        Breadcrumb.Clear();
        if (string.IsNullOrWhiteSpace(CurrentPath))
        {
            return;
        }

        try
        {
            var current = Path.GetFullPath(CurrentPath);
            var display = current;

            if (!string.IsNullOrWhiteSpace(_breadcrumbTrailPath))
            {
                try
                {
                    var trail = Path.GetFullPath(_breadcrumbTrailPath);
                    if (IsSameOrAncestor(current, trail))
                    {
                        // 当前是尾迹祖先 → 仍显示完整尾迹（后续灰显）
                        display = trail;
                    }
                    else if (IsSameOrAncestor(trail, current))
                    {
                        // 已深入超过旧尾迹 → 延伸
                        display = current;
                        _breadcrumbTrailPath = current;
                    }
                    else
                    {
                        display = current;
                        _breadcrumbTrailPath = current;
                    }
                }
                catch
                {
                    display = current;
                }
            }
            else
            {
                _breadcrumbTrailPath = current;
            }

            var root = Path.GetPathRoot(display);
            if (!string.IsNullOrEmpty(root))
            {
                Breadcrumb.Add(new PathSegmentViewModel(
                    root.TrimEnd('\\'),
                    root,
                    IsSegmentBeyondCurrent(root, current)));
            }

            if (root is null || display.Length <= root.Length)
            {
                return;
            }

            var remainder = display[root.Length..].Trim('\\');
            if (string.IsNullOrEmpty(remainder))
            {
                return;
            }

            var accumulated = root;
            foreach (var part in remainder.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                accumulated = Path.Combine(accumulated, part);
                Breadcrumb.Add(new PathSegmentViewModel(
                    part,
                    accumulated,
                    IsSegmentBeyondCurrent(accumulated, current)));
            }
        }
        catch
        {
            Breadcrumb.Add(new PathSegmentViewModel(CurrentPath, CurrentPath));
        }
    }

    private void UpdateBreadcrumbTrail(string newPath, bool reset)
    {
        string full;
        try
        {
            full = Path.GetFullPath(newPath);
        }
        catch
        {
            _breadcrumbTrailPath = newPath;
            return;
        }

        if (reset || string.IsNullOrWhiteSpace(_breadcrumbTrailPath))
        {
            _breadcrumbTrailPath = full;
            return;
        }

        string trail;
        try
        {
            trail = Path.GetFullPath(_breadcrumbTrailPath);
        }
        catch
        {
            _breadcrumbTrailPath = full;
            return;
        }

        // 回到尾迹内的祖先 → 保留灰尾
        if (IsSameOrAncestor(full, trail))
        {
            return;
        }

        // 进入尾迹内更深层（或超过旧尾迹）→ 延伸尾迹
        if (IsSameOrAncestor(trail, full))
        {
            _breadcrumbTrailPath = full;
            return;
        }

        // 离开原路径树 → 重置
        _breadcrumbTrailPath = full;
    }

    /// <summary>ancestor 是否与 path 相同，或为 path 的祖先目录。</summary>
    private static bool IsSameOrAncestor(string ancestor, string path)
    {
        if (PathsEqual(ancestor, path))
        {
            return true;
        }

        try
        {
            var a = TrimPathEnd(Path.GetFullPath(ancestor));
            var p = TrimPathEnd(Path.GetFullPath(path));
            if (a.Length == 0)
            {
                return false;
            }

            // 盘符根 "C:" → "C:\"
            if (a.Length == 2 && a[1] == ':')
            {
                a += "\\";
            }

            return p.StartsWith(a.EndsWith('\\') ? a : a + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSegmentBeyondCurrent(string segmentPath, string currentPath) =>
        !PathsEqual(segmentPath, currentPath) && IsSameOrAncestor(currentPath, segmentPath);

    private static string TrimPathEnd(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        // 保留 "C:\" 根
        if ((path.Length <= 3 && path.EndsWith(':')) ||
            (path.Length == 3 && path[1] == ':' && path[2] == '\\'))
        {
            return path.TrimEnd('\\') + "\\";
        }

        return path.TrimEnd('\\');
    }
}
