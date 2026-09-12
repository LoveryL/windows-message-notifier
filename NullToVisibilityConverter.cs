using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Notifier
{
    public class NullToVisibilityConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return !(value is null || string.IsNullOrEmpty(value as string));
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}