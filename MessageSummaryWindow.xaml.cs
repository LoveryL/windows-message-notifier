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
// 项目开了 UseWindowsForms：System.Windows.Forms 被隐式引入全局命名空间，
// Control / Panel 与其同名类型冲突（CS0104），这里用别名定向到 Avalonia 的实现。
// 别名优先于 using 命名空间导入，所以本文件里写 Control 就是 Avalonia 的。
using Control = global::Avalonia.Controls.Control;
using Panel = global::Avalonia.Controls.Panel;

namespace Notifier
{
    public partial class MessageSummaryWindow : Window
    {
        public event Action<bool>? ReportFocusState;
        public event Action? WindowClosed;

        internal bool _isClosing;
        private bool _allowDeactivate;

        /// <summary>应用退出时置 true，允许窗口真正关闭（否则一律只隐藏）。</summary>
        internal bool _allowRealClose;
        private bool _reallyClosed;

        /// <summary>窗口是否已被真正销毁（复用模式下通常始终为 false）。</summary>
        public bool IsReallyClosed => _reallyClosed;

        private const double SwipeDeleteThreshold = 110;
        private const double CollapseThreshold = 60;

        // iOS 风格滑动操作：
        // 卡片向右平移 → 左侧动作层按位移宽度逐渐露出（删除 / 唤醒两个按钮）；
        // 滑回左边 → 动作层收起；
        // 继续向左滑过阈值 → 直接删除整组。
        private const double ActionRevealWidth = 112;    // 两个按钮的总宽（56 × 2）
        private const double ActionRevealThreshold = 56; // 右滑超过这个距离就"吸附"到展开
        private const double SwipeMaxLeft = 170;         // 左滑的最大跟手位移（超出不再跟手）

        /// <summary>当前处于"动作展开"状态的卡片（同一时刻只允许一个，类似 iOS）。</summary>
        private Border? _openCard;

        private readonly Dictionary<Border, DragState> _dragStates = new();

        private sealed class DragState
        {
            public required Border Border { get; init; }
            public required ToastMessageGroup Group { get; init; }
            /// <summary>
            /// 按下点，相对**窗口**。位移一律用这份坐标计算。
            ///
            /// 关键：不能用相对卡片的坐标算位移 —— 卡片自己在跟着手移动，
            /// 它的坐标系也跟着走，于是"手指移动量"会被卡片自身的位移抵消掉一部分，
            /// 表现为拖到底了但算出来的位移仍然很小，永远达不到吸附阈值。
            /// 窗口在手势期间不动，用它做参照才是稳定的。
            /// </summary>
            public required global::Avalonia.Point StartWindow { get; init; }
            public bool StartedFromBottom { get; set; }
            /// <summary>按下那一刻卡片已有的 X 位移（展开状态下是 ActionRevealWidth）。</summary>
            public double StartTransformX { get; set; }
            public double LastX { get; set; }
            public double LastY { get; set; }
            public bool HasMoved { get; set; }
        }

        /// <summary>在本条目的容器里按 Name 找控件（DataTemplate 里 x:Name 不可靠，改用 Name 查找）。</summary>
        private static Control? FindSibling(Border card, string name)
        {
            if (card.Parent is not Panel panel) return null;

            foreach (var child in panel.Children)
            {
                if (child is Control c && string.Equals(c.Name, name, StringComparison.Ordinal))
                    return c;
            }

            return null;
        }

        private static Control? FindActionLayer(Border card) => FindSibling(card, "ActionLayer");

        /// <summary>把卡片平移到 x，并同步动作层的可见性与露出宽度。</summary>
        private static void SetCardOffset(Border? card, double x)
        {
            if (card == null) return;

            try
            {
                var tt = card.RenderTransform as TranslateTransform;
                if (tt == null)
                {
                    tt = new TranslateTransform();
                    card.RenderTransform = tt;
                }

                tt.X = x;
                tt.Y = 0;

                var layer = FindActionLayer(card);
                if (layer == null) return;

                if (x > 0.5)
                {
                    // 露出宽度 = 位移量（超出总宽后不再增加），配一点淡入让刚起步时不生硬
                    layer.IsVisible = true;
                    layer.Width = Math.Min(x, ActionRevealWidth);
                    layer.Opacity = Math.Min(1, x / 40);
                }
                else
                {
                    layer.IsVisible = false;
                    layer.Width = 0;
                }
            }
            catch { }
        }

        /// <summary>收起已展开的卡片（except 是要保留的那一个）。</summary>
        private void CloseOpenCard(Border? except)
        {
            if (_openCard == null) return;
            if (except != null && ReferenceEquals(_openCard, except)) return;

            SetCardOffset(_openCard, 0);
            _openCard = null;
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
                _reallyClosed = true;
                _visible = false;
                App.OnNewToastDetected -= OnNewToast;
                ToastMessageStore.Changed -= OnStoreChanged;
                if (App.setting != null)
                    App.setting.SettingChanged -= OnSettingChangedExternal;
                ThemeManager.ThemeChanged -= OnThemeChanged;

                if (_isClosing) return;
                _isClosing = true;
                WindowClosed?.Invoke();
            };
            // ItemsSource 全程只绑一次：后面所有刷新都是集合的增/删，不再整体替换
            MessageList.ItemsSource = _items;
            RefreshMessages();

            // 面板改为"收起"而不是"销毁"：Alt+F4、失焦关闭都只是隐藏，
            // 下次点托盘直接 Show，省掉重新解析 XAML + 重建控件树 + 重新订阅的开销。
            this.Closing += (_, e) =>
            {
                if (_allowRealClose || App.IsExiting)
                {
                    _reallyClosed = true;
                    return;
                }

                e.Cancel = true;
                _ = RequestHideAsync();
            };

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
            => _ = RequestHideAsync();

        /// <summary>收起面板：播完退场动画后隐藏，实例与控件树保留，下次直接 Show。</summary>
        private async Task RequestHideAsync()
        {
            if (_isClosing) return;
            _isClosing = true;

            try { await AnimateHideAsync(); }
            catch { }
            finally
            {
                SafeClose();
                _isClosing = false;
            }
        }

        private void SafeClose()
        {
            // 隐藏而不是 Close：Close 之后的窗口无法再次 Show，
            // 复用实例后"打开面板"从重建变成唤醒，响应快一个数量级。
            _visible = false;
            App.OnNewToastDetected -= OnNewToast;
            ToastMessageStore.Changed -= OnStoreChanged;
            WindowClosed?.Invoke();
            Hide();
        }

        private void OnThemeChanged() => ApplySettings();

        private void OnNewToast(ToastData t) => RefreshMessages();

        private void OnStoreChanged() => RefreshMessages();

        private volatile bool _refreshPending;
        private bool _visible = true;
        private bool _dirty = true;

        /// <summary>面板被 App 收起时调用（隐藏而非销毁）。</summary>
        public void OnAppHidden() => _visible = false;

        /// <summary>面板重新显示时调用：重新订阅变更源，必要时补一次刷新。</summary>
        public void OnAppShown()
        {
            bool wasHidden = !_visible;
            _visible = true;

            // 收起期间已解绑，这里成对补回（-= 先兜一遍，防止重复订阅）
            App.OnNewToastDetected -= OnNewToast;
            App.OnNewToastDetected += OnNewToast;
            ToastMessageStore.Changed -= OnStoreChanged;
            ToastMessageStore.Changed += OnStoreChanged;

            // 复用实例后不会再触发 Opened，入场动画要在这里补上，否则第二次打开是"直接出现"
            if (wasHidden)
            {
                try { PositionWindow(); } catch { }
                _ = AnimateShowAsync();
            }

            // 收起期间订阅是断开的，缓存可能已经变了：脏了或从隐藏恢复都要刷一次
            if (wasHidden || _dirty)
            {
                _dirty = false;
                RefreshMessages();
            }
        }

        /// <summary>
        /// 合并刷新：短时间内到达的多条通知只在下一帧重建一次列表，
        /// 避免"每来一条通知就整表重建 ItemsSource"。
        /// 面板处于收起状态时干脆不刷：不可见的列表重建只是白白烧 CPU 和内存，
        /// 记一个脏标记，重新显示时补一次即可。
        /// </summary>
        public void RefreshMessages()
        {
            if (!_visible)
            {
                _dirty = true;
                return;
            }

            if (_refreshPending) return;
            _refreshPending = true;

            Dispatcher.UIThread.Post(() =>
            {
                _refreshPending = false;
                _dirty = false;
                // 列表要变了，先收起动作区：否则它指向的那张卡片可能被回收/复用，
                // 把展开的偏移带到另一个分组上。
                CloseOpenCard(null);
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

        // ItemsSource 只赋值一次，之后所有刷新走增量。
        // 旧实现每次刷新都替换整个集合：ItemsControl 会把全部子项的视觉树推倒重建，
        // 消息一多就是几十毫秒的卡顿，还会产生大量临时对象（GC 压力）。
        private readonly ObservableCollection<ToastMessageGroup> _items = new();
        private readonly Dictionary<(string Title, string App), ToastMessageGroup> _groupMap = new();

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

            var next = new List<ToastMessageGroup>(groups.Count);
            var nowTicks = DateTime.Now.Ticks;

            foreach (var pair in groups)
            {
                var messages = pair.Value;
                // 原地排序，取代 OrderBy(...).ToList() 的迭代器 + 中间集合
                messages.Sort((a, b) => OrderKey(a, nowTicks).CompareTo(OrderKey(b, nowTicks)));

                // 复用上一轮的 group 对象：只要分组还在，视觉树就不用重建，
                // 只是把里面的消息换成最新的一份。
                if (!_groupMap.TryGetValue(pair.Key, out var group))
                {
                    group = new ToastMessageGroup { Title = pair.Key.Title, Time = DateTime.Now };
                    _groupMap[pair.Key] = group;
                }
                else
                {
                    group.Title = pair.Key.Title;
                    group.Time = DateTime.Now;
                }

                ApplyMessagesToGroup(group, messages);
                next.Add(group);
            }

            // 回收已经不存在的分组（分组被删掉时）
            if (_groupMap.Count > next.Count)
            {
                List<(string Title, string App)>? stale = null;
                foreach (var key in _groupMap.Keys)
                {
                    if (!groups.ContainsKey(key))
                        (stale ??= new List<(string, string)>()).Add(key);
                }

                if (stale != null)
                {
                    foreach (var key in stale) _groupMap.Remove(key);
                }
            }

            SyncItems(next);

            var status = msgs.Count > 0 ? $"共 {msgs.Count} 条消息" : "暂无未读通知";
            if (!string.Equals(StatusText.Text, status, StringComparison.Ordinal))
                StatusText.Text = status;
        }

        /// <summary>把最新消息列表写进（复用的）分组对象。</summary>
        private static void ApplyMessagesToGroup(ToastMessageGroup group, List<ToastData> messages)
        {
            var existing = group.Messages;

            // 内容完全没变就直接跳过：
            // RefreshDisplay() 会 Clear + 重填 VisibleMessages，触发集合变更通知，
            // 让整个分组的子项视觉树重建一次 —— 删除一条消息导致几十个分组全部重建，
            // 就是"点一下卡住"的来源。没变的分组一次都不该动。
            if (existing.Count == messages.Count)
            {
                bool same = true;
                for (int i = 0; i < existing.Count; i++)
                {
                    if (!ReferenceEquals(existing[i], messages[i])) { same = false; break; }
                }

                if (same) return;
            }

            // 绝大多数刷新只是"尾部追加了新通知"：走增量，避免 Clear() 引发整项重建
            bool appendOnly = existing.Count > 0 && messages.Count > existing.Count;
            if (appendOnly)
            {
                for (int i = 0; i < existing.Count; i++)
                {
                    if (!ReferenceEquals(existing[i], messages[i]))
                    {
                        appendOnly = false;
                        break;
                    }
                }
            }

            if (appendOnly)
            {
                for (int i = existing.Count; i < messages.Count; i++)
                    AppendMessage(group, messages[i]);
            }
            else
            {
                existing.Clear();
                group.Bodies.Clear();
                group.SourceApp = "";
                group.SampleAumid = null;
                group.ProcessName = "";

                foreach (var m in messages)
                    AppendMessage(group, m);
            }

            group.RefreshDisplay();
        }

        /// <summary>单次遍历同时完成 Bodies / SourceApp / SampleAumid / ProcessName 的提取。</summary>
        private static void AppendMessage(ToastMessageGroup group, ToastData m)
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

        /// <summary>把目标列表同步进 ObservableCollection：已存在的项原地不动，只做插入/删除。</summary>
        private void SyncItems(List<ToastMessageGroup> next)
        {
            for (int i = 0; i < next.Count; i++)
            {
                if (i < _items.Count && ReferenceEquals(_items[i], next[i]))
                    continue;

                _items.Insert(i, next[i]);
            }

            while (_items.Count > next.Count)
                _items.RemoveAt(_items.Count - 1);
        }

        private static long OrderKey(ToastData m, long nowTicks)
            => m.NotificationId == 0 ? nowTicks : m.NotificationId;

        private void MessageItem_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            // 必须确认是卡片本体：指针事件会冒泡，动作层里的按钮也在这个容器下，
            // 不校验 Name 的话按按钮会被当成按卡片。
            if (sender is not Border border || border.DataContext is not ToastMessageGroup group)
                return;
            if (!string.Equals(border.Name, "MessageCard", StringComparison.Ordinal))
                return;

            // 只保留一个展开项；同时把"不是当前展开项"的卡片偏移清零：
            // 虚拟化会回收复用容器，残留的位移会带到一个无关的条目上。
            if (!ReferenceEquals(_openCard, border))
            {
                CloseOpenCard(border);
                SetCardOffset(border, 0);
            }

            // 两套坐标：卡片相对坐标用于判定起手位置，窗口相对坐标用于计算位移
            var local = e.GetCurrentPoint(border).Position;
            var windowPos = e.GetCurrentPoint(this).Position;
            var startedFromBottom = group.IsExpanded && local.Y > border.Bounds.Height - 42;

            var currentX = (border.RenderTransform as TranslateTransform)?.X ?? 0;

            _dragStates[border] = new DragState
            {
                Border = border,
                Group = group,
                StartWindow = windowPos,
                StartedFromBottom = startedFromBottom,
                StartTransformX = currentX,
                HasMoved = false,
                LastX = currentX,
                LastY = 0
            };

            border.RenderTransform ??= new TranslateTransform();
            e.Handled = true;
        }

        private void MessageItem_PointerMoved(object? sender, PointerEventArgs e)
        {
            if (sender is not Border border || !_dragStates.TryGetValue(border, out var state))
                return;

            // 用相对窗口的坐标：卡片在动，用它的坐标系算位移会被自身位移抵消
            var windowPos = e.GetCurrentPoint(this).Position;
            var deltaX = windowPos.X - state.StartWindow.X;
            var deltaY = windowPos.Y - state.StartWindow.Y;
            state.LastY = deltaY;
            state.HasMoved = Math.Abs(deltaX) > 8 || Math.Abs(deltaY) > 8;

            if (!state.HasMoved)
                return;

            if (state.StartedFromBottom)
            {
                if (!state.Group.IsExpanded)
                    return;

                // 只处理上下方向；X 保持按下时的值（展开状态下上滑不该把动作区收掉）
                var transform = border.RenderTransform as TranslateTransform ?? new TranslateTransform();
                transform.X = state.StartTransformX;
                transform.Y = Math.Min(0, deltaY) * 0.6;
                border.RenderTransform = transform;
                e.Handled = true;
                return;
            }

            // 双向跟手：向右露出动作区，向左准备删除。
            // 位移以上一次的位置为基准累加，所以"展开后再左滑"能一路滑到删除阈值。
            double x = state.StartTransformX + deltaX;
            if (x > ActionRevealWidth) x = ActionRevealWidth;
            if (x < -SwipeMaxLeft) x = -SwipeMaxLeft;

            state.LastX = x;
            SetCardOffset(border, x);
            e.Handled = true;
        }

        private void MessageItem_PointerReleased(object? sender, PointerReleasedEventArgs e)
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
                    // 向左滑过阈值（含"先右滑露出、再一路滑到左边"这种情况）→ 删除整组
                    if (ReferenceEquals(_openCard, border)) _openCard = null;
                    SetCardOffset(border, 0);
                    DeleteGroupWithoutActivation(state.Group);
                    e.Handled = true;
                    return;
                }
                else if (state.LastX >= ActionRevealThreshold)
                {
                    // 右滑到位 → 吸附到展开，露出两个按钮
                    SetCardOffset(border, ActionRevealWidth);
                    _openCard = border;
                    e.Handled = true;
                    return;
                }

                if (!state.HasMoved)
                {
                    // 动作区已展开时点卡片本身 = 收起，不触发删除（避免误删）
                    if (ReferenceEquals(_openCard, border))
                    {
                        SetCardOffset(border, 0);
                        _openCard = null;
                        e.Handled = true;
                        return;
                    }

                    // 已展开 → 删除整组 + 唤醒来源应用
                    if (state.Group.IsExpanded)
                    {
                        ClearAndActivateCurrentMessage(state.Group);
                        e.Handled = true;
                        return;
                    }

                    if (state.Group.HasHiddenMessages)
                    {
                        state.Group.ToggleExpand();
                        e.Handled = true;
                        return;
                    }

                    ClearAndActivateCurrentMessage(state.Group);
                    e.Handled = true;
                }
            }
            finally
            {
                // 未走"吸附展开"分支的一律复位（包括右滑不足阈值时的回弹）
                if (!ReferenceEquals(_openCard, border))
                {
                    if (border.RenderTransform is TranslateTransform transform)
                        transform.Y = 0;

                    SetCardOffset(border, 0);
                }
            }
        }

        private void MessageItem_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            if (sender is not Border border)
                return;

            // 关键：这个事件在**正常松手之后也会触发一次**（PointerReleased 之后）。
            // 旧实现在这里无条件把偏移归零 —— 于是刚刚吸附展开的动作区立刻又被收起，
            // 表现就是"右滑到底松手，卡片弹回原位，按钮根本来不及点"。
            //
            // 区分方法：PointerReleased 开头会把该卡片从 _dragStates 移除。
            //   - 还有记录 → 手势中途被打断（滚动 / 窗口失焦等），才需要复位；
            //   - 没有记录 → 已经走完正常松手流程，保持现状，不要动它。
            bool gestureFinished = !_dragStates.Remove(border);
            if (gestureFinished)
                return;

            if (ReferenceEquals(_openCard, border)) _openCard = null;
            SetCardOffset(border, 0);
        }

        /// <summary>动作区「删除」按钮：删除整组，不唤醒应用。</summary>
        private void ActionDelete_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control c || c.DataContext is not ToastMessageGroup group)
                return;

            DismissActionArea(sender as Control);
            DeleteGroupWithoutActivation(group);
            e.Handled = true;
        }

        /// <summary>动作区「唤醒」按钮：唤醒来源应用，并删除整组。</summary>
        private void ActionActivate_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Control c || c.DataContext is not ToastMessageGroup group)
                return;

            DismissActionArea(sender as Control);
            ClearAndActivateCurrentMessage(group);
            e.Handled = true;
        }

        /// <summary>点完按钮后收起动作区（卡片多半要被删掉，先复位避免复用容器时残留偏移）。</summary>
        private void DismissActionArea(Control? sender)
        {
            try
            {
                if (sender is Border card)
                {
                    SetCardOffset(card, 0);
                    if (ReferenceEquals(_openCard, card)) _openCard = null;
                    return;
                }

                // 从按钮逐层往上找同层的卡片：Button → StackPanel → ActionLayer → 容器 Grid
                Control? node = sender;
                for (int i = 0; i < 6 && node != null; i++)
                {
                    if (node.Parent is Panel panel)
                    {
                        foreach (var child in panel.Children)
                        {
                            if (child is Border b && string.Equals(b.Name, "MessageCard", StringComparison.Ordinal))
                            {
                                SetCardOffset(b, 0);
                                if (ReferenceEquals(_openCard, b)) _openCard = null;
                                return;
                            }
                        }
                    }

                    node = node.Parent as Control;
                }

                CloseOpenCard(null);
            }
            catch { }
        }

        private void DeleteGroupWithoutActivation(ToastMessageGroup group)
        {
            var title = group.Title;
            if (string.IsNullOrWhiteSpace(title))
                return;

            ToastMessageStore.RemoveByTitleAndSync(title);
            RefreshMessages();
            try { ((App)global::Avalonia.Application.Current!).OnMessagesHaveBeenCleared(); } catch { }
        }

        private void DeleteCurrentMessageWithoutActivation(ToastMessageGroup group)
        {
            var title = group.Title;
            if (string.IsNullOrWhiteSpace(title))
                return;

            ToastMessageStore.RemoveByTitleAndSync(title);
            RefreshMessages();
            try { ((App)global::Avalonia.Application.Current!).OnMessagesHaveBeenCleared(); } catch { }
        }

        private Task ClearAndActivateCurrentMessage(ToastMessageGroup group)
        {
            var title = group.Title;
            var current = group.LatestMessage;
            if (string.IsNullOrWhiteSpace(title) || current == null)
                return Task.CompletedTask;

            // 1) 内存里先删 + 刷新 UI（这部分必须在 UI 线程，但很快）
            ToastMessageStore.RemoveByTitleAndSync(title);
            RefreshMessages();
            try { ((App)global::Avalonia.Application.Current!).OnMessagesHaveBeenCleared(); } catch { }

            // 2) 唤醒来源应用放到后台线程。
            //    这一步是"删一条消息时界面卡住"的真正元凶：
            //    active_app 里的 FindPackagesForUserWithPackageTypes 是**同步**的全包枚举，
            //    TryBringToFrontByAppName 里的 Process.GetProcesses() 是**同步**的全进程枚举，
            //    两者都可能几百毫秒，而旧实现是在 UI 线程上直接跑的。
            var target = current;
            _ = Task.Run(() => ActivateAppCore(target));

            return Task.CompletedTask;
        }

        /// <summary>后台唤醒：先按 AUMID，失败再按应用名置前。</summary>
        private static async Task ActivateAppCore(ToastData current)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(current.Aumid))
                {
                    var (ok, msg) = await AppActivator.active_app(current.Aumid);
                    if (ok) return;

                    Debug.WriteLine($"[AppActivator] 唤醒失败：{msg}");

                    if (!string.IsNullOrWhiteSpace(current.AppName))
                    {
                        var (ok2, msg2) = AppActivator.TryBringToFrontByAppName(current.AppName);
                        if (!ok2)
                            Debug.WriteLine($"[AppActivator] 按应用名置前失败：{msg2}");
                    }
                }
                else if (!string.IsNullOrWhiteSpace(current.AppName))
                {
                    var (ok2, msg2) = AppActivator.TryBringToFrontByAppName(current.AppName);
                    if (!ok2)
                        Debug.WriteLine($"[AppActivator] 按应用名置前失败：{msg2}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"唤醒来源应用时异常: {ex.Message}");
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
