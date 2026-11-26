using System.Globalization;
using System.Windows.Data;

namespace LyricsXQuiet.App.State;

public sealed class SettingChoiceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Equals(value, parameter);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Equals(value, true) ? parameter : System.Windows.Data.Binding.DoNothing;
}
