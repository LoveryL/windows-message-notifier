using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Notifier
{
    public partial class MessageSummaryWindow : Window
    {
        private bool _isDragging;
        private Point _dragStartPoint;
        private const string RegKey = @"Software\Notifier";

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

        [DllImport("gdi32.dll")]
        private static extern int DeleteObject(IntPtr hObject);

        public MessageSummaryWindow(IEnumerable<ToastData> messages)
        {
            InitializeComponent();
            DataContext = this;

            SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                IntPtr rgn = CreateRoundRectRgn(0, 0, (int)Width, (int)Height, 28, 28);
                if (rgn != IntPtr.Zero)
                {
                    SetWindowRgn(hwnd, rgn, true);
                    DeleteObject(rgn);
                }
            };

            Loaded += (s, e) =>
            {
                App.OnNewToastDetected += App_OnNewToastDetected;
                RefreshMessageList(messages);
            };
        }

        private void App_OnNewToastDetected(ToastData toastData)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => App_OnNewToastDetected(toastData));
                return;
            }

            RefreshMessages();
        }

        public void RefreshMessages()
        {
            var messages = ToastMessageStore.GetAll();
            RefreshMessageList(messages);
        }

        private void RefreshMessageList(IEnumerable<ToastData> messages)
        {
            var groups = new Dictionary<string, List<string>>();

            foreach (var m in messages)
            {
                var title = string.IsNullOrEmpty(m.Title) ? "新通知" : m.Title;
                if (!groups.ContainsKey(title))
                    groups[title] = new List<string>();
                if (!string.IsNullOrEmpty(m.Body))
                    groups[title].Add(m.Body);
            }

            MessageList.ItemsSource = groups.Select(g => new ToastMessageGroup
            {
                Title = g.Key,
                Bodies = new System.Collections.ObjectModel.ObservableCollection<string>(g.Value)
            });

            var count = groups.Count;
            if (StatusText != null)
            {
                StatusText.Text = count > 0 ? $"共 {count} 条未读通知" : "暂无未读通知";
            }
        }

        private void MessageItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is string title)
            {
                ToastMessageStore.RemoveByTitleAndSync(title);
                RefreshMessages();
                var app = System.Windows.Application.Current as App;
                app?.OnMessagesHaveBeenCleared();
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegKey);
                if (key != null)
                {
                    var left = key.GetValue("WindowLeft");
                    var top = key.GetValue("WindowTop");
                    if (left != null && top != null)
                    {
                        this.Left = System.Convert.ToDouble(left);
                        this.Top = System.Convert.ToDouble(top);
                    }
                }
            }
            catch { }
        }

        private void Window_Closed(object sender, System.EventArgs e)
        {
            App.OnNewToastDetected -= App_OnNewToastDetected;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegKey);
                key.SetValue("WindowLeft", this.Left);
                key.SetValue("WindowTop", this.Top);
            }
            catch { }

            Close();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            var allMessages = ToastMessageStore.GetAll();
            foreach (var msg in allMessages)
            {
                ToastMessageStore.RemoveAndSync(msg);
            }

            RefreshMessageList(new List<ToastData>());
            var app = System.Windows.Application.Current as App;
            app?.OnMessagesHaveBeenCleared();
        }

        private void RootGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDragging = true;
            _dragStartPoint = e.GetPosition(this);
            RootGrid.CaptureMouse();
        }

        private void RootGrid_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;

            var currentPoint = e.GetPosition(this);
            var offset = currentPoint - _dragStartPoint;

            this.Left += offset.X;
            this.Top += offset.Y;
        }

        private void RootGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;
            RootGrid.ReleaseMouseCapture();
        }
    }
}