using System;
using System.Linq;
using System.Reflection;
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

        /// <summary>应用正在退出。面板窗口平时只隐藏，只有退出时才真正关闭。</summary>
        public static bool IsExiting { get; private set; }
        private bool _isReadyForTrayClick = false;
        private ToastNotificationListener? _listener;
        private Forms.NotifyIcon? _notifyIcon;

        private MainWindow? _currentToastWindow;
        private MessageSummaryWindow? _summaryWindow;
        private SettingWindow? _settingWindow;

        private bool _summaryFocus;
        private bool _settingFocus;
        private CancellationTokenSource? _dismissCts;

        // 注意：逐字字符串（@""）里的 \\ 是"两个反斜杠"，不是转义。
        // 原来写成 @"Software\\Microsoft\\..." 实际会去查一个带双反斜杠的错误路径，
        // 注册表操作静默失败（OpenSubKey 返回 null，被 catch 吞掉），
        // 表现为"开机自启开关点了没反应"。
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
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
            // 渲染后端自检：平台图形上下文是初始化后才建好的，稍等一下再探测。
            // 延到 3 秒后 —— 这只是往日志里写一行诊断信息，不该和启动抢 CPU
            // （原来 800ms 时正好和窗口创建、监听启动叠在一起）。
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(3000);
                    LogRenderBackend();
                }
                catch { }
            });
            // 就绪等待从 1000ms 缩到 300ms：初始化（设置/主题/托盘/监听）都在上面同步或并行完成，
            // 多等 700ms 只是让用户"点了没反应"，白白拖慢首次打开面板的体感。
            _ = Task.Run(async () =>
            {
                await Task.Delay(300);
                _isReadyForTrayClick = true;
            });
        }

        /// <summary>
        /// 探测实际生效的渲染后端（GPU / 软件），结果写进 app.log。
        ///
        /// 判定依据：Avalonia 只有在 ANGLE(EGL)/OpenGL 初始化成功时才会向服务定位器注册
        /// 平台图形接口；软件光栅化（Software 模式）下它是不存在的。
        ///
        /// 全程走反射的原因：Avalonia 11 起 AvaloniaLocator 与 IPlatformGraphics 都被标记为
        /// [PrivateApi] —— 运行时存在、但编译期引用程序集里看不到，直接写
        /// `AvaloniaLocator.Current` 会报 CS0117。反射按类型名查找可以绕过这个限制，
        /// 同时顺带兼容不同版本间接口改名（IPlatformGraphics / IPlatformOpenGlInterface）。
        /// 全部包在 try 里，探测失败只影响日志，不影响运行。
        /// </summary>
        private static void LogRenderBackend()
        {
            try
            {
                if (TryResolvePlatformGraphics(out var ifaceName, out var implName))
                {
                    Logger.Info($"渲染后端：GPU 加速已启用（{ifaceName} → {implName}）");
                    return;
                }

                // 定位器里查不到时，再看进程里有没有 ANGLE 的原生库（libEGL / libGLESv2）。
                // ANGLE 初始化成功一定会把它们加载进来，可作为第二重证据。
                if (TryFindAngleModule(out var moduleName))
                {
                    Logger.Info($"渲染后端：GPU 加速已启用（已加载 ANGLE 原生库 {moduleName}）");
                    return;
                }

                Logger.Warn("渲染后端：软件光栅化（未找到平台图形接口，已回退 CPU 渲染）。"
                          + "常见原因：显卡驱动过旧 / 远程桌面 / 显卡被 Avalonia 黑名单命中（如 Microsoft Basic Render）。");
            }
            catch (Exception ex)
            {
                Logger.Info($"渲染后端探测失败（不影响运行）：{ex.Message}");
            }
        }

        /// <summary>在服务定位器里找平台图形接口；找到说明 GPU 后端初始化成功。</summary>
        private static bool TryResolvePlatformGraphics(out string ifaceName, out string implName)
        {
            ifaceName = implName = string.Empty;

            var locatorType = FindType("Avalonia.AvaloniaLocator",
                "Avalonia.Base", "Avalonia.Controls", "Avalonia");
            if (locatorType == null) return false;

            // Current 在 11.x 是非公开的静态属性
            var currentProp = locatorType.GetProperty("Current",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var locator = currentProp?.GetValue(null);
            if (locator == null) return false;

            var getService = locator.GetType().GetMethod("GetService", new[] { typeof(Type) })
                             ?? locator.GetType().GetMethods().FirstOrDefault(
                                 m => m.Name == "GetService" && m.GetParameters().Length == 1);
            if (getService == null) return false;

            foreach (var candidate in new[] { "IPlatformGraphics", "IPlatformOpenGlInterface" })
            {
                var iface = FindTypeByName(candidate);
                if (iface == null) continue;

                var service = getService.Invoke(locator, new object[] { iface });
                if (service == null) continue;

                ifaceName = iface.Name;
                implName = service.GetType().Name;
                return true;
            }

            return false;
        }

        /// <summary>在已加载的 Avalonia 程序集里按全名找类型（找不到再按名字扫描）。</summary>
        private static Type? FindType(string fullName, params string[] assemblyNames)
        {
            foreach (var name in assemblyNames)
            {
                var asm = TryLoad(name);
                var type = asm?.GetType(fullName);
                if (type != null) return type;
            }

            var shortName = fullName.Substring(fullName.LastIndexOf('.') + 1);
            return FindTypeByName(shortName);
        }

        /// <summary>
        /// 按类型名查找，**只在 Avalonia 自己的程序集里找**。
        ///
        /// 原来这里会遍历 AppDomain 里所有程序集并对每个调用 GetTypes()，
        /// 包括 System.Private.CoreLib（数千个类型）—— 一次自检要花上百毫秒 CPU，
        /// 而它唯一的作用只是往日志里写一行。限定到 Avalonia.* 后开销可以忽略。
        /// </summary>
        private static Type? FindTypeByName(string typeName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string? asmName;
                try { asmName = asm.GetName().Name; }
                catch { continue; }

                if (asmName == null ||
                    !(asmName == "Avalonia" || asmName.StartsWith("Avalonia.", StringComparison.Ordinal)))
                    continue;

                Type? type;
                try { type = asm.GetType(typeName, false, false); }
                catch { continue; }

                if (type != null) return type;

                // 命名空间可能随版本变，兜底在这一个程序集内扫一遍
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name == typeName) return t;
                    }
                }
                catch { }
            }

            return null;
        }

        private static Assembly? TryLoad(string name)
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (string.Equals(asm.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
                        return asm;
                }

                return Assembly.Load(name);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>进程里是否已加载 ANGLE 的原生库（GPU 渲染的旁证）。</summary>
        private static bool TryFindAngleModule(out string moduleName)
        {
            moduleName = string.Empty;

            try
            {
                foreach (System.Diagnostics.ProcessModule module in
                         System.Diagnostics.Process.GetCurrentProcess().Modules)
                {
                    string fileName;
                    try { fileName = module.ModuleName ?? string.Empty; }
                    catch { continue; }

                    if (fileName.StartsWith("libEGL", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("libGLESv2", StringComparison.OrdinalIgnoreCase))
                    {
                        moduleName = fileName;
                        return true;
                    }
                }
            }
            catch
            {
                // 枚举模块在某些环境（沙箱/受限进程）会抛，忽略即可
            }

            return false;
        }

        private void OnAppExit()
        {
            IsExiting = true;
            Logger.Info($"应用退出 (ListenerActive={_listener != null})");
            try { if (_settingWindow != null) { _settingWindow._allowRealClose = true; _settingWindow.Close(); } } catch { }
            try { if (_summaryWindow != null) { _summaryWindow._allowRealClose = true; _summaryWindow.Close(); } } catch { }
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
            // 面板实例复用：只创建一次，之后每次点托盘都是 Show 而不是 new。
            // 旧实现每次都要重新解析两份 XAML、重建控件树、重新初始化 SMTC，
            // 几百毫秒全花在"打开"这个动作上；现在只是唤醒已有实例。
            if (_summaryWindow == null || _settingWindow == null ||
                _summaryWindow.IsReallyClosed || _settingWindow.IsReallyClosed)
            {
                _summaryFocus = true;
                _settingFocus = true;

                if (_settingWindow == null || _settingWindow.IsReallyClosed)
                    _settingWindow = new SettingWindow();
                if (_summaryWindow == null || _summaryWindow.IsReallyClosed)
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

            // 重新挂上变更订阅、打开音量轮询；收起期间攒下的变更在这里补一次刷新
            _summaryWindow.OnAppShown();
            _settingWindow.OnAppShown();

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
                // 面板平时只隐藏，退出时必须显式放行，否则 Closing 被拦住窗口关不掉
                IsExiting = true;
                try { if (_settingWindow != null) _settingWindow._allowRealClose = true; } catch { }
                try { if (_summaryWindow != null) _summaryWindow._allowRealClose = true; } catch { }

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
                        _summaryWindow.OnAppShown();
                        _settingWindow.OnAppShown();
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

            // 窗口只是被隐藏：这里不再把引用置 null，
            // 否则下次打开又要 new 两个窗口（解析 XAML + 建控件树 + 初始化 SMTC）。
            _summaryWindow?.OnAppHidden();
            _settingWindow?.OnAppHidden();

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

            // 日志是明文文件，这里**不记标题/正文**：
            // 通知内容常含验证码、私信片段等敏感信息，写进 app.log 等于留了一份明文副本，
            // 任何能读日志的程序都能看到。保留来源应用名，诊断价值基本不损失。
            Logger.Info($"检测到新通知 App=\"{toast.AppName}\" Aumid=\"{toast.Aumid}\"");

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

        private const string StartupTaskId = "NotifierAutoStart";

        private bool IsAutoStartEnabled()
        {
            try
            {
                if (AppEnvironment.IsPackaged)
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
                if (AppEnvironment.IsPackaged)
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
