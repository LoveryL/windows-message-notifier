using Forms = System.Windows.Forms;
using WpfApp = System.Windows.Application;

using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Notifier
{
    
    public partial class App : WpfApp
    {
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
                        ((App)Application.Current).OnMessagesHaveBeenCleared();
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

            // Load or create configuration stored in registry. On first run defaults are written.
            Config = RegistryConfig.LoadOrCreateDefaults();

            InitializeNotifyIcon();
            _ = InitializeListenerAsync();

            // polling timer will be started after the listener initializes to avoid unnecessary ticks during startup.
            // (Timer creation moved to InitializeListenerAsync.)

            // If configuration indicates MainWindow should be shown at startup, create it now and apply stored properties.
            if (Config.MainWindowShown)
            {
                EnsureMainWindow();
                try
                {
                    if (!double.IsNaN(Config.MainWindowOpacity)) _currentToastWindow!.Opacity = Config.MainWindowOpacity;
                    if (!double.IsNaN(Config.MainWindowTop)) _currentToastWindow!.Top = Config.MainWindowTop;
                    if (!double.IsNaN(Config.MainWindowLeft)) _currentToastWindow!.Left = Config.MainWindowLeft;
                }
                catch { }

                _currentToastWindow!.Show();
            }
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
            if (!ok) { AddMessage($"新信息:⚠ 监听失败：{msg}"); return; }
            ToastMessageStore.Listener = _listener;
            _listener.OnToastDetected += OnToastDetected;
            AddMessage("新信息:✅ 通知监听已启动");

            // event-driven: polling removed. Listener will invoke OnToastDetected when new toasts arrive.
        }

        private void OnToastDetected(ToastData toast)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => OnToastDetected(toast)); return; }

            ToastMessageStore.Add(toast);
            SetAlertIcon();
            OnNewToastDetected?.Invoke(toast);
            _summaryWindow?.RefreshMessages();

            var text = !string.IsNullOrWhiteSpace(toast.Title) && !string.IsNullOrWhiteSpace(toast.Body)
                ? $"{toast.Title}:{toast.Body}" : toast.Title ?? toast.Body ?? "新通知";
            // pass along best-effort process identifier for bottom-right display
            EnsureMainWindow();
            _currentToastWindow!.AddMessage(text, toast.ProcessName);
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
        private bool IsAutoStartEnabled()
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(AppName) != null; }
            catch { return false; }
        }

        private void ToggleAutoStart()
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                if (IsAutoStartEnabled()) { k.DeleteValue(AppName, false); AddMessage("新信息:🔘 已关闭开机自启"); }
                else { k.SetValue(AppName, Environment.ProcessPath ?? ""); AddMessage("新信息:🔘 已开启开机自启"); }
            }
            catch (Exception ex) { AddMessage($"新信息:❌ 自启失败：{ex.Message}"); }
        }
        #endregion

        protected override void OnExit(ExitEventArgs e)
        {
            // stop listener and detach handlers
            try { if (_listener != null) { _listener.OnToastDetected -= OnToastDetected; _listener.StopListening(); ToastMessageStore.Listener = null; _listener = null; } } catch { }
            _notifyIcon?.Dispose();
            base.OnExit(e);
        }
    }
}
