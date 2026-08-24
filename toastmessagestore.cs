using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
namespace Notifier
{
    public static class ToastMessageStore
    {
        private static readonly List<ToastData> _unreadMessages = new();
        private static readonly object _lock = new();

        public static int UnreadCount
        {
            get
            {
                lock (_lock)
                {
                    return _unreadMessages.Count;
                }
            }
        }

        public static ToastNotificationListener? Listener { get; set; }

        public static void Add(ToastData toast)
        {
            lock (_lock)
            {
                _unreadMessages.Add(toast);
            }
        }

        public static IReadOnlyList<ToastData> GetAll()
        {
            lock (_lock)
            {
                return _unreadMessages.ToList(); // 返回快照
            }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _unreadMessages.Clear();
            }
        }

        public static void RemoveAndSync(ToastData data)
        {
            lock (_lock)
            {
                for (int i = _unreadMessages.Count - 1; i >= 0; i--)
                {
                    if (_unreadMessages[i] == data)
                    {
                        _unreadMessages.RemoveAt(i);
                        break;
                    }
                }

                var listener = Listener;
                if (listener != null && data.NotificationId > 0)
                {
                    try
                    {
                        listener.RemoveNotificationById(data.NotificationId);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"从通知中心删除通知失败: {ex.Message}");
                    }
                }
            }
        }

        public static void RemoveByTitleAndSync(string title)
        {
            lock (_lock)
            {
                for (int i = _unreadMessages.Count - 1; i >= 0; i--)
                {
                    if (_unreadMessages[i].Title == title)
                    {
                        var item = _unreadMessages[i];
                        _unreadMessages.RemoveAt(i);

                        var listener = Listener;
                        if (listener != null && item.NotificationId > 0)
                        {
                            try
                            {
                                listener.RemoveNotificationById(item.NotificationId);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"从通知中心删除通知失败: {ex.Message}");
                            }
                        }
                    }
                }
            }
        }
    }
}