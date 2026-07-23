using System;
using System.Threading.Tasks;
using System.Windows;

namespace Notifier
{
    /// <summary>
    /// SMTC 模块接入 - 部分类扩展
    /// 不修改原有 SettingWindow.cs 的任何代码
    /// </summary>
    public partial class SettingWindow
    {
        private SMTCController? _smtcController;
        private bool _smtcInitialized;
        private string _lastTitle = string.Empty;  // 记录上一次的标题，用于检测变化

        /// <summary>
        /// 初始化 SMTC（在窗口加载后调用）
        /// </summary>
        private async void InitializeSMTC()
        {
            if (_smtcInitialized) return;

            try
            {
                _smtcController = new SMTCController();
                await _smtcController.InitializeAsync();  // 异步初始化，避免 UI 线程死锁

                // 订阅 SMTC 事件
                _smtcController.OnPlaybackStateChanged += OnSmtcPlaybackStateChanged;
                _smtcController.OnSessionChanged += OnSmtcSessionChanged;

                // 尝试获取当前媒体信息并更新 UI
                TryUpdateMediaUI();

                _smtcInitialized = true;
                Console.WriteLine("SMTC 接入成功");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SMTC 初始化失败: {ex.Message}");
                // 静默失败，不影响主功能
            }
        }

        /// <summary>
        /// 尝试更新媒体 UI（歌曲信息 + 播放状态）
        /// </summary>
        private async void TryUpdateMediaUI()
        {
            if (_smtcController == null || !_smtcController.HasActiveSession())
            {
                // 没有活跃会话，保持默认文本
                return;
            }

            try
            {
                // 获取媒体信息（在后台线程执行，避免 UI 线程死锁）
                MediaInfo mediaInfo = await Task.Run(() => _smtcController.GetCurrentMediaInfo());
                PlaybackState playbackState = await Task.Run(() => _smtcController.GetPlaybackStatus());

                // 更新 UI（必须在 UI 线程）
                Dispatcher.Invoke(() =>
                {
                    // 只有标题发生变化时才更新（避免不必要的闪烁）
                    if (!string.IsNullOrEmpty(mediaInfo.Title) && mediaInfo.Title != _lastTitle)
                    {
                        _lastTitle = mediaInfo.Title;
                        SongTitle.Text = mediaInfo.Title;
                        SongArtist.Text = string.IsNullOrEmpty(mediaInfo.Artist) ? "未知艺术家" : mediaInfo.Artist;
                        Console.WriteLine($"歌曲切换: {mediaInfo.Title} - {mediaInfo.Artist}");
                    }
                    else if (string.IsNullOrEmpty(mediaInfo.Title))
                    {
                        // 标题为空时恢复默认
                        _lastTitle = string.Empty;
                        SongTitle.Text = "未在播放";
                        SongArtist.Text = "未知艺术家";
                    }

                    // 更新播放/暂停图标
                    UpdatePlayPauseIcon(playbackState);
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"更新媒体 UI 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新播放/暂停图标
        /// </summary>
        private void UpdatePlayPauseIcon(PlaybackState state)
        {
            switch (state)
            {
                case PlaybackState.Playing:
                    PlayPauseIcon.Text = "⏸";  // 暂停图标
                    break;
                case PlaybackState.Paused:
                case PlaybackState.Stopped:
                default:
                    PlayPauseIcon.Text = "▶️";  // 播放图标
                    break;
            }
        }

        /// <summary>
        /// SMTC 播放状态变化事件处理
        /// </summary>
        private void OnSmtcPlaybackStateChanged(PlaybackState state)
        {
            Dispatcher.Invoke(() =>
            {
                UpdatePlayPauseIcon(state);
                
                // ===== 关键修复：播放状态变化时也更新媒体信息 =====
                // 歌曲切换时，SMTC 会先触发 PlaybackStateChanged（短暂变为 Changing），
                // 然后触发 MediaPropertiesChanged。但 SMTC.cs 没有暴露后者，
                // 所以我们在这里主动刷新媒体信息。
                Dispatcher.BeginInvoke(new Action(() => TryUpdateMediaUI()),
                    System.Windows.Threading.DispatcherPriority.Background);
            });
        }

        /// <summary>
        /// SMTC 会话变化事件处理
        /// </summary>
        private void OnSmtcSessionChanged(bool hasSession)
        {
            if (hasSession)
            {
                // 有新的媒体会话，更新 UI
                TryUpdateMediaUI();
            }
            else
            {
                // 没有媒体会话，恢复默认显示
                Dispatcher.Invoke(() =>
                {
                    _lastTitle = string.Empty;
                    SongTitle.Text = "未在播放";
                    SongArtist.Text = "未知艺术家";
                    PlayPauseIcon.Text = "▶️";
                });
            }
        }

        /// <summary>
        /// 上一首按钮点击处理（async void + Task.Run 避免死锁）
        /// </summary>
        private async void OnPreviousClicked(object sender, RoutedEventArgs e)
        {
            if (_smtcController == null || !_smtcInitialized) return;

            try
            {
                // 在后台线程执行 SMTC 操作，避免 UI 线程死锁
                await Task.Run(() => _smtcController.SwitchTrack(TrackDirection.Previous));
                
                // 延迟一小段时间后更新媒体信息
                await Task.Delay(500);
                TryUpdateMediaUI();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"上一首操作失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 播放/暂停按钮点击处理（async void + Task.Run 避免死锁）
        /// </summary>
        private async void OnPlayPauseClicked(object sender, RoutedEventArgs e)
        {
            if (_smtcController == null || !_smtcInitialized) return;

            try
            {
                // 在后台线程执行 SMTC 操作，避免 UI 线程死锁
                await Task.Run(() => _smtcController.TogglePlayPause());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"播放/暂停操作失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 下一首按钮点击处理（async void + Task.Run 避免死锁）
        /// </summary>
        private async void OnNextClicked(object sender, RoutedEventArgs e)
        {
            if (_smtcController == null || !_smtcInitialized) return;

            try
            {
                // 在后台线程执行 SMTC 操作，避免 UI 线程死锁
                await Task.Run(() => _smtcController.SwitchTrack(TrackDirection.Next));
                
                // 延迟一小段时间后更新媒体信息
                await Task.Delay(500);
                TryUpdateMediaUI();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"下一首操作失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 清理 SMTC 资源
        /// </summary>
        private void CleanupSMTC()
        {
            if (_smtcController != null)
            {
                _smtcController.OnPlaybackStateChanged -= OnSmtcPlaybackStateChanged;
                _smtcController.OnSessionChanged -= OnSmtcSessionChanged;
                _smtcController.Dispose();
                _smtcController = null;
            }
            _smtcInitialized = false;
            _lastTitle = string.Empty;
        }
    }
}