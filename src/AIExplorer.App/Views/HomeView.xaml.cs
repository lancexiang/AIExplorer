using System.Collections.ObjectModel;
using AIExplorer_App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AIExplorer_App.Views;

/// <summary>主页卡片项：文件夹 / 驱动器 / 最近文件共用。XAML 编译器要求可写属性。</summary>
public sealed class HomeItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Glyph { get; set; } = "\uE8B7";
    public SolidColorBrush? Brush { get; set; }
    public double UsagePercent { get; set; }
    public SolidColorBrush? UsageBrush { get; set; }
    public string CapacityText { get; set; } = string.Empty;
}

/// <summary>
/// 新标签主页：常用文件夹（收藏）+ 系统文件夹 + 驱动器容量卡片 + 最近打开的文件。
/// </summary>
public sealed partial class HomeView : UserControl
{
    private static readonly SolidColorBrush UsageNormalBrush = new(Color.FromArgb(255, 0x4F, 0x9C, 0xE8));
    private static readonly SolidColorBrush UsageWarnBrush = new(Color.FromArgb(255, 0xE0, 0x5A, 0x4A));

    public ObservableCollection<HomeItem> FavoriteItems { get; } = [];
    public ObservableCollection<HomeItem> SystemItems { get; } = [];
    public ObservableCollection<HomeItem> DriveItems { get; } = [];
    public ObservableCollection<HomeItem> NetworkDriveItems { get; } = [];
    public ObservableCollection<HomeItem> RecentItems { get; } = [];

    /// <summary>点击文件夹/驱动器卡片时通知宿主导航。</summary>
    public event Action<string>? FolderActivated;

    public HomeView()
    {
        InitializeComponent();
        FavoriteGrid.ItemsSource = FavoriteItems;
        SystemGrid.ItemsSource = SystemItems;
        DriveGrid.ItemsSource = DriveItems;
        NetworkDriveGrid.ItemsSource = NetworkDriveItems;
        RecentGrid.ItemsSource = RecentItems;
    }

    /// <summary>刷新全部分区；favoriteFolders 为收藏夹中的文件夹叶子。</summary>
    public void Refresh(IEnumerable<(string Name, string Path)> favoriteFolders)
    {
        LoadFavorites(favoriteFolders);
        LoadSystemFolders();
        LoadDrives();
        LoadRecentFiles();
    }

    private void LoadFavorites(IEnumerable<(string Name, string Path)> favoriteFolders)
    {
        FavoriteItems.Clear();
        foreach (var (name, path) in favoriteFolders.Take(12))
        {
            FavoriteItems.Add(new HomeItem
            {
                Name = name,
                Path = path,
                Glyph = "\uE8B7",
                Brush = IconBrushes.Folder,
            });
        }

        FavoriteHeader.Text = $"常用文件夹 ({FavoriteItems.Count})";
        FavoriteHeader.Visibility = FavoriteItems.Count > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        FavoriteGrid.Visibility = FavoriteHeader.Visibility;
    }

    private void LoadSystemFolders()
    {
        SystemItems.Clear();
        var specials = new (string Name, string Glyph, Environment.SpecialFolder Folder, string? Sub)[]
        {
            ("Desktop", "\uE8FC", Environment.SpecialFolder.Desktop, null),
            ("Documents", "\uE8A5", Environment.SpecialFolder.MyDocuments, null),
            ("Pictures", "\uEB9F", Environment.SpecialFolder.MyPictures, null),
            ("Music", "\uEC4F", Environment.SpecialFolder.MyMusic, null),
            ("Videos", "\uE714", Environment.SpecialFolder.MyVideos, null),
            ("Downloads", "\uE896", Environment.SpecialFolder.UserProfile, "Downloads"),
        };

        foreach (var (name, glyph, folder, sub) in specials)
        {
            var path = Environment.GetFolderPath(folder);
            if (sub is not null)
            {
                path = System.IO.Path.Combine(path, sub);
            }

            if (!Directory.Exists(path))
            {
                continue;
            }

            SystemItems.Add(new HomeItem
            {
                Name = name,
                Path = path,
                Glyph = glyph,
                Brush = IconBrushes.ForGlyph(glyph),
            });
        }

        SystemHeader.Text = $"系统文件夹 ({SystemItems.Count})";
    }

    private void LoadDrives()
    {
        DriveItems.Clear();
        NetworkDriveItems.Clear();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady).OrderBy(d => d.Name))
        {
            var total = drive.TotalSize;
            var free = drive.AvailableFreeSpace;
            var usedPercent = total > 0 ? (total - free) * 100.0 / total : 0;
            var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "本地磁盘" : drive.VolumeLabel;

            var item = new HomeItem
            {
                Name = $"{label} ({drive.Name.TrimEnd('\\')})",
                Path = drive.RootDirectory.FullName,
                Glyph = drive.DriveType == DriveType.Network ? "\uE753" : "\uEDA2",
                Brush = drive.DriveType == DriveType.Network ? IconBrushes.Network : IconBrushes.Drive,
                UsagePercent = usedPercent,
                UsageBrush = usedPercent >= 90 ? UsageWarnBrush : UsageNormalBrush,
                CapacityText = $"{FormatSize(free)} 可用，共 {FormatSize(total)}",
            };

            if (drive.DriveType == DriveType.Network)
            {
                NetworkDriveItems.Add(item);
            }
            else
            {
                DriveItems.Add(item);
            }
        }

        DriveHeader.Text = $"本地驱动器 ({DriveItems.Count})";
        NetworkDriveHeader.Text = $"网络驱动器 ({NetworkDriveItems.Count})";
        NetworkSectionHeader.Visibility = NetworkDriveItems.Count > 0
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
        NetworkDriveGrid.Visibility = NetworkSectionHeader.Visibility;
    }

    private void LoadRecentFiles()
    {
        RecentItems.Clear();
        try
        {
            var recentDir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            var links = Directory.EnumerateFiles(recentDir, "*.lnk")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .Take(10);

            foreach (var link in links)
            {
                // Recent 中的 .lnk 命名保留原始文件名（含扩展名），去掉 .lnk 即可推断类型
                var displayName = System.IO.Path.GetFileNameWithoutExtension(link.Name);
                var ext = System.IO.Path.GetExtension(displayName);
                RecentItems.Add(new HomeItem
                {
                    Name = displayName,
                    Path = link.FullName,
                    Glyph = string.IsNullOrEmpty(ext) ? "\uE7C3" : FileListItemViewModel.ExtensionGlyph(ext),
                    Brush = string.IsNullOrEmpty(ext) ? IconBrushes.File : FileListItemViewModel.BrushForExtension(ext),
                });
            }
        }
        catch
        {
            // Recent 目录不可读时跳过该分区
        }

        RecentHeader.Text = $"最近打开的文件 ({RecentItems.Count})";
        RecentHeader.Visibility = RecentItems.Count > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        RecentGrid.Visibility = RecentHeader.Visibility;
    }

    private void OnFolderItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HomeItem item)
        {
            FolderActivated?.Invoke(item.Path);
        }
    }

    private void OnRecentItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not HomeItem item)
        {
            return;
        }

        try
        {
            // 直接启动 .lnk，由 Shell 解析目标
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.Path) { UseShellExecute = true });
        }
        catch
        {
            // 目标可能已被删除
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}
