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
        private DispatcherTimer? _hideTimer;
        private ObservableCollection<ToastMessageGroup> _messageGroups = new ObservableCollection<ToastMessageGroup>();

        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int GWL_EXSTYLE = -20;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        public MainWindow()
        {
            InitializeComponent();
            Loaded += (s, e) => PositionWindow();
            SourceInitialized += (s, e) =>
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT);
            };
            MessageList.ItemsSource = _messageGroups;
        }

        public void AddMessage(string text)
        {
            var (title, body) = ParseMessage(text);

            var existingGroup = _messageGroups.FirstOrDefault(g => g.Title == title);

            if (existingGroup != null)
            {
                if (!string.IsNullOrEmpty(body))
                {
                    existingGroup.Bodies.Add(body);
                }
            }
            else
            {
                var newGroup = new ToastMessageGroup
                {
                    Title = string.IsNullOrEmpty(title) ? "新通知" : title
                };
                if (!string.IsNullOrEmpty(body))
                {
                    newGroup.Bodies.Add(body);
                }
                _messageGroups.Add(newGroup);
            }

            if (this.Visibility == Visibility.Visible)
            {
                PositionWindow();
                PlaySlideInAnimation();
            }
            else
            {
                this.Visibility = Visibility.Visible;
                this.Show();
                this.Activate();
                this.Topmost = true;

                this.Dispatcher.InvokeAsync(() =>
                {
                    PositionWindow();
                    PlaySlideInAnimation();
                }, DispatcherPriority.Background);
            }

            StartHideTimer();
        }

        private (string Title, string Body) ParseMessage(string text)
        {
            if (string.IsNullOrEmpty(text))
                return ("", "");

            int colonIndex = text.IndexOf(':');
            if (colonIndex > 0 && colonIndex < text.Length - 1)
            {
                return (
                    text.Substring(0, colonIndex).Trim(),
                    text.Substring(colonIndex + 1).Trim()
                );
            }

            return ("", text.Trim());
        }

        private void PlaySlideInAnimation()
        {
            var storyboard = Resources["SlideInAnimation"] as Storyboard;
            if (storyboard != null)
                storyboard.Begin();
        }

        private void PlaySlideOutAnimationAndHide()
        {
            var storyboard = Resources["SlideOutAnimation"] as Storyboard;
            if (storyboard != null)
            {
                storyboard.Completed += (s, e) =>
                {
                    this.Visibility = Visibility.Collapsed;
                };
                storyboard.Begin();
            }
        }

        private void StartHideTimer()
        {
            _hideTimer?.Stop();

            _hideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };

            _hideTimer.Tick += (s, e) =>
            {
                PlaySlideOutAnimationAndHide();
                _hideTimer.Stop();
            };

            _hideTimer.Start();
        }

        private void PositionWindow()
        {
            var workArea = SystemParameters.WorkArea;

            this.UpdateLayout();
            this.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            this.Arrange(new Rect(new Point(0, 0), this.DesiredSize));

            double windowWidth = this.ActualWidth > 0 ? this.ActualWidth : 280;
            double windowHeight = this.ActualHeight > 0 ? this.ActualHeight : 130;

            double top = workArea.Top + (workArea.Height - windowHeight) * 0.03;
            double left = workArea.Right - windowWidth - 18;

            top = Math.Max(top, workArea.Top + 5);
            left = Math.Max(left, workArea.Left + 5);

            this.Top = top;
            this.Left = left;
        }
    }

    public class ToastMessageGroup
    {
        public string Title { get; set; } = "";
        public ObservableCollection<string> Bodies { get; set; } = new ObservableCollection<string>();
    }
}