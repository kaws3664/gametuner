using System.Globalization;
using System.Windows.Data;

namespace GameTuner.App.ViewModels;

public sealed class EnumChoiceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && parameter is string choice ? Enum.Parse(targetType, choice) : Binding.DoNothing;
}
