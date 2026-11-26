using System.Globalization;
using System.Windows.Data;

namespace LyricsXQuiet.App.State;

public sealed class LyricFontFamilyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        LyricFontResolver.Create(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}
