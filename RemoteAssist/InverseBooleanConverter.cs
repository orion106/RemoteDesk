namespace RemoteAssist;
public sealed class InverseBooleanConverter : IValueConverter { public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is bool b && !b; public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is bool b && !b; }
