using System;
using System.Collections.Generic;
using System.Linq;

namespace Notifier
{
    public static class ToastMessageStore
    {
        private static readonly List<ToastData> _unreadMessages = new();

        public static int UnreadCount => _unreadMessages.Count;

        public static ToastNotificationListener? Listener { get; set; }

        public static void Add(ToastData toast)
        {
            _unreadMessages.Add(toast);
        }

        public static IReadOnlyList<ToastData> GetAll()
        {
            return _unreadMessages.ToList();
        }

        public static void Clear()
        {
            _unreadMessages.Clear();
        }

        public static void RemoveAndSync(ToastData data)
        {
            for (int i = _unreadMessages.Count - 1; i >= 0; i--)
            {
                if (_unreadMessages[i] == data)
                {
                    _unreadMessages.RemoveAt(i);
                    break;
                }
            }

            if (Listener != null && data.NotificationId > 0)
            {
                try
                {
                    Listener.RemoveNotificationById(data.NotificationId);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"从通知中心删除通知失败: {ex.Message}");
                }
            }
        }

        public static void RemoveByTitleAndSync(string title)
        {
            for (int i = _unreadMessages.Count - 1; i >= 0; i--)
            {
                if (_unreadMessages[i].Title == title)
                {
                    var item = _unreadMessages[i];
                    _unreadMessages.RemoveAt(i);
                    
                    if (Listener != null && item.NotificationId > 0)
                    {
                        try
                        {
                            Listener.RemoveNotificationById(item.NotificationId);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"从通知中心删除通知失败: {ex.Message}");
                        }
                    }
                }
            }
        }
    }
}