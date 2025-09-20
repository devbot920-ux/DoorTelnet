using System;
using System.Globalization;
using System.Windows.Data;

namespace DoorTelnet.Wpf.Converters;

public class PercentToWidthConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int pct && parameter is string pStr && double.TryParse(pStr, out var max))
        {
            return Math.Max(0, Math.Min(100, pct)) / 100.0 * max;
        }
        return 0d;
    }

    public object? ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
