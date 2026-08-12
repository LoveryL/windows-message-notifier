using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Controls;


namespace Notifier
{
    public partial class MainWindow : Window
    {
#region Effect
        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINCOMPATTRDATA data);

        [StructLayout(LayoutKind.Sequential)]
        struct ACCENTPOLICY
        {
            public int nAccentState;
            public int nFlags;
            public int nColor;
            public int nAnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WINCOMPATTRDATA
        {
            public int nAttribute;
            public IntPtr pData;
            public int ulDataSize;
        }
        #endregion
        #region Win32 无焦点置顶 & 鼠标穿透
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

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);
        #endregion
        private DispatcherTimer? _hideTimer;
        private ObservableCollection<ToastMessageGroup> _messageGroups = new();
        private bool _isClosingAnimation = false;

        // New: queue incoming messages and display them sequentially (earliest->latest)
        private readonly System.Collections.Generic.List<QueuedMessage> _messageQueue = new();
        private DispatcherTimer _displayTimer;
                private bool _isDisplaying = false;

        // Pause/resume-aware display timer support
        private readonly TimeSpan _displayInterval = TimeSpan.FromSeconds(3);
        private DateTime _displayDeadline;
        private TimeSpan? _pausedRemaining = null;

        // Polling timer to detect Ctrl key pressed globally while the window is visible
        private DispatcherTimer _ctrlPollTimer;
        private bool _ctrlHeld = false;

        private record QueuedMessage(DateTime Time, string Title, string Body, string ProcessName);


        public MainWindow()
        {
            InitializeComponent();
            MessageList.ItemsSource = _messageGroups;

            // Apply configured opacity if available (defaults preserved otherwise)
            try
            {
                if (App.Config != null && !double.IsNaN(App.Config.MainWindowOpacity))
                    Opacity = App.Config.MainWindowOpacity;
            }
            catch { }

            Loaded += (s, e) =>
            {
                PositionWindow();
                
            };
            SourceInitialized += (_, __) =>
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT);
            };

            // Display timer: each message shows for 3s. Timer is paused during transitions.
            _displayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _displayTimer.Tick += (_, __) => OnDisplayTimerTick();

            // Polling timer to detect Ctrl key presses while notifications are showing
            _ctrlPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _ctrlPollTimer.Tick += CheckCtrlState;
            _ctrlPollTimer.Start();
        }

        #region 公开接口
        public void AddMessage(string text, string processName = "")
        {
            // ensure enqueue and UI operations run on UI thread
            Dispatcher.Invoke(() =>
            {
                var (title, body) = ParseMessage(text);
                var t = string.IsNullOrWhiteSpace(title) ? "新通知" : title;
                var b = string.IsNullOrWhiteSpace(body) ? "" : body;

                _messageQueue.Add(new QueuedMessage(DateTime.Now, t, b, processName ?? ""));
                _messageQueue.Sort((a, c) => a.Time.CompareTo(c.Time));

                if (!_isDisplaying)
                    StartDisplaying();
            });
        }
        #endregion

        private async void OnDisplayTimerTick()
        {
            // stop single-shot timer
            _displayTimer.Stop();

            if (_messageQueue.Count == 0)
            {
                _isDisplaying = false;
                return;
            }

            if (_messageQueue.Count > 1)
            {
                // transition to next message with fade-out then fade-in
                if (Resources["FadeOutStoryboard"] is Storyboard fadeOut)
                {
                    var fo = fadeOut.Clone();
                    var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
                    fo.Completed += (s, e) => tcs.TrySetResult(true);
                    fo.Begin(this);
                    await tcs.Task;
                }

                // remove the shown message and display next
                if (_messageQueue.Count > 0)
                    _messageQueue.RemoveAt(0);

                ShowCurrentQueueHeadImmediate();

                if (Resources["FadeInStoryboard"] is Storyboard fadeIn)
                {
                    var fi = fadeIn.Clone();
                    var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
                    fi.Completed += (s, e) => tcs.TrySetResult(true);
                    fi.Begin(this);
                    await tcs.Task;
                }

                // start timer for next message after fade-in completes
                StartDisplayTimer();
            }
            else
            {
                // last message: fade out and hide
                if (Resources["FadeOutStoryboard"] is Storyboard fadeOut)
                {
                    var fo = fadeOut.Clone();
                    var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
                    fo.Completed += (s, e) => tcs.TrySetResult(true);
                    fo.Begin(this);
                    await tcs.Task;
                }

                _messageQueue.Clear();
                _messageGroups.Clear();
                _isDisplaying = false;
                PlaySlideOutAnimationAndHide();
            }
        }

        private async void StartDisplaying()
        {
            if (_messageQueue.Count == 0) return;
            _isDisplaying = true;

            Visibility = Visibility.Visible;
            ShowNoActivateTopmost();

                    await Dispatcher.InvokeAsync(async () =>
            {
                PositionWindow();
                ShowCurrentQueueHeadImmediate();

                if (Resources["FadeInStoryboard"] is Storyboard fadeIn)
                        {
                            var fi = fadeIn.Clone();
                            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
                            fi.Completed += (s, e) => tcs.TrySetResult(true);
                            fi.Begin(this);
                            await tcs.Task;
                        }

                        // start single-shot timer for 3s after fade-in completes
                        StartDisplayTimer();
                    }, DispatcherPriority.Background);
                }

        private void ShowCurrentQueueHeadImmediate()
        {
            _messageGroups.Clear();
            var head = _messageQueue.FirstOrDefault();
            if (head != null)
            {
                var group = new ToastMessageGroup { Title = head.Title, ProcessName = head.ProcessName, Time = head.Time };
                if (!string.IsNullOrEmpty(head.Body))
                    group.Bodies.Add(head.Body);
                _messageGroups.Add(group);
            }
        }

        // Public accessor for App to query currently-displayed message title (null if none)
        public string? GetCurrentHeadTitle()
        {
            var head = _messageQueue.FirstOrDefault();
            return head == null ? null : (string.IsNullOrWhiteSpace(head.Title) ? "新通知" : head.Title);
        }

        #region 无焦点显示 / 隐藏
        /// <summary>
        /// 置顶显示
        /// </summary>
        private void ShowNoActivateTopmost()
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        private void RevokeTopmost()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        }
        #endregion

        #region 动画
        private void PlaySlideInAnimation()
        {
            // prefer fade-in if available, fallback to existing slide-in
            if (Resources["FadeInStoryboard"] is Storyboard fadeIn)
                fadeIn.Begin(this);
            else if (Resources["SlideInAnimation"] is Storyboard sb)
                sb.Begin();
            var hwnd = new WindowInteropHelper(this).Handle;
            var accent = new ACCENTPOLICY { nAccentState = 3, nColor = 0 };
            var data = new WINCOMPATTRDATA { nAttribute = 19, pData = Marshal.AllocHGlobal(Marshal.SizeOf(accent)), ulDataSize = Marshal.SizeOf(accent) };
            Marshal.StructureToPtr(accent, data.pData, false);
            SetWindowCompositionAttribute(hwnd, ref data);
            Marshal.FreeHGlobal(data.pData);
        }

        private void PlaySlideOutAnimationAndHide()
        {
            // prefer fade-out if available, fallback to existing slide-out
            if (Resources["FadeOutStoryboard"] is Storyboard fadeOut)
            {
                fadeOut = fadeOut.Clone();
                fadeOut.Completed -= OnSlideOutCompleted;
                fadeOut.Completed += OnSlideOutCompleted;
                fadeOut.Begin(this);
            }
            else if (Resources["SlideOutAnimation"] is Storyboard sb)
            {
                sb.Completed -= OnSlideOutCompleted; // 防重复挂
                sb.Completed += OnSlideOutCompleted;
                sb.Begin();
            }
            else
            {
                Visibility = Visibility.Collapsed;
                RevokeTopmost();
            }
        }

        private void OnSlideOutCompleted(object? sender, EventArgs e)
        {
            Visibility = Visibility.Collapsed;
            RevokeTopmost();
        }
        #endregion

        // New handlers for fade behaviour (wired from XAML)
        private void Window_Loaded_Extended(object sender, RoutedEventArgs e)
        {
            // enforce fixed size and position precisely (fix actual window size mismatch)
            this.SizeToContent = SizeToContent.Manual;
            this.Width = 375;
            this.Height = 75;

            // position and ensure top-most no-activate behavior
            PositionWindow();
            ShowNoActivateTopmost();

            // ensure OS-level window size matches exactly (use SetWindowPos)
            try
            {
                var hwnd = new WindowInteropHelper(this).EnsureHandle();
                SetWindowPos(hwnd, HWND_TOPMOST, (int)Math.Round(this.Left), (int)Math.Round(this.Top), (int)Math.Round(this.Width), (int)Math.Round(this.Height), SWP_NOACTIVATE);
            }
            catch { }
            // play fade-in (shortened duration handled in XAML resources)
            if (Resources["FadeInStoryboard"] is Storyboard fadeInExt)
                fadeInExt.Begin(this);

            // clip children to rounded corners to avoid square overlays covering corners
            try
            {
                var outer = this.FindName("OuterBorder") as Border;
                if (outer != null)
                {
                    void updateClip(object? s, EventArgs ea)
                    {
                        outer.Clip = new RectangleGeometry(new Rect(0, 0, outer.ActualWidth, outer.ActualHeight), outer.CornerRadius.TopLeft, outer.CornerRadius.TopLeft);
                    }
                    outer.SizeChanged += (s, e) => updateClip(s, e);
                    updateClip(null, EventArgs.Empty);
                }
            }
            catch { }

            // wire mouse click handler for Ctrl+click skipping
            try
            {
                this.MouseLeftButtonDown -= OnMouseLeftClick;
                this.MouseLeftButtonDown += OnMouseLeftClick;
            }
            catch { }
        }
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // position and ensure top-most no-activate behavior
            PositionWindow();
            ShowNoActivateTopmost();

            if (Resources["FadeInStoryboard"] is Storyboard fadeIn)
                fadeIn.Begin(this);
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_isClosingAnimation) return;
            e.Cancel = true;
            _isClosingAnimation = true;

                    // stop timers to avoid callbacks during shutdown
                    try { _displayTimer?.Stop(); } catch { }
                    try { _hideTimer?.Stop(); } catch { }
                    try { _ctrlPollTimer?.Stop(); } catch { }

                    if (Resources["FadeOutStoryboard"] is Storyboard fadeOut)
                    {
                        fadeOut = fadeOut.Clone();
                        fadeOut.Completed += (_, __) =>
                        {
                            // proceed with close after animation
                            Application.Current.Dispatcher.Invoke(() => this.Close());
                        };
                        fadeOut.Begin(this);
                    }
                    else
                    {
                        // fallback: use existing slide-out behaviour then close
                        PlaySlideOutAnimationAndHide();
                        Application.Current.Dispatcher.Invoke(() => this.Close());
                    }

                }

        #region 自动隐藏计时器
        private void StartHideTimer()
        {
            // reuse the same timer instance to avoid repeated allocations and duplicate handlers
            if (_hideTimer == null)
            {
                _hideTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(5)
                };

                _hideTimer.Tick += (_, __) =>
                {
                    _hideTimer.Stop();
                    PlaySlideOutAnimationAndHide();
                };
            }
            else
            {
                _hideTimer.Stop();
            }

            _hideTimer.Start();
        }
        #endregion

        // Start display timer with default interval and update deadline
        private void StartDisplayTimer()
        {
            try
            {
                _displayTimer.Interval = _displayInterval;
                _displayDeadline = DateTime.Now + _displayInterval;
                _pausedRemaining = null;
                _displayTimer.Start();
            }
            catch { }
        }

        // Polling handler to detect Ctrl press/release and Ctrl+Shift expansion
        private bool _isExpandedByShift = false;
        private double _savedHeight = 0;
        private SizeToContent _savedSizeToContent = SizeToContent.Manual;

        private void CheckCtrlState(object? sender, EventArgs e)
        {
            try
            {
                if (!_isDisplaying || Visibility != Visibility.Visible) return;
                bool isCtrlDown = System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LeftCtrl)
                                || System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RightCtrl);
                bool isShiftDown = System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LeftShift)
                                 || System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RightShift);

                // Ctrl press/release: background, timer pause, mouse transparency
                if (isCtrlDown && !_ctrlHeld)
                {
                    _ctrlHeld = true;
                    AnimateBorderToColor(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
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
                    AnimateBorderToColor(Color.FromArgb(0xAC, 0xFF, 0xFF, 0xFF));
                    if (_pausedRemaining.HasValue)
                    {
                        var rem = _pausedRemaining.Value;
                        _displayTimer.Interval = rem <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : rem;
                        _displayDeadline = DateTime.Now + _displayTimer.Interval;
                        _displayTimer.Start();
                        _pausedRemaining = null;
                    }
                    // if we were expanded due to shift, collapse when ctrl released
                    if (_isExpandedByShift)
                        CollapseFromShift();
                    EnableMouseTransparency();
                }

                // Ctrl+Shift expansion (only expand downward; do not change width)
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

        private void AnimateBorderToColor(Color target)
        {
            try
            {
                var outer = this.FindName("OuterBorder") as Border;
                if (outer == null) return;
                // ensure brush exists and is animatable
                if (!(outer.Background is SolidColorBrush scb))
                {
                    scb = new SolidColorBrush(((SolidColorBrush)(new BrushConverter().ConvertFrom("#ACFFFFFF"))).Color);
                    outer.Background = scb;
                }
                var ca = new ColorAnimation(target, TimeSpan.FromMilliseconds(120)) { EasingFunction = new SineEase() };
                scb.BeginAnimation(SolidColorBrush.ColorProperty, ca);
            }
            catch { }
        }

        private void DisableMouseTransparency()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                ex &= ~WS_EX_TRANSPARENT;
                SetWindowLong(hwnd, GWL_EXSTYLE, ex);
            }
            catch { }
        }

        private void EnableMouseTransparency()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT);
            }
            catch { }
        }

        // Expansion helpers for Ctrl+Shift: expand downward to show full content without changing width
        private double _savedMaxHeight = double.NaN;
        // save per-item original heights so they can be restored
        private readonly System.Collections.Generic.Dictionary<int, double> _savedItemHeights = new();

        private void ExpandForShift()
        {
            try
            {
                if (_isExpandedByShift) return;
                _isExpandedByShift = true;
                // save current sizing
                _savedHeight = this.Height;
                _savedSizeToContent = this.SizeToContent;
                _savedMaxHeight = this.MaxHeight;

                // remove any height limits so window may grow beyond screen if necessary
                this.MaxHeight = double.PositiveInfinity;

                // allow height to auto-size to content while preserving width
                this.SizeToContent = SizeToContent.Height;
                // set Height to Auto
                this.Height = double.NaN;
                this.UpdateLayout();

                // enable wrapping on all TextBlocks under RootGrid so full text can be displayed
                try
                {
                    var root = this.FindName("RootGrid") as System.Windows.DependencyObject ?? (System.Windows.DependencyObject)this;
                    foreach (var tb in FindVisualChildren<System.Windows.Controls.TextBlock>(root))
                    {
                        tb.TextWrapping = System.Windows.TextWrapping.Wrap;
                    }
                }
                catch { }

                // make item template grids auto-height so wrapped text can expand vertically
                try
                {
                    _savedItemHeights.Clear();
                    int count = MessageList.Items.Count;
                    for (int i = 0; i < count; i++)
                    {
                        var container = MessageList.ItemContainerGenerator.ContainerFromIndex(i) as System.Windows.FrameworkElement;
                        if (container == null) continue;
                        var grid = FindVisualChildren<System.Windows.Controls.Grid>(container).FirstOrDefault();
                        if (grid != null)
                        {
                            if (!double.IsNaN(grid.Height))
                                _savedItemHeights[i] = grid.Height;
                            grid.Height = double.NaN; // Auto
                        }
                    }
                }
                catch { }

                // force layout pass to recompute height
                this.UpdateLayout();

                // as a fallback, measure RootGrid and apply required height explicitly
                try
                {
                    var root = this.FindName("RootGrid") as System.Windows.FrameworkElement ?? this as System.Windows.FrameworkElement;
                    if (root != null)
                    {
                        root.Measure(new Size(this.Width, double.PositiveInfinity));
                        double needed = root.DesiredSize.Height + 20; // small padding
                        if (!double.IsNaN(needed) && needed > 0)
                            this.Height = needed;
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

                // revert wrapping on all text blocks under RootGrid
                try
                {
                    var root = this.FindName("RootGrid") as System.Windows.DependencyObject ?? (System.Windows.DependencyObject)this;
                    foreach (var tb in FindVisualChildren<System.Windows.Controls.TextBlock>(root))
                    {
                        tb.TextWrapping = System.Windows.TextWrapping.NoWrap;
                    }
                }
                catch { }

                // restore sizing
                this.SizeToContent = _savedSizeToContent;
                if (_savedSizeToContent == SizeToContent.Manual)
                {
                    this.Height = _savedHeight;
                }

                // restore MaxHeight
                try { this.MaxHeight = double.IsNaN(_savedMaxHeight) ? double.PositiveInfinity : _savedMaxHeight; } catch { }

                this.UpdateLayout();
            }
            catch { }
        }

        // Visual tree helper
        private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject depObj) where T : System.Windows.DependencyObject
        {
            if (depObj == null) yield break;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(depObj); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(depObj, i);
                if (child is T t) yield return t;
                foreach (var childOfChild in FindVisualChildren<T>(child))
                    yield return childOfChild;
            }
        }

        // Handle left-click while Ctrl is held to skip current message immediately.
        private void OnMouseLeftClick(object? sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (!_ctrlHeld || !_isDisplaying || Visibility != Visibility.Visible) return;
                // ensure left button
                if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                {
                    // Skip current message asynchronously
                    SkipCurrentMessage();
                    // mark handled so clicks don't propagate to underlying apps
                    e.Handled = true;
                }
            }
            catch { }
        }

        // Skip current message and transition to next immediately.
        public async void SkipCurrentMessage()
        {
            try
            {
                if (!_isDisplaying) return;

                // stop any running single-shot timer
                try { _displayTimer.Stop(); } catch { }

                if (_messageQueue.Count == 0)
                {
                    _isDisplaying = false;
                    return;
                }

                if (_messageQueue.Count > 1)
                {
                    // fade out current
                    if (Resources["FadeOutStoryboard"] is Storyboard fadeOut)
                    {
                        var fo = fadeOut.Clone();
                        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
                        fo.Completed += (s, e) => tcs.TrySetResult(true);
                        fo.Begin(this);
                        await tcs.Task;
                    }

                    // remove current and show next
                    if (_messageQueue.Count > 0)
                        _messageQueue.RemoveAt(0);

                    ShowCurrentQueueHeadImmediate();

                    if (Resources["FadeInStoryboard"] is Storyboard fadeIn)
                    {
                        var fi = fadeIn.Clone();
                        var tcs2 = new System.Threading.Tasks.TaskCompletionSource<bool>();
                        fi.Completed += (s, e) => tcs2.TrySetResult(true);
                        fi.Begin(this);
                        await tcs2.Task;
                    }

                    // If Ctrl is held, keep timer paused (set remaining to full interval)
                    if (_ctrlHeld)
                    {
                        _pausedRemaining = _displayInterval;
                    }
                    else
                    {
                        StartDisplayTimer();
                    }
                }
                else
                {
                    // last message: fade out and hide immediately
                    if (Resources["FadeOutStoryboard"] is Storyboard fadeOut)
                    {
                        var fo = fadeOut.Clone();
                        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
                        fo.Completed += (s, e) => tcs.TrySetResult(true);
                        fo.Begin(this);
                        await tcs.Task;
                    }

                    _messageQueue.Clear();
                    _messageGroups.Clear();
                    _isDisplaying = false;
                    PlaySlideOutAnimationAndHide();
                }
            }
            catch { }
        }

        #region 布局定位
        private void PositionWindow()
        {
            UpdateLayout();
            Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Arrange(new Rect(new Point(0, 0), DesiredSize));

            // If a saved position exists in configuration, honor it.
            try
            {
                if (App.Config != null && !double.IsNaN(App.Config.MainWindowTop) && !double.IsNaN(App.Config.MainWindowLeft))
                {
                    Top = App.Config.MainWindowTop;
                    Left = App.Config.MainWindowLeft;
                    return;
                }
            }
            catch { }

            // enforce fixed window size and center horizontally, 15px from top
            // Width/Height are set in XAML; use them directly to compute centered position
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            Top = 15.0;
            Left = (screenWidth - this.Width) / 2.0;
        }
        #endregion

        #region 文本解析
        private static (string Title, string Body) ParseMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return ("", "");

            int idx = text.IndexOf(':');
            if (idx > 0 && idx < text.Length - 1)
                return (text[..idx].Trim(), text[(idx + 1)..].Trim());

            return ("", text.Trim());
        }
        #endregion
    }
}
