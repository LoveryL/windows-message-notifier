using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace Notifier
{
    // Event-driven replacement for polling-based notification retrieval.
    public class ToastNotificationListener
    {
        private UserNotificationListener? _listener;
        private uint _lastNotificationId;
        private bool _initialized;
        private ToastData? _pendingToast;

        // Fired when a new toast of interest is detected
        public event Action<ToastData>? OnToastDetected;

        // Initialize and attach event handlers. Requests user permission if needed.
        public async Task<(bool Success, string Message)> InitializeAsync()
        {
            try
            {
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
            if (!_initialized || _listener == null)
                return Task.FromResult<(ToastData?, string?)>((null, "监听器未初始化"));

            var data = _pendingToast;
            _pendingToast = null; // consume
            if (data != null) return Task.FromResult<(ToastData?, string?)>((data, null));
            return Task.FromResult<(ToastData?, string?)>((null, null));
        }

        // Event callback from UserNotificationListener
        private async void Listener_NotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
        {
            try
            {
                if (args.ChangeKind != UserNotificationChangedKind.Added)
                    return; // only care about new toasts

                // Get current toast notifications and pick latest by id.
                var notifications = await sender.GetNotificationsAsync(NotificationKinds.Toast);
                if (notifications == null || notifications.Count == 0)
                    return;

                var notif = notifications.OrderByDescending(n => n.Id).First();
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
                _pendingToast = toastData;
                OnToastDetected?.Invoke(toastData);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"NotificationChanged handler error: {ex.Message}");
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

                // filter out WeChat noisy notifications as before
                if (title.Contains("微信") || title.Contains("WeChat") ||
                    body.Contains("微信") || body.Contains("WeChat"))
                {
                    return null;
                }

                return new ToastData
                {
                    AppName = appName,
                    Title = title,
                    Body = body,
                    Aumid = aumid,
                    InternalNotification = notification,
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
                if (_listener != null)
                {
                    var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                    if (notifications != null)
                    {
                        foreach (var notif in notifications)
                        {
                            try { _listener.RemoveNotification(notif.Id); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ClearNotifications error: {ex.Message}");
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
        public UserNotification? InternalNotification { get; set; }
        public string ProcessName { get; set; } = string.Empty;
    }

    public class ToastMessage
    {
        public string Title { get; set; } = string.Empty;
        public ObservableCollection<string> Bodies { get; set; } = new ObservableCollection<string>();
    }
}