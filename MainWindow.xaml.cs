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

        private IntPtr ResolveWindowHandle()
        {
            try
            {
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

        private void ShowNoActivateTopmost()
        {
            try
            {
                var hwnd = GetWindowHandle();
                if (hwnd == IntPtr.Zero) { Show(); return; }
                ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch { Show(); }
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

                this.Width = _windowWidth;
                this.InvalidateMeasure();
                this.UpdateLayout();

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

                            this.InvalidateMeasure();
                            this.UpdateLayout();
                        }
                        catch { }
                    });
                }
                else
                {
                    this.Height = _windowHeight;
                    this.InvalidateMeasure();
                    this.UpdateLayout();
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
            SetupRootTransform();

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
        private double _enterSlideX = 28;
        private double _enterScaleFrom = 0.94;
        private double _exitSlideX = 14;
        private double _exitScaleTo = 0.97;
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

            _enterSlideX = s.EnterSlideX;
            _enterScaleFrom = s.EnterScaleFrom;
            _exitSlideX = s.ExitSlideX;
            _exitScaleTo = s.ExitScaleTo;
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
        // 弹窗从屏幕右侧滑入：位移 + 轻微缩放 + 淡入，末段带一点过冲回弹（Q 弹）。

        // 动画全程复用同一组变换对象与画笔，避免每帧产生垃圾
        private readonly TranslateTransform _rootTranslate = new();
        private readonly ScaleTransform _rootScale = new() { ScaleX = 1, ScaleY = 1 };
        private SolidColorBrush? _borderAnimBrush;

        /// <summary>初始化 RootGrid 的变换（位移 + 缩放，以右侧中点为锚点）。</summary>
        private void SetupRootTransform()
        {
            try
            {
                RootGrid.RenderTransform = new TransformGroup
                {
                    Children = { _rootTranslate, _rootScale }
                };
                // 窗口贴在右上角，缩放/位移以右中点为原点更符合直觉
                RootGrid.RenderTransformOrigin = new RelativePoint(1, 0.5, RelativeUnit.Relative);
            }
            catch { }
        }

        /// <summary>把变换恢复到静止状态，避免下次出场时起点被上一段动画污染。</summary>
        private void ResetRootTransform()
        {
            try
            {
                _rootTranslate.X = 0;
                _rootScale.ScaleX = 1;
                _rootScale.ScaleY = 1;
            }
            catch { }
        }

        private async Task FadeOutAsync()
        {
            if (_isClosingAnimation) return;
            _isClosingAnimation = true;
            try
            {
                double fromOpacity = RootGrid.Opacity;
                double fromX = _rootTranslate.X;

                await UiTween.RunAsync(_exitDurationMs, t =>
                {
                    // 退场用 ease-in：起步慢、收尾快，视觉上"被收走"
                    double e = Easing.InCubic(t);
                    RootGrid.Opacity = fromOpacity * (1 - Easing.InQuad(t));
                    _rootTranslate.X = fromX + (_exitSlideX - fromX) * e;
                    double s = 1 - (1 - _exitScaleTo) * e;
                    _rootScale.ScaleX = s;
                    _rootScale.ScaleY = s;
                });
            }
            catch { }
            finally
            {
                _isClosingAnimation = false;
            }
        }

        private Task FadeInAsync()
        {
            try
            {
                // 入场动画失败也不能影响消息展示，错误在这里收敛
                return UiTween.RunAsync(_enterDurationMs, t =>
                {
                    // 位移与缩放用 OutBack：末段轻微越过终点再回弹 = Q 弹
                    double b = Easing.OutBack(t);
                    // 透明度单独走更快的一条曲线，避免"先看清了还在动"
                    double o = Easing.OutQuad(t / 0.65);
                    RootGrid.Opacity = o > 1 ? 1 : o;

                    _rootTranslate.X = _enterSlideX * (1 - b);

                    double s = _enterScaleFrom + (1 - _enterScaleFrom) * b;
                    _rootScale.ScaleX = s;
                    _rootScale.ScaleY = s;
                });
            }
            catch
            {
                RootGrid.Opacity = 1;
                ResetRootTransform();
                return Task.CompletedTask;
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
                // 复位变换，让下一次弹出从干净的起点开始
                ResetRootTransform();
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

        private async void StartDisplaying()
        {
            if (_isClosed || _isClosingAnimation) return;
            if (_messageQueue.Count == 0) return;

            StopDisplayTimer();
            _isDisplaying = true;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PositionWindow();
                ShowCurrentQueueHeadImmediate();
                // 先把内容复位到"入场起点"再显示窗口，否则第一帧会闪一下最终状态
                ResetRootTransform();
                RootGrid.Opacity = 0;
            });

            ShowNoActivateTopmost();
            EnableMouseTransparency();
            this.Focusable = false;
            try { if (this.WindowState == WindowState.Minimized) this.WindowState = WindowState.Normal; } catch { }
            try { if (_ctrlPollTimer != null && !_ctrlPollTimer.IsEnabled) _ctrlPollTimer.Start(); } catch { }

            // 滑入 + 缩放 + 淡入（结束再开始计时，用户看完整条才开始倒计时）
            await FadeInAsync();
            StartDisplayTimer();
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

        private void PositionWindow()
        {
            var screen = Screens.Primary;
            if (screen == null) return;

            var workArea = screen.WorkingArea;
            var left = App.setting.is_middle ? (workArea.Width - Width) / 2.0 : App.setting.window_left;
            Position = new PixelPoint((int)Math.Round(left), (int)Math.Round(App.setting.window_top));
        }
    }
}