namespace AIExplorer.Core.Favorites;

public sealed class FavoriteItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string DisplayName { get; set; } = string.Empty;
    public string? Path { get; set; }
    public List<FavoriteItem> Children { get; set; } = [];
    public bool IsGroup => string.IsNullOrWhiteSpace(Path);

    public IEnumerable<FavoriteItem> EnumerateFolderLeaves()
    {
        if (!IsGroup && !string.IsNullOrWhiteSpace(Path) && Directory.Exists(Path))
        {
            yield return this;
        }

        foreach (var child in Children)
        {
            foreach (var leaf in child.EnumerateFolderLeaves())
            {
                yield return leaf;
            }
        }
    }

    public FavoriteItem? FindByPath(string path)
    {
        if (!string.IsNullOrWhiteSpace(Path) && PathsEqual(Path, path))
        {
            return this;
        }

        foreach (var child in Children)
        {
            var found = child.FindByPath(path);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    public FavoriteItem? FindById(string id)
    {
        if (string.Equals(Id, id, StringComparison.Ordinal))
        {
            return this;
        }

        foreach (var child in Children)
        {
            var found = child.FindById(id);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    public bool MoveTo(string itemId, string targetGroupId)
    {
        var item = FindById(itemId);
        var target = FindById(targetGroupId);
        if (item is null || target is null || !target.IsGroup || ReferenceEquals(item, this))
        {
            return false;
        }

        // 不能把分组移入自身或自己的后代。
        if (ReferenceEquals(item, target) || item.FindById(target.Id) is not null)
        {
            return false;
        }

        var source = FindParentOf(itemId);
        if (source is null)
        {
            return false;
        }

        // 已在目标组内：视为成功（支持拖放到当前所在组）
        if (ReferenceEquals(source, target))
        {
            return true;
        }

        source.Children.Remove(item);
        target.Children.Add(item);
        return true;
    }

    public FavoriteItem? FindParentOf(string childId)
    {
        if (Children.Any(child => string.Equals(child.Id, childId, StringComparison.Ordinal)))
        {
            return this;
        }

        foreach (var child in Children)
        {
            var found = child.FindParentOf(childId);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                System.IO.Path.GetFullPath(left).TrimEnd('\\', '/'),
                System.IO.Path.GetFullPath(right).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

public interface IFavoritesRepository
{
    Task<FavoriteItem> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(FavoriteItem root, CancellationToken cancellationToken = default);
}
