using Forms = System.Windows.Forms;
using WpfApp = System.Windows.Application;

using System;
using System.Windows;
using System.Windows.Threading;

namespace Notifier
{
    public partial class App : WpfApp
    {
        private ToastNotificationListener? _listener;
        private DispatcherTimer? _pollingTimer;
        private Forms.NotifyIcon? _notifyIcon;
        private MainWindow? _currentToastWindow;

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
                MessageBox.Show(message, "通知监听初始化失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _listener.OnToastDetected += OnToastDetected;

            Dispatcher.Invoke(() =>
            {
                AddMessage("✅ 通知监听已启动");
            });
        }

        private void InitializeNotifyIcon()
        {
            _notifyIcon = new Forms.NotifyIcon();
            _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            _notifyIcon.Visible = true;
            _notifyIcon.Text = "通知";

            var contextMenu = new Forms.ContextMenuStrip();
            
            var exitMenuItem = new Forms.ToolStripMenuItem("退出");
            exitMenuItem.Click += (s, e) => Shutdown();
            contextMenu.Items.Add(exitMenuItem);
            
            _notifyIcon.ContextMenuStrip = contextMenu;
        }

        private async void PollingTimer_Tick(object? sender, EventArgs e)
        {
            if (_listener != null)
            {
                var (data, message) = await _listener.FetchLatestNotificationAsync();
                
                if (data != null)
                {
                    System.Diagnostics.Debug.WriteLine($"检测到新通知: {data.Title}");
                }
            }
        }

        private void OnToastDetected(ToastData toastData)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => OnToastDetected(toastData));
                return;
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

            System.Diagnostics.Debug.WriteLine($"显示通知窗口: {displayText}");

            AddMessage(displayText);
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