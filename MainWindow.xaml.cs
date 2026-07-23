using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media.Animation;

namespace Notifier
{
    public partial class MainWindow : Window
    {
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


        public MainWindow()
        {
            InitializeComponent();
            MessageList.ItemsSource = _messageGroups;

            Loaded += (_, __) => PositionWindow();

            SourceInitialized += (_, __) =>
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT);
            };
        }


        #region 公开接口
        public void AddMessage(string text)
        {
            var (title, body) = ParseMessage(text);

            var existing = _messageGroups.FirstOrDefault(g => g.Title == title);
            if (existing != null)
            {
                if (!string.IsNullOrWhiteSpace(body))
                    existing.Bodies.Add(body);
            }
            else
            {
                var group = new ToastMessageGroup
                {
                    Title = string.IsNullOrWhiteSpace(title) ? "新通知" : title
                };
                if (!string.IsNullOrWhiteSpace(body))
                    group.Bodies.Add(body);
                _messageGroups.Add(group);
            }

            if (Visibility == Visibility.Visible)
            {
                PositionWindow();
                PlaySlideInAnimation();
            }
            else
            {
                Visibility = Visibility.Visible;
                ShowNoActivateTopmost(); 

                Dispatcher.InvokeAsync(() =>
                {
                    PositionWindow();
                    PlaySlideInAnimation();
                }, DispatcherPriority.Background);
            }

            StartHideTimer();
        }
        #endregion


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
            if (Resources["SlideInAnimation"] is Storyboard sb)
                sb.Begin();
        }

        private void PlaySlideOutAnimationAndHide()
        {
            if (Resources["SlideOutAnimation"] is Storyboard sb)
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


        #region 自动隐藏计时器
        private void StartHideTimer()
        {
            _hideTimer?.Stop();

            _hideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };

            _hideTimer.Tick += (_, __) =>
            {
                _hideTimer.Stop();
                PlaySlideOutAnimationAndHide();
            };

            _hideTimer.Start();
        }
        #endregion


        #region 布局定位
        private void PositionWindow()
        {
            UpdateLayout();
            Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Arrange(new Rect(new Point(0, 0), DesiredSize));

            double w = ActualWidth > 0 ? ActualWidth : 280;
            double h = ActualHeight > 0 ? ActualHeight : 130;
            var area = SystemParameters.WorkArea;

            double top = area.Top + (area.Height - h) * 0.03;
            double left = area.Right - w - 18;

            Top = Math.Max(top, area.Top + 5);
            Left = Math.Max(left, area.Left + 5);
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