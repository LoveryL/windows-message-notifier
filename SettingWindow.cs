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

        public SettingWindow()
        {
            InitializeComponent();
            Loaded += OnFirstLoaded;
        }

        #region 入场
        private void OnFirstLoaded(object? sender, RoutedEventArgs e)
        {
            PositionWindow();

            if (Resources["SlideInAnimation"] is Storyboard sb)
            {
                sb.Completed += (_, __) => _allowDeactivate = true;
                sb.Begin(this);
            }
            else _allowDeactivate = true;

            Show();
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
            Top = s.Value.Top + Math.Max(20, (s.Value.Height - h) * 0.03)*2+h;
            Left = s.Value.Left + 18;
        }
        #endregion
    }
}