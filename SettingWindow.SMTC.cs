using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

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
        private bool _smtcBusy = false; // 防止并发执行 SMTC 操作
        private bool _updatingMediaUi = false; // 合并并发的媒体信息刷新

        /// <summary>
        /// 初始化 SMTC（在窗口加载后调用）。
        ///
        /// 旧实现只初始化一次：失败后 _smtcInitialized 永远是 false，
        /// 而"SMTC 检测不到"往往只是**初始化的那一刻**系统还没准备好
        /// （刚开机、音频服务还没起来、播放器还没启动），之后就再也不试了。
        /// 这里改成失败后按 1s → 3s → 6s 退避重试，最多 3 次。
        /// </summary>
        private async void InitializeSMTC()
        {
            if (_smtcInitialized) return;

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    if (_smtcController == null)
                        _smtcController = new SMTCController();

                    await _smtcController.InitializeAsync();  // 异步初始化，避免 UI 线程死锁

                    // 订阅 SMTC 事件（-= 先兜一遍：重试路径下可能已订阅）
                    _smtcController.OnPlaybackStateChanged -= OnSmtcPlaybackStateChanged;
                    _smtcController.OnSessionChanged -= OnSmtcSessionChanged;
                    _smtcController.OnMediaPropertiesChanged -= OnSmtcMediaPropertiesChanged;

                    _smtcController.OnPlaybackStateChanged += OnSmtcPlaybackStateChanged;
                    _smtcController.OnSessionChanged += OnSmtcSessionChanged;
                    _smtcController.OnMediaPropertiesChanged += OnSmtcMediaPropertiesChanged;

                    // 尝试获取当前媒体信息并更新 UI
                    TryUpdateMediaUI();

                    _smtcInitialized = true;
                    Debug.WriteLine("SMTC 接入成功");
                    return;
                }
                catch (Exception ex)
                {
                    global::Avalonia.Controls.ToolTip.SetTip(BtnPlayPause, $"SMTC 初始化失败: {ex.Message}");
                    Debug.WriteLine($"SMTC 初始化失败（第 {attempt}/3 次）: {ex.Message}");

                    if (attempt < 3)
                        await Task.Delay(attempt == 1 ? 1000 : 3000);
                }
            }

            // 三次都失败：不影响主功能，仅提示
            Logger.Warn("SMTC 初始化最终失败，媒体控制不可用（不影响通知功能）");
        }

        /// <summary>
        /// 面板重新显示时调用：收起期间媒体信息可能已经变了（切歌、换播放器），
        /// 而那时刷新是被跳过的，这里强制补一次。
        /// </summary>
        public void RefreshMediaNow()
        {
            _lastTitle = string.Empty; // 清掉去重标记，确保本次一定写进 UI
            TryUpdateMediaUI();
        }

        /// <summary>
        /// 尝试更新媒体 UI（歌曲信息 + 播放状态）
        /// </summary>
        private async void TryUpdateMediaUI()
        {
            // 面板收起时这台机器还在跑：收到 SMTC 事件也直接丢弃，
            // 不可见的 UI 刷新只是白烧 CPU（切歌频繁时尤其明显）。
            if (!_visible) return;

            if (_smtcController == null || !_smtcController.HasActiveSession())
            {
                // 没有活跃会话，保持默认文本
                global::Avalonia.Controls.ToolTip.SetTip(BtnPlayPause, "暂无在播放的音频");
                PlayPauseIcon.Text = "\u25B6";
                return;
            }

            // 状态变化/会话变化可能短时间内连发，用标志位合并，避免并发拉取同一份媒体信息
            if (_updatingMediaUi) return;
            _updatingMediaUi = true;

            try
            {
                // 直接 await WinRT 异步接口，不再 Task.Run(() => 同步阻塞)：
                // 后者会白占一个线程池线程，且在 STA 上下文下有死锁风险。
                MediaInfo mediaInfo = await _smtcController.GetCurrentMediaInfoAsync();

                // 播放器刚起来时 TryGetMediaPropertiesAsync 常常先返回空标题，
                // 旧实现拿到空就直接把 UI 置成"未在播放"，于是看起来像"检测不到"。
                // 这里空标题时短暂等待后重试一次。
                if (string.IsNullOrEmpty(mediaInfo.Title))
                {
                    await Task.Delay(400);
                    mediaInfo = await _smtcController.GetCurrentMediaInfoAsync();
                }

                PlaybackState playbackState = _smtcController.GetPlaybackStatus();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // 只有标题发生变化时才更新（避免不必要的闪烁）
                    if (!string.IsNullOrEmpty(mediaInfo.Title) && mediaInfo.Title != _lastTitle)
                    {
                        _lastTitle = mediaInfo.Title;
                        SongTitle.Text = mediaInfo.Title;
                        SongArtist.Text = string.IsNullOrEmpty(mediaInfo.Artist) ? "未知艺术家" : mediaInfo.Artist;
                        Debug.WriteLine($"歌曲切换: {mediaInfo.Title} - {mediaInfo.Artist}");
                    }
                    else if (string.IsNullOrEmpty(mediaInfo.Title))
                    {
                        // 标题为空时恢复默认
                        _lastTitle = string.Empty;
                        SongTitle.Text = "未在标题";
                        SongArtist.Text = "未知艺术家";
                    }

                    // 更新播放/暂停图标
                    UpdatePlayPauseIcon(playbackState);
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新媒体 UI 失败: {ex.Message}");
            }
            finally
            {
                _updatingMediaUi = false;
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
                    // Use plain unicode glyphs (no emoji variation selectors) to avoid colored/black-box rendering
                    PlayPauseIcon.Text = "\u23F8";  // ⏸ pause symbol (monochrome)
                    global::Avalonia.Controls.ToolTip.SetTip(BtnPlayPause, "暂停");
                    break;
                //case PlaybackState.Paused:
                //case PlaybackState.Stopped:
                default:
                    PlayPauseIcon.Text = "\u25B6";  // ▶ play symbol (monochrome)
                    global::Avalonia.Controls.ToolTip.SetTip(BtnPlayPause, "播放");
                    break;
            }

        }

        /// <summary>
        /// SMTC 播放状态变化事件处理
        /// </summary>
        private void OnSmtcPlaybackStateChanged(PlaybackState state)
        {
            Dispatcher.UIThread.Post(() => TryUpdateMediaUI());
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
                Dispatcher.UIThread.Post(() =>
                {
                    _lastTitle = string.Empty;
                    SongTitle.Text = "未在播放";
                    SongArtist.Text = "";
                    PlayPauseIcon.Text = "\u25B6";
                });
            }
        }

        /// <summary>
        /// 媒体属性变化（切歌）：立即刷新，省掉固定等待
        /// </summary>
        private void OnSmtcMediaPropertiesChanged()
        {
            // 该事件由 WinRT 回调触发，不一定在 UI 线程
            Dispatcher.UIThread.Post(() => TryUpdateMediaUI());
        }

        /// <summary>
        /// 上一首按钮点击处理（async void + Task.Run 避免死锁）
        /// </summary>
        private async void OnPreviousClicked(object? sender, RoutedEventArgs e)
        {
            if (_smtcController == null || !_smtcInitialized || _smtcBusy) return;
            _smtcBusy = true;
            try
            {
                // 直接 await 异步接口，不再占用线程池线程做同步阻塞等待
                await _smtcController.SwitchTrackAsync(TrackDirection.Previous);

                // 主要靠 MediaPropertiesChanged 事件刷新；这里只留一次短兜底，
                // 防止个别播放器不发属性变更事件导致 UI 不更新。
                await Task.Delay(250);
                TryUpdateMediaUI();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"上一首操作失败: {ex.Message}");
            }
            finally
            {
                _smtcBusy = false;
            }
        }

        /// <summary>
        /// 播放/暂停按钮点击处理（async void + Task.Run 避免死锁）
        /// </summary>
        private async void OnPlayPauseClicked(object? sender, RoutedEventArgs e)
        {
            if (_smtcController == null || !_smtcInitialized || _smtcBusy) return;
            _smtcBusy = true;
           try
{
    await _smtcController.TogglePlayPauseAsync();
    await Dispatcher.UIThread.InvokeAsync(() => TryUpdateMediaUI());
}
catch (Exception ex)
{
    Debug.WriteLine($"SMTC 操作失败: {ex.Message}");
}
finally
{
    _smtcBusy = false;
}
        }

        /// <summary>
        /// 下一首按钮点击处理（async void + Task.Run 避免死锁）
        /// </summary>
        private async void OnNextClicked(object? sender, RoutedEventArgs e)
        {
            if (_smtcController == null || !_smtcInitialized || _smtcBusy) return;
            _smtcBusy = true;
            try
            {
                // 直接 await 异步接口，不再占用线程池线程做同步阻塞等待
                await _smtcController.SwitchTrackAsync(TrackDirection.Next);

                // 同上：事件驱动刷新 + 短兜底
                await Task.Delay(250);
                TryUpdateMediaUI();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"下一首操作失败: {ex.Message}");
            }
            finally
            {
                _smtcBusy = false;
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
                _smtcController.OnMediaPropertiesChanged -= OnSmtcMediaPropertiesChanged;
                _smtcController.Dispose();
                _smtcController = null;
            }
            _smtcInitialized = false;
            _lastTitle = string.Empty;
        }
    }
}