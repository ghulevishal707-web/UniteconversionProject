using System.Globalization;
using System.Windows.Data;

namespace MusicTrackGen.App;

/// <summary>Lets two radio buttons share one bool property without a second field.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : false;
}
