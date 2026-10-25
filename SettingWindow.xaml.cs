using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Notifier
{
    /// <summary>
    /// 控制中心：系统音量 / 亮度 + SMTC 媒体控制。
    /// 设置项已迁出到独立的 PreferencesWindow（托盘「设置」打开），这里保持原功能定位。
    /// </summary>
    public partial class SettingWindow : Window
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

        /// <summary>面板是否处于显示状态。隐藏时不再刷新 UI、不再轮询系统音量。</summary>
        private bool _visible = true;

        // 复用进程内共享实例：避免每次打开面板都重建 NAudio 音频端点（枚举设备 + COM 初始化）
        private SystemSettingsManager _settings => SystemSettingsManager.Shared;
        private bool _isInitializing;

        // 滑块拖动的去抖：亮度走 WMI、音量走音频端点，都是毫秒级的同步调用，
        // 拖动时每变一个像素就调一次会让 UI 明显掉帧，这里合并成"停下 100ms 后应用最新值"。
        private CancellationTokenSource? _volumeDebounce;
        private CancellationTokenSource? _brightnessDebounce;
        private float _pendingVolume;
        private int _pendingBrightness;

        public SettingWindow()
        {
            InitializeComponent();
            SetupRootTransform();
            PlayPauseIcon.Text = "\u25B6";

            try
            {
                if (App.setting != null && !double.IsNaN(App.setting.opacity))
                    Opacity = App.setting.opacity;
            }
            catch { }

            // 阴影默认与原来 XAML 中的 "#00000040" 一致，但改为从设置读取，可被改色控制
            ApplyAppearance();

            LoadCurrentSettings();
            this.Opened += OnFirstLoaded;

            if (App.setting != null)
                App.setting.SettingChanged += OnSettingChangedExternal;
            ThemeManager.ThemeChanged += OnThemeChanged;

            this.Closed += (_, __) =>
            {
                if (App.setting != null)
                    App.setting.SettingChanged -= OnSettingChangedExternal;
                ThemeManager.ThemeChanged -= OnThemeChanged;
            };

            // 兜底：窗口若没走 RequestClose 路径（被系统或其它途径关闭），
            // 也要释放 SMTC 订阅并通知 App 做收尾，否则面板状态永远停留在"已打开"。
            this.Closed += (_, __) =>
            {
                _reallyClosed = true;
                _visible = false;
                try { SystemSettingsManager.Shared.VolumeChanged -= OnSystemVolumeChanged; } catch { }
                try { SystemSettingsManager.Shared.SetVolumeMonitoring(false); } catch { }

                if (_isClosing) return;
                _isClosing = true;
                CleanupSMTC();
                WindowClosed?.Invoke();
            };

            // 系统音量被别的程序改动时同步滑块（回调在音频 STA 线程，内部会切回 UI 线程）
            try { SystemSettingsManager.Shared.VolumeChanged += OnSystemVolumeChanged; } catch { }

            // 面板改为"收起"而不是"销毁"：Alt+F4 / 失焦关闭都只是隐藏，
            // 下次点托盘直接 Show，不用再重建控件树、重新初始化 SMTC。
            this.Closing += (_, e) =>
            {
                if (_allowRealClose || App.IsExiting)
                {
                    _reallyClosed = true;
                    return;
                }

                e.Cancel = true;
                _ = RequestCloseAsync();
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

        /// <summary>阴影 / 圆角 / 动画时长沿用设置（控制中心外观本身保持原样）。</summary>
        private void ApplyAppearance()
        {
            try
            {
                var s = App.setting;
                if (s == null || OuterBorder == null) return;

                OuterBorder.BoxShadow = ThemeManager.BuildPopupShadow(s.PopupShadowHex, 24, 0);

                _showDurationMs = Math.Max(0, s.PanelEnterMs);
                _hideDurationMs = Math.Max(0, s.PanelExitMs);
                _slideFromX = s.PanelSlideX;

                if (!double.IsNaN(s.opacity))
                    Opacity = s.opacity;
            }
            catch { }
        }

        private void OnSettingChangedExternal(Settings_Manager.SettingType type)
        {
            if (type == Settings_Manager.SettingType.Theme ||
                type == Settings_Manager.SettingType.Animation ||
                type == Settings_Manager.SettingType.Opacity ||
                type == Settings_Manager.SettingType.Reset)
            {
                ApplyAppearance();
            }
        }

        private void OnThemeChanged() => ApplyAppearance();

        private void LoadCurrentSettings()
        {
            _isInitializing = true;
            try
            {
                float vol = _settings.GetSystemVolume();
                // -1 表示音频端点还没就绪（STA 线程还在初始化）。
                // 旧实现会把 0 直接写进滑块，于是面板一打开音量就"跳到 0"，
                // 等端点就绪再跳回来 —— 现在这种情况保持滑块原值。
                if (vol >= 0)
                    VolumeSlider.Value = vol * 100.0;

                // 亮度读取（硬件路径要走一次 WMI 查询，几十毫秒）放后台，不拖慢窗口弹出动画
                _ = Task.Run(() =>
                {
                    int brightnessPercent;
                    if (_settings.BrightnessCapability == BrightnessCapability.Hardware)
                    {
                        brightnessPercent = _settings.GetScreenBrightness();
                    }
                    else
                    {
                        int sim = _settings.GetSimulatedBrightness();
                        brightnessPercent = sim >= 0 ? sim : 100;
                    }

                    var value = Math.Clamp(brightnessPercent, 0, 100);
                    Dispatcher.UIThread.Post(() =>
                    {
                        _isInitializing = true;
                        BrightnessSlider.Value = value;
                        _isInitializing = false;
                    });
                });
            }
            finally
            {
                _isInitializing = false;
            }
        }

        private async void OnFirstLoaded(object? sender, EventArgs e)
        {
            PositionWindow();
            _allowDeactivate = true;
            await AnimateShowAsync();

            VolumeSlider.ValueChanged += (_, _) => ScheduleVolumeApply();
            BrightnessSlider.ValueChanged += (_, _) => ScheduleBrightnessApply();

            InitializeSMTC();
            BtnPrevious.Click += OnPreviousClicked;
            BtnPlayPause.Click += OnPlayPauseClicked;
            BtnNext.Click += OnNextClicked;
        }

        private void ScheduleVolumeApply()
        {
            if (_isInitializing) return;

            _pendingVolume = (float)(VolumeSlider.Value / 100.0);
            Debounce(ref _volumeDebounce, 80, () => _settings.SetSystemVolume(_pendingVolume));
        }

        private void ScheduleBrightnessApply()
        {
            if (_isInitializing) return;

            _pendingBrightness = (int)Math.Clamp(BrightnessSlider.Value, 0, 100);
            Debounce(ref _brightnessDebounce, 120, ApplyPendingBrightness);
        }

        private void ApplyPendingBrightness()
        {
            int percent = _pendingBrightness;
            if (_settings.BrightnessCapability == BrightnessCapability.Hardware)
            {
                _settings.TrySetScreenBrightness(percent);
            }
            else
            {
                _settings.SetSimulatedBrightness(Math.Max(5, percent));
            }
        }

        /// <summary>合并短时间内的连续调用，只执行最后一次（尾部去抖）。</summary>
        private static void Debounce(ref CancellationTokenSource? cts, int delayMs, Action action)
        {
            // 先取消上一轮，再替换。旧的 CTS 在取消后即可释放。
            var previous = cts;
            var source = new CancellationTokenSource();
            cts = source;
            var token = source.Token;

            if (previous != null)
            {
                try { previous.Cancel(); } catch { }
            }

            _ = Task.Run(async () =>
            {
                try { await Task.Delay(delayMs, token); }
                catch
                {
                    // 被后一轮取代（取消/释放）就静默退出
                    return;
                }

                if (token.IsCancellationRequested) return;

                try { action(); }
                catch (Exception ex) { Logger.Error("应用设置失败", ex); }
            });
        }

        /// <summary>系统音量/静音被外部改变（音频 STA 线程回调）。</summary>
        private void OnSystemVolumeChanged(float volume, bool muted)
        {
            if (volume < 0) return;

            Dispatcher.UIThread.Post(() =>
            {
                if (!_visible) return;

                try
                {
                    // 抑制回环：程序化改滑块不该再触发一次写音量
                    _isInitializing = true;
                    VolumeSlider.Value = volume * 100.0;
                }
                catch { }
                finally
                {
                    _isInitializing = false;
                }
            });
        }

        /// <summary>面板重新显示：恢复刷新、打开系统音量轮询。</summary>
        public void OnAppShown()
        {
            bool wasHidden = !_visible;
            _visible = true;

            try { SystemSettingsManager.Shared.SetVolumeMonitoring(true); } catch { }

            // 复用实例后不会再触发 Opened，入场动画在这里补播
            if (wasHidden)
            {
                try { PositionWindow(); } catch { }
                _ = AnimateShowAsync();
                try { LoadCurrentSettings(); } catch { }
                // 收起期间媒体信息/会话可能已经变了，补一次刷新
                try { RefreshMediaNow(); } catch { }
            }
        }

        /// <summary>面板收起：停止一切后台刷新，常驻开销归零。</summary>
        public void OnAppHidden()
        {
            _visible = false;
            try { SystemSettingsManager.Shared.SetVolumeMonitoring(false); } catch { }
        }

        internal void RequestClose()
        {
            if (_isClosing) return;
            _isClosing = true;
            _ = RequestCloseAsync();
        }

        private void SafeClose()
        {
            // 隐藏而不是 Close：控件树、SMTC 订阅、音量轮询全部保留，
            // 下次打开是"唤醒"而不是"重建"。
            _visible = false;
            try { SystemSettingsManager.Shared.SetVolumeMonitoring(false); } catch { }
            WindowClosed?.Invoke();
            Hide();
        }

        private async Task RequestCloseAsync()
        {
            try { await AnimateHideAsync(); } catch { }
            finally
            {
                SafeClose();
                _isClosing = false;
            }
        }

        // ===== 面板进出场动画参数 =====
        // 时间驱动替换"固定帧数 + Task.Delay"，时长与位移从设置读取并缓存成字段。
        private double _slideFromX = -420;
        private int _showDurationMs = 240;
        private int _hideDurationMs = 200;

        private readonly TranslateTransform _rootTranslate = new();
        private readonly ScaleTransform _rootScale = new() { ScaleX = 1, ScaleY = 1 };

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
                    double e = Easing.OutCubic(t);
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

        private void PositionWindow()
        {
            var screen = Screens.Primary;
            if (screen == null) return;

            var workArea = screen.WorkingArea;
            var top = workArea.Y + (workArea.Height / 2) + 15;
            var left = workArea.X + 15;

            Position = new PixelPoint((int)Math.Round((double)left, 0, MidpointRounding.AwayFromZero), (int)Math.Round((double)top, 0, MidpointRounding.AwayFromZero));
        }
    }
}
