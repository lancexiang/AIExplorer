using AIExplorer_App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace AIExplorer_App.Views;

public sealed partial class SearchResultsView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(SearchResultsViewModel),
            typeof(SearchResultsView),
            new PropertyMetadata(null, OnViewModelChanged));

    private SearchHitItemViewModel? _contextHit;

    public SearchResultsView()
    {
        InitializeComponent();
    }

    public SearchResultsViewModel? ViewModel
    {
        get => (SearchResultsViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SearchResultsView view)
        {
            view.DataContext = e.NewValue;
        }
    }

    private async void OnListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var item = FindHit(e.OriginalSource as DependencyObject);
        if (item is null)
        {
            return;
        }

        await ViewModel.ActivateHitAsync(item);
        e.Handled = true;
    }

    private void OnListRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        _contextHit = FindHit(e.OriginalSource as DependencyObject);
        if (_contextHit is not null && ViewModel is not null)
        {
            ViewModel.SelectedItem = _contextHit;
        }
    }

    private async void OnOpenHitClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.OpenHitCommand.ExecuteAsync(_contextHit ?? ViewModel.SelectedItem);
    }

    private async void OnOpenContainingClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.OpenContainingFolderCommand.ExecuteAsync(_contextHit ?? ViewModel.SelectedItem);
    }

    private async void OnRevealInExplorerClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        await ViewModel.RevealInExplorerCommand.ExecuteAsync(_contextHit ?? ViewModel.SelectedItem);
    }

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        var item = _contextHit ?? ViewModel?.SelectedItem;
        if (item is null || string.IsNullOrWhiteSpace(item.FullPath))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(item.FullPath);
        Clipboard.SetContent(package);
    }

    private static SearchHitItemViewModel? FindHit(DependencyObject? start)
    {
        var current = start;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: SearchHitItemViewModel item })
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
