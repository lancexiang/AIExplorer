using Microsoft.UI.Xaml.Data;

namespace AIExplorer_App.Converters;

/// <summary>空字符串转为 null，避免 WinUI 对 "" 仍弹出空白 ToolTip。</summary>
public sealed class NullIfEmptyStringConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        var s = value as string;
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value ?? string.Empty;
}
