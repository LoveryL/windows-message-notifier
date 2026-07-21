using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using System.Collections.ObjectModel;

namespace Notifier
{
    public class ToastNotificationListener
    {
        private UserNotificationListener? _listener;
        private uint _lastNotificationId;
        private bool _initialized;
        
        public event Action<ToastData>? OnToastDetected;
        
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
        
        public async Task<(ToastData? Data, string? Message)> FetchLatestNotificationAsync()
        {
            if (_listener == null)
                return (null, "监听器未初始化");
            
            try
            {
                var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                
                if (notifications == null || notifications.Count == 0)
                    return (null, null);
                
                var latestNotif = notifications.OrderByDescending(n => n.Id).First();
                uint maxId = latestNotif.Id;
                
                if (maxId == 0)
                    return (null, null);
                
                if (!_initialized)
                {
                    _lastNotificationId = maxId;
                    _initialized = true;
                    return (null, "首次初始化完成");
                }
                
                if (maxId > _lastNotificationId)
                {
                    _lastNotificationId = maxId;
                    
                    var toastData = ExtractToastData(latestNotif);
                    
                    if (toastData != null)
                    {
                        OnToastDetected?.Invoke(toastData);
                        return (toastData, null);
                    }
                }
                
                return (null, null);
            }
            catch (Exception ex)
            {
                return (null, $"获取通知失败: {ex.Message}");
            }
        }
        
        private ToastData? ExtractToastData(UserNotification notification)
        {
            try
            {
                var appInfo = notification.AppInfo;
                var displayInfo = appInfo?.DisplayInfo;
                
                string appName = displayInfo?.DisplayName ?? "系统通知";
                string aumid = appInfo?.AppUserModelId ?? "";
                
                var toastNotification = notification.Notification;
                var visual = toastNotification?.Visual;
                
                if (visual == null)
                    return null;
                
                var binding = visual.GetBinding("ToastGeneric");
                
                if (binding == null)
                    return null;
                
                var textElements = binding.GetTextElements();
                
                if (textElements == null || textElements.Count == 0)
                    return null;
                
                string title = textElements[0]?.Text ?? "";
                string body = string.Join(" ", textElements.Skip(1).Select(t => t.Text));
                
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
                    Aumid = aumid
                };
            }
            catch
            {
                return null;
            }
        }
    }
    
    public class ToastData
    {
        public string AppName { get; set; } = "";
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public string Aumid { get; set; } = "";
    }
    public class ToastMessage
    {
        public string Title { get; set; } = "";
        public ObservableCollection<string> Bodies { get; set; } = new ObservableCollection<string>();
    }
}