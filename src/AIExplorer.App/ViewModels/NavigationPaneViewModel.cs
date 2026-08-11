using System.Collections.ObjectModel;
using AIExplorer.Core.Files;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace AIExplorer_App.ViewModels;

/// <summary>
/// 侧栏统一节点：分区头 / 固定访问叶子 / 可展开文件夹（此电脑下的盘符树）。
/// </summary>
public partial class FolderTreeNode : ObservableObject
{
    private bool _loaded;

    private FolderTreeNode(
        string name,
        string path,
        string glyph,
        bool isDrive,
        bool isPlaceholder,
        bool isSectionHeader,
        bool allowExpand,
        DriveMediaKind mediaKind = DriveMediaKind.Unknown,
        string? railCaption = null,
        bool isRecentHistory = false,
        string accessTimeText = "")
    {
        Name = name;
        Path = path;
        Glyph = glyph;
        IsDrive = isDrive;
        IsPlaceholder = isPlaceholder;
        IsSectionHeader = isSectionHeader;
        AllowExpand = allowExpand;
        MediaKind = mediaKind;
        RailCaption = railCaption ?? string.Empty;
        IsRecentHistory = isRecentHistory;
        AccessTimeText = accessTimeText;

        if (allowExpand && !isPlaceholder && !isSectionHeader && !string.IsNullOrWhiteSpace(path))
        {
            Children.Add(CreatePlaceholder());
        }
    }

    public static FolderTreeNode CreatePlaceholder() =>
        new("…", string.Empty, "\uE8B7", isDrive: false, isPlaceholder: true, isSectionHeader: false, allowExpand: false);

    public static FolderTreeNode CreateSectionHeader(string title) =>
        new(title, string.Empty, string.Empty, isDrive: false, isPlaceholder: false, isSectionHeader: true, allowExpand: false);

    public static FolderTreeNode CreateLeaf(string name, string path, string glyph) =>
        new(name, path, glyph, isDrive: false, isPlaceholder: false, isSectionHeader: false, allowExpand: false,
            railCaption: ShortCaption(name));

    public static FolderTreeNode CreateRecent(string path, DateTimeOffset accessedAt)
    {
        string name;
        try
        {
            var trimmed = path.TrimEnd('\\');
            name = System.IO.Path.GetFileName(trimmed);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = trimmed;
            }
        }
        catch
        {
            name = path;
        }

        return new(
            name,
            path,
            "\uE81C",
            isDrive: false,
            isPlaceholder: false,
            isSectionHeader: false,
            allowExpand: false,
            railCaption: ShortCaption(name),
            isRecentHistory: true,
            accessTimeText: FormatAccessTime(accessedAt));
    }

    private static string FormatAccessTime(DateTimeOffset accessedAt)
    {
        var local = accessedAt.ToLocalTime();
        var now = DateTimeOffset.Now;
        var time = local.ToString("HH:mm");
        if (local.Date == now.Date)
        {
            return $"今天 {time}";
        }

        if (local.Date == now.Date.AddDays(-1))
        {
            return $"昨天 {time}";
        }

        if (local.Year == now.Year)
        {
            return $"{local:MM-dd} {time}";
        }

        return $"{local:yyyy-MM-dd} {time}";
    }

    public static FolderTreeNode CreateExpandable(
        string name,
        string path,
        string glyph,
        bool isDrive = false,
        DriveMediaKind mediaKind = DriveMediaKind.Unknown) =>
        new(name, path, glyph, isDrive, isPlaceholder: false, isSectionHeader: false, allowExpand: true,
            mediaKind: mediaKind,
            railCaption: isDrive ? DriveLetterCaption(path) : ShortCaption(name));

    public string Name { get; }
    public string Path { get; }
    public string Glyph { get; }
    public bool IsDrive { get; }
    public bool IsPlaceholder { get; }
    public bool IsSectionHeader { get; }
    public bool AllowExpand { get; }
    public bool IsRecentHistory { get; }
    /// <summary>历史访问的访问时间文案；非历史项为空。</summary>
    public string AccessTimeText { get; }
    public Visibility AccessTimeVisibility =>
        string.IsNullOrEmpty(AccessTimeText) ? Visibility.Collapsed : Visibility.Visible;
    public DriveMediaKind MediaKind { get; }
    /// <summary>窄条上图标下方的短标签（如 C: / 桌面）。</summary>
    public string RailCaption { get; }
    public string RailToolTip =>
        IsDrive
            ? $"{Name} · {DriveMediaDetector.Label(MediaKind)}"
            : Name;
    /// <summary>悬停提示：历史项显示完整路径与访问时间。</summary>
    public string ToolTipText => IsRecentHistory
        ? (string.IsNullOrEmpty(AccessTimeText) ? Path : $"{Path}\n{AccessTimeText}")
        : RailToolTip;
    public bool IsNavigable => !IsSectionHeader && !IsPlaceholder && !string.IsNullOrWhiteSpace(Path);

    public Microsoft.UI.Xaml.Media.SolidColorBrush GlyphBrush =>
        IsRecentHistory ? IconBrushes.Folder
        : IsDrive ? (MediaKind == DriveMediaKind.Ssd ? IconBrushes.DriveSsd
            : MediaKind == DriveMediaKind.Hdd ? IconBrushes.DriveHdd
            : IconBrushes.Drive)
        : Path.StartsWith(@"\\", StringComparison.Ordinal) ? IconBrushes.Network
        : AllowExpand ? IconBrushes.Folder
        : IconBrushes.ForGlyph(Glyph);
    public ObservableCollection<FolderTreeNode> Children { get; } = [];

    [ObservableProperty]
    private bool isExpanded;

    [ObservableProperty]
    private bool isSelected;

    private static string DriveLetterCaption(string path)
    {
        try
        {
            var root = System.IO.Path.GetPathRoot(path)?.TrimEnd('\\');
            return string.IsNullOrEmpty(root) ? "?" : root;
        }
        catch
        {
            return "?";
        }
    }

    private static string ShortCaption(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        return name.Length <= 3 ? name : name[..2];
    }

    public void EnsureChildrenLoaded()
    {
        if (_loaded || IsPlaceholder || IsSectionHeader || !AllowExpand || string.IsNullOrWhiteSpace(Path))
        {
            return;
        }

        _loaded = true;
        Children.Clear();

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Path).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string name;
                try
                {
                    name = System.IO.Path.GetFileName(dir.TrimEnd('\\'));
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(name) || name.StartsWith('$'))
                {
                    continue;
                }

                Children.Add(CreateExpandable(name, dir, "\uE8B7"));
            }
        }
        catch
        {
        }
    }

    public FolderTreeNode? FindChildByPath(string fullPath)
    {
        foreach (var child in Children)
        {
            if (child.IsPlaceholder || string.IsNullOrWhiteSpace(child.Path))
            {
                continue;
            }

            try
            {
                if (string.Equals(
                        System.IO.Path.GetFullPath(child.Path).TrimEnd('\\'),
                        System.IO.Path.GetFullPath(fullPath).TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return child;
                }
            }
            catch
            {
            }
        }

        return null;
    }
}

public enum NavItemKind
{
    Header,
    Location,
    Drive,
    Network,
}

public partial class NavigationItemViewModel : ObservableObject
{
    public NavigationItemViewModel(string title, string glyph, NavItemKind kind, string? path = null)
    {
        Title = title;
        Glyph = glyph;
        Kind = kind;
        Path = path;
    }

    public string Title { get; }
    public string Glyph { get; }
    public NavItemKind Kind { get; }
    public string? Path { get; }
    public bool IsHeader => Kind == NavItemKind.Header;
    public bool IsSelectable => Kind != NavItemKind.Header;
}

public partial class NavigationPaneViewModel : ObservableObject
{
    private const int MaxRecentPaths = 20;
    private static readonly string RecentPathsFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AIExplorer",
        "recent-paths.json");

    private readonly List<RecentPathEntry> _recentPaths = [];

    private sealed class RecentPathEntry
    {
        public string Path { get; set; } = string.Empty;
        public DateTimeOffset AccessedAt { get; set; }
    }

    /// <summary>顶部常驻：固定访问叶子（不随下方目录树滚动）。</summary>
    public ObservableCollection<FolderTreeNode> QuickAccessRoots { get; } = [];

    /// <summary>可滚动目录树：此电脑 + 网络 + 历史访问。</summary>
    public ObservableCollection<FolderTreeNode> TreeRoots { get; } = [];

    /// <summary>左侧竖条：固定访问 + 盘符快捷方式（图标）。</summary>
    public ObservableCollection<FolderTreeNode> RailItems { get; } = [];

    [ObservableProperty]
    private bool isNavDrawerExpanded = true;

    /// <summary>兼容旧路径高亮逻辑（固定访问叶子的扁平镜像）。</summary>
    public ObservableCollection<NavigationItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private NavigationItemViewModel? selectedItem;

    public void Refresh()
    {
        Items.Clear();
        QuickAccessRoots.Clear();
        TreeRoots.Clear();

        AddSpecial("桌面", "\uE8FC", Environment.SpecialFolder.Desktop);
        AddSpecial("文档", "\uE8A5", Environment.SpecialFolder.MyDocuments);
        AddSpecial("下载", "\uE896", Environment.SpecialFolder.UserProfile, subPath: "Downloads");
        AddSpecial("图片", "\uEB9F", Environment.SpecialFolder.MyPictures);
        AddSpecial("音乐", "\uEC4F", Environment.SpecialFolder.MyMusic);
        AddSpecial("视频", "\uE714", Environment.SpecialFolder.MyVideos);
        AddSpecial("主文件夹", "\uE77B", Environment.SpecialFolder.UserProfile);

        TreeRoots.Add(FolderTreeNode.CreateSectionHeader("此电脑"));
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType != DriveType.Network).OrderBy(d => d.Name))
        {
            try
            {
                var media = DriveMediaDetector.Detect(drive);
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? $"{DriveTypeLabel(drive.DriveType)} ({drive.Name.TrimEnd('\\')})"
                    : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
                var path = drive.RootDirectory.FullName;
                var glyph = DriveMediaDetector.Glyph(media);
                TreeRoots.Add(FolderTreeNode.CreateExpandable(label, path, glyph, isDrive: true, mediaKind: media));
                Items.Add(new NavigationItemViewModel(label, glyph, NavItemKind.Drive, path));
            }
            catch
            {
            }
        }

        TreeRoots.Add(FolderTreeNode.CreateSectionHeader("网络"));
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Network && d.IsReady).OrderBy(d => d.Name))
        {
            try
            {
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? $"网络驱动器 ({drive.Name.TrimEnd('\\')})"
                    : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
                TreeRoots.Add(FolderTreeNode.CreateExpandable(
                    label,
                    drive.RootDirectory.FullName,
                    DriveMediaDetector.Glyph(DriveMediaKind.Network),
                    isDrive: true,
                    mediaKind: DriveMediaKind.Network));
            }
            catch
            {
            }
        }

        TreeRoots.Add(FolderTreeNode.CreateLeaf("网络邻居", @"\\", "\uE8CE"));
        Items.Add(new NavigationItemViewModel("网络邻居", "\uE8CE", NavItemKind.Network, @"\\"));

        RebuildHistorySection();
        RebuildRailItems();
    }

    public void LoadRecentPaths()
    {
        try
        {
            if (!File.Exists(RecentPathsFile))
            {
                return;
            }

            var json = File.ReadAllText(RecentPathsFile);
            _recentPaths.Clear();

            // 新格式：[{Path, AccessedAt}]；旧格式：["path", ...]
            try
            {
                var entries = System.Text.Json.JsonSerializer.Deserialize<List<RecentPathEntry>>(json);
                if (entries is { Count: > 0 } && entries.Any(e => !string.IsNullOrWhiteSpace(e.Path)))
                {
                    foreach (var entry in entries
                                 .Where(e => !string.IsNullOrWhiteSpace(e.Path))
                                 .OrderByDescending(e => e.AccessedAt)
                                 .Take(MaxRecentPaths))
                    {
                        try
                        {
                            entry.Path = System.IO.Path.GetFullPath(entry.Path).TrimEnd('\\');
                        }
                        catch
                        {
                            entry.Path = entry.Path.Trim();
                        }

                        if (entry.AccessedAt == default)
                        {
                            entry.AccessedAt = DateTimeOffset.Now;
                        }

                        _recentPaths.Add(entry);
                    }

                    RebuildHistorySection();
                    return;
                }
            }
            catch
            {
            }

            var legacy = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
            if (legacy is null)
            {
                return;
            }

            var rank = legacy.Count;
            foreach (var path in legacy.Where(p => !string.IsNullOrWhiteSpace(p)).Take(MaxRecentPaths))
            {
                string normalized;
                try
                {
                    normalized = System.IO.Path.GetFullPath(path).TrimEnd('\\');
                }
                catch
                {
                    normalized = path.Trim();
                }

                _recentPaths.Add(new RecentPathEntry
                {
                    Path = normalized,
                    // 旧数据无时间戳：按原 MRU 顺序给递减时间，保持「越上越新」
                    AccessedAt = DateTimeOffset.Now.AddMinutes(-rank),
                });
                rank--;
            }

            RebuildHistorySection();
        }
        catch
        {
        }
    }

    /// <summary>记录最近访问路径（按访问时间最新在前），并刷新侧栏「历史访问」分区。</summary>
    public void PushRecent(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == @"\\")
        {
            return;
        }

        string normalized;
        try
        {
            normalized = System.IO.Path.GetFullPath(path).TrimEnd('\\');
        }
        catch
        {
            return;
        }

        // 已在首位：只改时间戳落盘，不动 TreeView（切 Tab 不应刷侧栏）
        if (_recentPaths.Count > 0 &&
            string.Equals(_recentPaths[0].Path, normalized, StringComparison.OrdinalIgnoreCase))
        {
            _recentPaths[0].AccessedAt = DateTimeOffset.Now;
            SaveRecentPaths();
            return;
        }

        _recentPaths.RemoveAll(p => string.Equals(p.Path, normalized, StringComparison.OrdinalIgnoreCase));
        _recentPaths.Insert(0, new RecentPathEntry
        {
            Path = normalized,
            AccessedAt = DateTimeOffset.Now,
        });
        while (_recentPaths.Count > MaxRecentPaths)
        {
            _recentPaths.RemoveAt(_recentPaths.Count - 1);
        }

        SaveRecentPaths();
        RebuildHistorySection();
    }

    public void RemoveRecent(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string normalized;
        try
        {
            normalized = System.IO.Path.GetFullPath(path).TrimEnd('\\');
        }
        catch
        {
            normalized = path.Trim();
        }

        var removed = _recentPaths.RemoveAll(p => string.Equals(p.Path, normalized, StringComparison.OrdinalIgnoreCase));
        if (removed <= 0)
        {
            return;
        }

        SaveRecentPaths();
        RebuildHistorySection();
    }

    public void ClearRecent()
    {
        if (_recentPaths.Count == 0)
        {
            return;
        }

        _recentPaths.Clear();
        SaveRecentPaths();
        RebuildHistorySection();
    }

    private void SaveRecentPaths()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(RecentPathsFile)!);
            var ordered = _recentPaths
                .OrderByDescending(e => e.AccessedAt)
                .Take(MaxRecentPaths)
                .ToList();
            File.WriteAllText(
                RecentPathsFile,
                System.Text.Json.JsonSerializer.Serialize(
                    ordered,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    private void RebuildHistorySection()
    {
        for (var i = TreeRoots.Count - 1; i >= 0; i--)
        {
            var node = TreeRoots[i];
            if (node.IsRecentHistory ||
                (node.IsSectionHeader && string.Equals(node.Name, "历史访问", StringComparison.Ordinal)))
            {
                TreeRoots.RemoveAt(i);
            }
        }

        if (_recentPaths.Count == 0)
        {
            return;
        }

        TreeRoots.Add(FolderTreeNode.CreateSectionHeader("历史访问"));
        foreach (var entry in _recentPaths.OrderByDescending(e => e.AccessedAt))
        {
            TreeRoots.Add(FolderTreeNode.CreateRecent(entry.Path, entry.AccessedAt));
        }
    }

    private void RebuildRailItems()
    {
        RailItems.Clear();
        foreach (var leaf in QuickAccessRoots)
        {
            RailItems.Add(leaf);
        }

        foreach (var drive in TreeRoots.Where(n => n.IsDrive && n.IsNavigable))
        {
            RailItems.Add(drive);
        }

        var networkLeaf = TreeRoots.FirstOrDefault(n =>
            n.IsNavigable && string.Equals(n.Path, @"\\", StringComparison.OrdinalIgnoreCase));
        if (networkLeaf is not null)
        {
            RailItems.Add(networkLeaf);
        }
    }

    partial void OnIsNavDrawerExpandedChanged(bool value) => OnPropertyChanged(nameof(NavDrawerWidth));

    public GridLength NavDrawerWidth => IsNavDrawerExpanded ? new GridLength(180) : new GridLength(0);

    public void RebuildTreeRoots() => Refresh();

    public bool IsQuickAccessNode(FolderTreeNode? node) =>
        node is not null && QuickAccessRoots.Contains(node);

    public void TrackPath(string? path, bool autoReveal = true)
    {
        using var _ = AIExplorer_App.PerfLog.Measure(autoReveal ? "TrackPath(reveal)" : "TrackPath");
        if (string.IsNullOrWhiteSpace(path))
        {
            SelectedItem = null;
            return;
        }

        string? normalized;
        try
        {
            normalized = Path.GetFullPath(path).TrimEnd('\\');
        }
        catch
        {
            SelectedItem = null;
            return;
        }

        NavigationItemViewModel? best = null;
        var bestLength = -1;

        foreach (var item in Items)
        {
            if (!item.IsSelectable || item.Kind == NavItemKind.Network || string.IsNullOrWhiteSpace(item.Path))
            {
                continue;
            }

            string root;
            try
            {
                root = Path.GetFullPath(item.Path).TrimEnd('\\');
            }
            catch
            {
                continue;
            }

            if (normalized.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                if (root.Length > bestLength)
                {
                    best = item;
                    bestLength = root.Length;
                }
            }
        }

        SelectedItem = best;
        RevealInTree(normalized, autoReveal);
    }

    [ObservableProperty]
    private FolderTreeNode? selectedTreeNode;

    partial void OnSelectedTreeNodeChanged(FolderTreeNode? value)
    {
        ClearTreeSelection(QuickAccessRoots);
        ClearTreeSelection(TreeRoots);
        if (value is not null)
        {
            value.IsSelected = true;
        }
    }

    private static void ClearTreeSelection(IEnumerable<FolderTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.IsSelected)
            {
                node.IsSelected = false;
            }

            if (node.Children.Count > 0)
            {
                ClearTreeSelection(node.Children);
            }
        }
    }

    /// <summary>收起此电脑/网络下全部展开节点（一次性动作，不锁死后续手动展开）。</summary>
    public void CollapseAll()
    {
        CollapseRecursive(TreeRoots);
    }

    private static void CollapseRecursive(IEnumerable<FolderTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.IsExpanded)
            {
                node.IsExpanded = false;
            }

            if (node.Children.Count > 0)
            {
                CollapseRecursive(node.Children);
            }
        }
    }

    /// <summary>仅收起「其它盘符」根节点，保留当前盘下的展开，便于露出盘符列表。</summary>
    private void CollapseOtherDrives(FolderTreeNode keep)
    {
        foreach (var node in TreeRoots.Where(n => n.IsDrive && n.IsNavigable))
        {
            if (!ReferenceEquals(node, keep) && node.IsExpanded)
            {
                node.IsExpanded = false;
            }
        }
    }

    /// <param name="autoReveal">
    /// true：展开并选中当前路径（不整树收起，避免锁死手动展开）；
    /// false：只高亮对应盘符，不深展开。
    /// 「收起」按钮才是一次性 CollapseAll。
    /// </param>
    public void RevealInTree(string? path, bool autoReveal = true)
    {
        SelectedTreeNode = null;

        if (string.IsNullOrWhiteSpace(path) || (TreeRoots.Count == 0 && QuickAccessRoots.Count == 0))
        {
            return;
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(path).TrimEnd('\\');
        }
        catch
        {
            return;
        }

        // 固定访问精确匹配：只改选中，不 CollapseAll（否则桌面等路径会反复把树压扁）
        foreach (var leaf in QuickAccessRoots)
        {
            if (!leaf.IsNavigable)
            {
                continue;
            }

            try
            {
                var leafPath = Path.GetFullPath(leaf.Path).TrimEnd('\\');
                if (normalized.Equals(leafPath, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedTreeNode = leaf;
                    return;
                }
            }
            catch
            {
            }
        }

        var root = FindDriveRoot(normalized);
        if (root is null)
        {
            return;
        }

        // 跟随关闭：只高亮盘符，不深展开、不整树收起
        if (!autoReveal)
        {
            SelectedTreeNode = root;
            return;
        }

        // 换盘时收起其它盘符根，避免多盘深树同时撑开；当前盘内不强制 CollapseAll
        CollapseOtherDrives(root);
        root.IsExpanded = true;
        root.EnsureChildrenLoaded();

        var current = root;
        string remainder;
        try
        {
            var driveRoot = Path.GetPathRoot(normalized);
            remainder = driveRoot is not null && normalized.Length > driveRoot.Length
                ? normalized[driveRoot.Length..].Trim('\\')
                : string.Empty;
        }
        catch
        {
            SelectedTreeNode = root;
            return;
        }

        if (string.IsNullOrEmpty(remainder))
        {
            SelectedTreeNode = root;
            return;
        }

        var accumulated = Path.GetPathRoot(normalized) ?? root.Path;
        foreach (var part in remainder.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            accumulated = Path.Combine(accumulated, part);
            current.EnsureChildrenLoaded();
            var child = current.FindChildByPath(accumulated);
            if (child is null)
            {
                break;
            }

            child.IsExpanded = true;
            child.EnsureChildrenLoaded();
            current = child;
        }

        SelectedTreeNode = current;
    }

    private FolderTreeNode? FindDriveRoot(string normalized)
    {
        foreach (var node in TreeRoots.Where(n => n.IsDrive && n.IsNavigable))
        {
            try
            {
                var driveRoot = Path.GetPathRoot(Path.GetFullPath(node.Path));
                var pathRoot = Path.GetPathRoot(normalized);
                if (driveRoot is not null &&
                    pathRoot is not null &&
                    string.Equals(driveRoot, pathRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return node;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private void AddSpecial(string title, string glyph, Environment.SpecialFolder folder, string? subPath = null)
    {
        try
        {
            var root = Environment.GetFolderPath(folder);
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            var path = subPath is null ? root : Path.Combine(root, subPath);
            if (!Directory.Exists(path))
            {
                return;
            }

            QuickAccessRoots.Add(FolderTreeNode.CreateLeaf(title, path, glyph));
            Items.Add(new NavigationItemViewModel(title, glyph, NavItemKind.Location, path));
        }
        catch
        {
        }
    }

    private static string DriveTypeLabel(DriveType type) => type switch
    {
        DriveType.Removable => "可移动磁盘",
        DriveType.Network => "网络驱动器",
        DriveType.CDRom => "光盘",
        DriveType.Ram => "RAM 磁盘",
        _ => "本地磁盘",
    };
}
