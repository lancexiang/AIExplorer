using System.Collections.ObjectModel;
using AIExplorer.Core.Favorites;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AIExplorer_App.ViewModels;

public partial class FavoriteNodeViewModel : ObservableObject
{
    public FavoriteNodeViewModel(FavoriteItem model, FavoriteNodeViewModel? parent = null)
    {
        Model = model;
        Parent = parent;
        foreach (var child in model.Children)
        {
            Children.Add(new FavoriteNodeViewModel(child, this));
        }
    }

    public FavoriteItem Model { get; }
    public FavoriteNodeViewModel? Parent { get; }
    public ObservableCollection<FavoriteNodeViewModel> Children { get; } = [];

    public string DisplayName
    {
        get => Model.DisplayName;
        set
        {
            if (Model.DisplayName != value)
            {
                Model.DisplayName = value;
                OnPropertyChanged();
            }
        }
    }

    public string? Path => Model.Path;
    public bool IsGroup => Model.IsGroup;
    // 分组用填充文件夹 + 紫罗兰色，收藏项用实心星 + 金色，明显区别于文件列表的黄色文件夹
    public string IconGlyph => IsGroup ? "\uE8B7" : "\uE735";
    public Microsoft.UI.Xaml.Media.SolidColorBrush GlyphBrush => IsGroup ? IconBrushes.FavoriteGroup : IconBrushes.Favorite;
    public string Subtitle => IsGroup ? $"{Model.Children.Count} 项" : (Path ?? string.Empty);

    public FavoriteNodeViewModel AddGroup(string name)
    {
        var item = new FavoriteItem { DisplayName = name };
        Model.Children.Add(item);
        var vm = new FavoriteNodeViewModel(item, this);
        Children.Add(vm);
        return vm;
    }

    public FavoriteNodeViewModel AddFolder(string name, string path)
    {
        var item = new FavoriteItem { DisplayName = name, Path = path };
        Model.Children.Add(item);
        var vm = new FavoriteNodeViewModel(item, this);
        Children.Add(vm);
        return vm;
    }

    public void Remove()
    {
        Parent?.Model.Children.Remove(Model);
        Parent?.Children.Remove(this);
    }

    public IEnumerable<FavoriteItem> GetFolderLeaves() => Model.EnumerateFolderLeaves();

    public IEnumerable<FavoriteNodeViewModel> EnumerateGroups()
    {
        if (IsGroup)
        {
            yield return this;
        }

        foreach (var child in Children)
        {
            foreach (var group in child.EnumerateGroups())
            {
                yield return group;
            }
        }
    }
}
