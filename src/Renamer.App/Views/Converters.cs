using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Renamer.App.Views;

/// <summary>Visible when the bound value is true, or a non-empty string. "invert" as the parameter flips it.</summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isOn = value switch
        {
            bool flag => flag,
            string text => text.Length > 0,
            int number => number > 0,
            _ => value is not null,
        };

        if (parameter is "invert")
        {
            isOn = !isOn;
        }

        return isOn ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
