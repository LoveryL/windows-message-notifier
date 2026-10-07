using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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

        private const double SwipeDeleteThreshold = 110;
        private const double CollapseThreshold = 60;
        private readonly Dictionary<Border, DragState> _dragStates = new();

        private sealed class DragState
        {
            public required Border Border { get; init; }
            public required ToastMessageGroup Group { get; init; }
            public required global::Avalonia.Point StartPosition { get; init; }
            public bool StartedFromBottom { get; set; }
            public double LastX { get; set; }
            public double LastY { get; set; }
            public bool HasMoved { get; set; }
        }

        public MessageSummaryWindow()
        {
            InitializeComponent();

            try { if (App.setting != null && !double.IsNaN(App.setting.opacity)) Opacity = App.setting.opacity; } catch { }

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

        private async void OnFirstLoaded(object? sender, EventArgs e)
        {
            PositionWindow();
            _allowDeactivate = true;
            await AnimateShowAsync();
            ReportFocusState?.Invoke(true);
        }

        private async Task AnimateShowAsync()
        {
            try
            {
                const int frames = 5;
                const int msPerFrame = 3;
                double fromOpacity = 0.0, toOpacity = 1.0;
                double fromX = -420, toX = 0;

                for (int i = 0; i <= frames; i++)
                {
                    double t = (double)i / frames;
                    double curOpacity = fromOpacity + (toOpacity - fromOpacity) * t;
                    double curX = fromX + (toX - fromX) * t;

                    RootGrid.Opacity = curOpacity;
                    if (RootGrid.RenderTransform is TranslateTransform translate)
                        translate.X = curX;

                    await Task.Delay(msPerFrame);
                }
            }
            catch { }
        }

        private async Task AnimateHideAsync()
        {
            try
            {
                const int frames = 5;
                const int msPerFrame = 3;
                double fromOpacity = RootGrid.Opacity;
                double toOpacity = 0.0;
                double fromX = 0, toX = -420;

                for (int i = 0; i <= frames; i++)
                {
                    double t = (double)i / frames;
                    double curOpacity = fromOpacity + (toOpacity - fromOpacity) * t;
                    double curX = fromX + (toX - fromX) * t;

                    RootGrid.Opacity = curOpacity;
                    if (RootGrid.RenderTransform is TranslateTransform translate)
                        translate.X = curX;

                    await Task.Delay(msPerFrame);
                }
            }
            catch { }
        }

        internal void RequestCloseFromApp()
            => InternalRequestClose();

        private async void InternalRequestClose()
        {
            if (_isClosing) return;
            _isClosing = true;
            await AnimateHideAsync();
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
            var ordered = msgs.ToList();
            var groups = new Dictionary<string, List<ToastData>>();

            foreach (var m in ordered)
            {
                var titleKey = string.IsNullOrWhiteSpace(m.Title) ? "新通知" : m.Title;
                var appKey = string.IsNullOrWhiteSpace(m.AppName) ? (string.IsNullOrWhiteSpace(m.Aumid) ? "<unknown>" : m.Aumid) : m.AppName;
                var key = $"{titleKey}||{appKey}"; // group only when both title and app match

                if (!groups.ContainsKey(key))
                    groups[key] = new List<ToastData>();

                groups[key].Add(m);
            }

            var items = groups.Select(g =>
            {
                var messages = g.Value
                    .OrderBy(m => m.NotificationId == 0 ? DateTime.Now.Ticks : m.NotificationId)
                    .ToList();

                var group = new ToastMessageGroup
                {
                    Title = messages.Select(m => string.IsNullOrWhiteSpace(m.Title) ? "新通知" : m.Title).FirstOrDefault() ?? "新通知",
                    Bodies = new ObservableCollection<string>(messages
                        .Where(m => !string.IsNullOrWhiteSpace(m.Body))
                        .Select(m => m.Body)),
                    SourceApp = messages.Select(m => GetSourceAppLabel(m)).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? string.Empty,
                    SampleAumid = messages.Select(m => m.Aumid).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)),
                    ProcessName = messages.Select(m => m.ProcessName).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? string.Empty,
                    Time = DateTime.Now
                };

                foreach (var message in messages)
                    group.Messages.Add(message);

                return group;
            }).ToList();

            foreach (var item in items)
                item.RefreshDisplay();

            MessageList.ItemsSource = items;
            StatusText.Text = ordered.Count > 0 ? $"共 {ordered.Count} 条消息" : "暂无未读通知";
        }

        private void MessageItem_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Border border || border.DataContext is not ToastMessageGroup group)
                return;

            var point = e.GetCurrentPoint(border).Position;
            var startedFromBottom = group.IsExpanded && point.Y > border.Bounds.Height - 42;

            _dragStates[border] = new DragState
            {
                Border = border,
                Group = group,
                StartPosition = point,
                StartedFromBottom = startedFromBottom,
                HasMoved = false,
                LastX = 0,
                LastY = 0
            };

            border.RenderTransform ??= new TranslateTransform();
            e.Handled = true;
        }

        private void MessageItem_PointerMoved(object? sender, PointerEventArgs e)
        {
            if (sender is not Border border || !_dragStates.TryGetValue(border, out var state))
                return;

            var point = e.GetCurrentPoint(border).Position;
            var deltaX = point.X - state.StartPosition.X;
            var deltaY = point.Y - state.StartPosition.Y;
            state.LastX = deltaX;
            state.LastY = deltaY;
            state.HasMoved = Math.Abs(deltaX) > 8 || Math.Abs(deltaY) > 8;

            if (!state.HasMoved)
                return;

            if (state.StartedFromBottom)
            {
                if (!state.Group.IsExpanded)
                    return;

                var transform = border.RenderTransform as TranslateTransform ?? new TranslateTransform();
                transform.X = 0;
                transform.Y = Math.Min(0, deltaY) * 0.6;
                border.RenderTransform = transform;
                e.Handled = true;
                return;
            }

            var translate = border.RenderTransform as TranslateTransform ?? new TranslateTransform();
            translate.X = Math.Min(0, deltaX);
            translate.Y = 0;
            border.RenderTransform = translate;
            e.Handled = true;
        }

        private async void MessageItem_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (sender is not Border border || !_dragStates.TryGetValue(border, out var state))
                return;

            _dragStates.Remove(border);

            try
            {
                if (state.StartedFromBottom)
                {
                    if (state.Group.IsExpanded && state.LastY <= -CollapseThreshold)
                    {
                        state.Group.ToggleExpand();
                        e.Handled = true;
                        return;
                    }
                }
                else if (state.LastX <= -SwipeDeleteThreshold)
                {
                    await DeleteGroupWithoutActivation(state.Group);
                    e.Handled = true;
                    return;
                }

                if (!state.HasMoved)
                {
                    if (state.Group.IsExpanded)
                    {
                        await DeleteGroupWithoutActivation(state.Group);
                        e.Handled = true;
                        return;
                    }

                    if (state.Group.HasHiddenMessages && !state.Group.IsExpanded)
                    {
                        state.Group.ToggleExpand();
                        e.Handled = true;
                        return;
                    }

                    if (!state.Group.HasHiddenMessages)
                    {
                        await ClearAndActivateCurrentMessage(state.Group);
                        e.Handled = true;
                    }
                }
            }
            finally
            {
                if (border.RenderTransform is TranslateTransform transform)
                {
                    transform.X = 0;
                    transform.Y = 0;
                }
            }
        }

        private void MessageItem_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            if (sender is not Border border)
                return;

            _dragStates.Remove(border);
            if (border.RenderTransform is TranslateTransform transform)
            {
                transform.X = 0;
                transform.Y = 0;
            }
        }

        private async Task DeleteGroupWithoutActivation(ToastMessageGroup group)
        {
            var title = group.Title;
            if (string.IsNullOrWhiteSpace(title))
                return;

            ToastMessageStore.RemoveByTitleAndSync(title);
            RefreshMessages();
            try { ((App)global::Avalonia.Application.Current!).OnMessagesHaveBeenCleared(); } catch { }
        }

        private async Task DeleteCurrentMessageWithoutActivation(ToastMessageGroup group)
        {
            var title = group.Title;
            if (string.IsNullOrWhiteSpace(title))
                return;

            ToastMessageStore.RemoveByTitleAndSync(title);
            RefreshMessages();
            try { ((App)global::Avalonia.Application.Current!).OnMessagesHaveBeenCleared(); } catch { }
        }

        private async Task ClearAndActivateCurrentMessage(ToastMessageGroup group)
        {
            var title = group.Title;
            var current = group.LatestMessage;
            if (string.IsNullOrWhiteSpace(title) || current == null)
                return;

            // 删除同 title 下的全部通知（不再只删除单条），然后再尝试激活来源应用
            ToastMessageStore.RemoveByTitleAndSync(title);
            RefreshMessages();
            try { ((App)global::Avalonia.Application.Current!).OnMessagesHaveBeenCleared(); } catch { }

            if (!string.IsNullOrWhiteSpace(current.Aumid))
            {
                var (ok, msg) = await AppActivator.active_app(current.Aumid);
                if (!ok)
                {
                    Debug.WriteLine($"[AppActivator] 唤醒失败：{msg}");
                    if (!string.IsNullOrWhiteSpace(current.AppName))
                    {
                        var (ok2, msg2) = AppActivator.TryBringToFrontByAppName(current.AppName);
                        if (!ok2)
                            Debug.WriteLine($"[AppActivator] 按应用名置前失败：{msg2}");
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(current.AppName))
            {
                var (ok2, msg2) = AppActivator.TryBringToFrontByAppName(current.AppName);
                if (!ok2)
                    Debug.WriteLine($"[AppActivator] 按应用名置前失败：{msg2}");
            }
        }

        private async void ClearButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var listener = ToastMessageStore.Listener;
                if (listener != null)
                {
                    try { await listener.ClearAllNotificationsAsync(); } catch { }
                }
            }
            catch { }

            await Task.Run(() =>
            {
                foreach (var m in ToastMessageStore.GetAll().ToList())
                    ToastMessageStore.RemoveAndSync(m);
            });

            RefreshMessages();
            try { ((App)global::Avalonia.Application.Current!).OnMessagesHaveBeenCleared(); } catch { }
        }

        private void PositionWindow()
        {
            var screen = Screens.Primary;
            if (screen == null) return;

            var workArea = screen.WorkingArea;
            var top = workArea.Y + 15;
            var left = workArea.X + 15;

            Position = new PixelPoint((int)Math.Round((double)left, 0, MidpointRounding.AwayFromZero), (int)Math.Round((double)top, 0, MidpointRounding.AwayFromZero));
        }
    }

    public class ToastMessageGroup : INotifyPropertyChanged
    {
        public string Title { get; set; } = "";
        public ObservableCollection<string> Bodies { get; set; } = new();
        public ObservableCollection<ToastData> Messages { get; } = new();
        public ObservableCollection<ToastData> VisibleMessages { get; } = new();
        public bool IsExpanded { get; private set; }
        public ToastData? LatestMessage => Messages.LastOrDefault();
        public int HiddenCount => Math.Max(0, Messages.Count - 1);
        public bool HasHiddenMessages => Messages.Count > 1 && !IsExpanded;
        public string HiddenCountText => HasHiddenMessages ? HiddenCount.ToString() : "";
        public string SourceApp { get; set; } = "";
        public string? SampleAumid { get; set; }
        public string ProcessName { get; set; } = "";
        public DateTime Time { get; set; } = DateTime.Now;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void ToggleExpand()
        {
            IsExpanded = !IsExpanded;
            RefreshDisplay();
        }

        public void RefreshDisplay()
        {
            VisibleMessages.Clear();

            var displayMessages = IsExpanded ? Messages : Messages.TakeLast(1);
            foreach (var msg in displayMessages)
            {
                VisibleMessages.Add(msg);
            }

            if (VisibleMessages.Count == 0 && Messages.Count > 0)
            {
                var fallback = Messages.LastOrDefault();
                if (fallback != null)
                    VisibleMessages.Add(fallback);
            }

            OnPropertyChanged(nameof(VisibleMessages));
            OnPropertyChanged(nameof(IsExpanded));
            OnPropertyChanged(nameof(HiddenCount));
            OnPropertyChanged(nameof(HasHiddenMessages));
            OnPropertyChanged(nameof(HiddenCountText));
        }

        private void OnPropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
