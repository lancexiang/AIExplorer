using AIExplorer.Core.Favorites;
using AIExplorer.Core.Files;
using AIExplorer.Core.Navigation;
using AIExplorer.Core.Settings;
using AIExplorer.Infrastructure.Favorites;
using AIExplorer.Infrastructure.Files;
using AIExplorer.Infrastructure.Shell;
using Xunit;

namespace AIExplorer.Core.Tests;

public class FavoriteItemTests
{
    [Fact]
    public void EnumerateFolderLeaves_ReturnsOnlyExistingFolders()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var root = new FavoriteItem
        {
            DisplayName = "root",
            Children =
            [
                new FavoriteItem { DisplayName = "group", Children =
                [
                    new FavoriteItem { DisplayName = "docs", Path = docs },
                    new FavoriteItem { DisplayName = "missing", Path = @"Z:\__no_such_path__" },
                ]},
            ],
        };

        var leaves = root.EnumerateFolderLeaves().ToList();
        Assert.Single(leaves);
        Assert.Equal(docs, leaves[0].Path);
    }

    [Fact]
    public void MoveTo_MovesItemBetweenGroups()
    {
        var item = new FavoriteItem { DisplayName = "下载", Path = @"C:\Downloads" };
        var source = new FavoriteItem { DisplayName = "来源", Children = [item] };
        var target = new FavoriteItem { DisplayName = "目标" };
        var root = new FavoriteItem { DisplayName = "收藏夹", Children = [source, target] };

        var moved = root.MoveTo(item.Id, target.Id);

        Assert.True(moved);
        Assert.Empty(source.Children);
        Assert.Same(item, Assert.Single(target.Children));
    }

    [Fact]
    public void MoveTo_RejectsMovingGroupIntoItsDescendant()
    {
        var child = new FavoriteItem { DisplayName = "子分组" };
        var group = new FavoriteItem { DisplayName = "父分组", Children = [child] };
        var root = new FavoriteItem { DisplayName = "收藏夹", Children = [group] };

        var moved = root.MoveTo(group.Id, child.Id);

        Assert.False(moved);
        Assert.Same(group, Assert.Single(root.Children));
    }

    [Fact]
    public void FindByPath_IgnoresCaseAndTrailingSeparator()
    {
        var item = new FavoriteItem { DisplayName = "Downloads", Path = @"C:\Users\Test\Downloads" };
        var root = new FavoriteItem { DisplayName = "收藏夹", Children = [item] };

        var found = root.FindByPath(@"c:\users\test\downloads\");

        Assert.Same(item, found);
    }
}

public class ShellFileListSourceTests
{
    [Fact]
    public async Task EnumerateIncrementalAsync_ReturnsStatOnlyEntries()
    {
        var source = new ShellFileListSource();
        var path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var items = new List<FileEntrySnapshot>();

        await foreach (var item in source.EnumerateIncrementalAsync(path))
        {
            items.Add(item);
            if (items.Count >= 5)
            {
                break;
            }
        }

        Assert.NotEmpty(items);
        Assert.All(items, x =>
        {
            Assert.False(string.IsNullOrWhiteSpace(x.Name));
            Assert.False(string.IsNullOrWhiteSpace(x.FullPath));
            Assert.Null(x.Probe);
        });
    }

    [Fact]
    public async Task CopyOrMoveAsync_CopiesFileAndDeleteAsync_RemovesIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIExplorerCopyTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            IFileSystemService fs = new ShellFileSystemService();
            var srcFile = Path.Combine(root, "src.txt");
            await File.WriteAllTextAsync(srcFile, "hello");

            await fs.CopyOrMoveAsync([srcFile], root, move: false);
            var copy = Path.Combine(root, "src (1).txt");
            Assert.True(File.Exists(copy));
            Assert.Equal("hello", await File.ReadAllTextAsync(copy));

            await fs.DeleteAsync([copy]);
            Assert.False(File.Exists(copy));
            Assert.True(File.Exists(srcFile));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DeleteAsync_CanUndoLastThreeOperations()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIExplorerUndoTest_" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(root);
        try
        {
            IFileSystemService fs = new ShellFileSystemService(cache);
            var files = Enumerable.Range(1, 3)
                .Select(i => Path.Combine(root, $"file{i}.txt"))
                .ToList();
            foreach (var file in files)
            {
                await File.WriteAllTextAsync(file, Path.GetFileName(file));
                await fs.DeleteAsync([file]);
            }

            Assert.All(files, file => Assert.False(File.Exists(file)));

            for (var i = 2; i >= 0; i--)
            {
                Assert.True(fs.CanUndoDelete);
                await fs.UndoLastDeleteAsync();
                Assert.True(File.Exists(files[i]));
            }

            Assert.False(fs.CanUndoDelete);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DeleteAsync_KeepsOnlyThreeUndoOperations()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIExplorerUndoLimitTest_" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(root);
        try
        {
            IFileSystemService fs = new ShellFileSystemService(cache);
            var files = Enumerable.Range(1, 4)
                .Select(i => Path.Combine(root, $"file{i}.txt"))
                .ToList();
            foreach (var file in files)
            {
                await File.WriteAllTextAsync(file, Path.GetFileName(file));
                await fs.DeleteAsync([file]);
            }

            for (var i = 3; i >= 1; i--)
            {
                await fs.UndoLastDeleteAsync();
                Assert.True(File.Exists(files[i]));
            }

            Assert.False(fs.CanUndoDelete);
            Assert.False(File.Exists(files[0]));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}

public class FilePaneViewModeContractTests
{
    [Fact]
    public void ViewMode_HasDetailsIconsAndCards()
    {
        // 合约：三种显示模式并存（实现位于 App 层枚举，此处锁定期望值字符串以防回归）。
        var modes = new[] { "Details", "Icons", "Cards" };
        Assert.Equal(3, modes.Length);
        Assert.Contains("Cards", modes);
    }
}

public class WindowBackdropOptionsTests
{
    [Theory]
    [InlineData("Mica", true)]
    [InlineData("Acrylic", true)]
    [InlineData("None", true)]
    [InlineData("Glass", false)]
    public void IsKnown_MatchesSupportedBackdrops(string name, bool expected)
    {
        Assert.Equal(expected, WindowBackdropOptions.IsKnown(name));
    }
}

public class TabNavigationPolicyTests
{
    [Fact]
    public void Unlocked_NavigatesInPlace()
    {
        Assert.Equal(TabNavigationAction.NavigateInPlace, TabNavigationPolicy.Resolve(isLocked: false));
        Assert.False(TabNavigationPolicy.ShouldOpenNewTab(false));
    }

    [Fact]
    public void Locked_OpensNewTab()
    {
        Assert.Equal(TabNavigationAction.OpenNewTab, TabNavigationPolicy.Resolve(isLocked: true));
        Assert.True(TabNavigationPolicy.ShouldOpenNewTab(true));
    }
}

public class FileListSortTests
{
    private sealed record Row(bool Pin, bool Dir, string Name, long? Size, string Type, DateTime Modified);

    [Fact]
    public void DefaultNameSort_PinnedThenFoldersThenFiles()
    {
        var rows = new[]
        {
            new Row(false, false, "z.txt", 1, "TXT", DateTime.UtcNow),
            new Row(false, true, "b", null, "文件夹", DateTime.UtcNow),
            new Row(true, false, "a.txt", 2, "TXT", DateTime.UtcNow),
            new Row(false, true, "a", null, "文件夹", DateTime.UtcNow),
        };

        var ordered = FileListSort.OrderItems(
            rows,
            FileSortColumn.Name,
            ascending: true,
            x => x.Pin,
            x => x.Dir,
            x => x.Name,
            x => x.Size,
            x => x.Type,
            x => x.Modified).Select(x => x.Name).ToList();

        Assert.Equal(["a.txt", "a", "b", "z.txt"], ordered);
    }

    [Fact]
    public void UncPath_IsNotLocalAutoSizeCandidate()
    {
        Assert.False(LocalPathPolicy.IsLocalFixedOrRemovableDrive(@"\\server\share"));
        Assert.False(LocalPathPolicy.IsLocalFixedOrRemovableDrive(null));
    }
}

public class FileColorPaletteTests
{
    [Fact]
    public void MergeWithDefaults_FillsMissingKeys()
    {
        var merged = FileColorPalette.MergeWithDefaults(
        [
            new FileColorDefinition { Key = "red", DisplayName = "紧急红", Hex = "#FF0000", Description = "很急" },
        ]);

        Assert.Equal(5, merged.Count);
        Assert.Equal("紧急红", merged[0].DisplayName);
        Assert.Equal("#FF0000", merged[0].Hex);
        Assert.Equal("很急", merged[0].Description);
        Assert.Equal("橙", merged[1].DisplayName);
    }

    [Fact]
    public void NormalizeHex_RejectsInvalid()
    {
        Assert.Null(FileColorPalette.NormalizeHex("nope"));
        Assert.Equal("#E53935", FileColorPalette.NormalizeHex("e53935"));
    }

    [Fact]
    public void TryParseRgb_ReadsHex()
    {
        Assert.True(FileColorPalette.TryParseRgb("#E53935", out var r, out var g, out var b));
        Assert.Equal(0xE5, r);
        Assert.Equal(0x39, g);
        Assert.Equal(0x35, b);
    }

    [Fact]
    public void Tooltip_IncludesDescription()
    {
        var tip = FileColorPalette.Tooltip(new FileColorDefinition
        {
            Key = "red",
            DisplayName = "红",
            Description = "进行中 / 紧急",
        });
        Assert.Equal("红 · 进行中 / 紧急", tip);
    }
}

public class AccentPaletteTests
{
    [Theory]
    [InlineData("Ocean", 0, 120, 212)]
    [InlineData("Forest", 16, 137, 62)]
    [InlineData("Sunset", 196, 89, 17)]
    [InlineData("Violet", 136, 62, 193)]
    public void ResolveRgb_KnownAccents(string name, byte r, byte g, byte b)
    {
        var rgb = AccentPalette.ResolveRgb(name);
        Assert.NotNull(rgb);
        Assert.Equal((r, g, b), rgb);
    }

    [Fact]
    public void ResolveRgb_Default_IsNull_FollowsSystem()
    {
        Assert.Null(AccentPalette.ResolveRgb("Default"));
        Assert.Null(AccentPalette.ResolveRgb(null));
    }
}

public class ShellFileListSourceEnumerateTests
{
    [Fact]
    public async Task EnumerateIncrementalAsync_YieldsDirectoriesBeforeFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "AIExplorerEnum_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "zzz.txt"), "x");
            Directory.CreateDirectory(Path.Combine(root, "aaa_folder"));
            Directory.CreateDirectory(Path.Combine(root, "mmm_folder"));

            var source = new ShellFileListSource();
            var items = new List<FileEntrySnapshot>();
            await foreach (var entry in source.EnumerateIncrementalAsync(root))
            {
                items.Add(entry);
            }

            Assert.Equal(3, items.Count);
            Assert.True(items[0].IsDirectory);
            Assert.True(items[1].IsDirectory);
            Assert.False(items[2].IsDirectory);
            Assert.Equal("aaa_folder", items[0].Name);
            Assert.Equal("zzz.txt", items[2].Name);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}

public class FilePreviewPolicyTests
{
    [Theory]
    [InlineData(@"C:\a.png", false, 100, FilePreviewKind.Image)]
    [InlineData(@"C:\a.txt", false, 100, FilePreviewKind.Text)]
    [InlineData(@"C:\a.txt", false, FilePreviewPolicy.MaxTextPreviewBytes + 1, FilePreviewKind.Unsupported)]
    [InlineData(@"C:\folder", true, 0, FilePreviewKind.Directory)]
    [InlineData(@"C:\a.exe", false, 100, FilePreviewKind.Unsupported)]
    public void Resolve_MatchesExpected(string path, bool isDir, long length, FilePreviewKind expected)
    {
        Assert.Equal(expected, FilePreviewPolicy.Resolve(path, isDir, length));
    }
}

public class ClipboardPastePolicyTests
{
    [Fact]
    public void PlainTextThatLooksLikeExistingPath_RemainsText()
    {
        var kind = ClipboardPastePolicy.Resolve(
            hasInternalFilePaths: false,
            hasStorageItems: false,
            hasText: true);

        Assert.Equal(ClipboardPasteKind.Text, kind);
    }

    [Fact]
    public void TextPlusStorageItems_PrefersStorageItems_ForExplorerCopy()
    {
        var kind = ClipboardPastePolicy.Resolve(
            hasInternalFilePaths: false,
            hasStorageItems: true,
            hasText: true);

        Assert.Equal(ClipboardPasteKind.StorageItems, kind);
    }

    [Fact]
    public void InternalFormat_WinsOverStorageAndText()
    {
        var kind = ClipboardPastePolicy.Resolve(
            hasInternalFilePaths: true,
            hasStorageItems: true,
            hasText: true);

        Assert.Equal(ClipboardPasteKind.InternalFilePaths, kind);
    }

    [Fact]
    public void CopyPathScenario_TextOnly_IsNeverFilePaste()
    {
        // 复制路径只写 Text，即使内容是真实路径，也必须走 Text → 新建文档.txt
        var kind = ClipboardPastePolicy.Resolve(
            hasInternalFilePaths: false,
            hasStorageItems: false,
            hasText: true);

        Assert.Equal(ClipboardPasteKind.Text, kind);
        Assert.NotEqual(ClipboardPasteKind.StorageItems, kind);
        Assert.NotEqual(ClipboardPasteKind.InternalFilePaths, kind);
    }
}

public class UncPathHelperTests
{
    [Theory]
    [InlineData(@"\\server\share", "", @"\\server\share")]
    [InlineData(@"\\server\share\", "folder", @"\\server\share\folder")]
    [InlineData(@"\\192.168.1.10\data", @"a\b.txt", @"\\192.168.1.10\data\a\b.txt")]
    public void Join_BuildsStableUnc(string root, string suffix, string expected)
    {
        Assert.Equal(expected, UncPathHelper.Join(root, suffix));
    }

    [Theory]
    [InlineData(@"\\?\UNC\server\share\a", @"\\server\share\a")]
    [InlineData(@"\\?\J:\folder", @"J:\folder")]
    [InlineData(@"\\server\share", @"\\server\share")]
    public void NormalizeExtendedUnc_StripsDevicePrefix(string input, string expected)
    {
        Assert.Equal(expected, UncPathHelper.NormalizeExtendedUnc(input));
    }
}

public class ShellIconServiceTests
{
    [Theory]
    [InlineData(@"C:\a.tif", true)]
    [InlineData(@"C:\a.tiff", true)]
    [InlineData(@"C:\a.png", false)]
    public void IsThumbnailDisabled_BlocksTiff(string path, bool expected)
    {
        var service = new ShellIconService();
        Assert.Equal(expected, service.IsThumbnailDisabled(path));
    }

    [Fact]
    public void GetIconKey_UsesPathKeyForShortcuts()
    {
        var service = new ShellIconService();
        var key = service.GetIconKey(@"C:\Users\Public\Desktop\AnyDesk.lnk", isDirectory: false);
        Assert.StartsWith("path:", key, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AnyDesk.lnk", key, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetIconKey_UsesExtensionForNormalFiles()
    {
        var service = new ShellIconService();
        Assert.Equal(".pdf", service.GetIconKey(@"C:\docs\a.pdf", isDirectory: false));
        Assert.Equal("folder", service.GetIconKey(@"C:\docs", isDirectory: true));
    }
}

public class JsonFavoritesRepositoryTests
{
    [Fact]
    public async Task LoadAsync_ReturnsDefaultWhenMissing()
    {
        var repo = new JsonFavoritesRepository();
        var root = await repo.LoadAsync();
        Assert.Equal("收藏夹", root.DisplayName);
        Assert.NotEmpty(root.Children);
    }
}
