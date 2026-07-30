using System;
using Microsoft.Win32;

namespace Notifier
{
    public class AppConfig
    {
        public double MainWindowOpacity { get; set; } = 1.0;
        public double MessageSummaryOpacity { get; set; } = 1.0;
        public double SettingWindowOpacity { get; set; } = 1.0;
        public double MainWindowLeft { get; set; } = double.NaN;
        public double MainWindowTop { get; set; } = double.NaN;
        public bool MainWindowShown { get; set; } = false;
    }

    public static class RegistryConfig
    {
        private const string ConfigKey = @"Software\\Notifier\\Config";

        public static AppConfig LoadOrCreateDefaults()
        {
            var defaults = new AppConfig
            {
                MainWindowOpacity = 1.0,
                MessageSummaryOpacity = 1.0,
                SettingWindowOpacity = 1.0,
                MainWindowLeft = double.NaN,
                MainWindowTop = double.NaN,
                MainWindowShown = false
            };

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ConfigKey, true);
                if (key == null)
                {
                    // No existing config, write defaults and return them
                    Save(defaults);
                    return defaults;
                }

                // Read and validate values; if any value is missing or invalid, overwrite with defaults.
                if (!TryReadDoubleInRange(key, "MainWindowOpacity", 0.0, 1.0, out double mwOpacity)) { Save(defaults); return defaults; }
                if (!TryReadDoubleInRange(key, "MessageSummaryOpacity", 0.0, 1.0, out double msOpacity)) { Save(defaults); return defaults; }
                if (!TryReadDoubleInRange(key, "SettingWindowOpacity", 0.0, 1.0, out double sOpacity)) { Save(defaults); return defaults; }

                if (!TryReadDoubleNullable(key, "MainWindowLeft", out double left)) { Save(defaults); return defaults; }
                if (!TryReadDoubleNullable(key, "MainWindowTop", out double top)) { Save(defaults); return defaults; }

                if (!TryReadBool(key, "MainWindowShown", out bool shown)) { Save(defaults); return defaults; }

                return new AppConfig
                {
                    MainWindowOpacity = mwOpacity,
                    MessageSummaryOpacity = msOpacity,
                    SettingWindowOpacity = sOpacity,
                    MainWindowLeft = left,
                    MainWindowTop = top,
                    MainWindowShown = shown
                };
            }
            catch
            {
                // On any unexpected error, reset to defaults.
                Save(defaults);
                return defaults;
            }
        }

        private static bool TryReadDoubleInRange(RegistryKey key, string name, double min, double max, out double val)
        {
            val = double.NaN;
            var obj = key.GetValue(name);
            if (obj == null) return false;
            if (!TryConvertToDouble(obj, out double d)) return false;
            if (double.IsNaN(d) || d < min || d > max) return false;
            val = d; return true;
        }

        private static bool TryReadDoubleNullable(RegistryKey key, string name, out double val)
        {
            val = double.NaN;
            var obj = key.GetValue(name);
            if (obj == null) return true; // treat missing as NaN (unset)
            if (!TryConvertToDouble(obj, out double d)) return false;
            val = d; return true;
        }

        private static bool TryConvertToDouble(object obj, out double val)
        {
            val = double.NaN;
            switch (obj)
            {
                case double dd: val = dd; return true;
                case float f: val = f; return true;
                case int i: val = i; return true;
                case long l: val = l; return true;
                case string s when double.TryParse(s, out double parsed): val = parsed; return true;
                default: return false;
            }
        }

        private static bool TryReadBool(RegistryKey key, string name, out bool val)
        {
            val = false;
            var obj = key.GetValue(name);
            if (obj == null) return false;
            switch (obj)
            {
                case int i: val = i != 0; return true;
                case long l: val = l != 0; return true;
                case string s when bool.TryParse(s, out bool b): val = b; return true;
                case string s2 when int.TryParse(s2, out int i2): val = i2 != 0; return true;
                default: return false;
            }
        }

        public static void Save(AppConfig cfg)
        {
            using var key = Registry.CurrentUser.CreateSubKey(ConfigKey);
            key.SetValue("MainWindowOpacity", cfg.MainWindowOpacity);
            key.SetValue("MessageSummaryOpacity", cfg.MessageSummaryOpacity);
            key.SetValue("SettingWindowOpacity", cfg.SettingWindowOpacity);
            if (!double.IsNaN(cfg.MainWindowLeft)) key.SetValue("MainWindowLeft", cfg.MainWindowLeft);
            if (!double.IsNaN(cfg.MainWindowTop)) key.SetValue("MainWindowTop", cfg.MainWindowTop);
            key.SetValue("MainWindowShown", cfg.MainWindowShown ? 1 : 0);
            // also store executable path similarly to startup-on-boot logic for reference
            key.SetValue("ExecutablePath", Environment.ProcessPath ?? string.Empty);
        }
    }
}
