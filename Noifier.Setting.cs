using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace Notifier;

/// <summary>
/// 全量设置中心。
///
/// 设计要点（兼顾性能）：
/// 1. 所有可调项的默认值集中在一张表里，重置/序列化/兜底都读同一处，不会出现"改了默认值忘了改兜底"；
/// 2. 值以基元类型缓存在字典里，属性读取是一次 O(1) 字典查找，不碰磁盘、不做反序列化；
/// 3. 写盘去抖（300ms）：拖动滑块/取色器时每秒可能产生几十次变更，合并成一次文件写入；
/// 4. 变更事件带 key，订阅方（主题管理器、窗口）只处理自己关心的那一项，不做全量重建；
/// 5. 沿用旧 config.json 的键名，老用户升级后设置不丢。
/// </summary>
public sealed class Settings_Manager
{
    #region 旧版兼容枚举（MainWindow / 设置窗体仍在用）

    public enum SettingType
    {
        Toast,
        Opacity,
        window_top,
        ismiddle,
        window_left,
        show_time,

        // 以下为本次新增
        Theme,      // 任意颜色 / 字体 / 圆角 / 阴影
        Size,       // 弹窗与面板尺寸
        Animation,  // 动画时长与位移
        Advanced,   // 高级项
        Font,
        Reset
    }

    #endregion

    #region 键常量与默认值表

    public const string KToastEnabled = "Notifier.Toast.Enabled";
    public const string KOpacity = "Notifier.Opacity";
    public const string KWindowTop = "Notifier.Window.Top";
    public const string KIsMiddle = "Notifier.isMiddle";
    public const string KWindowLeft = "Notifier.Window.Left";
    public const string KShowTime = "Notifier.Message.ShowTime";

    // 颜色：统一存 "#AARRGGBB"
    public const string KPopupBackground = "Notifier.Color.PopupBackground";
    public const string KPopupForeground = "Notifier.Color.PopupForeground";
    public const string KPopupBodyText = "Notifier.Color.PopupBodyText";
    public const string KPopupMutedText = "Notifier.Color.PopupMutedText";
    public const string KPopupBorder = "Notifier.Color.PopupBorder";
    public const string KPopupShadow = "Notifier.Color.PopupShadow";
    public const string KAccent = "Notifier.Color.Accent";
    public const string KPanelBackground = "Notifier.Color.PanelBackground";
    public const string KPanelForeground = "Notifier.Color.PanelForeground";
    public const string KCardBackground = "Notifier.Color.CardBackground";
    public const string KCardBodyText = "Notifier.Color.CardBodyText";
    public const string KCardSubText = "Notifier.Color.CardSubText";
    public const string KCardTitleText = "Notifier.Color.CardTitleText";
    public const string KBadge = "Notifier.Color.Badge";
    public const string KDanger = "Notifier.Color.Danger";
    public const string KCtrlHold = "Notifier.Color.CtrlHold";
    public const string KCtrlRelease = "Notifier.Color.CtrlRelease";

    // 尺寸
    public const string KPopupWidth = "Notifier.Size.PopupWidth";
    public const string KPopupHeight = "Notifier.Size.PopupHeight";
    public const string KPopupMaxHeight = "Notifier.Size.PopupMaxHeight";
    public const string KPopupCorner = "Notifier.Size.PopupCorner";
    public const string KPanelWidth = "Notifier.Size.PanelWidth";
    public const string KPanelHeight = "Notifier.Size.PanelHeight";
    public const string KPanelCorner = "Notifier.Size.PanelCorner";
    public const string KCardCorner = "Notifier.Size.CardCorner";
    public const string KControlWidth = "Notifier.Size.ControlWidth";
    public const string KControlHeight = "Notifier.Size.ControlHeight";
    public const string KControlCorner = "Notifier.Size.ControlCorner";

    // 字体
    public const string KFontTitle = "Notifier.Font.Title";
    public const string KFontBody = "Notifier.Font.Body";
    public const string KFontCaption = "Notifier.Font.Caption";
    public const string KFontFamily = "Notifier.Font.Family";

    // 动画
    public const string KAnimEnabled = "Notifier.Anim.Enabled";
    public const string KPopupEnterMs = "Notifier.Anim.PopupEnterMs";
    public const string KPopupExitMs = "Notifier.Anim.PopupExitMs";
    public const string KPanelEnterMs = "Notifier.Anim.PanelEnterMs";
    public const string KPanelExitMs = "Notifier.Anim.PanelExitMs";
    public const string KBorderAnimMs = "Notifier.Anim.BorderMs";
    public const string KEnterSlideX = "Notifier.Anim.EnterSlideX";
    public const string KEnterScaleFrom = "Notifier.Anim.EnterScaleFrom";
    public const string KExitSlideX = "Notifier.Anim.ExitSlideX";
    public const string KExitScaleTo = "Notifier.Anim.ExitScaleTo";
    public const string KPanelSlideX = "Notifier.Anim.PanelSlideX";
    public const string KFrameRate = "Notifier.Anim.FrameRate";

    // 高级
    public const string KMaxMessages = "Notifier.Adv.MaxMessages";
    public const string KFilterKeywords = "Notifier.Adv.FilterKeywords";
    public const string KPersistDelayMs = "Notifier.Adv.PersistDelayMs";
    public const string KPollIntervalMs = "Notifier.Adv.PollIntervalMs";
    public const string KLogEnabled = "Notifier.Adv.LogEnabled";
    public const string KMouseTransparent = "Notifier.Adv.MouseTransparent";

    /// <summary>分组名 → 该组包含的键。用于按组重置。</summary>
    public static class Group
    {
        public const string Behavior = "Behavior";
        public const string Color = "Color";
        public const string Size = "Size";
        public const string Font = "Font";
        public const string Animation = "Animation";
        public const string Advanced = "Advanced";
    }

    private static readonly Dictionary<string, object> Defaults = new()
    {
        // 行为
        [KToastEnabled] = true,
        [KOpacity] = 1.0d,
        [KWindowTop] = 15.0d,
        [KIsMiddle] = true,
        [KWindowLeft] = 0.0d,
        [KShowTime] = 3,

        // 颜色
        [KPopupBackground] = "#ACFFFFFF",
        [KPopupForeground] = "#FF1C1C1E",
        [KPopupBodyText] = "#AA000000",
        [KPopupMutedText] = "#FF888888",
        [KPopupBorder] = "#40FFFFFF",
        [KPopupShadow] = "#40000000",
        [KAccent] = "#FF000000",
        [KPanelBackground] = "#AFFFFFFF",
        [KPanelForeground] = "#FF1C1C1E",
        [KCardBackground] = "#12000000",
        [KCardBodyText] = "#88000000",
        [KCardTitleText] = "#CC000000",
        [KCardSubText] = "#FF777777",
        [KBadge] = "#FFFF3B30",
        [KDanger] = "#FFD32F2F",
        [KCtrlHold] = "#FFFFFFFF",
        [KCtrlRelease] = "#ACFFFFFF",

        // 尺寸
        [KPopupWidth] = 375.0d,
        [KPopupHeight] = 65.0d,
        [KPopupMaxHeight] = 420.0d,
        [KPopupCorner] = 10.0d,
        [KPanelWidth] = 360.0d,
        [KPanelHeight] = 300.0d,
        [KPanelCorner] = 20.0d,
        [KCardCorner] = 16.0d,
        [KControlWidth] = 370.0d,
        [KControlHeight] = 480.0d,
        [KControlCorner] = 14.0d,

        // 字体
        [KFontTitle] = 13.0d,
        [KFontBody] = 11.0d,
        [KFontCaption] = 10.0d,
        [KFontFamily] = "Segoe UI Variable, Segoe UI, Microsoft YaHei, sans-serif",

        // 动画
        [KAnimEnabled] = true,
        [KPopupEnterMs] = 260,
        [KPopupExitMs] = 170,
        [KPanelEnterMs] = 240,
        [KPanelExitMs] = 200,
        [KBorderAnimMs] = 200,
        [KEnterSlideX] = 28.0d,
        [KEnterScaleFrom] = 0.94d,
        [KExitSlideX] = 14.0d,
        [KExitScaleTo] = 0.97d,
        [KPanelSlideX] = -420.0d,
        [KFrameRate] = 60,

        // 高级
        [KMaxMessages] = 500,
        [KFilterKeywords] = "微信,WeChat",
        [KPersistDelayMs] = 500,
        [KPollIntervalMs] = 150,
        [KLogEnabled] = true,
        [KMouseTransparent] = true
    };

    /// <summary>键 → 分组，用于按组重置与分组展示。</summary>
    private static readonly Dictionary<string, string> KeyGroups = BuildKeyGroups();

    private static Dictionary<string, string> BuildKeyGroups()
    {
        var map = new Dictionary<string, string>();
        void Add(string group, params string[] keys)
        {
            foreach (var k in keys) map[k] = group;
        }

        Add(Group.Behavior, KToastEnabled, KOpacity, KWindowTop, KIsMiddle, KWindowLeft, KShowTime);
        Add(Group.Color, KPopupBackground, KPopupForeground, KPopupBodyText, KPopupMutedText,
            KPopupBorder, KPopupShadow, KAccent, KPanelBackground, KPanelForeground,
            KCardBackground, KCardTitleText, KCardBodyText, KCardSubText, KBadge, KDanger, KCtrlHold, KCtrlRelease);
        Add(Group.Size, KPopupWidth, KPopupHeight, KPopupMaxHeight, KPopupCorner,
            KPanelWidth, KPanelHeight, KPanelCorner, KCardCorner, KControlWidth, KControlHeight, KControlCorner);
        Add(Group.Font, KFontTitle, KFontBody, KFontCaption, KFontFamily);
        Add(Group.Animation, KAnimEnabled, KPopupEnterMs, KPopupExitMs, KPanelEnterMs, KPanelExitMs,
            KBorderAnimMs, KEnterSlideX, KEnterScaleFrom, KExitSlideX, KExitScaleTo, KPanelSlideX, KFrameRate);
        Add(Group.Advanced, KMaxMessages, KFilterKeywords, KPersistDelayMs, KPollIntervalMs, KLogEnabled, KMouseTransparent);
        return map;
    }

    #endregion

    #region 存储与事件

    private readonly Dictionary<string, object> _values = new(Defaults.Count);
    private readonly object _ioLock = new();
    private System.Threading.Timer? _saveTimer;
    private int _saveScheduled;
    private const int SaveDelayMs = 300;

    /// <summary>单项变更（带键名）。订阅方按需只处理自己关心的项。</summary>
    public event Action<string>? ValueChanged;
    /// <summary>旧版兼容事件：按大类通知，用于窗口判断是否需要重新应用。</summary>
    public event Action<SettingType>? SettingChanged;

    // 打包态（MSIX）下程序目录是只读的 WindowsApps，配置必须写应用数据目录；
    // 未打包时 AppEnvironment.DataFolder 就是原来的 %LOCALAPPDATA%\Notifier，行为不变。
    private static string LocalConfigPath => Path.Combine(AppEnvironment.DataFolder, "config.json");
    // 只用于读取旧版本留在程序目录里的配置（兼容用，不写入）
    private static string AppConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false
    };

    #endregion

    #region 兼容属性（现有代码直接使用这些名字）

    public bool is_toast_enabled { get => GetBool(KToastEnabled); private set => SetValue(KToastEnabled, value); }
    public float opacity { get => (float)GetDouble(KOpacity); private set => SetValue(KOpacity, (double)value); }
    public float window_top { get => (float)GetDouble(KWindowTop); private set => SetValue(KWindowTop, (double)value); }
    public bool is_middle { get => GetBool(KIsMiddle); private set => SetValue(KIsMiddle, value); }
    public float window_left { get => (float)GetDouble(KWindowLeft); private set => SetValue(KWindowLeft, (double)value); }
    public int show_time { get => GetInt(KShowTime); private set => SetValue(KShowTime, value); }

    #endregion

    #region 颜色

    public string PopupBackgroundHex { get => GetString(KPopupBackground); private set => SetValue(KPopupBackground, value); }
    public string PopupForegroundHex { get => GetString(KPopupForeground); private set => SetValue(KPopupForeground, value); }
    public string PopupBodyTextHex { get => GetString(KPopupBodyText); private set => SetValue(KPopupBodyText, value); }
    public string PopupMutedTextHex { get => GetString(KPopupMutedText); private set => SetValue(KPopupMutedText, value); }
    public string PopupBorderHex { get => GetString(KPopupBorder); private set => SetValue(KPopupBorder, value); }
    public string PopupShadowHex { get => GetString(KPopupShadow); private set => SetValue(KPopupShadow, value); }
    public string AccentHex { get => GetString(KAccent); private set => SetValue(KAccent, value); }
    public string PanelBackgroundHex { get => GetString(KPanelBackground); private set => SetValue(KPanelBackground, value); }
    public string PanelForegroundHex { get => GetString(KPanelForeground); private set => SetValue(KPanelForeground, value); }
    public string CardBackgroundHex { get => GetString(KCardBackground); private set => SetValue(KCardBackground, value); }
    public string CardBodyTextHex { get => GetString(KCardBodyText); private set => SetValue(KCardBodyText, value); }
    public string CardTitleTextHex { get => GetString(KCardTitleText); private set => SetValue(KCardTitleText, value); }
    public string CardSubTextHex { get => GetString(KCardSubText); private set => SetValue(KCardSubText, value); }
    public string BadgeHex { get => GetString(KBadge); private set => SetValue(KBadge, value); }
    public string DangerHex { get => GetString(KDanger); private set => SetValue(KDanger, value); }
    public string CtrlHoldHex { get => GetString(KCtrlHold); private set => SetValue(KCtrlHold, value); }
    public string CtrlReleaseHex { get => GetString(KCtrlRelease); private set => SetValue(KCtrlRelease, value); }

    #endregion

    #region 尺寸

    public double PopupWidth { get => GetDouble(KPopupWidth); private set => SetValue(KPopupWidth, value); }
    public double PopupHeight { get => GetDouble(KPopupHeight); private set => SetValue(KPopupHeight, value); }
    public double PopupMaxHeight { get => GetDouble(KPopupMaxHeight); private set => SetValue(KPopupMaxHeight, value); }
    public double PopupCorner { get => GetDouble(KPopupCorner); private set => SetValue(KPopupCorner, value); }
    public double PanelWidth { get => GetDouble(KPanelWidth); private set => SetValue(KPanelWidth, value); }
    public double PanelHeight { get => GetDouble(KPanelHeight); private set => SetValue(KPanelHeight, value); }
    public double PanelCorner { get => GetDouble(KPanelCorner); private set => SetValue(KPanelCorner, value); }
    public double CardCorner { get => GetDouble(KCardCorner); private set => SetValue(KCardCorner, value); }
    public double ControlWidth { get => GetDouble(KControlWidth); private set => SetValue(KControlWidth, value); }
    public double ControlHeight { get => GetDouble(KControlHeight); private set => SetValue(KControlHeight, value); }
    public double ControlCorner { get => GetDouble(KControlCorner); private set => SetValue(KControlCorner, value); }

    #endregion

    #region 字体

    public double FontTitle { get => GetDouble(KFontTitle); private set => SetValue(KFontTitle, value); }
    public double FontBody { get => GetDouble(KFontBody); private set => SetValue(KFontBody, value); }
    public double FontCaption { get => GetDouble(KFontCaption); private set => SetValue(KFontCaption, value); }
    public string FontFamily { get => GetString(KFontFamily); private set => SetValue(KFontFamily, value); }

    #endregion

    #region 动画

    public bool AnimEnabled { get => GetBool(KAnimEnabled); private set => SetValue(KAnimEnabled, value); }
    public int PopupEnterMs { get => GetInt(KPopupEnterMs); private set => SetValue(KPopupEnterMs, value); }
    public int PopupExitMs { get => GetInt(KPopupExitMs); private set => SetValue(KPopupExitMs, value); }
    public int PanelEnterMs { get => GetInt(KPanelEnterMs); private set => SetValue(KPanelEnterMs, value); }
    public int PanelExitMs { get => GetInt(KPanelExitMs); private set => SetValue(KPanelExitMs, value); }
    public int BorderAnimMs { get => GetInt(KBorderAnimMs); private set => SetValue(KBorderAnimMs, value); }
    public double EnterSlideX { get => GetDouble(KEnterSlideX); private set => SetValue(KEnterSlideX, value); }
    public double EnterScaleFrom { get => GetDouble(KEnterScaleFrom); private set => SetValue(KEnterScaleFrom, value); }
    public double ExitSlideX { get => GetDouble(KExitSlideX); private set => SetValue(KExitSlideX, value); }
    public double ExitScaleTo { get => GetDouble(KExitScaleTo); private set => SetValue(KExitScaleTo, value); }
    public double PanelSlideX { get => GetDouble(KPanelSlideX); private set => SetValue(KPanelSlideX, value); }
    public int FrameRate { get => GetInt(KFrameRate); private set => SetValue(KFrameRate, value); }

    #endregion

    #region 高级

    public int MaxMessages { get => GetInt(KMaxMessages); private set => SetValue(KMaxMessages, value); }
    public string FilterKeywords { get => GetString(KFilterKeywords); private set => SetValue(KFilterKeywords, value); }
    public int PersistDelayMs { get => GetInt(KPersistDelayMs); private set => SetValue(KPersistDelayMs, value); }
    public int PollIntervalMs { get => GetInt(KPollIntervalMs); private set => SetValue(KPollIntervalMs, value); }
    public bool LogEnabled { get => GetBool(KLogEnabled); private set => SetValue(KLogEnabled, value); }
    public bool MouseTransparent { get => GetBool(KMouseTransparent); private set => SetValue(KMouseTransparent, value); }

    #endregion

    #region 读写基元

    private bool GetBool(string key) => TryGet(key, out bool v) ? v : (bool)Defaults[key];
    private int GetInt(string key) => TryGet(key, out int v) ? v : (int)Defaults[key];
    private double GetDouble(string key) => TryGet(key, out double v) ? v : (double)Defaults[key];
    private string GetString(string key) => TryGet(key, out string v) && v != null! ? v : (string)Defaults[key];

    private bool TryGet<T>(string key, out T value)
    {
        if (_values.TryGetValue(key, out var raw))
        {
            switch (raw)
            {
                case T typed:
                    value = typed; return true;
                case double d when typeof(T) == typeof(int):
                    value = (T)(object)(int)Math.Round(d); return true;
                case long l when typeof(T) == typeof(int):
                    value = (T)(object)(int)l; return true;
                case int i when typeof(T) == typeof(double):
                    value = (T)(object)(double)i; return true;
                case long l2 when typeof(T) == typeof(double):
                    value = (T)(object)(double)l2; return true;
                case int i2 when typeof(T) == typeof(bool):
                    value = (T)(object)(i2 != 0); return true;
            }
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// 按类型分派的写入口。值未变化时不触发任何通知。
    /// 原 WinForms 设置窗体（已移除）走的就是这几个重载，保留以免外部代码/插件引用失效；
    /// 新的设置窗口统一走 <see cref="Set(string, object)"/>。
    /// </summary>
    public void set_setting(SettingType type, bool value)
    {
        switch (type)
        {
            case SettingType.Toast: SetValue(KToastEnabled, value); break;
            case SettingType.ismiddle: SetValue(KIsMiddle, value); break;
        }
    }

    public void set_setting(SettingType type, int value)
    {
        switch (type)
        {
            case SettingType.show_time: SetValue(KShowTime, Math.Max(1, value)); break;
        }
    }

    public void set_setting(SettingType type, float value)
    {
        switch (type)
        {
            case SettingType.Opacity: SetValue(KOpacity, (double)Math.Clamp(value, 0.05f, 1f)); break;
            case SettingType.window_top: SetValue(KWindowTop, (double)value); break;
            case SettingType.window_left: SetValue(KWindowLeft, (double)value); break;
        }
    }

    /// <summary>通用写入口（供设置面板直接按键名写入）。</summary>
    public void Set(string key, object value)
    {
        if (!Defaults.ContainsKey(key)) return;
        SetValue(key, value);
    }

    public object? Get(string key) => _values.TryGetValue(key, out var v) ? v : (Defaults.TryGetValue(key, out var d) ? d : null);

    private void SetValue(string key, object value)
    {
        object normalized = Normalize(key, value);

        lock (_values)
        {
            if (_values.TryGetValue(key, out var current) && Equals(current, normalized))
                return;
            _values[key] = normalized;
        }

        ScheduleSave();
        RaiseChanged(key);
    }

    /// <summary>按默认值的类型归一化，避免 JSON 往返后类型漂移（int 变 double、bool 变字符串等）。</summary>
    private static object Normalize(string key, object value)
    {
        if (!Defaults.TryGetValue(key, out var def)) return value;

        try
        {
            switch (def)
            {
                case bool:
                    return value is bool b ? b : Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                case int:
                    var i = value is int vi ? vi : Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    return key == KShowTime || key == KFrameRate ? Math.Max(1, i) : i;
                case double:
                    return value is double vd ? vd : Convert.ToDouble(value, CultureInfo.InvariantCulture);
                case string:
                    return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }
        catch
        {
            return def;
        }

        return value;
    }

    private void RaiseChanged(string key)
    {
        ValueChanged?.Invoke(key);

        var type = key switch
        {
            KShowTime => SettingType.show_time,
            KOpacity => SettingType.Opacity,
            KWindowTop => SettingType.window_top,
            KWindowLeft => SettingType.window_left,
            KIsMiddle => SettingType.ismiddle,
            KToastEnabled => SettingType.Toast,
            _ => Classify(key)
        };

        SettingChanged?.Invoke(type);
    }

    private static SettingType Classify(string key)
        => KeyGroups.TryGetValue(key, out var g)
            ? g switch
            {
                Group.Color => SettingType.Theme,
                Group.Size => SettingType.Size,
                Group.Font => SettingType.Font,
                Group.Animation => SettingType.Animation,
                Group.Advanced => SettingType.Advanced,
                _ => SettingType.Advanced
            }
            : SettingType.Advanced;

    #endregion

    #region 重置

    /// <summary>重置某一分组（如 "Color"）；传 null 重置全部。</summary>
    public void ResetGroup(string? group)
    {
        List<string> changed = new();

        lock (_values)
        {
            foreach (var pair in Defaults)
            {
                if (group != null && (!KeyGroups.TryGetValue(pair.Key, out var g) || g != group))
                    continue;

                if (_values.TryGetValue(pair.Key, out var cur) && Equals(cur, pair.Value))
                    continue;

                _values[pair.Key] = pair.Value;
                changed.Add(pair.Key);
            }
        }

        if (changed.Count == 0) return;

        ScheduleSave();
        foreach (var key in changed) RaiseChanged(key);
        SettingChanged?.Invoke(SettingType.Reset);
    }

    public void ResetAll() => ResetGroup(null);

    #endregion

    #region 加载 / 保存

    public void init_settings()
    {
        var path = ResolveReadPath();

        try
        {
            if (path == null || !File.Exists(path))
            {
                // 没有配置文件也要保证内存里有完整默认值，避免后续到处兜底
                ResetToDefaultSilent();
                SaveNow();
                return;
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                ResetToDefaultSilent();
                SaveNow();
                return;
            }

            var loaded = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            if (loaded == null)
            {
                ResetToDefaultSilent();
                return;
            }

            lock (_values)
            {
                foreach (var pair in loaded)
                {
                    if (!Defaults.ContainsKey(pair.Key)) continue;

                    // JsonElement 在这里一次性转成 CLR 基元，之后读取不再有转换开销
                    var raw = pair.Value is JsonElement je ? Unwrap(je, Defaults[pair.Key]) : pair.Value;
                    if (raw == null) continue;

                    _values[pair.Key] = Normalize(pair.Key, raw);
                }

                // 补齐缺失项（旧配置升级场景）
                foreach (var pair in Defaults)
                {
                    if (!_values.ContainsKey(pair.Key))
                        _values[pair.Key] = pair.Value;
                }
            }

            Logger.Debug("Readed config");
        }
        catch (JsonException ex)
        {
            Logger.Debug($"JSON解析错误:{ex.Message}");
            ResetToDefaultSilent();
            SaveNow();
        }
        catch (Exception ex)
        {
            Logger.Debug($"加载配置失败:{ex.Message}");
            ResetToDefaultSilent();
        }
    }

    private void ResetToDefaultSilent()
    {
        lock (_values)
        {
            _values.Clear();
            foreach (var pair in Defaults) _values[pair.Key] = pair.Value;
        }
    }

    private static object? Unwrap(JsonElement el, object def)
    {
        try
        {
            switch (def)
            {
                case bool:
                    return el.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Number => el.TryGetDouble(out var dn) && dn != 0,
                        JsonValueKind.String => bool.TryParse(el.GetString(), out var sb) && sb,
                        _ => null
                    };
                case int:
                    if (el.ValueKind == JsonValueKind.Number) return el.GetInt32();
                    if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var si)) return si;
                    return null;
                case double:
                    if (el.ValueKind == JsonValueKind.Number) return el.GetDouble();
                    if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sd)) return sd;
                    return null;
                case string:
                    return el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string? ResolveReadPath()
    {
        // 兼容：旧版本把 config.json 写在程序目录，优先读存在的那一份
        try { if (File.Exists(LocalConfigPath)) return LocalConfigPath; } catch { }
        try { if (File.Exists(AppConfigPath)) return AppConfigPath; } catch { }
        return null;
    }

    private void ScheduleSave()
    {
        if (Interlocked.Exchange(ref _saveScheduled, 1) == 1) return;

        _saveTimer ??= new System.Threading.Timer(_ =>
        {
            Volatile.Write(ref _saveScheduled, 0);
            SaveNow();
        }, null, Timeout.Infinite, Timeout.Infinite);

        _saveTimer.Change(SaveDelayMs, Timeout.Infinite);
    }

    /// <summary>立即写盘（退出时调用）。</summary>
    public void SaveNow()
    {
        Dictionary<string, object> snapshot;
        lock (_values) snapshot = new Dictionary<string, object>(_values);

        lock (_ioLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(LocalConfigPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
                var temp = LocalConfigPath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, LocalConfigPath, true);
            }
            catch (Exception ex)
            {
                Logger.Debug($"无法保存设置:{ex.Message}");
            }
        }
    }

    #endregion

    #region 供设置面板枚举用的元数据

    public enum SettingKind
    {
        Bool,
        Int,
        Double,
        Color,
        Text
    }

    /// <summary>面板上一行的描述：键、展示名、分组、控件类型与取值范围。</summary>
    public sealed record SettingMeta(
        string Key,
        string Label,
        string Group,
        SettingKind Kind,
        double Min = 0,
        double Max = 100,
        double Step = 1);

    public static IReadOnlyDictionary<string, object> DefaultValues => Defaults;

    /// <summary>分组展示顺序。</summary>
    public static readonly string[] GroupOrder =
    {
        Group.Behavior, Group.Color, Group.Size, Group.Font, Group.Animation, Group.Advanced
    };

    public static string GroupLabel(string group) => group switch
    {
        Group.Behavior => "行为",
        Group.Color => "颜色",
        Group.Size => "尺寸与圆角",
        Group.Font => "字体",
        Group.Animation => "动画",
        Group.Advanced => "高级",
        _ => group
    };

    /// <summary>
    /// 面板按此数组顺序渲染，因此顺序是显式声明的（不依赖字典遍历顺序）。
    /// 想新增可调项：在 Defaults 加默认值 → 在 KeyGroups 归组 → 在这里加一行即可。
    /// </summary>
    public static readonly SettingMeta[] Items =
    {
        // 行为
        new(KToastEnabled, "启用通知监听", Group.Behavior, SettingKind.Bool),
        new(KShowTime, "弹窗停留（秒）", Group.Behavior, SettingKind.Int, 1, 60, 1),
        new(KOpacity, "整体不透明度", Group.Behavior, SettingKind.Double, 0.05, 1, 0.01),
        new(KIsMiddle, "弹窗居中显示", Group.Behavior, SettingKind.Bool),
        new(KWindowTop, "弹窗距顶部（px）", Group.Behavior, SettingKind.Double, 0, 800, 1),
        new(KWindowLeft, "弹窗距左侧（px）", Group.Behavior, SettingKind.Double, 0, 3000, 1),

        // 颜色
        new(KPopupBackground, "弹窗背景", Group.Color, SettingKind.Color),
        new(KPopupForeground, "弹窗主文字", Group.Color, SettingKind.Color),
        new(KPopupBodyText, "弹窗正文", Group.Color, SettingKind.Color),
        new(KPopupMutedText, "弹窗次要文字", Group.Color, SettingKind.Color),
        new(KPopupBorder, "弹窗边框", Group.Color, SettingKind.Color),
        new(KPopupShadow, "弹窗阴影", Group.Color, SettingKind.Color),
        new(KAccent, "强调色（图标）", Group.Color, SettingKind.Color),
        new(KPanelBackground, "面板背景", Group.Color, SettingKind.Color),
        new(KPanelForeground, "面板主文字", Group.Color, SettingKind.Color),
        new(KCardBackground, "卡片背景", Group.Color, SettingKind.Color),
        new(KCardTitleText, "卡片标题", Group.Color, SettingKind.Color),
        new(KCardBodyText, "卡片正文", Group.Color, SettingKind.Color),
        new(KCardSubText, "卡片来源文字", Group.Color, SettingKind.Color),
        new(KBadge, "未读角标", Group.Color, SettingKind.Color),
        new(KDanger, "危险按钮", Group.Color, SettingKind.Color),
        new(KCtrlHold, "按住 Ctrl 高亮", Group.Color, SettingKind.Color),
        new(KCtrlRelease, "松开 Ctrl 恢复", Group.Color, SettingKind.Color),

        // 尺寸与圆角
        new(KPopupWidth, "弹窗宽度", Group.Size, SettingKind.Double, 240, 640, 1),
        new(KPopupHeight, "弹窗高度", Group.Size, SettingKind.Double, 40, 200, 1),
        new(KPopupMaxHeight, "弹窗展开最大高度", Group.Size, SettingKind.Double, 120, 800, 5),
        new(KPopupCorner, "弹窗圆角", Group.Size, SettingKind.Double, 0, 32, 1),
        new(KPanelWidth, "面板宽度", Group.Size, SettingKind.Double, 280, 560, 1),
        new(KPanelHeight, "面板高度", Group.Size, SettingKind.Double, 200, 900, 1),
        new(KPanelCorner, "面板圆角", Group.Size, SettingKind.Double, 0, 40, 1),
        new(KCardCorner, "卡片圆角", Group.Size, SettingKind.Double, 0, 28, 1),
        new(KControlWidth, "控制中心宽度", Group.Size, SettingKind.Double, 300, 560, 1),
        new(KControlHeight, "控制中心高度", Group.Size, SettingKind.Double, 260, 900, 1),
        new(KControlCorner, "控制中心圆角", Group.Size, SettingKind.Double, 0, 32, 1),

        // 字体
        new(KFontTitle, "标题字号", Group.Font, SettingKind.Double, 8, 28, 0.5),
        new(KFontBody, "正文字号", Group.Font, SettingKind.Double, 7, 24, 0.5),
        new(KFontCaption, "辅助字号", Group.Font, SettingKind.Double, 6, 20, 0.5),
        new(KFontFamily, "字体", Group.Font, SettingKind.Text),

        // 动画
        new(KAnimEnabled, "启用动画", Group.Animation, SettingKind.Bool),
        new(KPopupEnterMs, "弹窗入场（毫秒）", Group.Animation, SettingKind.Int, 0, 1000, 10),
        new(KPopupExitMs, "弹窗退场（毫秒）", Group.Animation, SettingKind.Int, 0, 1000, 10),
        new(KPanelEnterMs, "面板入场（毫秒）", Group.Animation, SettingKind.Int, 0, 1000, 10),
        new(KPanelExitMs, "面板退场（毫秒）", Group.Animation, SettingKind.Int, 0, 1000, 10),
        new(KBorderAnimMs, "边框过渡（毫秒）", Group.Animation, SettingKind.Int, 0, 1000, 10),
        new(KEnterSlideX, "入场位移（px）", Group.Animation, SettingKind.Double, 0, 200, 1),
        new(KEnterScaleFrom, "入场起始缩放", Group.Animation, SettingKind.Double, 0.7, 1.2, 0.01),
        new(KExitSlideX, "退场位移（px）", Group.Animation, SettingKind.Double, 0, 200, 1),
        new(KExitScaleTo, "退场结束缩放", Group.Animation, SettingKind.Double, 0.7, 1.2, 0.01),
        new(KPanelSlideX, "面板滑入起点（px）", Group.Animation, SettingKind.Double, -900, 0, 10),
        new(KFrameRate, "动画帧率（fps）", Group.Animation, SettingKind.Int, 15, 144, 1),

        // 高级
        new(KMaxMessages, "最多保留消息数", Group.Advanced, SettingKind.Int, 50, 2000, 10),
        new(KFilterKeywords, "过滤关键词（逗号分隔）", Group.Advanced, SettingKind.Text),
        new(KPersistDelayMs, "落盘延迟（毫秒）", Group.Advanced, SettingKind.Int, 100, 5000, 100),
        new(KPollIntervalMs, "按键轮询间隔（毫秒）", Group.Advanced, SettingKind.Int, 50, 500, 10),
        new(KLogEnabled, "启用日志", Group.Advanced, SettingKind.Bool),
        new(KMouseTransparent, "鼠标穿透", Group.Advanced, SettingKind.Bool)
    };

    #endregion
}
