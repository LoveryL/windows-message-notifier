using System;
using System.Globalization;
using System.Windows.Data;

namespace Notifier
{
    // Converts various Play/Pause text forms (emoji, words) into a normalized state string: "Play", "Pause", or "Unknown".
    public class PlayPauseTextToStateConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value as string;
            if (string.IsNullOrEmpty(s)) return "Unknown";

            // Normalize common play symbols
            if (s.Contains('\u25B6') || s.Contains('▶') || s.Contains('►') || s.Contains('▶')) return "Play"; // ▶
            if (s.Contains('\u23F8') || s.Contains('⏸')) return "Pause"; // ⏸

            // Word-based fallbacks
            if (s.IndexOf("play", StringComparison.OrdinalIgnoreCase) >= 0) return "Play";
            if (s.IndexOf("pause", StringComparison.OrdinalIgnoreCase) >= 0) return "Pause";

            return "Unknown";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}