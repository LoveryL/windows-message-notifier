using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace Notifier
{
    public partial class SettingWindow : Window
    {
        public event Action<bool>? ReportFocusState;
        public event Action? WindowClosed;

        internal bool _isClosing;
        private bool _allowDeactivate;

        private readonly SystemSettingsManager _settings = new();
        private bool _isInitializing;

        public SettingWindow()
        {
            InitializeComponent();
            LoadCurrentSettings();
            Loaded += OnFirstLoaded;
        }

        #region 启动读取
        private void LoadCurrentSettings()
        {
            _isInitializing = true;

            try
            {
                // ---- 音量 ----
                float vol = _settings.GetSystemVolume();
                VolumeSlider.Value = vol * 100.0;

                // ---- 亮度 ----
                int brightnessPercent;

                if (_settings.BrightnessCapability == BrightnessCapability.Hardware)
                {
                    brightnessPercent = _settings.GetScreenBrightness();
                }
                else
                {
                    int sim = _settings.GetSimulatedBrightness();
                    brightnessPercent = sim >= 0 ? sim : 70; // 默认 70%
                }

                BrightnessSlider.Value = Math.Clamp(brightnessPercent, 0, 100);
            }
            finally
            {
                _isInitializing = false;
            }
        }
        #endregion

        #region 入场
        private void OnFirstLoaded(object? sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(PositionWindow),
                System.Windows.Threading.DispatcherPriority.Background);

            VolumeSlider.ValueChanged += OnVolumeChanged;
            BrightnessSlider.ValueChanged += OnBrightnessChanged;

            if (Resources["SlideInAnimation"] is Storyboard sb)
            {
                sb.Completed += (_, __) => _allowDeactivate = true;
                sb.Begin(this);
            }
            else _allowDeactivate = true;

            // ========== [新增] SMTC 初始化 ==========
            InitializeSMTC();

            // ========== [新增] 绑定 SMTC 按钮事件 ==========
            BtnPrevious.Click += OnPreviousClicked;
            BtnPlayPause.Click += OnPlayPauseClicked;
            BtnNext.Click += OnNextClicked;

            Show();
        }
        #endregion

        #region 滑块拖动|同步系统
        private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isInitializing) return;

            float level = (float)(e.NewValue / 100.0);
            _settings.SetSystemVolume(level);
        }

        private void OnBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isInitializing) return;

            int percent = (int)Math.Clamp(e.NewValue, 0, 100);

            if (_settings.BrightnessCapability == BrightnessCapability.Hardware)
            {
                _settings.TrySetScreenBrightness(percent);
            }
            else
            {
                int safePercent = Math.Max(5, percent);
                _settings.SetSimulatedBrightness(safePercent);
            }
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

        #region 退场
        public void RequestClose()
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
            WindowClosed?.Invoke();
            _settings.Dispose();

            // ========== [新增] 清理 SMTC 资源 ==========
            CleanupSMTC();

            Close();
        }
        #endregion

        #region 布局
        private void PositionWindow()
        {
            UpdateLayout();
            double h = ActualHeight > 0 ? ActualHeight : Height;
            var s = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea;
            if (s == null) return;
            Top = s.Value.Top + Math.Max(20, (s.Value.Height - h) * 0.03) * 2 + h;
            Left = s.Value.Left + 18;
        }
        #endregion
    }
}