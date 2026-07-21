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

        private bool _hasUnreadMessages;

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "Notifier";

        public static event Action<ToastData>? OnNewToastDetected;

        private void OnStartup(object sender, StartupEventArgs e)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            InitializeNotifyIcon();

            _listener = new ToastNotificationListener();
            _ = InitializeListenerAsync();

            _pollingTimer = new DispatcherTimer();
            _pollingTimer.Interval = TimeSpan.FromMilliseconds(500);
            _pollingTimer.Tick += PollingTimer_Tick!;
            _pollingTimer.Start();
        }

        private async System.Threading.Tasks.Task InitializeListenerAsync()
        {
            var (success, message) = await _listener!.InitializeAsync();

            System.Diagnostics.Debug.WriteLine($"初始化结果: {success} - {message}");

            if (!success)
            {
                AddMessage($"❌ 通知监听初始化失败: {message}");
                return;
            }

            ToastMessageStore.Listener = _listener;
            _listener.OnToastDetected += OnToastDetected;
            AddMessage("✅ 通知监听已启动");
        }

        private void InitializeNotifyIcon()
        {
            _notifyIcon = new Forms.NotifyIcon();
            SetNormalIcon();
            _notifyIcon.Visible = true;
            _notifyIcon.Text = "通知助手";

            var contextMenu = new Forms.ContextMenuStrip();

            var autoStartItem = new Forms.ToolStripMenuItem("开机自启");
            autoStartItem.Checked = IsAutoStartEnabled();
            autoStartItem.Click += (s, e) =>
            {
                ToggleAutoStart();
                autoStartItem.Checked = IsAutoStartEnabled();
            };
            contextMenu.Items.Add(autoStartItem);

            contextMenu.Items.Add(new Forms.ToolStripSeparator());

            var exitItem = new Forms.ToolStripMenuItem("退出");
            exitItem.Click += (s, e) => Shutdown();
            contextMenu.Items.Add(exitItem);

            _notifyIcon.ContextMenuStrip = contextMenu;
            _notifyIcon.MouseClick += NotifyIcon_MouseClick;
        }

        private bool IsAutoStartEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                var value = key?.GetValue(AppName);
                return value != null;
            }
            catch
            {
                return false;
            }
        }

        private void ToggleAutoStart()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (IsAutoStartEnabled())
                {
                    key.DeleteValue(AppName, false);
                    AddMessage("🔘 已关闭开机自启");
                }
                else
                {
                    var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                    key.SetValue(AppName, exePath);
                    AddMessage("🔘 已开启开机自启");
                }
            }
            catch (Exception ex)
            {
                AddMessage($"❌ 设置开机自启失败: {ex.Message}");
            }
        }

        private void SetNormalIcon()
        {
            try
            {
                _notifyIcon!.Icon = new System.Drawing.Icon(
                    System.Reflection.Assembly.GetExecutingAssembly()
                        .GetManifestResourceStream("Notifier.Resources.icon_normal.ico")!);
            }
            catch
            {
                _notifyIcon!.Icon = System.Drawing.SystemIcons.Application;
            }
        }

        private void SetAlertIcon()
        {
            try
            {
                _notifyIcon!.Icon = new System.Drawing.Icon(
                    System.Reflection.Assembly.GetExecutingAssembly()
                        .GetManifestResourceStream("Notifier.Resources.icon_alert.ico")!);
            }
            catch
            {
                _notifyIcon!.Icon = System.Drawing.SystemIcons.Application;
            }
        }

        private void NotifyIcon_MouseClick(object? sender, Forms.MouseEventArgs e)
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                if (_summaryWindow != null && _summaryWindow.IsVisible)
                {
                    _summaryWindow.Activate();
                    _summaryWindow.Focus();
                    return;
                }

                var messages = ToastMessageStore.GetAll();
                _summaryWindow = new MessageSummaryWindow(messages);
                _summaryWindow.Closed += (s, args) =>
                {
                    _summaryWindow = null;
                };
                _summaryWindow.Show();
            }
        }

        private async void PollingTimer_Tick(object? sender, EventArgs e)
        {
            if (_listener != null)
            {
                await _listener.FetchLatestNotificationAsync();
            }
        }

        private void OnToastDetected(ToastData toastData)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => OnToastDetected(toastData));
                return;
            }

            ToastMessageStore.Add(toastData);
            _hasUnreadMessages = true;

            if (_hasUnreadMessages) SetAlertIcon(); else SetNormalIcon();

            OnNewToastDetected?.Invoke(toastData);

            if (_summaryWindow != null && _summaryWindow.IsVisible)
            {
                _summaryWindow.RefreshMessages();
            }

            string displayText;
            if (!string.IsNullOrEmpty(toastData.Title) && !string.IsNullOrEmpty(toastData.Body))
            {
                displayText = $"{toastData.Title}: {toastData.Body}";
            }
            else if (!string.IsNullOrEmpty(toastData.Title))
            {
                displayText = toastData.Title;
            }
            else
            {
                displayText = toastData.Body ?? "新通知";
            }

            AddMessage(displayText);
        }

        public void OnMessagesHaveBeenCleared()
        {
            _hasUnreadMessages = ToastMessageStore.UnreadCount > 0;
            if (_hasUnreadMessages) SetAlertIcon(); else SetNormalIcon();
        }

        private void AddMessage(string text)
        {
            if (_currentToastWindow == null || !_currentToastWindow.IsVisible)
            {
                _currentToastWindow = new MainWindow();
                _currentToastWindow.Closed += (s, e) => _currentToastWindow = null;
            }

            _currentToastWindow.AddMessage(text);
        }

        private void OnExit(object? sender, ExitEventArgs e)
        {
            _pollingTimer?.Stop();
            _notifyIcon?.Dispose();
        }
    }
}