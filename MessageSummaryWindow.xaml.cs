using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
            RefreshMessages();
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


        private static string GetSourceAppLabel(ToastData m)
        {

            if (!string.IsNullOrWhiteSpace(m.AppName))
                return m.AppName!;


            if (!string.IsNullOrWhiteSpace(m.Aumid))
            {
                var a = m.Aumid!;

                int bang = a.IndexOf('!');
                var head = bang > 0 ? a.Substring(0, bang) : a;


                int under = head.LastIndexOf('_');
                if (under > 0 && head.Length - under <= 14) //  heuristically a hash suffix
                    head = head.Substring(0, under);

                return head;
            }

            return "";
        }

        private void RefreshMessageList(IEnumerable<ToastData> msgs)
        {

            var groups = new Dictionary<string, (List<string> Bodies, string SourceApp, string? SampleAumid)>();

            foreach (var m in msgs)
            {
                var t = string.IsNullOrWhiteSpace(m.Title) ? "新通知" : m.Title;

                if (!groups.ContainsKey(t))
                {
                    groups[t] = (
                        new List<string>(),
                        GetSourceAppLabel(m),
                        m.Aumid
                    );
                }

                if (!string.IsNullOrWhiteSpace(m.Body))
                    groups[t].Bodies.Add(m.Body!);
            }

            MessageList.ItemsSource = groups.Select(g => new ToastMessageGroup
            {
                Title = g.Key,
                Bodies = new System.Collections.ObjectModel.ObservableCollection<string>(g.Value.Bodies),
                SourceApp = g.Value.SourceApp,
                SampleAumid = g.Value.SampleAumid
            });

            StatusText.Text = groups.Count > 0 ? $"共 {groups.Count} 条未读" : "暂无未读通知";
        }


        private async void MessageItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border { Tag: string title })
                return;

            var target = ToastMessageStore.GetAll()
                .FirstOrDefault(m => string.Equals(
                    string.IsNullOrWhiteSpace(m.Title) ? "新通知" : m.Title,
                    title, StringComparison.Ordinal));

            string? aumid = target?.Aumid;

            ToastMessageStore.RemoveByTitleAndSync(title);
            RefreshMessages();
            ((App)Application.Current).OnMessagesHaveBeenCleared();

            if (!string.IsNullOrWhiteSpace(aumid))
            {
                var (ok, msg) = await AppActivator.active_app(aumid);
                if (!ok)
                    System.Diagnostics.Debug.WriteLine($"[AppActivator] 唤醒失败：{msg}");
            }
        }

        private async void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            var aumids = ToastMessageStore.GetAll()
                .Select(m => m.Aumid)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct()
                .ToList();

            foreach (var m in ToastMessageStore.GetAll().ToList())
                ToastMessageStore.RemoveAndSync(m);

            RefreshMessages();
            ((App)Application.Current).OnMessagesHaveBeenCleared();

            foreach (var a in aumids)
            {
                var (ok, msg) = await AppActivator.active_app(a!);
                if (!ok)
                    System.Diagnostics.Debug.WriteLine($"[AppActivator] 唤醒失败：{msg}");
            }
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


    public class ToastMessageGroup
    {
        public string Title { get; set; } = "";
        public System.Collections.ObjectModel.ObservableCollection<string> Bodies { get; set; } = new();
        /// <summary>
        /// 来源应用名
        /// </summary>
        public string SourceApp { get; set; } = "";
        public string? SampleAumid { get; set; }
    }
}
