using System.IO;
using System.Text.Json;

namespace Notifier;
public class Settings_Manager
{
    public bool is_toast_enabled { get; private set; } = true;
    public float opacity { get; private set; } = 1.0f;
    public float window_top { get; private set; } = 15.0f;
    public bool is_middle{ get; private set; } = true;
    public int show_time { get; private set; } = 3;
    public float window_left { get; private set; } = float.NaN;
    public enum SettingType
        {
            Toast,
            Opacity,
            window_top,
            ismiddle,
            window_left,
            show_time
    }
    private Dictionary<string, object> config = new Dictionary<string, object>{};
    private void resetvalues() {    
        config.Clear();
        config = new Dictionary<string, object>
        {
            { "Notifier.Toast.Enabled", is_toast_enabled },
            { "Notifier.Opacity", opacity },
            { "Notifier.Window.Top", window_top },
            { "Notifier.isMiddle", is_middle },
            { "Notifier.Window.Left", window_left },
            { "Notifier.Message.ShowTime", show_time }
        };
    }
    public void init_settings()
    {
        var config = new Dictionary<string, object>();
        try
        {
            string jsonString = File.ReadAllText("config.json");
            config = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonString) ?? new Dictionary<string, object>();
            Logger.Debug("Readed config");
            if(config.ContainsKey("Notifier.Toast.Enabled")) is_toast_enabled = ((JsonElement)config["Notifier.Toast.Enabled"]).GetBoolean();
            if(config.ContainsKey("Notifier.Opacity")) opacity = ((JsonElement)config["Notifier.Opacity"]).GetSingle();
            if(config.ContainsKey("Notifier.Window.Top")) window_top = ((JsonElement)config["Notifier.Window.Top"]).GetSingle();
            if(config.ContainsKey("Notifier.isMiddle")) is_middle = ((JsonElement)config["Notifier.isMiddle"]).GetBoolean();
            if(config.ContainsKey("Notifier.Window.Left")) window_left = ((JsonElement)config["Notifier.Window.Left"]).GetSingle();
            if(config.ContainsKey("Notifier.Message.ShowTime")) show_time = ((JsonElement)config["Notifier.Message.ShowTime"]).GetInt32();
            //if(config.ContainsKey("Notifier.SMTC.Enabled")) is_SMTC_enabled = ((JsonElement)config["Notifier.SMTC.Enabled"]).GetBoolean();
        }
        catch (FileNotFoundException ex)
        {
            Logger.Debug($"无法找到文件，尝试创建:{ex.Message}");
            try{
            var defaultConfig = new Dictionary<string, object>
            {
                { "Notifier.Toast.Enabled", true },
                { "Notifier.Opacity", 1.0f },
                { "Notifier.Window.Top", 15.0f },
                { "Notifier.isMiddle", true },
                { "Notifier.Window.Left", 0f },
                { "Notifier.Message.ShowTime", 3 }
                //{ "Notifier.Sound.Enabled", true },
                //{ "Notifier.SMTC.Enabled", true }
            };
            File.WriteAllText("config.json", JsonSerializer.Serialize(defaultConfig));
            }catch (Exception ez)
            {
                Logger.Debug($"无法创建文件:{ez.Message}");
            }
        }
        catch (JsonException ey)
        {
            Logger.Debug($"JSON解析错误:{ey.Message}");
        }
        catch (Exception ez)
        {
            Logger.Debug($"未预期的错误:{ez.Message}");
        }
    }
    
    public void set_setting(SettingType type,  bool value)
    {
        try{
        switch (type)
        {
            case SettingType.Toast:
                is_toast_enabled = value;
                break;
            case SettingType.ismiddle:
                is_middle = value;
                break;
            //case SettingType.Sound:
            //    is_sound_enabled = value;
            //    break;
            //case SettingType.SMTC:
            //    is_SMTC_enabled = value;
            //    break;
        }
        resetvalues();
        File.WriteAllText("config.json", JsonSerializer.Serialize(config));
    }catch (Exception ex)
    {
        Logger.Debug($"无法保存设置:{ex.Message}");
    }
    }
    public void set_setting(SettingType type,  int value)
    {
        try
        {
            switch (type)
            {
                case SettingType.show_time:
                    show_time = value;
                    break;
            }
            resetvalues();
            File.WriteAllText("config.json", JsonSerializer.Serialize(config));
        }
        catch (Exception ex)
        {
            Logger.Debug($"无法保存设置:{ex.Message}");
        }
    }
    public void set_setting(SettingType type,  float value)
    {
        try{
        switch (type)
        {
            case SettingType.Opacity:
                opacity = value ;
                break;
            case SettingType.window_top:
                window_top = value;
                break;
            case SettingType.window_left:
                window_left = value;
                break;
        }
        resetvalues();
        File.WriteAllText("config.json", JsonSerializer.Serialize(config));
    }catch (Exception ex)
    {
        Logger.Debug($"无法保存设置:{ex.Message}");
    }
    }
}

