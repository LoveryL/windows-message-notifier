using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

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
            PlayPauseIcon.Text = "\u25B6";

            try
            {
                if (App.Config != null && !double.IsNaN(App.Config.SettingWindowOpacity))
                    Opacity = App.Config.SettingWindowOpacity;
            }
            catch { }

            LoadCurrentSettings();
            this.Opened += OnFirstLoaded;
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

        private void LoadCurrentSettings()
        {
            _isInitializing = true;
            try
            {
                float vol = _settings.GetSystemVolume();
                VolumeSlider.Value = vol * 100.0;

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

                BrightnessSlider.Value = Math.Clamp(brightnessPercent, 0, 100);
            }
            finally
            {
                _isInitializing = false;
            }
        }

        private void OnFirstLoaded(object? sender, EventArgs e)
        {
            PositionWindow();
            _allowDeactivate = true;
            RootGrid.Opacity = 1;

            VolumeSlider.ValueChanged += (_, _) =>
            {
                if (_isInitializing) return;
                float level = (float)(VolumeSlider.Value / 100.0);
                _settings.SetSystemVolume(level);
            };

            BrightnessSlider.ValueChanged += (_, _) =>
            {
                if (_isInitializing) return;
                int percent = (int)Math.Clamp(BrightnessSlider.Value, 0, 100);
                if (_settings.BrightnessCapability == BrightnessCapability.Hardware)
                {
                    _settings.TrySetScreenBrightness(percent);
                }
                else
                {
                    int safePercent = Math.Max(5, percent);
                    _settings.SetSimulatedBrightness(safePercent);
                }
            };

            InitializeSMTC();
            BtnPrevious.Click += OnPreviousClicked;
            BtnPlayPause.Click += OnPlayPauseClicked;
            BtnNext.Click += OnNextClicked;
        }

        public void RequestClose()
        {
            if (_isClosing) return;
            _isClosing = true;
            SafeClose();
        }

        private void SafeClose()
        {
            WindowClosed?.Invoke();
            _settings.Dispose();
            CleanupSMTC();
            Close();
        }

        private void PositionWindow()
        {
            var screen = Screens.Primary;
            if (screen == null) return;

            var workArea = screen.WorkingArea;
            const int leftOffset = 18;
            const int panelWidth = 370;
            const int horizontalGap = 18;
            var top = workArea.Y + 20;
            var left = workArea.X + leftOffset + panelWidth + horizontalGap;

            Position = new PixelPoint(left, top);
        }
    }
}
