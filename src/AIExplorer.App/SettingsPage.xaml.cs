using AIExplorer_App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIExplorer_App;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.Reload();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
        else
        {
            Frame.Navigate(typeof(MainPage));
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveCommand.ExecuteAsync(null);
        var dialog = new ContentDialog
        {
            Title = "已保存",
            Content = "设置已写入。部分扩展开关将在下次启动或重新初始化后生效。",
            CloseButtonText = "确定",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
