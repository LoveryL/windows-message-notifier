using Forms = System.Windows.Forms;
using WpfApp = System.Windows.Application;

using System;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Notifier
{
    public partial class App : WpfApp
    {
        private ToastNotificationListener? _listener;
        private DispatcherTimer? _pollingTimer;
        private Forms.NotifyIcon? _notifyIcon;

        private MainWindow? _currentToastWindow;

        private MessageSummaryWindow? _summaryWindow;
        private SettingWindow? _settingWindow;

        private bool _summaryFocus;
        private bool _settingFocus;

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "Notifier";

        public static event Action<ToastData>? OnNewToastDetected;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            InitializeNotifyIcon();
            _ = InitializeListenerAsync();

            _pollingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _pollingTimer.Tick += (_, __) => _ = _listener?.FetchLatestNotificationAsync();
            _pollingTimer.Start();
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
        }

        private void OnToastDetected(ToastData toast)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => OnToastDetected(toast)); return; }

            ToastMessageStore.Add(toast);
            SetAlertIcon();
            OnNewToastDetected?.Invoke(toast);
            _summaryWindow?.RefreshMessages();

            var text = !string.IsNullOrWhiteSpace(toast.Title) && !string.IsNullOrWhiteSpace(toast.Body)
                ? $"{toast.Title}：{toast.Body}" : toast.Title ?? toast.Body ?? "新通知";
            AddMessage(text);
        }

        public void OnMessagesHaveBeenCleared()
        {
            if (ToastMessageStore.UnreadCount <= 0) SetNormalIcon();
        }

        private void AddMessage(string text)
        {
            if (_currentToastWindow == null || !_currentToastWindow.IsVisible)
            {
                _currentToastWindow = new MainWindow();
                _currentToastWindow.Closed += (_, __) => _currentToastWindow = null;
            }
            _currentToastWindow.AddMessage(text);
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
            _pollingTimer?.Stop();
            _notifyIcon?.Dispose();
            base.OnExit(e);
        }
    }
}