using System.IO;
using System.Text.Json;

namespace Notifier;
public class Settings_Manager
{
    public bool is_toast_enabled { get; set; } = true;
    public enum SettingType
        {
            Toast
        }
    public void init_settings()
    {
        var config = new Dictionary<string, object>();
        try
        {
            string jsonString = File.ReadAllText("config.json");
            config = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonString);
            Logger.Debug("Readed config");
            if(config.ContainsKey("Notifier.Toast.Enabled")) is_toast_enabled = ((JsonElement)config["Notifier.Toast.Enabled"]).GetBoolean();
            //if(config.ContainsKey("Notifier.Sound.Enabled")) is_sound_enabled = ((JsonElement)config["Notifier.Sound.Enabled"]).GetBoolean();
            //if(config.ContainsKey("Notifier.SMTC.Enabled")) is_SMTC_enabled = ((JsonElement)config["Notifier.SMTC.Enabled"]).GetBoolean();
        }
        catch (FileNotFoundException ex)
        {
            Logger.Debug($"无法找到文件，尝试创建:{ex.Message}");
            try{
            var defaultConfig = new Dictionary<string, object>
            {
                { "Notifier.Toast.Enabled", true },
                { "Notifier.Sound.Enabled", true },
                { "Notifier.SMTC.Enabled", true }
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
    public void set_setting(SettingType type, bool value)
    {
        try{
        switch (type)
        {
            case SettingType.Toast:
                is_toast_enabled = value;
                break;
            //case SettingType.Sound:
            //    is_sound_enabled = value;
            //    break;
            //case SettingType.SMTC:
            //    is_SMTC_enabled = value;
            //    break;
        }
        var config = new Dictionary<string, object>
        {
            { "Notifier.Toast.Enabled", is_toast_enabled },
            //{ "Notifier.Sound.Enabled", is_sound_enabled },
            //{ "Notifier.SMTC.Enabled", is_SMTC_enabled }
        };
        File.WriteAllText("config.json", JsonSerializer.Serialize(config));
    }catch (Exception ex)
    {
        Logger.Debug($"无法保存设置:{ex.Message}");
    }
    }
    
}