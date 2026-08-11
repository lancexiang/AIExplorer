using System.Text.Json;
using AIExplorer.Core.Favorites;

namespace AIExplorer.Infrastructure.Favorites;

public sealed class JsonFavoritesRepository : IFavoritesRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _storagePath;

    public JsonFavoritesRepository()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIExplorer");
        Directory.CreateDirectory(folder);
        _storagePath = Path.Combine(folder, "favorites.json");
    }

    public async Task<FavoriteItem> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_storagePath))
        {
            return CreateDefaultRoot();
        }

        try
        {
            await using var stream = File.OpenRead(_storagePath);
            var root = await JsonSerializer.DeserializeAsync<FavoriteItem>(stream, JsonOptions, cancellationToken);
            return root ?? CreateDefaultRoot();
        }
        catch
        {
            return CreateDefaultRoot();
        }
    }

    public async Task SaveAsync(FavoriteItem root, CancellationToken cancellationToken = default)
    {
        await using var stream = File.Create(_storagePath);
        await JsonSerializer.SerializeAsync(stream, root, JsonOptions, cancellationToken);
    }

    private static FavoriteItem CreateDefaultRoot()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");

        return new FavoriteItem
        {
            DisplayName = "收藏夹",
            Children =
            [
                new FavoriteItem
                {
                    DisplayName = "常用",
                    Children =
                    [
                        new FavoriteItem
                        {
                            DisplayName = "文档",
                            Path = Directory.Exists(documents) ? documents : null,
                        },
                        new FavoriteItem
                        {
                            DisplayName = "下载",
                            Path = Directory.Exists(downloads) ? downloads : null,
                        },
                    ],
                },
            ],
        };
    }
}
