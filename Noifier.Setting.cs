using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Notifier;
public class Settings_Manager
{
    public event Action<SettingType>? SettingChanged;

    private static string AppConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");
    private static string LocalConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Notifier", "config.json");

    // Resolve config read/write paths: prefer per-user local appdata for writes (works when packaged),
    // but if legacy file exists in AppContext.BaseDirectory, read from there for compatibility.
    private static string ConfigPath
    {
        get
        {
            if (File.Exists(LocalConfigPath)) return LocalConfigPath;
            return AppConfigPath;
        }
    }

    public bool is_toast_enabled { get; private set; } = true;
    public float opacity { get; private set; } = 1.0f;
    public float window_top { get; private set; } = 15.0f;
    public bool is_middle { get; private set; } = true;
    public int show_time { get; private set; } = 3;
    public float window_left { get; private set; } = 0f;

    public enum SettingType
    {
        Toast,
        Opacity,
        window_top,
        ismiddle,
        window_left,
        show_time
    }

    private Dictionary<string, object> BuildConfigSnapshot()
    {
        return new Dictionary<string, object>
        {
            ["Notifier.Toast.Enabled"] = is_toast_enabled,
            ["Notifier.Opacity"] = opacity,
            ["Notifier.Window.Top"] = window_top,
            ["Notifier.isMiddle"] = is_middle,
            ["Notifier.Window.Left"] = window_left,
            ["Notifier.Message.ShowTime"] = show_time
        };
    }

    private void SaveConfig()
    {
        var directory = Path.GetDirectoryName(LocalConfigPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(LocalConfigPath, JsonSerializer.Serialize(BuildConfigSnapshot()));
    }

    public void init_settings()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                SaveConfig();
                return;
            }

            var jsonString = File.ReadAllText(ConfigPath);
            if (string.IsNullOrWhiteSpace(jsonString))
            {
                SaveConfig();
                return;
            }

            var loadedConfig = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonString)
                ?? new Dictionary<string, object>();

            Logger.Debug("Readed config");

            if (loadedConfig.TryGetValue("Notifier.Toast.Enabled", out var toastEnabled))
                is_toast_enabled = ((JsonElement)toastEnabled).GetBoolean();
            if (loadedConfig.TryGetValue("Notifier.Opacity", out var opacityValue))
                opacity = ((JsonElement)opacityValue).GetSingle();
            if (loadedConfig.TryGetValue("Notifier.Window.Top", out var windowTop))
                window_top = ((JsonElement)windowTop).GetSingle();
            if (loadedConfig.TryGetValue("Notifier.isMiddle", out var middle))
                is_middle = ((JsonElement)middle).GetBoolean();
            if (loadedConfig.TryGetValue("Notifier.Window.Left", out var windowLeft))
                window_left = ((JsonElement)windowLeft).GetSingle();
            if (loadedConfig.TryGetValue("Notifier.Message.ShowTime", out var showTime))
                show_time = ((JsonElement)showTime).GetInt32();
        }
        catch (FileNotFoundException ex)
        {
            Logger.Debug($"无法找到文件，尝试创建:{ex.Message}");
            try { SaveConfig(); } catch (Exception ez) { Logger.Debug($"无法创建文件:{ez.Message}"); }
        }
        catch (JsonException ey)
        {
            Logger.Debug($"JSON解析错误:{ey.Message}");
            try { SaveConfig(); } catch (Exception ez) { Logger.Debug($"无法恢复配置文件:{ez.Message}"); }
        }
        catch (Exception ez)
        {
            Logger.Debug($"未预期的错误:{ez.Message}");
        }
    }

    public void set_setting(SettingType type, bool value)
    {
        try
        {
            switch (type)
            {
                case SettingType.Toast:
                    is_toast_enabled = value;
                    break;
                case SettingType.ismiddle:
                    is_middle = value;
                    break;
            }

            SaveConfig();
            SettingChanged?.Invoke(type);
        }
        catch (Exception ex)
        {
            Logger.Debug($"无法保存设置:{ex.Message}");
        }
    }

    public void set_setting(SettingType type, int value)
    {
        try
        {
            switch (type)
            {
                case SettingType.show_time:
                    show_time = Math.Max(1, value);
                    break;
            }

            SaveConfig();
            SettingChanged?.Invoke(type);
        }
        catch (Exception ex)
        {
            Logger.Debug($"无法保存设置:{ex.Message}");
        }
    }

    public void set_setting(SettingType type, float value)
    {
        try
        {
            switch (type)
            {
                case SettingType.Opacity:
                    opacity = value;
                    break;
                case SettingType.window_top:
                    window_top = value;
                    break;
                case SettingType.window_left:
                    window_left = value;
                    break;
            }

            SaveConfig();
            SettingChanged?.Invoke(type);
        }
        catch (Exception ex)
        {
            Logger.Debug($"无法保存设置:{ex.Message}");
        }
    }
}

