using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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
        public static Settings_Manager setting => sets;
        private bool _isReadyForTrayClick = false;
        private ToastNotificationListener? _listener;
        private Forms.NotifyIcon? _notifyIcon;

        private MainWindow? _currentToastWindow;
        private MessageSummaryWindow? _summaryWindow;
        private SettingWindow? _settingWindow;

        private bool _summaryFocus;
        private bool _settingFocus;
        private CancellationTokenSource? _dismissCts;

        private const string RunKey = @"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string AppName = "Notifier";

        // Loaded configuration (from registry)

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
            // 主题桥接必须在任何窗口创建之前：之后所有窗口的 DynamicResource
            // 都会读到按设置覆盖过的画笔，不需要重启即可换肤。
            ThemeManager.Initialize();
            InitializeNotifyIcon();
            Logger.Info("托盘图标已初始化");
            // 后台预热消息缓存，把首次读盘 + JSON 反序列化的开销挪出 UI 线程
            ToastMessageStore.WarmUp();
            // 预热系统设置管理器：它的构造会初始化 NAudio 音频端点（COM）并做一次 WMI 亮度能力探测，
            // 合计可达数百毫秒。放在启动后台做，避免首次打开设置面板时卡这一下。
            _ = Task.Run(() =>
            {
                try { _ = SystemSettingsManager.Shared; }
                catch (Exception ex) { Logger.Error("预热系统设置管理器失败", ex); }
            });
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
            try { ToastMessageStore.PersistNow(); } catch (Exception ex) { Logger.Error("保存通知缓存时异常", ex); }
            try { sets.SaveNow(); } catch (Exception ex) { Logger.Error("保存设置时异常", ex); }
            try { if (SystemSettingsManager.IsSharedCreated) SystemSettingsManager.Shared.Dispose(); } catch (Exception ex) { Logger.Error("释放系统设置管理器时异常", ex); }
            try { _notifyIcon?.Dispose(); } catch (Exception ex) { Logger.Error("释放托盘图标时异常", ex); }
            Logger.Flush();
        }

        private void EnsureMainWindow()
        {
            // 原来这里还判了 !IsVisible：弹窗 Hide 之后（一条消息显示完就是 Hide）
            // 每次来新消息都会 new 一个 MainWindow —— 重新解析 XAML、重新建窗口句柄、
            // 重新注册事件。隐藏的窗口可以直接复用，窗口真正 Close 时下面的 handler
            // 已经把它置空，那时才会重建。
            if (_currentToastWindow == null)
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

        public void ShowPanelsFromApp()
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

            CancelPendingDismiss();
            ToastMessageStore.SetSummaryWindowVisible(true);

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

                // Activate setting first then summary so MessageSummaryWindow
                // ends up with focus by default when panels are shown.
                _settingWindow.Activate();
                _summaryWindow.Activate();
            });
        }

        private void CancelPendingDismiss()
        {
            try { _dismissCts?.Cancel(); } catch { }
            try { _dismissCts?.Dispose(); } catch { }
            _dismissCts = null;
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
                // 改用 Avalonia 设置窗口（原 WinForms 设置窗体已移除）。
                // Open() 内部会把创建/显示投递到 Avalonia UI 线程。
                PreferencesWindow.Open();
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

            // All Avalonia UI operations must run on the UI thread. The NotifyIcon event
            // runs on a WinForms thread which can cause cross-thread exceptions or crashes
            // when creating/showing Avalonia windows. Dispatch the work and protect with
            // a try/catch so exceptions don't crash the process.
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (_summaryWindow != null && _settingWindow != null && _summaryWindow.IsVisible)
                    {
                        _summaryWindow.RefreshMessages();
                        // Activate setting first then summary so MessageSummaryWindow
                        // ends up with focus by default when panels are shown.
                        _settingWindow?.Activate();
                        _summaryWindow.Activate();
                        return;
                    }

                    ShowPanelsFromApp();
                }
                catch (Exception ex)
                {
                    try { Logger.Error("托盘点击处理失败", ex); } catch { }
                }
            });
        }

        private void TryDismissPanel()
        {
            var summaryWindow = _summaryWindow;
            var settingWindow = _settingWindow;

            if (summaryWindow == null || settingWindow == null)
                return;

            if (summaryWindow._isClosing || settingWindow._isClosing)
                return;

            // Debounce dismiss to avoid transient focus changes closing the panels.
            // Cancel any pending dismissal and schedule a short delayed check.
            CancelPendingDismiss();
            _dismissCts = new CancellationTokenSource();
            var token = _dismissCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(80, token);
                    if (token.IsCancellationRequested) return;

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (token.IsCancellationRequested) return;

                        bool anyWindowHasFocus = (summaryWindow.IsActive || settingWindow.IsActive || _summaryFocus || _settingFocus);
                        if (!anyWindowHasFocus && summaryWindow.IsVisible && settingWindow.IsVisible)
                        {
                            try { summaryWindow.RequestCloseFromApp(); } catch { }
                            try { settingWindow.RequestClose(); } catch { }
                        }
                    });
                }
                catch (TaskCanceledException) { }
                catch { }
            });
        }

        private void PanelCleanup()
        {
            _summaryFocus = false;
            _settingFocus = false;
            CancelPendingDismiss();

            if (_summaryWindow != null && !_summaryWindow.IsVisible)
                _summaryWindow = null;
            if (_settingWindow != null && !_settingWindow.IsVisible)
                _settingWindow = null;

            if (_summaryWindow == null && _settingWindow == null)
                ToastMessageStore.SetSummaryWindowVisible(false);
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
            // 汇总面板通过 App.OnNewToastDetected 自行合并刷新，这里不再额外触发一次全量重建
            OnNewToastDetected?.Invoke(toast);

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

        // 托盘图标解码（ICO → GDI 位图）不便宜，而且状态切换非常频繁，这里做一次性缓存
        private static System.Drawing.Icon? _iconNormal;
        private static System.Drawing.Icon? _iconAlert;

        private static System.Drawing.Icon? LoadEmbeddedIcon(string resourceName)
        {
            try
            {
                var stream = typeof(App).Assembly.GetManifestResourceStream(resourceName);
                if (stream == null) return null;
                return new System.Drawing.Icon(stream);
            }
            catch
            {
                return null;
            }
        }

        private void SetNormalIcon()
        {
            try
            {
                _iconNormal ??= LoadEmbeddedIcon("Notifier.Resources.icon_normal.ico");
                _notifyIcon!.Icon = _iconNormal ?? System.Drawing.SystemIcons.Application;
            }
            catch { }
        }

        private void SetAlertIcon()
        {
            try
            {
                _iconAlert ??= LoadEmbeddedIcon("Notifier.Resources.icon_alert.ico");
                _notifyIcon!.Icon = _iconAlert ?? System.Drawing.SystemIcons.Application;
            }
            catch { }
        }

        private static int _packaged = -1; // -1 未探测 / 0 未打包 / 1 已打包

        private static bool IsPackaged()
        {
            // 结果不会变化，缓存起来，避免每次判断都做两次 kernel32 调用
            var cached = Volatile.Read(ref _packaged);
            if (cached >= 0) return cached == 1;

            var result = 0;
            try
            {
                var length = 0u;
                GetCurrentPackageFullName(ref length, null);
                var sb = new StringBuilder((int)length);
                result = GetCurrentPackageFullName(ref length, sb) == 0 ? 1 : 0;
            }
            catch { result = 0; }

            Volatile.Write(ref _packaged, result);
            return result == 1;
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
    }
}
