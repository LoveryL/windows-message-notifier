using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
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

        private async void OnFirstLoaded(object? sender, EventArgs e)
        {
            PositionWindow();
            _allowDeactivate = true;
            await AnimateShowAsync();

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

        internal void RequestClose()
        {
            if (_isClosing) return;
            _isClosing = true;
            _ = RequestCloseAsync();
        }

        private void SafeClose()
        {
            WindowClosed?.Invoke();
            _settings.Dispose();
            CleanupSMTC();
            Close();
        }

        private async Task RequestCloseAsync()
        {
            try { await AnimateHideAsync(); } catch { }
            SafeClose();
        }

        private async Task AnimateShowAsync()
        {
            try
            {
                const int frames = 8;
                const int msPerFrame = 4;
                double fromOpacity = 0.0, toOpacity = 1.0;
                double fromX = -420, toX = 0;

                for (int i = 0; i <= frames; i++)
                {
                    double t = (double)i / frames;
                    RootGrid.Opacity = fromOpacity + (toOpacity - fromOpacity) * t;
                    if (RootGrid.RenderTransform is TranslateTransform tr)
                        tr.X = fromX + (toX - fromX) * t;
                    await Task.Delay(msPerFrame);
                }
            }
            catch { }
        }

        private async Task AnimateHideAsync()
        {
            try
            {
                const int frames = 8;
                const int msPerFrame = 4;
                double fromOpacity = RootGrid.Opacity, toOpacity = 0.0;
                double fromX = 0, toX = -420;

                for (int i = 0; i <= frames; i++)
                {
                    double t = (double)i / frames;
                    RootGrid.Opacity = fromOpacity + (toOpacity - fromOpacity) * t;
                    if (RootGrid.RenderTransform is TranslateTransform tr)
                        tr.X = fromX + (toX - fromX) * t;
                    await Task.Delay(msPerFrame);
                }
            }
            catch { }
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
