using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace Notifier
{
    #region Enums

    public enum PlaybackState { Playing, Paused, Stopped, Changing, Unknown }
    public enum TrackDirection { Previous, Next }
    public enum MediaPlaybackTypeEnum { Music, Video, Image, Unknown }

    #endregion

    #region Data Classes

    public class MediaInfo
    {
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public List<string> Genres { get; set; } = new List<string>();
        public bool HasThumbnail { get; set; }
        public MediaPlaybackTypeEnum PlaybackType { get; set; }
    }

    public class ButtonStates
    {
        public bool IsPlayEnabled { get; set; }
        public bool IsPauseEnabled { get; set; }
        public bool IsStopEnabled { get; set; }
        public bool IsNextEnabled { get; set; }
        public bool IsPreviousEnabled { get; set; }
        public bool IsFastForwardEnabled { get; set; }
        public bool IsRewindEnabled { get; set; }
        public bool IsChannelDownEnabled { get; set; }
        public bool IsChannelUpEnabled { get; set; }
        public bool IsRecordEnabled { get; set; }

        public int EnabledCount
        {
            get
            {
                int c = 0;
                if (IsPlayEnabled) c++;
                if (IsPauseEnabled) c++;
                if (IsStopEnabled) c++;
                if (IsNextEnabled) c++;
                if (IsPreviousEnabled) c++;
                if (IsFastForwardEnabled) c++;
                if (IsRewindEnabled) c++;
                if (IsChannelDownEnabled) c++;
                if (IsChannelUpEnabled) c++;
                if (IsRecordEnabled) c++;
                return c;
            }
        }
    }

    public class MediaSummary
    {
        public PlaybackState PlaybackState { get; set; }
        public MediaInfo MediaInfo { get; set; } = new MediaInfo();
        public ButtonStates ButtonStates { get; set; } = new ButtonStates();
    }

    #endregion

    #region SMTC Controller

    public class SMTCController : IDisposable
    {
        private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;
        private bool _initialized = false;
        private System.Threading.Timer? _monitorTimer;

        public event Action<PlaybackState>? OnPlaybackStateChanged;
        public event Action<TrackDirection>? OnTrackChanged;
        public event Action<bool>? OnSessionChanged;

        public SMTCController() { }

        /// <summary>
        /// 异步初始化 SMTC 会话管理器
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_initialized) return;

            try
            {
                _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                
                if (_sessionManager == null)
                {
                    throw new InvalidOperationException("Failed to get session manager");
                }

                // 获取当前会话
                UpdateCurrentSession();

                // 监听会话变化
                _sessionManager.SessionsChanged += SessionManager_SessionsChanged;

                _initialized = true;
                Console.WriteLine("SMTC initialized successfully!");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to initialize SMTC: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 同步初始化包装器
        /// </summary>
        public void Initialize()
        {
            try
            {
                InitializeAsync().GetAwaiter().GetResult();
            }
            catch (AggregateException ae)
            {
                throw ae.InnerException ?? ae;
            }
        }

        /// <summary>
        /// 等待直到检测到媒体会话
        /// </summary>
        public async Task<bool> WaitForSessionAsync(int timeoutSeconds = 30)
        {
            if (_currentSession != null) return true;

            var tcs = new TaskCompletionSource<bool>();
            var cts = new System.Threading.CancellationTokenSource();
            
            // 超时处理
            cts.Token.Register(() => tcs.TrySetResult(false));
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            // 监听会话变化
            Action<bool> handler = null!;
            handler = (hasSession) =>
            {
                if (hasSession)
                {
                    OnSessionChanged -= handler;
                    tcs.TrySetResult(true);
                }
            };
            
            OnSessionChanged += handler;

            // 再次检查
            if (_currentSession != null)
            {
                OnSessionChanged -= handler;
                return true;
            }

            return await tcs.Task;
        }

        #region Control Methods

        public async Task TogglePlayPauseAsync()
        {
            EnsureInitialized();
            if (_currentSession == null) return;

            var status = _currentSession.GetPlaybackInfo().PlaybackStatus;
            
            if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                await _currentSession.TryPauseAsync();
                OnPlaybackStateChanged?.Invoke(PlaybackState.Paused);
            }
            else
            {
                await _currentSession.TryPlayAsync();
                OnPlaybackStateChanged?.Invoke(PlaybackState.Playing);
            }
        }

        public void TogglePlayPause()
        {
            TogglePlayPauseAsync().GetAwaiter().GetResult();
        }

        public async Task SetPlaybackStateAsync(PlaybackState state)
        {
            EnsureInitialized();
            if (_currentSession == null) return;

            switch (state)
            {
                case PlaybackState.Playing:
                    await _currentSession.TryPlayAsync();
                    break;
                case PlaybackState.Paused:
                    await _currentSession.TryPauseAsync();
                    break;
                case PlaybackState.Stopped:
                    await _currentSession.TryStopAsync();
                    break;
            }
            OnPlaybackStateChanged?.Invoke(state);
        }

        public void SetPlaybackState(PlaybackState state)
        {
            SetPlaybackStateAsync(state).GetAwaiter().GetResult();
        }

        public async Task SwitchTrackAsync(TrackDirection direction)
        {
            EnsureInitialized();
            if (_currentSession == null) return;

            if (direction == TrackDirection.Next)
            {
                await _currentSession.TrySkipNextAsync();
            }
            else
            {
                await _currentSession.TrySkipPreviousAsync();
            }
            OnTrackChanged?.Invoke(direction);
        }

        public void SwitchTrack(TrackDirection direction)
        {
            SwitchTrackAsync(direction).GetAwaiter().GetResult();
        }

        #endregion

        #region Info Getters

        public PlaybackState GetPlaybackStatus()
        {
            EnsureInitialized();
            if (_currentSession == null) return PlaybackState.Unknown;

            try
            {
                var status = _currentSession.GetPlaybackInfo().PlaybackStatus;
                return ConvertPlaybackStatus(status);
            }
            catch
            {
                return PlaybackState.Unknown;
            }
        }

        public async Task<MediaInfo> GetCurrentMediaInfoAsync()
        {
            EnsureInitialized();
            if (_currentSession == null) return new MediaInfo();

            try
            {
                var properties = await _currentSession.TryGetMediaPropertiesAsync();
                
                return new MediaInfo
                {
                    Title = properties?.Title ?? string.Empty,
                    Artist = properties?.Artist ?? string.Empty,
                    Album = properties?.AlbumTitle ?? string.Empty,
                    Genres = properties?.Genres?.Count > 0 
                        ? new List<string>(properties.Genres) 
                        : new List<string>(),
                    HasThumbnail = properties?.Thumbnail != null,
                    PlaybackType = MediaPlaybackTypeEnum.Music
                };
            }
            catch
            {
                return new MediaInfo();
            }
        }

        public MediaInfo GetCurrentMediaInfo()
        {
            return GetCurrentMediaInfoAsync().GetAwaiter().GetResult();
        }

        public ButtonStates GetAllButtonStates()
        {
            EnsureInitialized();
            if (_currentSession == null) return new ButtonStates();

            try
            {
                var controls = _currentSession.GetPlaybackInfo().Controls;
                
                return new ButtonStates
                {
                    IsPlayEnabled = controls.IsPlayEnabled,
                    IsPauseEnabled = controls.IsPauseEnabled,
                    IsStopEnabled = controls.IsStopEnabled,
                    IsNextEnabled = controls.IsNextEnabled,
                    IsPreviousEnabled = controls.IsPreviousEnabled,
                    IsFastForwardEnabled = controls.IsFastForwardEnabled,
                    IsRewindEnabled = controls.IsRewindEnabled,
                    IsChannelDownEnabled = false,
                    IsChannelUpEnabled = false,
                    IsRecordEnabled = false
                };
            }
            catch
            {
                return new ButtonStates();
            }
        }

        public bool IsSMTCAvailable()
        {
            try
            {
                return _initialized && _sessionManager != null;
            }
            catch
            {
                return false;
            }
        }

        public bool HasActiveSession()
        {
            return _currentSession != null;
        }

        public MediaSummary GetMediaSummary()
        {
            return new MediaSummary
            {
                PlaybackState = GetPlaybackStatus(),
                MediaInfo = GetCurrentMediaInfo(),
                ButtonStates = GetAllButtonStates()
            };
        }

        public IDisposable MonitorChanges(Action<MediaSummary> onStatusChanged)
        {
            EnsureInitialized();
            
            _monitorTimer = new System.Threading.Timer(state =>
            {
                if (state is Action<MediaSummary> callback)
                {
                    var summary = GetMediaSummary();
                    callback(summary);
                }
            }, onStatusChanged, 0, 2000);
            
            return this;
        }

        #endregion

        #region Event Handlers

        private void UpdateCurrentSession()
        {
            if (_sessionManager == null) return;
            
            var previousSession = _currentSession;
            _currentSession = _sessionManager.GetCurrentSession();
            
            if (_currentSession != previousSession)
            {
                if (previousSession != null)
                {
                    previousSession.PlaybackInfoChanged -= CurrentSession_PlaybackInfoChanged;
                    previousSession.MediaPropertiesChanged -= CurrentSession_MediaPropertiesChanged;
                }
                
                if (_currentSession != null)
                {
                    _currentSession.PlaybackInfoChanged += CurrentSession_PlaybackInfoChanged;
                    _currentSession.MediaPropertiesChanged += CurrentSession_MediaPropertiesChanged;
                    OnSessionChanged?.Invoke(true);
                    Console.WriteLine("Media session detected!");
                }
                else
                {
                    OnSessionChanged?.Invoke(false);
                    Console.WriteLine("No media session available");
                }
            }
        }

        private void SessionManager_SessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        {
            UpdateCurrentSession();
        }

        private void CurrentSession_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            try
            {
                var status = sender.GetPlaybackInfo().PlaybackStatus;
                OnPlaybackStateChanged?.Invoke(ConvertPlaybackStatus(status));
            }
            catch
            {
                // 忽略错误
            }
        }

        private void CurrentSession_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            // 媒体属性变化时可以触发UI更新
        }

        #endregion

        #region Private Methods

        private void EnsureInitialized()
        {
            if (!_initialized)
                throw new InvalidOperationException("SMTCController not initialized. Call Initialize() or InitializeAsync() first.");
        }

        private PlaybackState ConvertPlaybackStatus(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
        {
            switch (status)
            {
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing:
                    return PlaybackState.Playing;
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused:
                    return PlaybackState.Paused;
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped:
                    return PlaybackState.Stopped;
                case GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing:
                    return PlaybackState.Changing;
                default:
                    return PlaybackState.Unknown;
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _monitorTimer?.Dispose();
            _monitorTimer = null;
            
            if (_currentSession != null)
            {
                _currentSession.PlaybackInfoChanged -= CurrentSession_PlaybackInfoChanged;
                _currentSession.MediaPropertiesChanged -= CurrentSession_MediaPropertiesChanged;
            }
            
            if (_sessionManager != null)
            {
                _sessionManager.SessionsChanged -= SessionManager_SessionsChanged;
            }
        }

        #endregion
    }

    #endregion
}