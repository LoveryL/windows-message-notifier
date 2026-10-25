using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace Notifier
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer? _hideTimer;
        private readonly ObservableCollection<ToastMessageGroup> _messageGroups = new();
        private readonly System.Collections.Generic.List<QueuedMessage> _messageQueue = new();
        private DispatcherTimer _displayTimer;
        private bool _isDisplaying = false;
        private bool _isClosed = false;
        private bool _isClosingAnimation = false;
        private DateTime _displayDeadline;
        private TimeSpan? _pausedRemaining;
        private DispatcherTimer _ctrlPollTimer;
        private bool _ctrlHeld = false;

        private TimeSpan GetDisplayInterval()
        {
            var seconds = App.setting != null ? Math.Max(1, App.setting.show_time) : 3;
            return TimeSpan.FromSeconds(seconds);
        }

        #region Win32 interop & transparency
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;

        private const int SW_SHOWNOACTIVATE = 4;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const int WS_EX_LAYERED = 0x00080000;
        private const uint SWP_FRAMECHANGED = 0x0800;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size == 8)
            {
                return GetWindowLongPtr64(hWnd, nIndex);
            }
            else
            {
                return new IntPtr(GetWindowLong(hWnd, nIndex));
            }
        }

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            if (IntPtr.Size == 8)
            {
                return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
            }
            else
            {
                return new IntPtr(SetWindowLong(hWnd, nIndex, dwNewLong.ToInt32()));
            }
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_LSHIFT = 0xA0;
        private const int VK_RSHIFT = 0xA1;

        // 窗口句柄一经确定就不再变化：旧的反射 BFS 搜索每次弹窗都要跑一遍（枚举平台实现的
        // 全部属性/字段并逐个取值）。这里缓存句柄，并用一次 IsWindow 做有效性校验
        // （一次 Win32 调用，开销可忽略），避免窗口重建后仍然拿着失效句柄。
        private IntPtr _hwnd;
        private bool _hwndResolved;

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        private IntPtr GetWindowHandle()
        {
            if (_hwndResolved && _hwnd != IntPtr.Zero && IsWindow(_hwnd))
                return _hwnd;

            _hwndResolved = true;
            try { _hwnd = ResolveWindowHandle(); }
            catch { _hwnd = IntPtr.Zero; }

            return _hwnd;
        }

        // Avalonia 官方取句柄的 API（TopLevel.TryGetPlatformHandle）。
        // 用反射调用而不是直接调用：既能拿到 O(1) 的句柄，又不会因为不同 Avalonia 版本
        // 的方法签名差异而编译失败；拿不到时再退回下面的反射搜索。
        private static System.Reflection.MethodInfo? _platformHandleMethod;
        private static bool _platformHandleMethodResolved;

        private IntPtr TryGetPlatformHandleFast()
        {
            try
            {
                if (!_platformHandleMethodResolved)
                {
                    _platformHandleMethodResolved = true;
                    _platformHandleMethod = typeof(Window).GetMethod(
                        "TryGetPlatformHandle",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                }

                var method = _platformHandleMethod;
                if (method == null) return IntPtr.Zero;

                var platformHandle = method.Invoke(this, null);
                if (platformHandle == null) return IntPtr.Zero;

                var handleProp = platformHandle.GetType().GetProperty("Handle");
                var value = handleProp?.GetValue(platformHandle);
                return value is IntPtr ip ? ip : IntPtr.Zero;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private IntPtr ResolveWindowHandle()
        {
            try
            {
                // 首选官方 API（一次调用），反射 BFS 只作为兜底
                var fast = TryGetPlatformHandleFast();
                if (fast != IntPtr.Zero) return fast;

                var pi = this.GetType().GetProperty("PlatformImpl", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                var platformImpl = pi?.GetValue(this);
                if (platformImpl == null) return IntPtr.Zero;

                // Try common property chains first
                var handle = TryGetHandleFromObject(platformImpl);
                if (handle != IntPtr.Zero) return handle;

                // Fallback: scan properties/fields one level deep for IntPtr or numeric handle
                foreach (var prop in platformImpl.GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    try
                    {
                        var val = prop.GetValue(platformImpl);
                        var h = TryGetHandleFromObject(val);
                        if (h != IntPtr.Zero) return h;
                    }
                    catch { }
                }

                foreach (var fld in platformImpl.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    try
                    {
                        var val = fld.GetValue(platformImpl);
                        var h = TryGetHandleFromObject(val);
                        if (h != IntPtr.Zero) return h;
                    }
                    catch { }
                }

                return IntPtr.Zero;
            }
            catch { return IntPtr.Zero; }
        }

        private IntPtr TryGetHandleFromObject(object? obj)
        {
            if (obj == null) return IntPtr.Zero;
            try
            {
                // Direct integer/IntPtr
                if (obj is IntPtr ip) return ip;
                if (obj is long l) return new IntPtr(l);
                if (obj is int i) return new IntPtr(i);

                var visited = new System.Collections.Generic.HashSet<object>();
                var q = new System.Collections.Generic.Queue<object>();
                q.Enqueue(obj);

                while (q.Count > 0)
                {
                    var cur = q.Dequeue();
                    if (cur == null) continue;
                    if (visited.Contains(cur)) continue;
                    visited.Add(cur);

                    var t = cur.GetType();

                    // check common property names
                    foreach (var name in new[] { "Handle", "WindowHandle", "Hwnd", "hwnd", "NativeHandle" })
                    {
                        try
                        {
                            var p = t.GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                            if (p != null)
                            {
                                var val = p.GetValue(cur);
                                if (val is IntPtr ip2) return ip2;
                                if (val is long l2) return new IntPtr(l2);
                                if (val is int i2) return new IntPtr(i2);
                                if (val != null) q.Enqueue(val);
                            }
                        }
                        catch { }
                        try
                        {
                            var f = t.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                            if (f != null)
                            {
                                var val = f.GetValue(cur);
                                if (val is IntPtr ip3) return ip3;
                                if (val is long l3) return new IntPtr(l3);
                                if (val is int i3) return new IntPtr(i3);
                                if (val != null) q.Enqueue(val);
                            }
                        }
                        catch { }
                    }

                    // check SafeHandle/DangerousGetHandle
                    try
                    {
                        var methods = t.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        foreach (var m in methods)
                        {
                            if (m.Name == "DangerousGetHandle" && m.GetParameters().Length == 0)
                            {
                                try
                                {
                                    var val = m.Invoke(cur, null);
                                    if (val is IntPtr ip4) return ip4;
                                    if (val is long l4) return new IntPtr(l4);
                                    if (val is int i4) return new IntPtr(i4);
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }

                    // enqueue properties/fields for further search (one level deep by design)
                    try
                    {
                        foreach (var prop in t.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                        {
                            try
                            {
                                var val = prop.GetValue(cur);
                                if (val != null && !visited.Contains(val)) q.Enqueue(val);
                            }
                            catch { }
                        }
                    }
                    catch { }
                    try
                    {
                        foreach (var fld in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                        {
                            try
                            {
                                var val = fld.GetValue(cur);
                                if (val != null && !visited.Contains(val)) q.Enqueue(val);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                return IntPtr.Zero;
            }
            catch { return IntPtr.Zero; }
        }

        /// <summary>
        /// 显示窗口（不抢焦点、置顶）。
        ///
        /// 必须先走 Avalonia 的 Show()，再动 Win32：
        /// 只对 hwnd 调 ShowWindow 能把 Win32 窗口显示出来，但 Avalonia 这一侧的
        /// 窗口状态/合成器表面并没有初始化 —— 表现就是"窗口句柄存在、进程正常、
        /// 日志无异常，但屏幕上就是没有东西"。
        /// 旧实现只在拿不到 hwnd 时才 Show()，等于把初始化交给了运气。
        /// Show() 本身是幂等的（已显示时无操作），每次都调没有副作用。
        /// 窗口在 XAML 里设了 ShowActivated="False"，所以 Show() 不会抢焦点。
        /// </summary>
        private void ShowNoActivateTopmost()
        {
            try
            {
                // 1) 先让 Avalonia 完成窗口初始化 / 首次呈现
                if (!IsVisible) Show();

                // 2) 再用 Win32 保证"显示且不激活、且置顶"
                var hwnd = GetWindowHandle();
                if (hwnd == IntPtr.Zero) return;

                ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch (Exception ex)
            {
                Logger.Warn($"显示弹窗失败，回退到 Show(): {ex.Message}");
                try { Show(); } catch { }
            }
        }

        private void RevokeTopmost()
        {
            try
            {
                var hwnd = GetWindowHandle();
                if (hwnd == IntPtr.Zero) return;
                SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch { }
        }

        private void DisableMouseTransparency()
{
    try
    {
        var hwnd = GetWindowHandle();
        if (hwnd == IntPtr.Zero) return;
        var exPtr = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        long ex = exPtr.ToInt64();
        ex &= ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }
    catch { }
}

private void EnableMouseTransparency()
{
    try
    {
        var hwnd = GetWindowHandle();
        if (hwnd == IntPtr.Zero) return;
        var exPtr = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        long ex = exPtr.ToInt64();
        ex |= WS_EX_LAYERED | WS_EX_TRANSPARENT;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }
    catch { }
}

        // 时间驱动 + 复用同一个画笔实例：
        // 旧实现是 8 帧 × Task.Delay(15ms)，且每帧 new 一个 SolidColorBrush。
        // 现在总时长精确 200ms，动画期间零分配。

        private void AnimateBorderToColor(Avalonia.Media.Color target)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var outer = this.FindControl<Border>("OuterBorder");
                    if (outer == null) return;

                    var current = (outer.Background as SolidColorBrush)?.Color ?? Colors.Transparent;

                    // 全程只用一个画笔，逐帧改 Color，不再每帧新建对象
                    _borderAnimBrush ??= new SolidColorBrush(current);
                    _borderAnimBrush.Color = current;
                    outer.Background = _borderAnimBrush;

                    await UiTween.RunAsync(_borderAnimMs, t =>
                    {
                        double e = Easing.OutQuad(t);
                        _borderAnimBrush.Color = Avalonia.Media.Color.FromArgb(
                            (byte)(current.A + (target.A - current.A) * e),
                            (byte)(current.R + (target.R - current.R) * e),
                            (byte)(current.G + (target.G - current.G) * e),
                            (byte)(current.B + (target.B - current.B) * e));
                    });
                }
                catch { }
            });
        }

        private bool _isExpandedByShift = false;

        private double CalculateExpandedHeightFromContent()
        {
            try
            {
                var text = string.Join("\n", _messageGroups
                    .SelectMany(g => new[] { g.Title }.Concat(g.Bodies)))
                    .Trim();

                if (string.IsNullOrEmpty(text))
                    return _windowHeight;

                var estimatedLines = Math.Max(1, (int)Math.Ceiling(text.Length / 30.0));
                return Math.Min(_maxExpandedHeight, _windowHeight + (estimatedLines - 1) * 18);
            }
            catch
            {
                return _windowHeight;
            }
        }

        private void ApplyShiftWindowState(bool expand)
        {
            try
            {
                foreach (var tb in this.GetVisualDescendants().OfType<TextBlock>())
                {
                    tb.TextWrapping = expand ? TextWrapping.Wrap : TextWrapping.NoWrap;
                }

                // 不再同步强制布局：UpdateLayout() 会立刻跑一遍完整布局树，
                // 按 Ctrl+Shift 展开时每次都要等它，手感明显变钝。
                // 改完尺寸交给 Avalonia 在下一帧统一测量即可。
                this.Width = _windowWidth;

                if (expand)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            this.InvalidateMeasure();
                            this.UpdateLayout();

                            var itemHeight = MessageList?.DesiredSize.Height ?? 0;
                            if (double.IsNaN(itemHeight) || itemHeight <= 0)
                            {
                                this.Height = _windowHeight;
                                return;
                            }

                            var targetHeight = Math.Max(_windowHeight, itemHeight + 36);
                            this.Height = Math.Min(_maxExpandedHeight, targetHeight);
                        }
                        catch { }
                    });
                }
                else
                {
                    this.Height = _windowHeight;
                }
            }
            catch { }
        }

        private void ExpandForShift()
        {
            try
            {
                if (_isExpandedByShift) return;
                _isExpandedByShift = true;

                foreach (var tb in this.GetVisualDescendants().OfType<TextBlock>())
                {
                    tb.TextWrapping = TextWrapping.Wrap;
                }

                this.Width = _windowWidth;
                this.Height = CalculateExpandedHeightFromContent();
            }
            catch { }
        }

        private void CollapseFromShift()
        {
            try
            {
                if (!_isExpandedByShift) return;
                _isExpandedByShift = false;

                foreach (var tb in this.GetVisualDescendants().OfType<TextBlock>())
                {
                    tb.TextWrapping = TextWrapping.NoWrap;
                }

                this.Width = _windowWidth;
                this.Height = _windowHeight;
            }
            catch { }
        }
        #endregion

        private record QueuedMessage(DateTime Time, string Title, string Body, string ProcessName);

        public MainWindow()
        {
            InitializeComponent();
            MessageList.ItemsSource = _messageGroups;

            ApplySettings();

            this.Opened += (_, __) => PositionWindow();
            this.Closed += OnWindowClosed;

            if (App.setting != null)
                App.setting.SettingChanged += OnSettingChanged;
            ThemeManager.ThemeChanged += OnThemeChanged;

            var initialShowTime = App.setting != null ? Math.Max(1, App.setting.show_time) : 3;
            _displayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(initialShowTime) };
            _displayTimer.Tick += OnDisplayTimerTick;

            // 键盘状态只能轮询。间隔由设置控制（默认 150ms）：
            // 每秒约 6~7 次唤醒，人手几乎感知不到差异，但常驻开销明显低于 100ms。
            _ctrlPollTimer = new DispatcherTimer();
            _ctrlPollTimer.Tick += CheckCtrlState;
            // do not start polling until the window is actually displaying to reduce CPU usage

            this.PointerPressed += OnMouseLeftClick;
        }

        #region 设置驱动的外观 / 动画参数

        // 这些值在展示期间被高频读取（每帧、每次轮询），因此缓存成字段，
        // 只在设置变更时刷新，避免热路径上反复查设置字典。
        private double _windowWidth = 375;
        private double _windowHeight = 65;
        private double _maxExpandedHeight = 420;
        private int _enterDurationMs = 260;
        private int _exitDurationMs = 170;
        private int _borderAnimMs = 200;
        private bool _animEnabled = true;
        private int _pollIntervalMs = 150;

        private Avalonia.Media.Color _ctrlHoldColor = Avalonia.Media.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        private Avalonia.Media.Color _ctrlReleaseColor = Avalonia.Media.Color.FromArgb(0xAC, 0xFF, 0xFF, 0xFF);

        /// <summary>把所有可调项应用到本窗口。构造与设置变更时各调一次。</summary>
        private void ApplySettings()
        {
            var s = App.setting;

            _windowWidth = Math.Max(200, s.PopupWidth);
            _windowHeight = Math.Max(40, s.PopupHeight);
            _maxExpandedHeight = Math.Max(_windowHeight, s.PopupMaxHeight);

            // 进出场是纯淡入淡出，不再有位移/缩放（滑入相关的设置项已停用）
            _enterDurationMs = Math.Max(0, s.PopupEnterMs);
            _exitDurationMs = Math.Max(0, s.PopupExitMs);
            _borderAnimMs = Math.Max(0, s.BorderAnimMs);
            _animEnabled = s.AnimEnabled;
            _pollIntervalMs = Math.Clamp(s.PollIntervalMs, 50, 1000);

            _ctrlHoldColor = ThemeManager.ParseColor(s.CtrlHoldHex) ?? _ctrlHoldColor;
            _ctrlReleaseColor = ThemeManager.ParseColor(s.CtrlReleaseHex) ?? _ctrlReleaseColor;

            // 动画总开关：关闭后时长归零，UiTween 会直接跳到终态（等价于无动画）
            if (!_animEnabled)
            {
                _enterDurationMs = 0;
                _exitDurationMs = 0;
                _borderAnimMs = 0;
            }

            Width = _windowWidth;
            Height = _windowHeight;
            MinHeight = _windowHeight;

            try
            {
                var outer = this.FindControl<Border>("OuterBorder");
                if (outer != null)
                {
                    outer.CornerRadius = new CornerRadius(Math.Max(0, s.PopupCorner));
                    outer.BoxShadow = ThemeManager.BuildPopupShadow(s.PopupShadowHex, 24, 0);
                }
            }
            catch { }

            try
            {
                if (!double.IsNaN(s.opacity))
                    Opacity = s.opacity;
            }
            catch { }

            try
            {
                if (_ctrlPollTimer != null)
                {
                    bool wasRunning = _ctrlPollTimer.IsEnabled;
                    _ctrlPollTimer.Interval = TimeSpan.FromMilliseconds(_pollIntervalMs);
                    // Interval 赋值不会打断已在运行的计时器，按需重启一次让新间隔生效
                    if (wasRunning)
                    {
                        _ctrlPollTimer.Stop();
                        _ctrlPollTimer.Start();
                    }
                }
            }
            catch { }

            try { PositionWindow(); } catch { }
        }

        private void OnThemeChanged() => ApplySettings();

        #endregion

        private void OnSettingChanged(Settings_Manager.SettingType type)
        {
            try
            {
                // 尺寸/动画/不透明度等变化都要重新应用；show_time 只影响计时器
                if (type != Settings_Manager.SettingType.show_time)
                    ApplySettings();
            }
            catch { }

            if (type != Settings_Manager.SettingType.show_time)
                return;

            try
            {
                if (_displayTimer == null)
                    return;

                var interval = GetDisplayInterval();
                _displayTimer.Interval = interval;

                if (_isDisplaying && _displayTimer.IsEnabled)
                {
                    _displayDeadline = DateTime.Now + _displayTimer.Interval;
                    _displayTimer.Start();
                }
            }
            catch { }
        }

        public void AddMessage(string title, string body, string processName = "")
        {
            if (_isClosed) return;

            Dispatcher.UIThread.Post(() =>
            {
                var t = string.IsNullOrWhiteSpace(title) ? "新通知" : title;
                var b = string.IsNullOrWhiteSpace(body) ? "" : body;

                var msg = new QueuedMessage(DateTime.Now, t, b, processName ?? "");

                // 队列基本是按时间追加的，没必要每来一条就整队 Sort（O(n log n) + 委托分配）。
                // 绝大多数情况直接追加；只有乱序时才找插入位置。
                if (_messageQueue.Count == 0 || _messageQueue[^1].Time <= msg.Time)
                {
                    _messageQueue.Add(msg);
                }
                else
                {
                    int index = _messageQueue.FindIndex(m => m.Time > msg.Time);
                    if (index < 0) _messageQueue.Add(msg);
                    else _messageQueue.Insert(index, msg);
                }

                if (!_isDisplaying && !_isClosed)
                    StartDisplaying();
            });
        }

        // ===== 弹窗进出场动画参数 =====
        // 进出场是**纯淡入淡出**（只改 Opacity），不再有位移与缩放。
        //
        // 为什么去掉滑入/缩放：
        // 1) RenderTransform 每帧都要重算变换矩阵并重新合成整个弹窗表面，
        //    透明窗口 + 阴影的成本比只改透明度高得多；
        // 2) 位移/缩放会让弹窗"动起来"的观感依赖动画是否完整跑完 ——
        //    一旦被别的事打断，内容会停在偏移/缩小的中间态，而纯淡入淡出最坏也只是半透明；
        // 3) 少一个变换对象就少一处状态，不必再担心起点被上一段动画污染。
        //
        // 只保留一个画笔对象，避免每帧产生垃圾。
        private SolidColorBrush? _borderAnimBrush;

        /// <summary>
        /// 动画期间改用小半径阴影。
        /// 透明窗口的 BoxShadow 模糊每帧都要重新合成，24 的模糊半径在滑入/滑出的十几帧里
        /// 是纯 GPU 浪费（视觉上几乎看不出差别），静止时再换回完整阴影。
        /// </summary>
        private void SetPopupShadow(bool cheap)
        {
            try
            {
                var s = App.setting;
                var outer = this.FindControl<Border>("OuterBorder");
                if (s == null || outer == null) return;

                outer.BoxShadow = ThemeManager.BuildPopupShadow(s.PopupShadowHex, cheap ? 6 : 24, 0);
            }
            catch { }
        }

        private async Task FadeOutAsync()
        {
            if (_isClosingAnimation) return;
            _isClosingAnimation = true;

            bool cheap = _exitDurationMs > 0;
            if (cheap) SetPopupShadow(true);
            try
            {
                double fromOpacity = RootGrid.Opacity;

                await UiTween.RunAsync(_exitDurationMs, t =>
                {
                    // 纯淡出：ease-in，起步慢、收尾快，视觉上"被收走"
                    RootGrid.Opacity = fromOpacity * (1 - Easing.InQuad(t));
                });
            }
            catch { }
            finally
            {
                _isClosingAnimation = false;
                if (cheap) SetPopupShadow(false);
            }
        }

        private async Task FadeInAsync()
        {
            bool cheap = _enterDurationMs > 0;
            if (cheap) SetPopupShadow(true);
            try
            {
                // 入场动画失败也不能影响消息展示，错误在这里收敛
                await UiTween.RunAsync(_enterDurationMs, t =>
                {
                    // 纯淡入：透明度走略快于线性的曲线，前段就基本清晰
                    double o = Easing.OutQuad(t / 0.65);
                    RootGrid.Opacity = o > 1 ? 1 : o;
                });
            }
            catch
            {
                RootGrid.Opacity = 1;
            }
            finally
            {
                if (cheap) SetPopupShadow(false);
            }
        }

        private void StopDisplayTimer()
        {
            try { if (_displayTimer != null && _displayTimer.IsEnabled) _displayTimer.Stop(); } catch { }
        }

        private async Task HideWindowAndResetState(bool clearQueue = true)
        {
            if (_isClosed || _isClosingAnimation) return;

            StopDisplayTimer();
            try { if (_ctrlPollTimer != null && _ctrlPollTimer.IsEnabled) _ctrlPollTimer.Stop(); } catch { }
            _pausedRemaining = null;
            _isDisplaying = false;
            if (!_isClosed)
            {
                await FadeOutAsync();
                RootGrid.Opacity = 0;
                Hide();
                RevokeTopmost();
            }
            if (clearQueue)
            {
                _messageQueue.Clear();
                _messageGroups.Clear();
            }
        }

        private async void OnDisplayTimerTick(object? sender, EventArgs e)
        {
            if (_isClosed || _isClosingAnimation) return;

            StopDisplayTimer();

            if (_messageQueue.Count == 0)
            {
                await HideWindowAndResetState(true);
                return;
            }

            if (_messageQueue.Count > 1)
            {
                await FadeOutAsync();

                if (_messageQueue.Count > 0)
                    _messageQueue.RemoveAt(0);

                ShowCurrentQueueHeadImmediate();
                await FadeInAsync();

                _isDisplaying = true;
                StartDisplayTimer();
                return;
            }

            await FadeOutAsync();
            if (_messageQueue.Count > 0)
                _messageQueue.RemoveAt(0);

            if (_messageQueue.Count == 0)
            {
                await HideWindowAndResetState(true);
                return;
            }

            ShowCurrentQueueHeadImmediate();
            await FadeInAsync();
            _isDisplaying = true;
            StartDisplayTimer();
        }

        // 上一次"开始显示"的时间戳，用于卡死自检：
        // _isDisplaying 为 true 却迟迟看不到窗口时，说明中间某一步卡住了，
        // 此时要强制复位状态，否则后续所有消息都会被"正在显示中"挡住，一条都不再弹。
        private long _displayStartedAt;

        private async void StartDisplaying()
        {
            // 卡死自检：正在显示却长时间不可见 → 说明上一次流程中途夭折（无异常、无日志）。
            // 这里强制复位，让本次消息能正常弹出，避免"从此再也不显示"。
            if (_isDisplaying && !_isClosingAnimation && !IsVisible &&
                Environment.TickCount64 - _displayStartedAt > 3000)
            {
                Logger.Warn("检测到显示状态卡死（标记显示中但窗口不可见），已强制复位");
                _isDisplaying = false;
                _isClosingAnimation = false;
            }

            if (_isClosed || _isClosingAnimation) return;
            if (_messageQueue.Count == 0) return;

            StopDisplayTimer();
            _isDisplaying = true;
            _displayStartedAt = Environment.TickCount64;

            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    PositionWindow();
                    ShowCurrentQueueHeadImmediate();
                    // 先把透明度归零再显示窗口，否则第一帧会闪一下最终状态
                    RootGrid.Opacity = 0;
                });

                ShowNoActivateTopmost();
                EnableMouseTransparency();
                this.Focusable = false;
                try { if (this.WindowState == WindowState.Minimized) this.WindowState = WindowState.Normal; } catch { }
                try { if (_ctrlPollTimer != null && !_ctrlPollTimer.IsEnabled) _ctrlPollTimer.Start(); } catch { }

                // 窗口已经显示出来了：无论入场动画成败，内容都必须可见。
                // 这里给动画加一道超时（时长 + 800ms），超时就直接落到终态，
                // 杜绝"动画没跑完 → Opacity 停在 0 → 窗口存在但看不见"。
                var fadeIn = FadeInAsync();
                var guard = Task.Delay(_enterDurationMs + 800);
                if (await Task.WhenAny(fadeIn, guard) == guard)
                {
                    Logger.Warn($"入场动画超时（{_enterDurationMs}ms），强制落到终态以保证弹窗可见");
                    ForceVisibleState();
                }

                // 双保险：动画结束后若仍不可见，直接拉到终态
                if (RootGrid.Opacity < 0.99) ForceVisibleState();
            }
            catch (Exception ex)
            {
                // 以前这里是 async void + 无 catch：任何异常都被吞掉，
                // 表现为"不显示，但进程正常、日志干净"，极难排查。
                Logger.Error("显示弹窗过程中异常", ex);
                try { ForceVisibleState(); } catch { }
            }
            finally
            {
                StartDisplayTimer();
            }
        }

        /// <summary>把弹窗强制拉到"完全可见"的静止终态。</summary>
        private void ForceVisibleState()
        {
            try
            {
                RootGrid.Opacity = 1;
                if (!IsVisible) Show();
            }
            catch (Exception ex)
            {
                Logger.Warn($"强制显示弹窗失败: {ex.Message}");
            }
        }

        private void ShowCurrentQueueHeadImmediate()
        {
            _messageGroups.Clear();
            var head = _messageQueue.FirstOrDefault();
            if (head != null)
            {
                var group = new ToastMessageGroup
                {
                    Title = head.Title,
                    ProcessName = head.ProcessName,
                    Time = head.Time
                };
                if (!string.IsNullOrEmpty(head.Body))
                    group.Bodies.Add(head.Body);

                _messageGroups.Add(group);
            }
        }

        public string? GetCurrentHeadTitle()
        {
            var head = _messageQueue.FirstOrDefault();
            return head == null ? null : (string.IsNullOrWhiteSpace(head.Title) ? "新通知" : head.Title);
        }

        private void CheckCtrlState(object? sender, EventArgs e)
        {
            if (_isClosed || !_isDisplaying || !IsVisible) return;

            try
            {
                bool isCtrlDown = (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0 || (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0;
                bool isShiftDown = (GetAsyncKeyState(VK_LSHIFT) & 0x8000) != 0 || (GetAsyncKeyState(VK_RSHIFT) & 0x8000) != 0;

                if (isCtrlDown && !_ctrlHeld)
                {
                    _ctrlHeld = true;
                    AnimateBorderToColor(_ctrlHoldColor);
                    if (_displayTimer.IsEnabled)
                    {
                        var remaining = _displayDeadline - DateTime.Now;
                        _pausedRemaining = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
                        _displayTimer.Stop();
                    }
                    DisableMouseTransparency();
                }
                else if (!isCtrlDown && _ctrlHeld)
                {
                    _ctrlHeld = false;
                    AnimateBorderToColor(_ctrlReleaseColor);
                    if (_pausedRemaining.HasValue)
                    {
                        var rem = _pausedRemaining.Value;
                        _displayTimer.Interval = rem <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : rem;
                        _displayDeadline = DateTime.Now + _displayTimer.Interval;
                        _displayTimer.Start();
                        _pausedRemaining = null;
                    }
                    if (_isExpandedByShift)
                        CollapseFromShift();
                    EnableMouseTransparency();
                }

                if (isCtrlDown && isShiftDown && !_isExpandedByShift)
                {
                    ExpandForShift();
                }
                else if ((!isCtrlDown || !isShiftDown) && _isExpandedByShift)
                {
                    CollapseFromShift();
                }
            }
            catch { }
        }

        private void OnWindowClosed(object? sender, EventArgs e)
        {
            _isClosed = true;
            _isDisplaying = false;
            _messageQueue.Clear();
            _messageGroups.Clear();

            try { if (_displayTimer != null) { _displayTimer.Stop(); _displayTimer.Tick -= OnDisplayTimerTick; } } catch { }
            try { if (_hideTimer != null) { _hideTimer.Stop(); _hideTimer = null; } } catch { }
            try { if (_ctrlPollTimer != null) { _ctrlPollTimer.Stop(); _ctrlPollTimer.Tick -= CheckCtrlState; } } catch { }
            try { this.PointerPressed -= OnMouseLeftClick; } catch { }
            // App.setting 是进程级单例，不解绑会让每一个已关闭的弹窗实例都无法被回收。
            // 弹窗会被反复创建（隐藏后再次来消息就 new 一个），这是持续增长的内存泄漏。
            try { if (App.setting != null) App.setting.SettingChanged -= OnSettingChanged; } catch { }
            try { ThemeManager.ThemeChanged -= OnThemeChanged; } catch { }
        }

        private void StartHideTimer()
        {
            if (_hideTimer == null)
            {
                _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _hideTimer.Tick += (_, __) =>
                {
                    if (_isClosed || _isClosingAnimation) return;
                    _hideTimer.Stop();
                    _ = HideWindowAndResetState(true);
                };
            }
            else
            {
                _hideTimer.Stop();
            }

            _hideTimer.Start();
        }

        private void StartDisplayTimer()
        {
            try
            {
                var interval = GetDisplayInterval();
                _displayTimer.Interval = interval;
                _displayDeadline = DateTime.Now + interval;
                _pausedRemaining = null;

                if (!_isClosed && !_isClosingAnimation)
                    _displayTimer.Start();
            }
            catch { }
        }

        private void OnMouseLeftClick(object? sender, PointerPressedEventArgs e)
        {
            try
            {
                if (!_ctrlHeld || !_isDisplaying || !IsVisible) return;
                var props = e.GetCurrentPoint(this).Properties;

                // Ctrl + Right -> show panels (like tray click)
                if (props.IsRightButtonPressed)
                {
                    try
                    {
                        var app = (App)global::Avalonia.Application.Current!;
                        Dispatcher.UIThread.Post(() =>
                        {
                            try { app.ShowPanelsFromApp(); } catch { }
                        });
                    }
                    catch { }
                    e.Handled = true;
                    return;
                }

                // Ctrl + Middle -> attempt to wake/bring app to front for current toast
                if (props.IsMiddleButtonPressed)
                {
                    try
                    {
                        var title = GetCurrentHeadTitle();
                        if (!string.IsNullOrWhiteSpace(title))
                        {
                            var target = ToastMessageStore.GetAll().FirstOrDefault(m => string.Equals(
                                string.IsNullOrWhiteSpace(m.Title) ? "新通知" : m.Title,
                                title, StringComparison.Ordinal));

                            if (target != null)
                            {
                                try
                                {
                                    ToastMessageStore.RemoveByTitleAndSync(title);
                                    try { ((App)global::Avalonia.Application.Current!).OnMessagesHaveBeenCleared(); } catch { }
                                }
                                catch { }

                                // Fire-and-forget activation so UI thread isn't blocked.
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        bool success = false;
                                        if (!string.IsNullOrWhiteSpace(target.Aumid))
                                        {
                                            var res = await AppActivator.active_app(target.Aumid);
                                            success = res.Success;
                                        }

                                        if (!success && !string.IsNullOrWhiteSpace(target.AppName))
                                        {
                                            AppActivator.TryBringToFrontByAppName(target.AppName);
                                        }
                                    }
                                    catch { }
                                });
                            }
                        }
                    }
                    catch { }

                    try { SkipCurrentMessage(); } catch { }
                    e.Handled = true;
                    return;
                }

                // Default: left click with Ctrl -> skip message
                if (props.IsLeftButtonPressed)
                {
                    SkipCurrentMessage();
                    e.Handled = true;
                }
            }
            catch { }
        }

        public async void SkipCurrentMessage()
        {
            try
            {
                if (!_isDisplaying || _isClosed) return;

                StopDisplayTimer();

                if (_messageQueue.Count == 0)
                {
                    await HideWindowAndResetState(true);
                    return;
                }

                if (_messageQueue.Count > 1)
                {
                    await FadeOutAsync();

                    if (_messageQueue.Count > 0)
                        _messageQueue.RemoveAt(0);

                    ShowCurrentQueueHeadImmediate();
                    await FadeInAsync();

                    _isDisplaying = true;

                    if (_ctrlHeld)
                    {
                        _pausedRemaining = GetDisplayInterval();
                    }
                    else
                    {
                        StartDisplayTimer();
                    }

                    return;
                }

                _messageQueue.Clear();
                _messageGroups.Clear();
                await HideWindowAndResetState(false);
            }
            catch { }
        }

        // Screens.Primary 内部是一次 Win32 显示器枚举，而弹窗每次显示都要定位一次。
        // 结果缓存 5 秒：换显示器/改分辨率后自动过期，正常情况省掉这次系统调用。
        // 这里缓存的是工作区矩形而不是 Screen 对象：
        // 项目开了 UseWindowsForms，System.Windows.Forms 被隐式全局引入，
        // 直接写 Screen 会与 Avalonia 的 Screen 撞名（CS0104），
        // 而 Screen 在 Avalonia 各版本里还换过命名空间（Controls / Platform），
        // 写全限定名同样不稳。缓存 PixelRect 就绕开了整个类型。
        // 缓存字段放在实例上：Screens 是 WindowBase 的实例属性（不是静态的），
        // 所以查询方法只能是实例方法。
        private PixelRect? _cachedWorkArea;
        private long _screenCacheStamp;
        private const int ScreenCacheTtlMs = 5000;

        private PixelRect? PrimaryWorkAreaCached()
        {
            var now = Environment.TickCount64;
            if (_cachedWorkArea != null && now - _screenCacheStamp < ScreenCacheTtlMs)
                return _cachedWorkArea;

            var screen = this.Screens.Primary; // var 推断，不出现 Screen 类型名 —— 避免与 WinForms 的同名类型冲突
            _cachedWorkArea = screen?.WorkingArea;
            _screenCacheStamp = now;
            return _cachedWorkArea;
        }

        private void PositionWindow()
        {
            var workArea = PrimaryWorkAreaCached();
            if (workArea == null) return;

            var area = workArea.Value;
            var left = App.setting.is_middle ? (area.Width - Width) / 2.0 : App.setting.window_left;
            Position = new PixelPoint((int)Math.Round(left), (int)Math.Round(App.setting.window_top));
        }
    }
}