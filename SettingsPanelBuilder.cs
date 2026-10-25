using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

// 项目开启了 UseWindowsForms，System.Windows.Forms 被隐式引入全局命名空间，
// 与 Avalonia 有一批同名类型（Control / Button / TextBox / CheckBox / Orientation / ...）。
// 这里用别名显式指定 Avalonia 侧，避免 CS0104 歧义（项目里已有同类写法：global::Avalonia.Application）。
using Control = Avalonia.Controls.Control;
using Button = Avalonia.Controls.Button;
using TextBox = Avalonia.Controls.TextBox;
using CheckBox = Avalonia.Controls.CheckBox;
using StackPanel = Avalonia.Controls.StackPanel;
using WrapPanel = Avalonia.Controls.WrapPanel;
using Slider = Avalonia.Controls.Slider;
using Expander = Avalonia.Controls.Expander;
using Border = Avalonia.Controls.Border;
using Grid = Avalonia.Controls.Grid;
using TextBlock = Avalonia.Controls.TextBlock;
using Orientation = Avalonia.Layout.Orientation;
using HorizontalAlignment = Avalonia.Layout.HorizontalAlignment;
using VerticalAlignment = Avalonia.Layout.VerticalAlignment;
using Colors = Avalonia.Media.Colors;
using Brushes = Avalonia.Media.Brushes;

namespace Notifier;

/// <summary>
/// 设置面板的 UI 构建器。
///
/// 为什么用代码而不是 XAML 堆控件：
/// 1. 50 多个可调项，手写 XAML 维护性差，且每一项都要 x:Name 才能绑定事件；
/// 2. 新增一项只需在 Settings_Manager.Items 里加一行，面板自动出现；
/// 3. 控件一次性构建（分组默认折叠），不需要虚拟化也不会拖慢开窗。
///
/// 性能处理：
/// - 颜色改的是共享画笔的 Color，即时生效、零分配，因此不需要去抖；
/// - 数值滑块每变一个像素都会触发，写入设置走 60ms 去抖，避免高频资源重算与写盘；
/// - 文本输入走 300ms 去抖。
/// </summary>
internal static class SettingsPanelBuilder
{
    /// <summary>常用预设色，点击即应用到对应项。</summary>
    private static readonly string[] Swatches =
    {
        "#FFFFFFFF", "#ACFFFFFF", "#78FFFFFF", "#40FFFFFF",
        "#FF1C1C1E", "#FF000000", "#FF888888", "#CC000000",
        "#FFFF3B30", "#FFD32F2F", "#FF007AFF", "#FF34C759"
    };

    private static readonly Dictionary<string, bool> ExpandedState = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, CancellationTokenSourceHolder> Debounces = new(StringComparer.Ordinal);

    /// <summary>构建面板。rebuild 用于"恢复默认"后整体重建，让控件显示回到最新值。</summary>
    public static void Build(StackPanel host, Settings_Manager s, Action rebuild)
    {
        foreach (var holder in Debounces.Values) holder.Cancel();
        Debounces.Clear();
        host.Children.Clear();

        // 顶部：全局操作
        var topBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 6) };

        var resetAll = new Button
        {
            Content = "恢复全部默认",
            FontSize = 11,
            Padding = new Thickness(10, 4),
            Background = null,
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray
        };
        resetAll.Click += (_, _) =>
        {
            s.ResetAll();
            rebuild();
        };

        var saveNow = new Button
        {
            Content = "立即保存",
            FontSize = 11,
            Padding = new Thickness(10, 4),
            Background = null,
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray
        };
        saveNow.Click += (_, _) => s.SaveNow();

        topBar.Children.Add(resetAll);
        topBar.Children.Add(saveNow);
        host.Children.Add(topBar);

        foreach (var group in Settings_Manager.GroupOrder)
        {
            var body = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 4) };
            var groupKey = group;
            var bodyBuilt = false;

            // 分组内容懒构建：设置项有几十个，但绝大多数分组默认折叠。
            // 旧实现开窗时就把所有行全部创建出来（几十个 Slider/TextBox/取色按钮 + 上百个刷子），
            // 这些控件一次都没显示过就先付了一遍创建与布局成本。
            // 改成展开时才构建，首次开窗只创建"分组标题 + 空容器"。
            void EnsureBody()
            {
                if (bodyBuilt) return;
                bodyBuilt = true;
                body.Children.Clear();

                foreach (var item in Settings_Manager.Items)
                {
                    if (!string.Equals(item.Group, groupKey, StringComparison.Ordinal)) continue;

                    var row = BuildRow(item, s);
                    if (row != null) body.Children.Add(row);
                }

                var resetGroup = new Button
                {
                    Content = $"恢复「{Settings_Manager.GroupLabel(groupKey)}」默认",
                    FontSize = 10,
                    Padding = new Thickness(8, 3),
                    Margin = new Thickness(0, 6, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Background = null,
                    BorderThickness = new Thickness(1),
                    BorderBrush = Brushes.Gray
                };
                resetGroup.Click += (_, _) =>
                {
                    s.ResetGroup(groupKey);
                    rebuild();
                };
                body.Children.Add(resetGroup);
            }

            var expander = new Expander
            {
                Header = Settings_Manager.GroupLabel(group),
                Content = body,
                IsExpanded = ExpandedState.TryGetValue(group, out var ex) && ex
            };

            if (expander.IsExpanded) EnsureBody();

            expander.PropertyChanged += (_, e) =>
            {
                if (e.Property != Expander.IsExpandedProperty) return;

                ExpandedState[group] = expander.IsExpanded;
                if (expander.IsExpanded) EnsureBody();
            };

            host.Children.Add(expander);
        }
    }

    private static Control? BuildRow(Settings_Manager.SettingMeta item, Settings_Manager s)
    {
        return item.Kind switch
        {
            Settings_Manager.SettingKind.Bool => BuildBoolRow(item, s),
            Settings_Manager.SettingKind.Color => BuildColorRow(item, s),
            Settings_Manager.SettingKind.Text => BuildTextRow(item, s),
            _ => BuildNumberRow(item, s)
        };
    }

    private static Control BuildBoolRow(Settings_Manager.SettingMeta item, Settings_Manager s)
    {
        var box = new CheckBox
        {
            Content = item.Label,
            FontSize = 11,
            IsChecked = ReadBool(s, item.Key)
        };
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == CheckBox.IsCheckedProperty)
                s.Set(item.Key, box.IsChecked ?? false);
        };
        return box;
    }

    private static Control BuildNumberRow(Settings_Manager.SettingMeta item, Settings_Manager s)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(38)));

        var label = new TextBlock
        {
            Text = item.Label,
            FontSize = 11,
            Width = 132,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 0);

        var slider = new Slider
        {
            Minimum = item.Min,
            Maximum = item.Max,
            TickFrequency = item.Step,
            IsSnapToTickEnabled = true,
            Value = ReadDouble(s, item.Key),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0)
        };
        Grid.SetColumn(slider, 1);

        var valueText = new TextBlock
        {
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Text = FormatValue(ReadDouble(s, item.Key), item)
        };
        Grid.SetColumn(valueText, 2);

        slider.ValueChanged += (_, _) =>
        {
            var v = slider.Value;
            valueText.Text = FormatValue(v, item);

            Debounce(item.Key, 60, () =>
            {
                if (item.Kind == Settings_Manager.SettingKind.Int)
                    s.Set(item.Key, (int)Math.Round(v));
                else
                    s.Set(item.Key, v);
            });
        };

        grid.Children.Add(label);
        grid.Children.Add(slider);
        grid.Children.Add(valueText);
        return grid;
    }

    private static Control BuildColorRow(Settings_Manager.SettingMeta item, Settings_Manager s)
    {
        var panel = new StackPanel { Spacing = 3, Margin = new Thickness(0, 2, 0, 2) };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        header.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        header.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(96)));

        var label = new TextBlock
        {
            Text = item.Label,
            FontSize = 11,
            Width = 132,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 0);

        var current = ReadString(s, item.Key);
        var preview = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Gray,
            Background = new SolidColorBrush(ThemeManager.ParseColor(current) ?? Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(preview, 1);

        var hexBox = new TextBox
        {
            Text = current,
            FontSize = 10,
            Padding = new Thickness(4, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(hexBox, 2);

        var swatchPanel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(132, 2, 0, 0) };
        foreach (var sw in Swatches)
        {
            var color = ThemeManager.ParseColor(sw);
            if (color == null) continue;

            var btn = new Button
            {
                Width = 14,
                Height = 14,
                Margin = new Thickness(0, 0, 3, 3),
                Padding = new Thickness(0),
                Background = new SolidColorBrush(color.Value),
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Gray
            };
            var hex = sw;
            btn.Click += (_, _) =>
            {
                hexBox.Text = hex;
                ApplyColor(s, item.Key, hex, preview);
            };
            swatchPanel.Children.Add(btn);
        }

        // 文本输入去抖：边打字边解析 hex，避免每敲一个字符就改一次画笔
        hexBox.TextChanged += (_, _) =>
        {
            Debounce(item.Key + ":text", 300, () =>
            {
                var text = hexBox.Text;
                if (ThemeManager.ParseColor(text) == null) return;
                ApplyColor(s, item.Key, text!, preview);
            });
        };

        header.Children.Add(label);
        header.Children.Add(preview);
        header.Children.Add(hexBox);
        panel.Children.Add(header);
        panel.Children.Add(swatchPanel);
        return panel;
    }

    private static void ApplyColor(Settings_Manager s, string key, string hex, Border preview)
    {
        var color = ThemeManager.ParseColor(hex);
        if (color == null) return;

        s.Set(key, hex);
        if (preview.Background is SolidColorBrush b)
            b.Color = color.Value; // 复用实例，不新建
    }

    private static Control BuildTextRow(Settings_Manager.SettingMeta item, Settings_Manager s)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

        var label = new TextBlock
        {
            Text = item.Label,
            FontSize = 11,
            Width = 132,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 0);

        var box = new TextBox
        {
            Text = ReadString(s, item.Key),
            FontSize = 10,
            Padding = new Thickness(4, 2),
            Margin = new Thickness(6, 0, 0, 0)
        };
        Grid.SetColumn(box, 1);

        box.TextChanged += (_, _) =>
        {
            var text = box.Text;
            Debounce(item.Key, 300, () => s.Set(item.Key, text ?? string.Empty));
        };

        grid.Children.Add(label);
        grid.Children.Add(box);
        return grid;
    }

    private static string FormatValue(double v, Settings_Manager.SettingMeta item)
        => item.Kind == Settings_Manager.SettingKind.Int
            ? ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture)
            : v.ToString("0.##", CultureInfo.InvariantCulture);

    private static bool ReadBool(Settings_Manager s, string key) => s.Get(key) is bool b && b;

    private static double ReadDouble(Settings_Manager s, string key) => s.Get(key) switch
    {
        double d => d,
        int i => i,
        long l => l,
        float f => f,
        _ => 0
    };

    private static string ReadString(Settings_Manager s, string key) => s.Get(key) as string ?? string.Empty;

    #region 去抖

    private static void Debounce(string key, int delayMs, Action action)
    {
        if (!Debounces.TryGetValue(key, out var holder))
        {
            holder = new CancellationTokenSourceHolder();
            Debounces[key] = holder;
        }

        holder.Restart(delayMs, action);
    }

    private sealed class CancellationTokenSourceHolder
    {
        private System.Threading.CancellationTokenSource? _cts;

        public void Restart(int delayMs, Action action)
        {
            Cancel();
            var cts = new System.Threading.CancellationTokenSource();
            _cts = cts;
            var token = cts.Token;

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try { await System.Threading.Tasks.Task.Delay(delayMs, token); }
                catch (System.Threading.Tasks.TaskCanceledException) { return; }
                if (token.IsCancellationRequested) return;

                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested) return;
                    try { action(); }
                    catch (Exception ex) { Logger.Error("应用设置失败", ex); }
                });
            });
        }

        public void Cancel()
        {
            try { _cts?.Cancel(); } catch { }
            try { _cts?.Dispose(); } catch { }
            _cts = null;
        }
    }

    #endregion
}
