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
        private readonly TimeSpan _displayInterval = TimeSpan.FromSeconds(3);
        private DateTime _displayDeadline;
        private TimeSpan? _pausedRemaining;
        private DispatcherTimer _ctrlPollTimer;
        private bool _ctrlHeld = false;

        #region Win32 interop & transparency
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;

        private const int SW_SHOWNOACTIVATE = 4;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;

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

        private IntPtr GetWindowHandle()
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
                ex |= WS_EX_TRANSPARENT;
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
            }
            catch { }
        }

        private async void AnimateBorderToColor(Avalonia.Media.Color target)
        {
            try
            {
                var outer = this.FindControl<Border>("OuterBorder");
                if (outer == null) return;
                var current = Colors.Transparent;
                if (outer.Background is SolidColorBrush scb)
                    current = scb.Color;
                const int frames = 8;
                const int msPerFrame = 15;
                for (int i = 1; i <= frames; i++)
                {
                    double t = (double)i / frames;
                    byte r = (byte)(current.R + (target.R - current.R) * t);
                    byte g = (byte)(current.G + (target.G - current.G) * t);
                    byte b = (byte)(current.B + (target.B - current.B) * t);
                    byte a = (byte)(current.A + (target.A - current.A) * t);
                    var c = Avalonia.Media.Color.FromArgb(a, r, g, b);
                    await Dispatcher.UIThread.InvokeAsync(() => outer.Background = new SolidColorBrush(c));
                    await Task.Delay(msPerFrame);
                }
            }
            catch { }
        }

        private bool _isExpandedByShift = false;
        private double _savedHeight = 0;
        private void ExpandForShift()
        {
            try
            {
                if (_isExpandedByShift) return;
                _isExpandedByShift = true;
                _savedHeight = this.Height;
                this.Height = double.NaN;
                // try to wrap textblocks
                try
                {
                    foreach (var tb in this.GetVisualDescendants().OfType<TextBlock>())
                    {
                        tb.TextWrapping = TextWrapping.Wrap;
                    }
                }
                catch { }
            }
            catch { }
        }

        private void CollapseFromShift()
        {
            try
            {
                if (!_isExpandedByShift) return;
                _isExpandedByShift = false;
                try
                {
                    foreach (var tb in this.GetVisualDescendants().OfType<TextBlock>())
                    {
                        tb.TextWrapping = TextWrapping.NoWrap;
                    }
                }
                catch { }
                this.Height = _savedHeight;
            }
            catch { }
        }
        #endregion

        private record QueuedMessage(DateTime Time, string Title, string Body, string ProcessName);

        public MainWindow()
        {
            InitializeComponent();
            MessageList.ItemsSource = _messageGroups;

            try
            {
                if (App.Config != null && !double.IsNaN(App.Config.MainWindowOpacity))
                    Opacity = App.Config.MainWindowOpacity;
            }
            catch { }

            this.Opened += (_, __) => PositionWindow();
            this.Closed += OnWindowClosed;

            _displayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _displayTimer.Tick += OnDisplayTimerTick;

            _ctrlPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _ctrlPollTimer.Tick += CheckCtrlState;
            // do not start polling until the window is actually displaying to reduce CPU usage

            this.PointerPressed += OnMouseLeftClick;
        }

        public void AddMessage(string title, string body, string processName = "")
        {
            if (_isClosed) return;

            Dispatcher.UIThread.Post(() =>
            {
                var t = string.IsNullOrWhiteSpace(title) ? "新通知" : title;
                var b = string.IsNullOrWhiteSpace(body) ? "" : body;

                _messageQueue.Add(new QueuedMessage(DateTime.Now, t, b, processName ?? ""));
                _messageQueue.Sort((a, c) => a.Time.CompareTo(c.Time));

                if (!_isDisplaying && !_isClosed)
                    StartDisplaying();
            });
        }

        private async Task FadeOutAsync()
        {
            if (_isClosingAnimation) return;
            _isClosingAnimation = true;
            try
            {
                const int frames = 10;
                const int msPerFrame = 12;
                double fromOpacity = RootGrid.Opacity;
                double toOpacity = 0.0;

                for (int i = 0; i <= frames; i++)
                {
                    double t = (double)i / frames;
                    RootGrid.Opacity = fromOpacity + (toOpacity - fromOpacity) * t;
                    await Task.Delay(msPerFrame);
                }
            }
            catch { }
            finally { _isClosingAnimation = false; }
        }

        private async Task FadeInAsync()
        {
            try
            {
                const int frames = 10;
                const int msPerFrame = 12;
                double fromOpacity = RootGrid.Opacity;
                double toOpacity = 1.0;

                for (int i = 0; i <= frames; i++)
                {
                    double t = (double)i / frames;
                    RootGrid.Opacity = fromOpacity + (toOpacity - fromOpacity) * t;
                    await Task.Delay(msPerFrame);
                }
            }
            catch { }
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
            });

            RootGrid.Opacity = 1;
            ShowNoActivateTopmost();
            EnableMouseTransparency();
            try { if (_ctrlPollTimer != null && !_ctrlPollTimer.IsEnabled) _ctrlPollTimer.Start(); } catch { }
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
                    AnimateBorderToColor(Avalonia.Media.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
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
                    AnimateBorderToColor(Avalonia.Media.Color.FromArgb(0xAC, 0xFF, 0xFF, 0xFF));
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
                _displayTimer.Interval = _displayInterval;
                _displayDeadline = DateTime.Now + _displayInterval;
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
                        _pausedRemaining = _displayInterval;
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
            var left = App.Config.IsMainWindowMiddle ? (workArea.Width - Width) / 2.0 : App.Config.MainWindowLeft;
            Position = new PixelPoint((int)Math.Round(left), (int)Math.Round(App.Config.MainWindowTop));
        }
    }
}