using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Garou.UI.Converters;

public sealed class BoundsToLocalRectConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is Rect bounds
            ? new Rect(0, 0, bounds.Width, bounds.Height)
            : new Rect();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
