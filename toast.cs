using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json.Serialization;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace Notifier
{
    // Event-driven replacement for polling-based notification retrieval.
    public class ToastNotificationListener
    {
        // 过滤关键词改为从设置读取，并缓存成数组：
        // 通知到达是高频路径，不该每条都 Split 一次字符串。设置变更时才重建缓存。
        private static string[] _filters = Array.Empty<string>();
        private static readonly object _filterLock = new();
        private static bool _filterSubscribed;

        private static void EnsureFilterSubscription()
        {
            if (_filterSubscribed) return;
            _filterSubscribed = true;

            try
            {
                App.setting.ValueChanged += key =>
                {
                    if (string.Equals(key, Settings_Manager.KFilterKeywords, StringComparison.Ordinal))
                        RefreshFilters();
                };
            }
            catch { }

            RefreshFilters();
        }

        private static void RefreshFilters()
        {
            try
            {
                var raw = App.setting?.FilterKeywords ?? string.Empty;
                var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                lock (_filterLock) _filters = parts;
            }
            catch
            {
                lock (_filterLock) _filters = Array.Empty<string>();
            }
        }

        private static bool IsFiltered(string title, string body)
        {
            string[] filters;
            lock (_filterLock) filters = _filters;

            if (filters.Length == 0) return false;

            for (int i = 0; i < filters.Length; i++)
            {
                var kw = filters[i];
                if (kw.Length == 0) continue;

                // OrdinalIgnoreCase：免分配，且不走文化敏感比较（后者对短字符串开销明显）
                if (title.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                    body.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private UserNotificationListener? _listener;
        private uint _lastNotificationId;
        private bool _initialized;
        private ToastData? _pendingToast;
        private readonly object _toastLock = new();

        // Fired when a new toast of interest is detected
        public event Action<ToastData>? OnToastDetected;

        // Initialize and attach event handlers. Requests user permission if needed.
        public async Task<(bool Success, string Message)> InitializeAsync()
        {
            try
            {
                EnsureFilterSubscription();
                _listener = UserNotificationListener.Current;
                var accessStatus = await _listener.RequestAccessAsync();

                if (accessStatus == UserNotificationListenerAccessStatus.Allowed)
                {
                    var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                    if (notifications != null && notifications.Count > 0)
                    {
                        _lastNotificationId = notifications.Max(n => n.Id);
                    }

                    // subscribe to change events
                    _listener.NotificationChanged += Listener_NotificationChanged;
                    _initialized = true;
                    return (true, "通知访问权限已获取");
                }
                else
                {
                    return (false, $"无法获取通知访问权限: {accessStatus}，请前往 设置 > 隐私和安全性 > 通知 允许此应用访问通知");
                }
            }
            catch (Exception ex)
            {
                return (false, $"初始化失败: {ex.Message}");
            }
        }

        // Stop listening and detach event handlers
        public void StopListening()
        {
            try
            {
                if (_listener != null)
                {
                    _listener.NotificationChanged -= Listener_NotificationChanged;
                }
            }
            catch { }
        }

        // Backwards-compatible helper: return the latest detected toast (set by event handler)
        public Task<(ToastData? Data, string? Message)> FetchLatestNotificationAsync()
        {
            lock (_toastLock)
            {
                if (!_initialized || _listener == null)
                    return Task.FromResult<(ToastData?, string?)>((null, "监听器未初始化"));

                var data = _pendingToast;
                _pendingToast = null; // consume
                return Task.FromResult<(ToastData?, string?)>((data, null));
            }
        }

        // Event callback from UserNotificationListener
        private async void Listener_NotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
        {
            try
            {
                if (args.ChangeKind != UserNotificationChangedKind.Added)
                    return; // only care about new toasts

                // 直接用事件自带的 Id 取单条通知：
                // 旧实现会在这次回调里 GetNotificationsAsync 拉全量列表再 OrderByDescending 排序，
                // 通知越多越慢，而且是跨进程 WinRT 调用。GetNotification(id) 是 O(1)。
                uint id = args.UserNotificationId;
                UserNotification? notif = null;

                if (id > 0)
                {
                    try { notif = sender.GetNotification(id); }
                    catch { notif = null; }
                }

                // 兜底：个别系统上 GetNotification 可能取不到，退回全量拉取（线性找最大 Id，不再排序）
                if (notif == null)
                    notif = await TryGetLatestByScanAsync(sender);

                if (notif == null)
                    return;

                if (!_initialized)
                {
                    _lastNotificationId = notif.Id;
                    _initialized = true;
                    return;
                }

                if (notif.Id <= _lastNotificationId)
                    return; // already seen or duplicate

                var toastData = ExtractToastData(notif);
                if (toastData == null)
                    return;

                toastData.NotificationId = notif.Id;
                toastData.InternalNotification = notif;
                _lastNotificationId = notif.Id;

                lock (_toastLock)
                {
                    _pendingToast = toastData;
                }

                OnToastDetected?.Invoke(toastData);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"NotificationChanged handler error: {ex.Message}");
            }
        }

        // 仅在 GetNotification 不可用时使用：单次线性扫描取 Id 最大的通知（O(n)，无排序、无中间集合）
        private static async Task<UserNotification?> TryGetLatestByScanAsync(UserNotificationListener sender)
        {
            try
            {
                var notifications = await sender.GetNotificationsAsync(NotificationKinds.Toast);
                if (notifications == null || notifications.Count == 0)
                    return null;

                UserNotification? latest = null;
                foreach (var n in notifications)
                {
                    if (latest == null || n.Id > latest.Id)
                        latest = n;
                }
                return latest;
            }
            catch
            {
                return null;
            }
        }

        private ToastData? ExtractToastData(UserNotification notification)
        {
            try
            {
                var appInfo = notification.AppInfo;
                var displayInfo = appInfo?.DisplayInfo;

                string appName = displayInfo?.DisplayName ?? "系统通知";
                string aumid = appInfo?.AppUserModelId ?? string.Empty;

                var toastNotification = notification.Notification;
                var visual = toastNotification?.Visual;
                if (visual == null) return null;

                var binding = visual.GetBinding("ToastGeneric");
                if (binding == null) return null;

                var textElements = binding.GetTextElements();
                if (textElements == null || textElements.Count == 0) return null;

                string title = textElements[0]?.Text ?? string.Empty;
                string body = string.Join(" ", textElements.Skip(1).Select(t => t.Text));

                // 按设置的关键词过滤（默认仍是 微信 / WeChat）
                EnsureFilterSubscription();
                if (IsFiltered(title, body))
                    return null;

                return new ToastData
                {
                    AppName = appName,
                    Title = title,
                    Body = body,
                    Aumid = aumid,
                    InternalNotification = notification,
                    Time = DateTime.Now,
                    NotificationId = notification.Id,
                    ProcessName = appInfo?.AppUserModelId ?? appName
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ExtractToastData error: {ex.Message}");
                return null;
            }
        }

        public async Task ClearAllNotificationsAsync()
        {
            try
            {
                if (_listener == null) return;

                var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                if (notifications == null || notifications.Count == 0) return;

                var ids = new List<uint>(notifications.Count);
                foreach (var notif in notifications)
                    ids.Add(notif.Id);

                // 每条 RemoveNotification 都是一次跨进程 WinRT 调用，
                // 原来在 UI 线程上串行执行 N 次，通知多了会明显卡一下，挪到线程池。
                await Task.Run(() => RemoveIds(ids));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ClearNotifications error: {ex.Message}");
            }
        }

        private void RemoveIds(List<uint> ids)
        {
            foreach (var id in ids)
            {
                try { _listener?.RemoveNotification(id); }
                catch (Exception ex) { Debug.WriteLine($"删除通知失败: {ex.Message}"); }
            }
        }

        public void RemoveNotificationById(uint notificationId)
        {
            if (_listener != null)
            {
                try { _listener.RemoveNotification(notificationId); } catch (Exception ex)
                {
                    Debug.WriteLine($"删除通知失败: {ex.Message}");
                }
            }
        }
    }

    public class ToastData
    {
        public string AppName { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string Aumid { get; set; } = string.Empty;
        public uint NotificationId { get; set; }
        public DateTime Time { get; set; } = DateTime.Now;
        [JsonIgnore]
        public UserNotification? InternalNotification { get; set; }
        public string ProcessName { get; set; } = string.Empty;
    }

    public class ToastMessage
    {
        public string Title { get; set; } = string.Empty;
        public ObservableCollection<string> Bodies { get; set; } = new ObservableCollection<string>();
    }
}