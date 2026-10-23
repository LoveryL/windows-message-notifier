using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;

namespace Notifier;

/// <summary>
/// 主题桥接层：把设置里的颜色/字号/圆角实时映射到 Avalonia 资源字典。
///
/// 实时生效的关键做法：
/// 1. 颜色不改"资源条目"，而是复用同一个 SolidColorBrush 实例、只改它的 Color 属性。
///    SolidColorBrush 是 AvaloniaObject，属性变更会自动让引用它的控件重绘，
///    既不依赖资源字典的变更传播，也不会每帧新建画笔 —— 拖动取色器时零分配。
/// 2. 字号/圆角这类值类型无法原地修改，只能替换资源条目；
///    为避免拖滑块时每变一个像素就触发一次全树资源重算，这里对它们做合并刷新（一帧一次）。
/// 3. 值未变化时不重复写入，避免无意义的重绘。
/// </summary>
public static class ThemeManager
{
    private static readonly Dictionary<string, SolidColorBrush> _brushes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, object?> _lastApplied = new(StringComparer.Ordinal);

    private static bool _initialized;
    private static bool _valueRefreshPending;

    /// <summary>设置键 → 资源键（颜色）。</summary>
    private static readonly (string SettingKey, string ResourceKey)[] ColorMap =
    {
        (Settings_Manager.KPopupBackground, "PopupBackground"),
        (Settings_Manager.KPopupForeground, "PopupForeground"),
        (Settings_Manager.KPopupBodyText, "PopupBodyText"),
        (Settings_Manager.KPopupMutedText, "PopupMutedText"),
        (Settings_Manager.KPopupBorder, "GlassBorderBrush"),
        (Settings_Manager.KAccent, "AccentBrush"),
        (Settings_Manager.KPanelBackground, "LiquidGlassBackground"),
        (Settings_Manager.KPanelForeground, "LiquidGlassForeground"),
        (Settings_Manager.KCardBackground, "CardBackground"),
        (Settings_Manager.KCardTitleText, "CardTitleText"),
        (Settings_Manager.KCardBodyText, "CardBodyText"),
        (Settings_Manager.KCardSubText, "CardSubText"),
        (Settings_Manager.KBadge, "BadgeBrush"),
        (Settings_Manager.KDanger, "DangerBrush"),
        (Settings_Manager.KCtrlHold, "CtrlHoldBrush"),
        (Settings_Manager.KCtrlRelease, "CtrlReleaseBrush")
    };

    /// <summary>设置键 → 资源键（double，走条目替换）。</summary>
    private static readonly (string SettingKey, string ResourceKey)[] DoubleMap =
    {
        (Settings_Manager.KFontTitle, "FontSizeTitle"),
        (Settings_Manager.KFontBody, "FontSizeBody"),
        (Settings_Manager.KFontCaption, "FontSizeCaption"),
        (Settings_Manager.KCardCorner, "CardCornerRadius")
    };

    /// <summary>非颜色类主题项变化后通知窗口做本地更新（阴影、圆角、尺寸等）。</summary>
    public static event Action? ThemeChanged;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        var s = App.setting;

        foreach (var (settingKey, resourceKey) in ColorMap)
            ApplyColor(settingKey, resourceKey, s.Get(settingKey) as string);

        foreach (var (settingKey, resourceKey) in DoubleMap)
            ApplyDouble(settingKey, resourceKey, s.Get(settingKey));

        ApplyFontFamily(s.FontFamily);

        s.ValueChanged += OnSettingChanged;
    }

    private static void OnSettingChanged(string key)
    {
        var s = App.setting;

        for (int i = 0; i < ColorMap.Length; i++)
        {
            if (!string.Equals(ColorMap[i].SettingKey, key, StringComparison.Ordinal)) continue;
            ApplyColor(key, ColorMap[i].ResourceKey, s.Get(key) as string);
            return;
        }

        for (int i = 0; i < DoubleMap.Length; i++)
        {
            if (!string.Equals(DoubleMap[i].SettingKey, key, StringComparison.Ordinal)) continue;
            ApplyDouble(key, DoubleMap[i].ResourceKey, s.Get(key));
            ScheduleValueRefresh();
            return;
        }

        if (string.Equals(key, Settings_Manager.KFontFamily, StringComparison.Ordinal))
        {
            ApplyFontFamily(s.FontFamily);
            ScheduleValueRefresh();
            return;
        }

        // 其余主题相关项（阴影颜色、圆角、动画开关等）交给窗口自行处理
        if (string.Equals(key, Settings_Manager.KPopupShadow, StringComparison.Ordinal) ||
            string.Equals(key, Settings_Manager.KPopupCorner, StringComparison.Ordinal) ||
            string.Equals(key, Settings_Manager.KPanelCorner, StringComparison.Ordinal) ||
            string.Equals(key, Settings_Manager.KControlCorner, StringComparison.Ordinal) ||
            string.Equals(key, Settings_Manager.KAnimEnabled, StringComparison.Ordinal) ||
            string.Equals(key, Settings_Manager.KFrameRate, StringComparison.Ordinal))
        {
            ScheduleValueRefresh();
        }
    }

    /// <summary>把 hex 写进共享画笔。值未变则不动。</summary>
    private static void ApplyColor(string settingKey, string resourceKey, string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return;

        var color = ParseColor(hex);
        if (color == null) return;

        if (_lastApplied.TryGetValue(settingKey, out var prev) && prev is Avalonia.Media.Color pc && pc == color.Value) return;
        _lastApplied[settingKey] = color.Value;

        var resources = global::Avalonia.Application.Current?.Resources;
        if (resources == null) return;

        // 复用同一个 brush 实例：改 Color 即可触发重绘，无需替换资源条目
        if (!_brushes.TryGetValue(resourceKey, out var brush))
        {
            brush = new SolidColorBrush(color.Value);
            _brushes[resourceKey] = brush;
            resources[resourceKey] = brush;
        }
        else
        {
            brush.Color = color.Value;
        }
    }

    private static void ApplyDouble(string settingKey, string resourceKey, object? raw)
    {
        double? value = raw switch
        {
            double d => d,
            int i => i,
            long l => l,
            float f => f,
            string str when double.TryParse(str, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var pd) => pd,
            _ => null
        };

        if (value == null) return;

        if (_lastApplied.TryGetValue(settingKey, out var prev) && prev is double pvd && Math.Abs(pvd - value.Value) < 0.0001)
            return;
        _lastApplied[settingKey] = value.Value;

        var resources = global::Avalonia.Application.Current?.Resources;
        if (resources == null) return;

        if (string.Equals(resourceKey, "CardCornerRadius", StringComparison.Ordinal))
            resources[resourceKey] = new CornerRadius(value.Value);
        else
            resources[resourceKey] = value.Value;
    }

    private static void ApplyFontFamily(string? family)
    {
        if (string.IsNullOrWhiteSpace(family)) return;

        var resources = global::Avalonia.Application.Current?.Resources;
        if (resources == null) return;

        try
        {
            resources["AppFontFamily"] = Avalonia.Media.FontFamily.Parse(family);
        }
        catch
        {
            // 字体名非法时忽略，保持原有字体
        }
    }

    /// <summary>合并值类型资源的刷新：同一帧内的多次变更只通知一次。</summary>
    private static void ScheduleValueRefresh()
    {
        if (_valueRefreshPending) return;
        _valueRefreshPending = true;

        Dispatcher.UIThread.Post(() =>
        {
            _valueRefreshPending = false;
            ThemeChanged?.Invoke();
        });
    }

    /// <summary>解析 "#AARRGGBB" / "#RRGGBB" / "#ARGB" / "#RGB"，失败返回 null。</summary>
    public static Avalonia.Media.Color? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;

        hex = hex.Trim();
        if (hex[0] == '#') hex = hex.Substring(1);

        try
        {
            return hex.Length switch
            {
                8 => Avalonia.Media.Color.FromArgb(
                    byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber),
                    byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber),
                    byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber),
                    byte.Parse(hex.Substring(6, 2), System.Globalization.NumberStyles.HexNumber)),
                6 => Avalonia.Media.Color.FromRgb(
                    byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber),
                    byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber),
                    byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber)),
                _ => Avalonia.Media.Color.Parse("#" + hex)
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Color → "#AARRGGBB"，用于把当前值回填到输入框/预设。</summary>
    public static string ToHex(Avalonia.Media.Color c)
        => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>把设置的阴影色与圆角合成 BoxShadows，供窗口本地应用。</summary>
    public static BoxShadows BuildPopupShadow(string? hex, double blur = 24, double offsetY = 0)
    {
        // ParseColor 返回可空值，这里已用 ?? 兜底，color 本身就是不可空的 Color（值类型，没有 .Value）
        var color = ParseColor(hex) ?? Avalonia.Media.Color.FromArgb(0x40, 0, 0, 0);
        return new BoxShadows(new BoxShadow
        {
            Color = color,
            Blur = blur,
            OffsetX = 0,
            OffsetY = offsetY,
            Spread = 0
        });
    }
}
