using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace AIExplorer_App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is true;
        if (parameter is string text && text.Equals("Invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
