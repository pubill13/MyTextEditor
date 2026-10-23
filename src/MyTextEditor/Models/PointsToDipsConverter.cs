using System.Globalization;
using System.Windows.Data;

namespace MyTextEditor.Models;

public sealed class PointsToDipsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double points ? points * 96d / 72d : System.Windows.DependencyProperty.UnsetValue;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
