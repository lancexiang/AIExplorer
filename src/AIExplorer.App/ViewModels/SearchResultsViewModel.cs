using System.Collections.ObjectModel;
using System.Diagnostics;
using AIExplorer.Core.Extensions;
using AIExplorer.Core.Files;
using AIExplorer_App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;

namespace AIExplorer_App.ViewModels;

public partial class SearchHitItemViewModel : ObservableObject
{
    public SearchHitItemViewModel(SearchHit hit)
    {
        Hit = hit;
        TryApplyMaterialIcon();
    }

    public SearchHit Hit { get; }
    public string Name => Hit.Name;
    public string FullPath => Hit.FullPath;
    public bool IsDirectory => Hit.IsDirectory;
    public string DirectoryPath =>
        Hit.IsDirectory
            ? Hit.FullPath
            : (Path.GetDirectoryName(Hit.FullPath) ?? Hit.FullPath);

    public string LocationText => DirectoryPath;
    public string SizeText => Hit.IsDirectory || Hit.Size is null ? string.Empty : FormatSize(Hit.Size.Value);
    public string TypeText => Hit.IsDirectory ? "文件夹" : (Path.GetExtension(Hit.Name).TrimStart('.') is { Length: > 0 } ext ? ext.ToUpperInvariant() : "文件");
    public string IconGlyph => Hit.IsDirectory
        ? "\uE8B7"
        : FileListItemViewModel.ExtensionGlyph(Path.GetExtension(Hit.Name));

    [ObservableProperty]
    private ImageSource? iconImage;

    [ObservableProperty]
    private bool hasBitmapIcon;

    private void TryApplyMaterialIcon()
    {
        var image = FileTypeIconService.GetImageSource(Hit.Name, Hit.IsDirectory);
        if (image is null)
        {
            return;
        }

        IconImage = image;
        HasBitmapIcon = true;
    }

    private static string FormatSize(long bytes)
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

public partial class SearchResultsViewModel : ObservableObject, IDisposable
{
    private readonly IFileSystemService _fileSystemService;
    private readonly Func<string, Task> _navigateToFolderAsync;
    private CancellationTokenSource? _cts;

    public SearchResultsViewModel(
        string query,
        string providerLabel,
        IFileSystemService fileSystemService,
        Func<string, Task> navigateToFolderAsync)
    {
        Query = query;
        ProviderLabel = providerLabel;
        _fileSystemService = fileSystemService;
        _navigateToFolderAsync = navigateToFolderAsync;
        Title = $"搜索: {query}";
    }

    public string Query { get; }
    public string ProviderLabel { get; }
    public string Title { get; private set; }

    public ObservableCollection<SearchHitItemViewModel> Results { get; } = [];

    [ObservableProperty]
    private bool isSearching;

    [ObservableProperty]
    private string statusText = "正在搜索…";

    [ObservableProperty]
    private SearchHitItemViewModel? selectedItem;

    public async Task RunAsync(ISearchProvider provider, SearchRequest request)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Results.Clear();
        IsSearching = true;
        StatusText = $"正在通过 {ProviderLabel} 搜索…";

        try
        {
            await foreach (var hit in provider.SearchAsync(request, token))
            {
                Results.Add(new SearchHitItemViewModel(hit));
            }

            StatusText = Results.Count == 0
                ? $"无结果（{ProviderLabel}）"
                : $"{Results.Count} 条结果 · {ProviderLabel}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
        }
        catch (Exception ex)
        {
            StatusText = $"搜索失败：{ex.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsSearching = false;
            }
        }
    }

    [RelayCommand]
    private async Task ActivateSelectedAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        await ActivateHitAsync(SelectedItem);
    }

    public async Task ActivateHitAsync(SearchHitItemViewModel item)
    {
        SelectedItem = item;
        if (item.IsDirectory)
        {
            await _navigateToFolderAsync(item.FullPath);
            return;
        }

        // 文件：直接用系统关联打开（工作目录由 OpenPathAsync 设为文件所在目录）
        await _fileSystemService.OpenPathAsync(item.FullPath);
    }

    [RelayCommand]
    private async Task OpenContainingFolderAsync(SearchHitItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null)
        {
            return;
        }

        var folder = item.IsDirectory
            ? item.FullPath
            : item.DirectoryPath;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            await _navigateToFolderAsync(folder);
        }
    }

    [RelayCommand]
    private async Task OpenHitAsync(SearchHitItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        await ActivateHitAsync(item);
    }

    [RelayCommand]
    private async Task RevealInExplorerAsync(SearchHitItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null)
        {
            return;
        }

        try
        {
            var target = item.FullPath;
            if (File.Exists(target) || Directory.Exists(target))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{target}\"",
                    UseShellExecute = true,
                });
            }
        }
        catch
        {
        }

        await Task.CompletedTask;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
