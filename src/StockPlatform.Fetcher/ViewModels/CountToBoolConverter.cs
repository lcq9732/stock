using System.Globalization;
using System.Windows.Data;

namespace StockPlatform.Fetcher.ViewModels;

/// <summary>Converts an int count to bool: 0 → false, anything else → true.</summary>
public class CountToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i > 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
