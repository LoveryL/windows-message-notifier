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
                _displayTimer.Start();
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
                        _displayTimer.Start();
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
