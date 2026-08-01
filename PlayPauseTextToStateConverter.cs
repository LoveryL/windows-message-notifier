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
            var s = value as string ?? string.Empty;
            if (string.IsNullOrWhiteSpace(s)) return "Unknown";

            // Normalize common play symbols (check any of the common play glyphs)
            if (s.IndexOfAny(new[] {'\u25B6', '▶', '►'}) >= 0) return "Play";
            if (s.IndexOfAny(new[] {'\u23F8', '⏸'}) >= 0) return "Pause";

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