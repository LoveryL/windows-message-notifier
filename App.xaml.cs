using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Forms = System.Windows.Forms;

namespace Notifier
{
    public partial class App : global::Avalonia.Application
    {
        public App()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private static Settings_Manager sets = new Settings_Manager();
        private bool on_setting = false;
        private bool _isReadyForTrayClick = false;
        private ToastNotificationListener? _listener;
        private Forms.NotifyIcon? _notifyIcon;

        private MainWindow? _currentToastWindow;
        private MessageSummaryWindow? _summaryWindow;
        private SettingWindow? _settingWindow;

        private bool _summaryFocus;
        private bool _settingFocus;

        private const string RunKey = @"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string AppName = "Notifier";

        // Loaded configuration (from registry)
        public static AppConfig Config { get; private set; } = new AppConfig();

        public static event Action<ToastData>? OnNewToastDetected;

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                desktop.Exit += (_, __) => OnAppExit();
            }

            base.OnFrameworkInitializationCompleted();
            sets.init_settings();
            resets();
            InitializeNotifyIcon();
            Logger.Info("托盘图标已初始化");
            _ = InitializeListenerAsync();
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                _isReadyForTrayClick = true;
            });
        }

        private void OnAppExit()
        {
            Logger.Info($"应用退出 (ListenerActive={_listener != null})");
            try { if (_listener != null) { _listener.OnToastDetected -= OnToastDetected; _listener.StopListening(); ToastMessageStore.Listener = null; _listener = null; } } catch (Exception ex) { Logger.Error("停止监听时异常", ex); }
            try { _notifyIcon?.Dispose(); } catch (Exception ex) { Logger.Error("释放托盘图标时异常", ex); }
        }

        private void EnsureMainWindow()
        {
            if (_currentToastWindow == null || !_currentToastWindow.IsVisible)
            {
                _currentToastWindow = new MainWindow();
                AttachMainWindowHandlers(_currentToastWindow);
                _currentToastWindow.Closed += (_, __) =>
                {
                    try { if (_currentToastWindow != null) DetachMainWindowHandlers(_currentToastWindow); } catch { }
                    _currentToastWindow = null;
                };
            }
        }

        private void AttachMainWindowHandlers(MainWindow w)
        {
            try
            {
                w.PointerPressed -= MainWindow_PointerPressed;
                w.PointerPressed += MainWindow_PointerPressed;
            }
            catch { }
        }

        private void DetachMainWindowHandlers(MainWindow w)
        {
            try
            {
                w.PointerPressed -= MainWindow_PointerPressed;
            }
            catch { }
        }

        private async void MainWindow_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            try
            {
                var props = e.GetCurrentPoint((Visual)sender!);
                if (props.Properties.IsRightButtonPressed && false)
                {
                    ShowPanelsFromApp();
                    e.Handled = true;
                    return;
                }

                if (props.Properties.IsMiddleButtonPressed && false)
                {
                    if (_currentToastWindow == null) return;
                    var title = _currentToastWindow.GetCurrentHeadTitle();
                    if (string.IsNullOrWhiteSpace(title)) return;

                    var target = ToastMessageStore.GetAll().FirstOrDefault(m => string.Equals(
                        string.IsNullOrWhiteSpace(m.Title) ? "新通知" : m.Title,
                        title, StringComparison.Ordinal));

                    if (target != null)
                    {
                        try
                        {
                            ToastMessageStore.RemoveByTitleAndSync(title);
                            _summaryWindow?.RefreshMessages();
                            ((App)global::Avalonia.Application.Current!).OnMessagesHaveBeenCleared();
                        }
                        catch { }

                        bool success = false;
                        string msg = "";
                        if (!string.IsNullOrWhiteSpace(target.Aumid))
                        {
                            var res = await AppActivator.active_app(target.Aumid);
                            success = res.Success;
                            msg = res.Message;
                            System.Diagnostics.Debug.WriteLine($"[AppActivator] 唤醒结果: Success={success}, Msg={msg}");
                        }

                        if (!success && !string.IsNullOrWhiteSpace(target.AppName))
                        {
                            var res2 = AppActivator.TryBringToFrontByAppName(target.AppName);
                            success = res2.Success;
                            msg = res2.Message;
                            System.Diagnostics.Debug.WriteLine($"[AppActivator] 置前结果: Success={success}, Msg={msg}");
                        }
                    }

                    try { _currentToastWindow.SkipCurrentMessage(); } catch { }
                    e.Handled = true;
                }
            }
            catch { }
        }

        private void ShowPanelsFromApp()
        {
            if (_summaryWindow == null || _settingWindow == null)
            {
                _summaryFocus = true;
                _settingFocus = true;

                _settingWindow = new SettingWindow();
                _summaryWindow = new MessageSummaryWindow();

                _summaryWindow.ReportFocusState += f =>
                {
                    _summaryFocus = f;
                    TryDismissPanel();
                };
                _settingWindow.ReportFocusState += f =>
                {
                    _settingFocus = f;
                    TryDismissPanel();
                };

                _summaryWindow.WindowClosed += PanelCleanup;
                _settingWindow.WindowClosed += PanelCleanup;
            }

            if (!_summaryWindow.IsVisible)
                _summaryWindow.Show();
            if (!_settingWindow.IsVisible)
                _settingWindow.Show();

            Dispatcher.UIThread.Post(() =>
            {
                if (_summaryWindow.WindowState == WindowState.Minimized)
                    _summaryWindow.WindowState = WindowState.Normal;
                if (_settingWindow.WindowState == WindowState.Minimized)
                    _settingWindow.WindowState = WindowState.Normal;

                _summaryWindow.Topmost = true;
                _settingWindow.Topmost = true;

                _summaryWindow.Activate();
                _settingWindow.Activate();
            });
        }

        private void InitializeNotifyIcon()
        {
            _notifyIcon = new Forms.NotifyIcon();
            SetNormalIcon();
            _notifyIcon.Visible = true;
            _notifyIcon.Text = "通知";

            var menu = new Forms.ContextMenuStrip();
            var auto = new Forms.ToolStripMenuItem("开机自启") { Checked = IsAutoStartEnabled() };
            auto.Click += (_, __) => { ToggleAutoStart(); auto.Checked = IsAutoStartEnabled(); };
            menu.Items.Add(auto);

            var setting = new Forms.ToolStripMenuItem("设置");
            setting.Click += (_, __) =>
            {
                if (on_setting) return;

                on_setting = true;
                AddMessage("设置", "打开设置窗口", "Notifier");

                var settingForm = new Set(sets);
                settingForm.FormClosed += (_, __) =>
                {
                    on_setting = false;
                    resets();
                    AddMessage("设置", "设置窗口已关闭", "Notifier");
                };
                settingForm.StartPosition = Forms.FormStartPosition.CenterScreen;
                settingForm.Show();
            };
            menu.Items.Add(setting);

            menu.Items.Add(new Forms.ToolStripSeparator());
            var exit = new Forms.ToolStripMenuItem("退出");
            exit.Click += (_, __) =>
            {
                if (global::Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    desktop.Shutdown();
            };
            menu.Items.Add(exit);

            _notifyIcon.ContextMenuStrip = menu;
            _notifyIcon.MouseClick += NotifyIcon_MouseClick;
        }

        private void NotifyIcon_MouseClick(object? sender, Forms.MouseEventArgs e)
        {
            if (e.Button != Forms.MouseButtons.Left) return;
            if (!_isReadyForTrayClick)
            {
                _notifyIcon?.ShowBalloonTip(1000, "正在初始化", "请稍后...", Forms.ToolTipIcon.Info);
                return;
            }

            if (_summaryWindow != null && _settingWindow != null && _summaryWindow.IsVisible)
            {
                _summaryWindow.RefreshMessages();
                _summaryWindow.Activate();
                _settingWindow?.Activate();
                return;
            }

            ShowPanelsFromApp();
        }

        private void TryDismissPanel()
        {
            var summaryWindow = _summaryWindow;
            var settingWindow = _settingWindow;

            if (summaryWindow == null || settingWindow == null)
                return;

            if (summaryWindow._isClosing || settingWindow._isClosing)
                return;

            bool anyWindowHasFocus = (summaryWindow.IsActive || settingWindow.IsActive || _summaryFocus || _settingFocus);
            if (!anyWindowHasFocus && summaryWindow.IsVisible && settingWindow.IsVisible)
            {
                summaryWindow.RequestCloseFromApp();
                settingWindow.RequestClose();
            }
        }

        private void PanelCleanup()
        {
            _summaryFocus = false;
            _settingFocus = false;

            if (_summaryWindow != null && !_summaryWindow.IsVisible)
                _summaryWindow = null;
            if (_settingWindow != null && !_settingWindow.IsVisible)
                _settingWindow = null;
        }

        private async Task InitializeListenerAsync()
        {
            _listener = new ToastNotificationListener();
            var (ok, msg) = await _listener.InitializeAsync();
            if (!ok)
            {
                AddMessage("通知状态", $"⚠ 监听失败：{msg}", "Notifier");
                Logger.Error($"监听初始化失败：{msg}");
                return;
            }
            ToastMessageStore.Listener = _listener;
            _listener.OnToastDetected += OnToastDetected;
            AddMessage("通知状态", "✅ 通知监听已启动", "Notifier");
            Logger.Info("通知监听已启动");
        }

        private void OnToastDetected(ToastData toast)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => OnToastDetected(toast));
                return;
            }

            if (!sets.is_toast_enabled) return;
            ToastMessageStore.Add(toast);
            SetAlertIcon();
            OnNewToastDetected?.Invoke(toast);
            _summaryWindow?.RefreshMessages();

            Logger.Info($"检测到新通知 Title=\"{toast.Title}\" App=\"{toast.AppName}\" Aumid=\"{toast.Aumid}\"");

            AddMessage(toast.Title, toast.Body, toast.ProcessName);
        }

        public void AddMessage(string title, string body, string processName = "")
        {
            EnsureMainWindow();
            _currentToastWindow!.AddMessage(title, body, processName);
        }

        public void OnMessagesHaveBeenCleared()
        {
            if (ToastMessageStore.UnreadCount <= 0)
                SetNormalIcon();
        }

        private void SetNormalIcon()
        {
            try { _notifyIcon!.Icon = new System.Drawing.Icon(GetType().Assembly.GetManifestResourceStream("Notifier.Resources.icon_normal.ico")!); }
            catch { _notifyIcon!.Icon = System.Drawing.SystemIcons.Application; }
        }

        private void SetAlertIcon()
        {
            try { _notifyIcon!.Icon = new System.Drawing.Icon(GetType().Assembly.GetManifestResourceStream("Notifier.Resources.icon_alert.ico")!); }
            catch { _notifyIcon!.Icon = System.Drawing.SystemIcons.Application; }
        }

        private static bool IsPackaged()
        {
            try
            {
                var length = 0u;
                GetCurrentPackageFullName(ref length, null);
                var sb = new StringBuilder((int)length);
                return GetCurrentPackageFullName(ref length, sb) == 0;
            }
            catch { return false; }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, StringBuilder? packageFullName);

        private const string StartupTaskId = "NotifierAutoStart";

        private bool IsAutoStartEnabled()
        {
            try
            {
                if (IsPackaged())
                {
                    var task = StartupTaskGetSync();
                    return task == StartupTaskState.Enabled;
                }
                else
                {
                    using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                    return k?.GetValue(AppName) != null;
                }
            }
            catch { return false; }
        }

        private void ToggleAutoStart()
        {
            try
            {
                if (IsPackaged())
                {
                    _ = TogglePackagedAutoStart();
                }
                else
                {
                    using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                    if (k.GetValue(AppName) != null)
                    {
                        k.DeleteValue(AppName, false);
                        AddMessage("开机自启", "🔘 已关闭开机自启", "Notifier");
                        Logger.Info("开机自启已关闭(注册表)");
                    }
                    else
                    {
                        k.SetValue(AppName, Environment.ProcessPath ?? "");
                        AddMessage("开机自启", "🔘 已开启开机自启", "Notifier");
                        Logger.Info($"开机自启已开启(注册表) Path=\"{Environment.ProcessPath}\"");
                    }
                }
            }
            catch (Exception ex) { AddMessage("开机自启", $"❌ 自启失败：{ex.Message}", "Notifier"); }
        }

        private static StartupTaskState StartupTaskGetSync()
        {
            try
            {
                return Task.Run(() =>
                {
                    Logger.Info($"准备获取 StartupTask,当前 StartupTaskId 值为: {StartupTaskId}");
                    var task = StartupTask.GetAsync(StartupTaskId).GetAwaiter().GetResult();
                    return task.State;
                }).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Logger.Error("获取 StartupTask 状态失败", ex);
                return StartupTaskState.Disabled;
            }
        }

        private async Task TogglePackagedAutoStart()
        {
            var task = await StartupTask.GetAsync(StartupTaskId);
            Logger.Info($"打包自启切换，当前 State={task.State}");
            switch (task.State)
            {
                case StartupTaskState.Enabled:
                    task.Disable();
                    AddMessage("开机自启", "🔘 已关闭开机自启", "Notifier");
                    Logger.Info("开机自启已关闭(StartupTask)");
                    break;
                case StartupTaskState.Disabled:
                    var r = await task.RequestEnableAsync();
                    AddMessage("开机自启",
                        r == StartupTaskState.Enabled ? "🔘 已开启开机自启" : "⚠️ 用户未确认开启自启",
                        "Notifier");
                    Logger.Info($"开机自启开启(StartupTask) 结果={r}");
                    break;
                case StartupTaskState.DisabledByUser:
                    AddMessage("开机自启", "⚠️ 已被你在任务管理器禁用，请到 设置→应用→启动 打开", "Notifier");
                    Logger.Warn("开机自启被用户禁用(DisabledByUser)");
                    break;
                default:
                    AddMessage("开机自启", "⚠️ 系统策略禁止自启", "Notifier");
                    Logger.Warn($"开机自启受系统策略限制 State={task.State}");
                    break;
            }
        }

        private void resets()
        {
            Config.IsMainWindowMiddle = sets.is_middle;
            Config.MainWindowTop = sets.window_top;
            Config.MainWindowOpacity = sets.opacity;
            if (!sets.is_middle)
                Config.MainWindowLeft = sets.window_left;
        }
    }
}
