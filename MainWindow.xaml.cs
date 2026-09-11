using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Notifier
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer? _hideTimer;
        private readonly ObservableCollection<ToastMessageGroup> _messageGroups = new();
        private readonly System.Collections.Generic.List<QueuedMessage> _messageQueue = new();
        private DispatcherTimer _displayTimer;
        private bool _isDisplaying = false;
        private bool _isClosed = false;
        private bool _isClosingAnimation = false;
        private readonly TimeSpan _displayInterval = TimeSpan.FromSeconds(3);
        private DateTime _displayDeadline;
        private TimeSpan? _pausedRemaining;
        private DispatcherTimer _ctrlPollTimer;
        private bool _ctrlHeld = false;

        private record QueuedMessage(DateTime Time, string Title, string Body, string ProcessName);

        public MainWindow()
        {
            InitializeComponent();
            MessageList.ItemsSource = _messageGroups;

            try
            {
                if (App.Config != null && !double.IsNaN(App.Config.MainWindowOpacity))
                    Opacity = App.Config.MainWindowOpacity;
            }
            catch { }

            this.Opened += (_, __) => PositionWindow();
            this.Closed += OnWindowClosed;

            _displayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _displayTimer.Tick += OnDisplayTimerTick;

            _ctrlPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _ctrlPollTimer.Tick += CheckCtrlState;
            _ctrlPollTimer.Start();

            this.PointerPressed += OnMouseLeftClick;
        }

        public void AddMessage(string title, string body, string processName = "")
        {
            if (_isClosed) return;

            Dispatcher.UIThread.Post(() =>
            {
                var t = string.IsNullOrWhiteSpace(title) ? "新通知" : title;
                var b = string.IsNullOrWhiteSpace(body) ? "" : body;

                _messageQueue.Add(new QueuedMessage(DateTime.Now, t, b, processName ?? ""));
                _messageQueue.Sort((a, c) => a.Time.CompareTo(c.Time));

                if (!_isDisplaying && !_isClosed)
                    StartDisplaying();
            });
        }

        private async Task FadeOutAsync()
        {
            await Task.Delay(120);
        }

        private async Task FadeInAsync()
        {
            await Task.Delay(120);
        }

        private void StopDisplayTimer()
        {
            try { if (_displayTimer != null && _displayTimer.IsEnabled) _displayTimer.Stop(); } catch { }
        }

        private async Task HideWindowAndResetState(bool clearQueue = true)
        {
            if (_isClosed || _isClosingAnimation) return;

            StopDisplayTimer();
            _pausedRemaining = null;
            _isDisplaying = false;
            if (!_isClosed)
            {
                await FadeOutAsync();
                RootGrid.Opacity = 0;
                Hide();
            }
            if (clearQueue)
            {
                _messageQueue.Clear();
                _messageGroups.Clear();
            }
        }

        private async void OnDisplayTimerTick(object? sender, EventArgs e)
        {
            if (_isClosed || _isClosingAnimation) return;

            StopDisplayTimer();

            if (_messageQueue.Count == 0)
            {
                await HideWindowAndResetState(true);
                return;
            }

            if (_messageQueue.Count > 1)
            {
                await FadeOutAsync();

                if (_messageQueue.Count > 0)
                    _messageQueue.RemoveAt(0);

                ShowCurrentQueueHeadImmediate();
                await FadeInAsync();

                _isDisplaying = true;
                StartDisplayTimer();
                return;
            }

            await FadeOutAsync();
            if (_messageQueue.Count > 0)
                _messageQueue.RemoveAt(0);

            if (_messageQueue.Count == 0)
            {
                await HideWindowAndResetState(true);
                return;
            }

            ShowCurrentQueueHeadImmediate();
            await FadeInAsync();
            _isDisplaying = true;
            StartDisplayTimer();
        }

        private async void StartDisplaying()
        {
            if (_isClosed || _isClosingAnimation) return;
            if (_messageQueue.Count == 0) return;

            StopDisplayTimer();
            _isDisplaying = true;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PositionWindow();
                ShowCurrentQueueHeadImmediate();
            });

            RootGrid.Opacity = 1;
            Show();
            StartDisplayTimer();
        }

        private void ShowCurrentQueueHeadImmediate()
        {
            _messageGroups.Clear();
            var head = _messageQueue.FirstOrDefault();
            if (head != null)
            {
                var group = new ToastMessageGroup
                {
                    Title = head.Title,
                    ProcessName = head.ProcessName,
                    Time = head.Time
                };
                if (!string.IsNullOrEmpty(head.Body))
                    group.Bodies.Add(head.Body);

                _messageGroups.Add(group);
            }
        }

        public string? GetCurrentHeadTitle()
        {
            var head = _messageQueue.FirstOrDefault();
            return head == null ? null : (string.IsNullOrWhiteSpace(head.Title) ? "新通知" : head.Title);
        }

        private void CheckCtrlState(object? sender, EventArgs e)
        {
            if (_isClosed || !_isDisplaying || !IsVisible) return;

            try
            {
                bool isCtrlDown = false;

                if (isCtrlDown && !_ctrlHeld)
                {
                    _ctrlHeld = true;
                    if (_displayTimer.IsEnabled)
                    {
                        var remaining = _displayDeadline - DateTime.Now;
                        _pausedRemaining = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
                        _displayTimer.Stop();
                    }
                }
                else if (!isCtrlDown && _ctrlHeld)
                {
                    _ctrlHeld = false;
                    if (_pausedRemaining.HasValue)
                    {
                        var rem = _pausedRemaining.Value;
                        _displayTimer.Interval = rem <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : rem;
                        _displayDeadline = DateTime.Now + _displayTimer.Interval;
                        _displayTimer.Start();
                        _pausedRemaining = null;
                    }
                }
            }
            catch { }
        }

        private void OnWindowClosed(object? sender, EventArgs e)
        {
            _isClosed = true;
            _isDisplaying = false;
            _messageQueue.Clear();
            _messageGroups.Clear();
        }

        private void StartHideTimer()
        {
            if (_hideTimer == null)
            {
                _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _hideTimer.Tick += (_, __) =>
                {
                    if (_isClosed || _isClosingAnimation) return;
                    _hideTimer.Stop();
                    _ = HideWindowAndResetState(true);
                };
            }
            else
            {
                _hideTimer.Stop();
            }

            _hideTimer.Start();
        }

        private void StartDisplayTimer()
        {
            try
            {
                _displayTimer.Interval = _displayInterval;
                _displayDeadline = DateTime.Now + _displayInterval;
                _pausedRemaining = null;

                if (!_isClosed && !_isClosingAnimation)
                    _displayTimer.Start();
            }
            catch { }
        }

        private void OnMouseLeftClick(object? sender, PointerPressedEventArgs e)
        {
            try
            {
                if (!_ctrlHeld || !_isDisplaying || !IsVisible) return;
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    SkipCurrentMessage();
                    e.Handled = true;
                }
            }
            catch { }
        }

        public async void SkipCurrentMessage()
        {
            try
            {
                if (!_isDisplaying || _isClosed) return;

                StopDisplayTimer();

                if (_messageQueue.Count == 0)
                {
                    await HideWindowAndResetState(true);
                    return;
                }

                if (_messageQueue.Count > 1)
                {
                    await FadeOutAsync();

                    if (_messageQueue.Count > 0)
                        _messageQueue.RemoveAt(0);

                    ShowCurrentQueueHeadImmediate();
                    await FadeInAsync();

                    _isDisplaying = true;

                    if (_ctrlHeld)
                    {
                        _pausedRemaining = _displayInterval;
                    }
                    else
                    {
                        StartDisplayTimer();
                    }

                    return;
                }

                _messageQueue.Clear();
                _messageGroups.Clear();
                await HideWindowAndResetState(false);
            }
            catch { }
        }

        private void PositionWindow()
        {
            var screen = Screens.Primary;
            if (screen == null) return;

            var workArea = screen.WorkingArea;
            var left = App.Config.IsMainWindowMiddle ? (workArea.Width - Width) / 2.0 : App.Config.MainWindowLeft;
            Position = new PixelPoint((int)Math.Round(left), (int)Math.Round(App.Config.MainWindowTop));
        }
    }
}