using AIExplorer.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AIExplorer_App.Converters;

public sealed class ColorKeyToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value as string;
        if (string.IsNullOrWhiteSpace(key))
        {
            return new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        }

        IEnumerable<FileColorDefinition> palette = FileColorPalette.Defaults;
        try
        {
            var settings = App.Services.GetService<ISettingsService>();
            if (settings?.FileColors is { Count: > 0 })
            {
                palette = settings.FileColors;
            }
        }
        catch
        {
        }

        var def = FileColorPalette.Find(palette, key);
        if (def is not null && FileColorPalette.TryParseRgb(def.Hex, out var r, out var g, out var b))
        {
            return new SolidColorBrush(Color.FromArgb(255, r, g, b));
        }

        return new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotImplementedException();
}
