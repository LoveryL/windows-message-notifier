using System;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Notifier;

/// <summary>
/// 独立设置窗口（托盘「设置」打开）。
///
/// 与 SettingWindow（控制中心：音量/亮度/SMTC）分工明确，这里只承载可调项。
/// 用带标题栏的常规窗口而不是无边框浮层：设置窗口需要能拖动、缩放、Alt+Tab 找回，
/// 且不应与弹窗抢 Topmost。
/// </summary>
public partial class PreferencesWindow : Window
{
    // 单开：托盘连点时复用同一个实例并激活，避免堆出多个设置窗口
    private static PreferencesWindow? _current;

    public PreferencesWindow()
    {
        InitializeComponent();
        BuildPanel();

        if (StatusText != null)
            StatusText.Text = $"共 {Settings_Manager.Items.Length} 项可调";

        if (CloseButton != null)
            CloseButton.Click += (_, _) => Close();

        // 复用而非销毁：设置项有几十个，重建一次面板要重新创建全部控件，
        // 关掉再开是很常见的操作，这里只隐藏（退出时由 App.IsExiting 放行真正关闭）。
        this.Closing += (_, e) =>
        {
            if (App.IsExiting) return;
            e.Cancel = true;
            Hide();
            try { App.setting?.SaveNow(); } catch { }
        };

        this.Closed += (_, __) =>
        {
            if (ReferenceEquals(_current, this)) _current = null;
            // 去抖写入可能还有 300ms 的尾巴，关闭时立刻落盘，保证不丢
            try { App.setting?.SaveNow(); } catch (Exception ex) { Logger.Error("保存设置失败", ex); }
        };
    }

    private void BuildPanel()
    {
        try
        {
            if (SettingsHost != null && App.setting != null)
                SettingsPanelBuilder.Build(SettingsHost, App.setting, BuildPanel);
        }
        catch (Exception ex)
        {
            Logger.Error("构建设置面板失败", ex);
        }
    }

    /// <summary>托盘菜单入口。WinForms 的菜单事件不在 UI 线程，必须投递到 Avalonia UI 线程。</summary>
    public static void Open()
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_current == null)
                {
                    _current = new PreferencesWindow();
                    _current.Show();
                    return;
                }

                // 之前只是隐藏：直接唤醒，不再重建面板
                if (!_current.IsVisible)
                    _current.Show();

                if (_current.WindowState == WindowState.Minimized)
                    _current.WindowState = WindowState.Normal;

                _current.Activate();
            }
            catch (Exception ex)
            {
                Logger.Error("打开设置窗口失败", ex);
            }
        });
    }
}
