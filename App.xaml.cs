using Forms = System.Windows.Forms;
using WpfApp = System.Windows.Application;

using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Notifier
{
    
    public partial class App : WpfApp
    {
        private static Settings_Manager sets = new Settings_Manager();
        private bool on_setting=false;
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

        // Ensure main toast window exists and attach app-level handlers
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
                w.PreviewMouseRightButtonDown -= MainWindow_PreviewMouseRightButtonDown;
                w.PreviewMouseRightButtonDown += MainWindow_PreviewMouseRightButtonDown;

                // middle button for app activation from toast (use PreviewMouseDown and check ChangedButton)
                w.PreviewMouseDown -= MainWindow_PreviewMouseDown;
                w.PreviewMouseDown += MainWindow_PreviewMouseDown;
            }
            catch { }
        }

        private void DetachMainWindowHandlers(MainWindow w)
        {
            try
            {
                w.PreviewMouseRightButtonDown -= MainWindow_PreviewMouseRightButtonDown;
                w.PreviewMouseDown -= MainWindow_PreviewMouseDown;
            }
            catch { }
        }

        private void MainWindow_PreviewMouseRightButtonDown(object? sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (e.ChangedButton != System.Windows.Input.MouseButton.Right) return;
                bool ctrl = System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LeftCtrl) || System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RightCtrl);
                if (!ctrl) return;
                ShowPanelsFromApp();
                e.Handled = true;
            }
            catch { }
        }

        // Ctrl + middle click: attempt to wake the app that sent the current toast, then advance to next message
        private async void MainWindow_PreviewMouseDown(object? sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (e.ChangedButton != System.Windows.Input.MouseButton.Middle) return;
                bool ctrl = System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LeftCtrl) || System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RightCtrl);
                if (!ctrl) return;

                if (_currentToastWindow == null) return;
                var title = _currentToastWindow.GetCurrentHeadTitle();
                if (string.IsNullOrWhiteSpace(title)) return;

                // find the toast matching the displayed title
                var target = ToastMessageStore.GetAll().FirstOrDefault(m => string.Equals(
                    string.IsNullOrWhiteSpace(m.Title) ? "新通知" : m.Title,
                    title, StringComparison.Ordinal));

                if (target != null)
                {
                    // mimic MessageSummaryWindow: remove from store first, refresh UI, notify app
                    try
                    {
                        ToastMessageStore.RemoveByTitleAndSync(title);
                        _summaryWindow?.RefreshMessages();
                        ((App)System.Windows.Application.Current).OnMessagesHaveBeenCleared();
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

                    if (!success)
                    {
                        if (!string.IsNullOrWhiteSpace(target.AppName))
                        {
                            var res2 = AppActivator.TryBringToFrontByAppName(target.AppName);
                            success = res2.Success;
                            msg = res2.Message;
                            System.Diagnostics.Debug.WriteLine($"[AppActivator] 置前结果: Success={success}, Msg={msg}");
                        }
                    }
                }

                // regardless of success, instruct main window to skip current message and show next
                try { _currentToastWindow.SkipCurrentMessage(); } catch { }

                e.Handled = true;
            }
            catch { }
        }

        private void ShowPanelsFromApp()
        {
            if (_summaryWindow?.IsLoaded == true)
            {
                _summaryWindow.RefreshMessages();
                _summaryWindow.Activate();
                _settingWindow?.Activate();
                return;
            }

            _summaryFocus = false;
            _settingFocus = false;

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

            _summaryWindow.Show();
            _settingWindow.Show();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            sets.init_settings();
            resets();
            // Load or create configuration stored in registry. On first run defaults are written.
            //Config = RegistryConfig.LoadOrCreateDefaults();
            //Logger.Info($"应用启动 (MainWindowShown={Config.MainWindowShown})");

            InitializeNotifyIcon();
            Logger.Info("托盘图标已初始化");
            _ = InitializeListenerAsync();
	Task.Run(async () =>
        {
            await Task.Delay(1000);
            _isReadyForTrayClick = true;
        });

        }

        #region 托盘
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
                AddMessage("新通知:打开设置窗口", "Notifier");

                var settingForm = new Set(sets);
                settingForm.FormClosed += (_, __) =>
                {
                    on_setting = false;
                    resets();
                    AddMessage("新通知:设置窗口已关闭", "Notifier");
                };
                settingForm.StartPosition = Forms.FormStartPosition.CenterScreen;
                settingForm.Show();
                
            };
            menu.Items.Add(setting);

            menu.Items.Add(new Forms.ToolStripSeparator());
            var exit = new Forms.ToolStripMenuItem("退出");
            exit.Click += (_, __) => Shutdown();
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
            if (_summaryWindow?.IsLoaded == true)
            {
                _summaryWindow.RefreshMessages();
                _summaryWindow.Activate();
                _settingWindow?.Activate();
                return;
            }

            _summaryFocus = false;
            _settingFocus = false;

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

            _summaryWindow.Show();
            _settingWindow.Show();
        }

        private void TryDismissPanel()
        {
            if (_summaryWindow?._isClosing == true || _settingWindow?._isClosing == true)
                return;

            if (!_summaryFocus && !_settingFocus)
            {
                _summaryWindow?.RequestCloseFromApp();
                _settingWindow?.RequestClose();
            }
        }

        private void PanelCleanup()
        {
            _summaryFocus = false;
            _settingFocus = false;
            _summaryWindow = null;
            _settingWindow = null;
        }
        #endregion

        #region 监听
        private async System.Threading.Tasks.Task InitializeListenerAsync()
        {
            _listener = new ToastNotificationListener();
            var (ok, msg) = await _listener.InitializeAsync();
            if (!ok) { AddMessage($"新信息:⚠ 监听失败：{msg}", "Notifier"); Logger.Error($"监听初始化失败：{msg}"); return; }
            ToastMessageStore.Listener = _listener;
            _listener.OnToastDetected += OnToastDetected;
            AddMessage("新信息:✅ 通知监听已启动", "Notifier");
            Logger.Info("通知监听已启动");
            // event-driven: polling removed. Listener will invoke OnToastDetected when new toasts arrive.
        }

        private void OnToastDetected(ToastData toast)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => OnToastDetected(toast)); return; }
            if(!sets.is_toast_enabled) return;
            ToastMessageStore.Add(toast);
            SetAlertIcon();
            OnNewToastDetected?.Invoke(toast);
            _summaryWindow?.RefreshMessages();

            Logger.Info($"检测到新通知 Title=\"{toast.Title}\" App=\"{toast.AppName}\" Aumid=\"{toast.Aumid}\"");

            var text = !string.IsNullOrWhiteSpace(toast.Title) && !string.IsNullOrWhiteSpace(toast.Body)
                ? $"{toast.Title}:{toast.Body}" : toast.Title ?? toast.Body ?? "新通知";
            // pass along best-effort process identifier for bottom-right display
            AddMessage(text, toast.ProcessName);
        }

        public void OnMessagesHaveBeenCleared()
        {
            if (ToastMessageStore.UnreadCount <= 0) SetNormalIcon();
        }

        private void AddMessage(string text, string processName = "")
        {
            EnsureMainWindow();
            _currentToastWindow!.AddMessage(text, processName);
        }
        #endregion

        #region 托盘图标
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
        #endregion

        #region 自启
private static bool IsPackaged()
{
    try
    {
        var length = 0u;
        GetCurrentPackageFullName(ref length, null);
        var sb = new System.Text.StringBuilder((int)length);
        return GetCurrentPackageFullName(ref length, sb) == 0;
    }
    catch { return false; }
}

[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, System.Text.StringBuilder? packageFullName);

private const string StartupTaskId = "NotifierAutoStart";

private bool IsAutoStartEnabled()
{
    try
    {
        if (IsPackaged())
        {
            // 同步包装一下异步（托盘菜单构造时用）
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
    AddMessage("新信息:🔘 已关闭开机自启","Notifier");
    Logger.Info("开机自启已关闭(注册表)");
}
else
{
    k.SetValue(AppName, Environment.ProcessPath ?? "");
    AddMessage("新信息:🔘 已开启开机自启","Notifier");
    Logger.Info($"开机自启已开启(注册表) Path=\"{Environment.ProcessPath}\"");
}
        }
    }
    catch (Exception ex) { AddMessage($"新信息:❌ 自启失败：{ex.Message}","Notifier"); }
}

private static StartupTaskState StartupTaskGetSync()
{
    // 直接在新线程上同步等待，避免死锁
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
            AddMessage("新信息:🔘 已关闭开机自启", "Notifier");
            Logger.Info("开机自启已关闭(StartupTask)");
            break;
        case StartupTaskState.Disabled:
            var r = await task.RequestEnableAsync();
            AddMessage(r == StartupTaskState.Enabled
                ? "新信息:🔘 已开启开机自启"
                : "新信息:⚠️ 用户未确认开启自启", "Notifier");
            Logger.Info($"开机自启开启(StartupTask) 结果={r}");
            break;
        case StartupTaskState.DisabledByUser:
            AddMessage("新信息:⚠️ 已被你在任务管理器禁用，请到 设置→应用→启动 打开", "Notifier");
            Logger.Warn("开机自启被用户禁用(DisabledByUser)");
            break;
        default:
            AddMessage("新信息:⚠️ 系统策略禁止自启", "Notifier");
            Logger.Warn($"开机自启受系统策略限制 State={task.State}");
            break;
    }
}
#endregion

        protected override void OnExit(ExitEventArgs e)
        {
            Logger.Info($"应用退出 (ListenerActive={_listener != null})");
            // stop listener and detach handlers
            try { if (_listener != null) { _listener.OnToastDetected -= OnToastDetected; _listener.StopListening(); ToastMessageStore.Listener = null; _listener = null; } } catch (Exception ex) { Logger.Error("停止监听时异常", ex); }
            try { _notifyIcon?.Dispose(); } catch (Exception ex) { Logger.Error("释放托盘图标时异常", ex); }
            base.OnExit(e);
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
