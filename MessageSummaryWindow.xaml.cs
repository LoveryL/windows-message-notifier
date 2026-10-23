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
            SetupRootTransform();

            try { if (App.setting != null && !double.IsNaN(App.setting.opacity)) Opacity = App.setting.opacity; } catch { }

            this.Opened += OnFirstLoaded;
            App.OnNewToastDetected += OnNewToast;
            // 消息集合的任何变更（新增/删除/清空）都会走到一次合并刷新
            ToastMessageStore.Changed += OnStoreChanged;
            ApplySettings();
            // 与 MainWindow 保持一致：显式判空后再订阅（App.setting 为静态属性，
            // 编译器的可空流分析会在跨类型访问时保守地认为可能为 null）
            if (App.setting != null)
                App.setting.SettingChanged += OnSettingChangedExternal;
            ThemeManager.ThemeChanged += OnThemeChanged;

            // 兜底：窗口若没走 InternalRequestClose 路径（被系统或其它途径关闭），
            // 也要解绑静态事件并通知 App 收尾，否则面板状态停在"已打开"，
            // 内存缓存不会释放、消息也不会落盘。
            this.Closed += (_, __) =>
            {
                App.OnNewToastDetected -= OnNewToast;
                ToastMessageStore.Changed -= OnStoreChanged;
                if (App.setting != null)
                    App.setting.SettingChanged -= OnSettingChangedExternal;
                ThemeManager.ThemeChanged -= OnThemeChanged;

                if (_isClosing) return;
                _isClosing = true;
                WindowClosed?.Invoke();
            };
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

        // ===== 面板进出场动画参数 =====
        // 旧实现是 5 帧 × Task.Delay(3ms)：Windows 上 Task.Delay 的实际分辨率约 15ms，
        // 3ms 会被拉成 15ms，420px 的位移只剩 5 帧、总时长约 75ms，看起来像"啪"一下瞬移。
        // 现在改为时间驱动：240ms / ~60fps，约 14 帧，时长精确可控。
        // 时长与位移改为从设置读取并缓存成字段：动画是热路径，不该每帧查设置
        private double _slideFromX = -420;
        private int _showDurationMs = 240;
        private int _hideDurationMs = 200;

        /// <summary>把设置里的尺寸/圆角/阴影/动画参数应用到本窗口。</summary>
        private void ApplySettings()
        {
            try
            {
                var s = App.setting;

                Width = Math.Clamp(s.PanelWidth, MinWidth, 900);
                Height = Math.Clamp(s.PanelHeight, MinHeight, 1100);

                _slideFromX = s.PanelSlideX;
                _showDurationMs = Math.Max(0, s.PanelEnterMs);
                _hideDurationMs = Math.Max(0, s.PanelExitMs);

                var outer = this.FindControl<Border>("OuterBorder");
                if (outer != null)
                {
                    outer.CornerRadius = new CornerRadius(Math.Max(0, s.PanelCorner));
                    outer.BoxShadow = ThemeManager.BuildPopupShadow(s.PopupShadowHex, 24, 8);
                }

                if (!double.IsNaN(s.opacity))
                    Opacity = s.opacity;
            }
            catch { }
        }

        private void OnSettingChangedExternal(Settings_Manager.SettingType type)
        {
            if (type == Settings_Manager.SettingType.Size ||
                type == Settings_Manager.SettingType.Animation ||
                type == Settings_Manager.SettingType.Opacity ||
                type == Settings_Manager.SettingType.Reset)
            {
                ApplySettings();
            }
        }

        // 全程复用同一组变换对象，避免每帧产生垃圾
        private readonly TranslateTransform _rootTranslate = new();
        private readonly ScaleTransform _rootScale = new() { ScaleX = 1, ScaleY = 1 };

        /// <summary>统一在代码里设置变换（位移 + 缩放），以左侧中点为锚点。</summary>
        private void SetupRootTransform()
        {
            try
            {
                RootGrid.RenderTransform = new TransformGroup
                {
                    Children = { _rootTranslate, _rootScale }
                };
                RootGrid.RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative);
            }
            catch { }
        }

        private Task AnimateShowAsync()
        {
            try
            {
                return UiTween.RunAsync(_showDurationMs, t =>
                {
                    // 位移距离大，用无过冲的 OutCubic，免得冲过头
                    double e = Easing.OutCubic(t);
                    // 缩放幅度小，可以带一点回弹，做出"贴上去"的手感
                    double s = Easing.OutBackSoft(t);

                    RootGrid.Opacity = Math.Min(1, Easing.OutQuad(t / 0.7));
                    _rootTranslate.X = _slideFromX * (1 - e);

                    double scale = 0.97 + 0.03 * s;
                    _rootScale.ScaleX = scale;
                    _rootScale.ScaleY = scale;
                });
            }
            catch
            {
                RootGrid.Opacity = 1;
                return Task.CompletedTask;
            }
        }

        private Task AnimateHideAsync()
        {
            try
            {
                double fromOpacity = RootGrid.Opacity;
                double fromX = _rootTranslate.X;

                return UiTween.RunAsync(_hideDurationMs, t =>
                {
                    double e = Easing.InCubic(t);
                    RootGrid.Opacity = fromOpacity * (1 - Easing.InQuad(t));
                    _rootTranslate.X = fromX + (_slideFromX - fromX) * e;
                });
            }
            catch
            {
                RootGrid.Opacity = 0;
                return Task.CompletedTask;
            }
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
            ToastMessageStore.Changed -= OnStoreChanged;
            WindowClosed?.Invoke();
            Close();
        }

        private void OnThemeChanged() => ApplySettings();

        private void OnNewToast(ToastData t) => RefreshMessages();

        private void OnStoreChanged() => RefreshMessages();

        private volatile bool _refreshPending;

        /// <summary>
        /// 合并刷新：短时间内到达的多条通知只在下一帧重建一次列表，
        /// 避免"每来一条通知就整表重建 ItemsSource"（旧实现里 App 和本窗口还会各触发一次，等于翻倍）。
        /// </summary>
        public void RefreshMessages()
        {
            if (_refreshPending) return;
            _refreshPending = true;

            Dispatcher.UIThread.Post(() =>
            {
                _refreshPending = false;
                RefreshMessageList(ToastMessageStore.GetAll());
            });
        }

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

        private void RefreshMessageList(IReadOnlyList<ToastData> msgs)
        {
            // 用元组做 key：省掉每条消息一次字符串拼接（$"{title}||{app}"）的分配
            var groups = new Dictionary<(string Title, string App), List<ToastData>>(msgs.Count);

            foreach (var m in msgs)
            {
                var titleKey = string.IsNullOrWhiteSpace(m.Title) ? "新通知" : m.Title;
                var appKey = string.IsNullOrWhiteSpace(m.AppName)
                    ? (string.IsNullOrWhiteSpace(m.Aumid) ? "<unknown>" : m.Aumid)
                    : m.AppName;

                var key = (titleKey, appKey);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<ToastData>(1);
                    groups.Add(key, list);
                }

                list.Add(m);
            }

            var items = new List<ToastMessageGroup>(groups.Count);
            var nowTicks = DateTime.Now.Ticks;

            foreach (var pair in groups)
            {
                var messages = pair.Value;
                // 原地排序，取代 OrderBy(...).ToList() 的迭代器 + 中间集合
                messages.Sort((a, b) => OrderKey(a, nowTicks).CompareTo(OrderKey(b, nowTicks)));

                var group = new ToastMessageGroup
                {
                    Title = pair.Key.Title,
                    Time = DateTime.Now
                };

                // 单次遍历同时完成 Bodies / SourceApp / SampleAumid / ProcessName 的提取，
                // 旧实现用了 4 次独立的 LINQ 遍历
                foreach (var m in messages)
                {
                    group.Messages.Add(m);

                    if (!string.IsNullOrWhiteSpace(m.Body))
                        group.Bodies.Add(m.Body);

                    if (group.SourceApp.Length == 0)
                    {
                        var label = GetSourceAppLabel(m);
                        if (!string.IsNullOrWhiteSpace(label)) group.SourceApp = label;
                    }

                    if (string.IsNullOrWhiteSpace(group.SampleAumid) && !string.IsNullOrWhiteSpace(m.Aumid))
                        group.SampleAumid = m.Aumid;

                    if (group.ProcessName.Length == 0 && !string.IsNullOrWhiteSpace(m.ProcessName))
                        group.ProcessName = m.ProcessName;
                }

                group.RefreshDisplay();
                items.Add(group);
            }

            MessageList.ItemsSource = items;
            StatusText.Text = msgs.Count > 0 ? $"共 {msgs.Count} 条消息" : "暂无未读通知";
        }

        private static long OrderKey(ToastData m, long nowTicks)
            => m.NotificationId == 0 ? nowTicks : m.NotificationId;

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

            // 一次清空：只触发一次集合变更。
            // 旧实现逐条 RemoveAndSync，N 条消息 = N 次列表重建 + N 次落盘调度。
            // 系统通知中心上一步已经整体清空，这里不必再逐条删一次。
            ToastMessageStore.Clear(removeFromNotificationCenter: false);

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
