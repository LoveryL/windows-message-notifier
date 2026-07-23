using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Notifier
{
    public partial class MessageSummaryWindow : Window
    {
        public event Action<bool>? ReportFocusState;
        public event Action? WindowClosed;

        internal bool _isClosing;
        private bool _allowDeactivate;

        public MessageSummaryWindow()
        {
            InitializeComponent();
            Loaded += OnFirstLoaded;
            App.OnNewToastDetected += OnNewToast;
        }

        #region 入场
        private void OnFirstLoaded(object? sender, RoutedEventArgs e)
        {
            PositionWindow();

            if (Resources["SlideInAnimation"] is Storyboard sb)
            {
                sb.Completed += (_, __) =>
                {
                    _allowDeactivate = true;
                    ReportFocusState?.Invoke(true);
                };
                sb.Begin(this);
            }
            else
            {
                _allowDeactivate = true;
                ReportFocusState?.Invoke(true);
            }

            Show();
            Activate();
            Focus();
        }
        #endregion

        #region 焦点
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (_allowDeactivate)
                ReportFocusState?.Invoke(true);
        }

        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);
            if (!_allowDeactivate) return;

            Dispatcher.BeginInvoke(() =>
                ReportFocusState?.Invoke(false),
                System.Windows.Threading.DispatcherPriority.Background);
        }
        #endregion

        #region App 调用
        public void RequestCloseFromApp()
            => InternalRequestClose();

        private void InternalRequestClose()
        {
            if (_isClosing) return;
            _isClosing = true;

            if (Resources["SlideOutAnimation"] is Storyboard sb)
            {
                sb.Completed -= SlideOut_Completed;
                sb.Completed += SlideOut_Completed;
                sb.Begin(this);
            }
            else SafeClose();
        }

        private void SlideOut_Completed(object? sender, EventArgs e)
            => SafeClose();

        private void SafeClose()
        {
            App.OnNewToastDetected -= OnNewToast;
            WindowClosed?.Invoke();
            Close();
        }
        #endregion

        #region 数据 & 清空
        private void OnNewToast(ToastData t) => Dispatcher.Invoke(RefreshMessages);

        public void RefreshMessages()
            => RefreshMessageList(ToastMessageStore.GetAll());

        private void RefreshMessageList(IEnumerable<ToastData> msgs)
        {
            var groups = new Dictionary<string, List<string>>();
            foreach (var m in msgs)
            {
                var t = string.IsNullOrWhiteSpace(m.Title) ? "新通知" : m.Title;
                if (!groups.ContainsKey(t)) groups[t] = new();
                if (!string.IsNullOrWhiteSpace(m.Body)) groups[t].Add(m.Body);
            }

            MessageList.ItemsSource = groups.Select(g => new ToastMessageGroup
            {
                Title = g.Key,
                Bodies = new System.Collections.ObjectModel.ObservableCollection<string>(g.Value)
            });

            StatusText.Text = groups.Count > 0 ? $"共 {groups.Count} 条未读" : "暂无未读通知";
        }

        private void MessageItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border { Tag: string title })
            {
                ToastMessageStore.RemoveByTitleAndSync(title);
                RefreshMessages();
                ((App)Application.Current).OnMessagesHaveBeenCleared();
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var m in ToastMessageStore.GetAll().ToList())
                ToastMessageStore.RemoveAndSync(m);
            RefreshMessages();
            ((App)Application.Current).OnMessagesHaveBeenCleared();
        }
        #endregion

        #region 布局
        private void PositionWindow()
        {
            UpdateLayout();
            double h = ActualHeight > 0 ? ActualHeight : Height;
            var s = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea;
            if (s == null) return;
            Top = s.Value.Top + Math.Max(20, (s.Value.Height - h) * 0.03);
            Left = s.Value.Left + 18;
        }
        #endregion
    }
}