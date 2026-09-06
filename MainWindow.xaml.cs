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
using System.Threading.Tasks;

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
        private bool _isClosed = false;  

        // 消息队列
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

        private Storyboard? _currentFadeInAnimation;
        private Storyboard? _currentFadeOutAnimation;

        private record QueuedMessage(DateTime Time, string Title, string Body, string ProcessName);

        public MainWindow()
        {
            InitializeComponent();
            MessageList.ItemsSource = _messageGroups;

            // Apply configured opacity if available
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

            // Display timer
            _displayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _displayTimer.Tick += OnDisplayTimerTick;  

            // Polling timer
            _ctrlPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _ctrlPollTimer.Tick += CheckCtrlState;
            _ctrlPollTimer.Start();

            this.Closed += OnWindowClosed;
        }

        #region 公开接口
        public void AddMessage(string text, string processName = "")
        {
            // 如果窗口已关闭，忽略新消息
            if (_isClosed) return;

            Dispatcher.Invoke(() =>
            {
                var (title, body) = ParseMessage(text);
                var t = string.IsNullOrWhiteSpace(title) ? "新通知" : title;
                var b = string.IsNullOrWhiteSpace(body) ? "" : body;

                _messageQueue.Add(new QueuedMessage(DateTime.Now, t, b, processName ?? ""));
                _messageQueue.Sort((a, c) => a.Time.CompareTo(c.Time));

                if (!_isDisplaying && !_isClosed)
                    StartDisplaying();
            });
        }
        #endregion

        #region 动画辅助方法
        private async Task FadeOutAsync()
        {
            if (_currentFadeOutAnimation != null)
            {
                _currentFadeOutAnimation.Stop();
                _currentFadeOutAnimation = null;
            }

            if (Resources["FadeOutStoryboard"] is Storyboard template)
            {
                _currentFadeOutAnimation = template.Clone();
                var tcs = new TaskCompletionSource<bool>();
                
                void handler(object? s, EventArgs e)
                {
                    _currentFadeOutAnimation!.Completed -= handler;
                    tcs.TrySetResult(true);
                }
                
                _currentFadeOutAnimation.Completed += handler;
                _currentFadeOutAnimation.Begin(this);
                await tcs.Task;
                _currentFadeOutAnimation = null;
            }
        }

        private async Task FadeInAsync()
        {
            if (_currentFadeInAnimation != null)
            {
                _currentFadeInAnimation.Stop();
                _currentFadeInAnimation = null;
            }

            if (Resources["FadeInStoryboard"] is Storyboard template)
            {
                _currentFadeInAnimation = template.Clone();
                var tcs = new TaskCompletionSource<bool>();
                
                void handler(object? s, EventArgs e)
                {
                    _currentFadeInAnimation!.Completed -= handler;
                    tcs.TrySetResult(true);
                }
                
                _currentFadeInAnimation.Completed += handler;
                _currentFadeInAnimation.Begin(this);
                await tcs.Task;
                _currentFadeInAnimation = null;
            }
        }
        #endregion

        #region 显示逻辑
        private void StopDisplayTimer()
        {
            try
            {
                if (_displayTimer != null && _displayTimer.IsEnabled)
                    _displayTimer.Stop();
            }
            catch { }
        }

        private async Task HideWindowAndResetState(bool clearQueue = true)
        {
            if (_isClosed || _isClosingAnimation)
                return;

            StopDisplayTimer();
            _pausedRemaining = null;
            _isDisplaying = false;
            if (!_isClosed)
            {
                await FadeOutAsync();
                Visibility = Visibility.Collapsed;
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
            if (_isClosed || _isClosingAnimation)
                return;

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

            _messageQueue.Clear();
            _messageGroups.Clear();
            await HideWindowAndResetState(false);
        }

        private async void StartDisplaying()
        {
            if (_isClosed || _isClosingAnimation)
                return;

            if (_messageQueue.Count == 0)
                return;

            StopDisplayTimer();
            _isDisplaying = true;
            
            ShowNoActivateTopmost();

            await Dispatcher.InvokeAsync(() =>
            {
                PositionWindow();
                ShowCurrentQueueHeadImmediate();
            }, DispatcherPriority.Normal);

            await FadeInAsync();
            Visibility = Visibility.Visible;
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
        #endregion

        #region 动画
        private void PlaySlideOutAnimationAndHide()
        {
            if (_isClosed || _isClosingAnimation)
                return;

            _ = HideWindowAndResetState(true);
        }

        #endregion

        #region 无焦点显示 / 隐藏
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

        #region 窗口事件
        private void Window_Loaded_Extended(object sender, RoutedEventArgs e)
        {
            this.SizeToContent = SizeToContent.Manual;
            this.Width = 375;
            this.Height = 75;

            PositionWindow();
            ShowNoActivateTopmost();

            try
            {
                var hwnd = new WindowInteropHelper(this).EnsureHandle();
                SetWindowPos(hwnd, HWND_TOPMOST, (int)Math.Round(this.Left), (int)Math.Round(this.Top), 
                    (int)Math.Round(this.Width), (int)Math.Round(this.Height), SWP_NOACTIVATE);
            }
            catch { }

            if (Resources["FadeInStoryboard"] is Storyboard fadeInExt)
                fadeInExt.Begin(this);

            // clip children to rounded corners
            try
            {
                var outer = this.FindName("OuterBorder") as Border;
                if (outer != null)
                {
                    void updateClip(object? s, EventArgs ea)
                    {
                        outer.Clip = new RectangleGeometry(
                            new Rect(0, 0, outer.ActualWidth, outer.ActualHeight), 
                            outer.CornerRadius.TopLeft, 
                            outer.CornerRadius.TopLeft);
                    }
                    outer.SizeChanged += (s, e) => updateClip(s, e);
                    updateClip(null, EventArgs.Empty);
                }
            }
            catch { }

            try
            {
                this.MouseLeftButtonDown -= OnMouseLeftClick;
                this.MouseLeftButtonDown += OnMouseLeftClick;
            }
            catch { }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            PositionWindow();
            ShowNoActivateTopmost();

            if (Resources["FadeInStoryboard"] is Storyboard fadeIn)
                fadeIn.Begin(this);
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_isClosingAnimation)
            {
                e.Cancel = false;
                return;
            }

            // 取消关闭，执行动画
            e.Cancel = true;
            _isClosingAnimation = true;

            // 停止所有计时器
            try 
            { 
                _displayTimer?.Stop();
                _displayTimer.Tick -= OnDisplayTimerTick;  
            } 
            catch { }
            
            try { _hideTimer?.Stop(); } catch { }
            
            try 
            { 
                _ctrlPollTimer?.Stop();
                _ctrlPollTimer.Tick -= CheckCtrlState;
            } 
            catch { }

            // 清理动画资源
            try
            {
                _currentFadeInAnimation?.Stop();
                _currentFadeInAnimation = null;
                _currentFadeOutAnimation?.Stop();
                _currentFadeOutAnimation = null;
            }
            catch { }

            if (Resources["FadeOutStoryboard"] is Storyboard fadeOut)
            {
                fadeOut = fadeOut.Clone();
                fadeOut.Completed += (_, __) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        _isClosingAnimation = false;
                        Close();
                    });
                };
                fadeOut.Begin(this);
            }
            else
            {
                PlaySlideOutAnimationAndHide();
                Dispatcher.Invoke(() =>
                {
                    _isClosingAnimation = false;
                    Close();
                });
            }
        }

        private void OnWindowClosed(object? sender, EventArgs e)
        {
            _isClosed = true;
            _isDisplaying = false;
            
            // 清理所有动画资源
            _currentFadeInAnimation?.Stop();
            _currentFadeInAnimation = null;
            _currentFadeOutAnimation?.Stop();
            _currentFadeOutAnimation = null;
            
            // 清空队列
            _messageQueue.Clear();
            _messageGroups.Clear();
        }
        #endregion

        #region 自动隐藏计时器
        private void StartHideTimer()
        {
            if (_hideTimer == null)
            {
                _hideTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(5)
                };

                _hideTimer.Tick += (_, __) =>
                {
                    if (_isClosed || _isClosingAnimation)
                        return;

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
        #endregion

        #region 计时器管理
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
        #endregion

        #region Ctrl 状态检测
        private bool _isExpandedByShift = false;
        private double _savedHeight = 0;
        private SizeToContent _savedSizeToContent = SizeToContent.Manual;

        private void CheckCtrlState(object? sender, EventArgs e)
        {
            if (_isClosed) return;
            if (!_isDisplaying || Visibility != Visibility.Visible) return;

            try
            {
                bool isCtrlDown = System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LeftCtrl)
                                || System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RightCtrl);
                bool isShiftDown = System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LeftShift)
                                 || System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RightShift);

                if (isCtrlDown && !_ctrlHeld)
                {
                    _ctrlHeld = true;
                    AnimateBorderToColor(System.Windows.Media.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
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
                    AnimateBorderToColor(System.Windows.Media.Color.FromArgb(0xAC, 0xFF, 0xFF, 0xFF));
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

        private void AnimateBorderToColor(System.Windows.Media.Color target)
        {
            try
            {
                var outer = this.FindName("OuterBorder") as Border;
                if (outer == null) return;
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

        private double _savedMaxHeight = double.NaN;
        private readonly System.Collections.Generic.Dictionary<int, double> _savedItemHeights = new();

        private void ExpandForShift()
        {
            try
            {
                if (_isExpandedByShift) return;
                _isExpandedByShift = true;

                _savedHeight = this.Height;
                _savedSizeToContent = this.SizeToContent;
                _savedMaxHeight = this.MaxHeight;

                this.MaxHeight = double.PositiveInfinity;
                this.SizeToContent = SizeToContent.Height;
                this.Height = double.NaN;
                this.UpdateLayout();

                try
                {
                    var root = this.FindName("RootGrid") as System.Windows.DependencyObject ?? (System.Windows.DependencyObject)this;
                    foreach (var tb in FindVisualChildren<System.Windows.Controls.TextBlock>(root))
                    {
                        tb.TextWrapping = System.Windows.TextWrapping.Wrap;
                    }
                }
                catch { }

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
                            grid.Height = double.NaN;
                        }
                    }
                }
                catch { }

                this.UpdateLayout();

                try
                {
                    var root = this.FindName("RootGrid") as System.Windows.FrameworkElement ?? this as System.Windows.FrameworkElement;
                    if (root != null)
                    {
                        root.Measure(new System.Windows.Size(this.Width, double.PositiveInfinity));
                        double needed = root.DesiredSize.Height + 20;
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

                try
                {
                    var root = this.FindName("RootGrid") as System.Windows.DependencyObject ?? (System.Windows.DependencyObject)this;
                    foreach (var tb in FindVisualChildren<System.Windows.Controls.TextBlock>(root))
                    {
                        tb.TextWrapping = System.Windows.TextWrapping.NoWrap;
                    }
                }
                catch { }

                this.SizeToContent = _savedSizeToContent;
                if (_savedSizeToContent == SizeToContent.Manual)
                {
                    this.Height = _savedHeight;
                }

                try { this.MaxHeight = double.IsNaN(_savedMaxHeight) ? double.PositiveInfinity : _savedMaxHeight; } catch { }

                this.UpdateLayout();
            }
            catch { }
        }

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
        #endregion

        #region 鼠标事件
        private void OnMouseLeftClick(object? sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                if (!_ctrlHeld || !_isDisplaying || Visibility != Visibility.Visible) return;
                if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
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
        #endregion

        #region 布局定位
        private void PositionWindow()
        {
            UpdateLayout();
            Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            Arrange(new Rect(new System.Windows.Point(0, 0), DesiredSize));
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            Top = App.Config.MainWindowTop;
            if(App.Config.IsMainWindowMiddle)
            Left = (screenWidth - this.Width) / 2.0;
            else Left = App.Config.MainWindowLeft;
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