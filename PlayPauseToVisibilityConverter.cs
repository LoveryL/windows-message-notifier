using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Notifier
{
    // Converts PlayPauseIcon.Text into Visibility for a target state passed via ConverterParameter ("Play" or "Pause").
    public class PlayPauseToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value as string ?? string.Empty;
            var param = (parameter as string) ?? string.Empty;
            if (string.IsNullOrEmpty(param)) return Visibility.Collapsed;

            bool isPlay = s.Contains('\u25B6') || s.Contains('▶') || s.IndexOf("play", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isPause = s.Contains('\u23F8') || s.Contains('⏸') || s.IndexOf("pause", StringComparison.OrdinalIgnoreCase) >= 0;

            if (param.Equals("Play", StringComparison.OrdinalIgnoreCase) && isPlay) return Visibility.Visible;
            if (param.Equals("Pause", StringComparison.OrdinalIgnoreCase) && isPause) return Visibility.Visible;

            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}