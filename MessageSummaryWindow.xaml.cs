using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

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

            try { if (App.Config != null && !double.IsNaN(App.Config.MessageSummaryOpacity)) Opacity = App.Config.MessageSummaryOpacity; } catch { }

            this.Opened += OnFirstLoaded;
            App.OnNewToastDetected += OnNewToast;
            RefreshMessages();

            this.Activated += (_, __) =>
            {
                if (_allowDeactivate)
                    ReportFocusState?.Invoke(true);
            };

            this.Deactivated += (_, __) =>
            {
                if (!_allowDeactivate) return;
                Dispatcher.UIThread.Post(() => ReportFocusState?.Invoke(false));
            };
        }

        private void OnFirstLoaded(object? sender, EventArgs e)
        {
            PositionWindow();
            _allowDeactivate = true;
            RootGrid.Opacity = 1;
            if (RootGrid.RenderTransform is TranslateTransform translate)
                translate.X = 0;
            ReportFocusState?.Invoke(true);
        }

        public void RequestCloseFromApp()
            => InternalRequestClose();

        private void InternalRequestClose()
        {
            if (_isClosing) return;
            _isClosing = true;
            SafeClose();
        }

        private void SafeClose()
        {
            App.OnNewToastDetected -= OnNewToast;
            WindowClosed?.Invoke();
            Close();
        }

        private void OnNewToast(ToastData t) => Dispatcher.UIThread.Post(RefreshMessages);

        public void RefreshMessages() => RefreshMessageList(ToastMessageStore.GetAll());

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
                if (under > 0 && head.Length - under <= 14)
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

        private async void MessageItem_MouseLeftButtonDown(object? sender, PointerPressedEventArgs e)
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
            ((App)global::Avalonia.Application.Current!).OnMessagesHaveBeenCleared();

            if (!string.IsNullOrWhiteSpace(aumid))
            {
                var (ok, msg) = await AppActivator.active_app(aumid);
                if (!ok)
                {
                    Debug.WriteLine($"[AppActivator] 唤醒失败：{msg}");
                    if (!string.IsNullOrWhiteSpace(target?.AppName))
                    {
                        var (ok2, msg2) = AppActivator.TryBringToFrontByAppName(target.AppName);
                        if (!ok2)
                            Debug.WriteLine($"[AppActivator] 按应用名置前失败：{msg2}");
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(target?.AppName))
            {
                var (ok2, msg2) = AppActivator.TryBringToFrontByAppName(target.AppName);
                if (!ok2)
                    Debug.WriteLine($"[AppActivator] 按应用名置前失败：{msg2}");
            }
        }

        private async void ClearButton_Click(object? sender, RoutedEventArgs e)
        {
            var aumids = ToastMessageStore.GetAll()
                .Select(m => m.Aumid)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct()
                .ToList();

            await Task.Run(() =>
            {
                foreach (var m in ToastMessageStore.GetAll().ToList())
                    ToastMessageStore.RemoveAndSync(m);
            });

            RefreshMessages();
            ((App)global::Avalonia.Application.Current!).OnMessagesHaveBeenCleared();
        }

        private void PositionWindow()
        {
            var screen = Screens.Primary;
            if (screen == null) return;

            var workArea = screen.WorkingArea;
            const int leftOffset = 18;
            var top = workArea.Y + 20;
            var left = workArea.X + leftOffset;

            Position = new PixelPoint(left, top);
        }
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
        // optional process identifier for compact displays
        public string ProcessName { get; set; } = "";
        // timestamp for display (used by compact toast template)
        public DateTime Time { get; set; } = DateTime.Now;
    }
}
