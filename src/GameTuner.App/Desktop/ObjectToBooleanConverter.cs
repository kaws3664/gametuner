using System.Globalization;
using System.Windows.Data;

namespace GameTuner.App.Desktop;

public sealed class ObjectToBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not null;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
