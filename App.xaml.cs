using Forms = System.Windows.Forms;
using WpfApp = System.Windows.Application;

using System;
using System.Linq;
using System.Runtime.InteropServices;
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

        private LowLevelMouseHook? _mouseHook;

        private void OnStartup(object sender, StartupEventArgs e)
{
    ShutdownMode = ShutdownMode.OnExplicitShutdown;

    InitializeNotifyIcon();


    _mouseHook = new LowLevelMouseHook();
    _mouseHook.MouseDown += (_, __) =>
    {
        if (_summaryWindow != null && _summaryWindow.IsLoaded && !_summaryWindow.IsActive)
        {
            Dispatcher.Invoke(() => _summaryWindow.RequestClose());
        }
    };
    _mouseHook.Start();

    _ = InitializeListenerAsync();

    _pollingTimer = new DispatcherTimer
    {
        Interval = TimeSpan.FromMilliseconds(500)
    };
    _pollingTimer.Tick += PollingTimer_Tick!;
    _pollingTimer.Start();
}

        private async System.Threading.Tasks.Task InitializeListenerAsync()
        {
            _listener = new ToastNotificationListener();
            var (ok, msg) = await _listener.InitializeAsync();
            System.Diagnostics.Debug.WriteLine($"初始化结果: {ok} - {msg}");
            if (!ok) { AddMessage($"警告:❌ {msg}"); return; }

            ToastMessageStore.Listener = _listener;
            _listener.OnToastDetected += OnToastDetected;
            AddMessage("新通知:✅ 通知监听已启动");
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

            if (_summaryWindow != null && _summaryWindow.IsLoaded)
            {
                _summaryWindow.RefreshMessages();
                _summaryWindow.Activate();
                return;
            }

            _summaryWindow = new MessageSummaryWindow(ToastMessageStore.GetAll());
            _summaryWindow.WindowClosed += () => _summaryWindow = null;
            _summaryWindow.Show();
        }

        private void OnToastDetected(ToastData toast)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => OnToastDetected(toast));
                return;
            }

            ToastMessageStore.Add(toast);
            _hasUnreadMessages = true;
            SetAlertIcon();

            OnNewToastDetected?.Invoke(toast);

            if (_summaryWindow != null && _summaryWindow.IsVisible)
                _summaryWindow.RefreshMessages();

            var text = !string.IsNullOrEmpty(toast.Title) && !string.IsNullOrEmpty(toast.Body)
                ? $"{toast.Title}: {toast.Body}"
                : toast.Title ?? toast.Body ?? "新通知";
            AddMessage(text);
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

        private async void PollingTimer_Tick(object? sender, EventArgs e)
        {
            if (_listener != null)
                await _listener.FetchLatestNotificationAsync();
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

        private bool IsAutoStartEnabled()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey);
                return k?.GetValue(AppName) != null;
            }
            catch { return false; }
        }

        private void ToggleAutoStart()
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                if (IsAutoStartEnabled())
                {
                    k.DeleteValue(AppName, false);
                    AddMessage("新通知:🔘 已关闭开机自启");
                }
                else
                {
                    k.SetValue(AppName, Environment.ProcessPath ?? "");
                    AddMessage("新通知:🔘 已开启开机自启");
                }
            }
            catch (Exception ex) { AddMessage($"新通知:❌ 自启失败: {ex.Message}"); }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _pollingTimer?.Stop();
            _notifyIcon?.Dispose();
            _mouseHook?.Stop();
            base.OnExit(e);
        }

        private sealed class LowLevelMouseHook
        {
            private const int WH_MOUSE_LL = 14;
            private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
            private readonly HookProc _proc;
            private IntPtr _hookID;

            public event EventHandler? MouseDown;

            public LowLevelMouseHook() => _proc = HookCallback;

            public void Start()
            {
                using var cur = Forms.Cursor.Current;
                _hookID = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
            }

            public void Stop()
            {
                if (_hookID != IntPtr.Zero)
                    UnhookWindowsHookEx(_hookID);
            }

            private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
            {
                if (nCode >= 0 && (wParam.ToInt32() is 0x0201 or 0x0204))
                    MouseDown?.Invoke(null, EventArgs.Empty);
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }

            [DllImport("user32.dll")]
            private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
            [DllImport("user32.dll")]
            private static extern bool UnhookWindowsHookEx(IntPtr hhk);
            [DllImport("user32.dll")]
            private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
            [DllImport("kernel32.dll")]
            private static extern IntPtr GetModuleHandle(string? lpModuleName);
        }
    }
}