using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Notifier
{
    public static class ToastMessageStore
    {
        private static readonly List<ToastData> _memoryMessages = new();
        private static readonly List<ToastData> _diskMessages = new();
        private static readonly object _lock = new();
        private const string StoreFileName = "toast-messages.json";
        private static bool _summaryWindowVisible;

        public static bool IsSummaryWindowVisible
        {
            get
            {
                lock (_lock)
                {
                    return _summaryWindowVisible;
                }
            }
        }

        public static int UnreadCount
        {
            get
            {
                lock (_lock)
                {
                    return _summaryWindowVisible
                        ? _memoryMessages.Count
                        : LoadFromDiskUnsafe().Count;
                }
            }
        }

        public static ToastNotificationListener? Listener { get; set; }

        public static void SetSummaryWindowVisible(bool visible)
        {
            lock (_lock)
            {
                if (_summaryWindowVisible == visible)
                    return;

                if (visible)
                {
                    ReloadMemoryFromDiskUnsafe();
                    _summaryWindowVisible = true;
                    return;
                }

                _summaryWindowVisible = false;
                if (_memoryMessages.Count > 0)
                {
                    var snapshot = _memoryMessages.ToList();
                    SyncDiskMessagesUnsafe(snapshot);
                }
                ReleaseMemoryCacheUnsafe();
            }
        }

        public static void Add(ToastData toast)
        {
            lock (_lock)
            {
                if (_summaryWindowVisible)
                {
                    _memoryMessages.Add(toast);
                    return;
                }

                var list = LoadFromDiskUnsafe();
                list.Add(toast);
                SyncDiskMessagesUnsafe(list);
            }
        }

        public static IReadOnlyList<ToastData> GetAll()
        {
            lock (_lock)
            {
                if (_summaryWindowVisible)
                    return _memoryMessages.ToList();

                return LoadFromDiskUnsafe();
            }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _memoryMessages.Clear();
                _diskMessages.Clear();
                QueuePersistAsync(new List<ToastData>());
            }
        }

        public static void RemoveAndSync(ToastData data)
        {
            lock (_lock)
            {
                var list = _summaryWindowVisible ? _memoryMessages : LoadFromDiskUnsafe();
                var oldCount = list.Count;

                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (MatchesToast(list[i], data))
                    {
                        var item = list[i];
                        list.RemoveAt(i);

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

                if (oldCount != list.Count)
                {
                    if (_summaryWindowVisible)
                    {
                        QueuePersistAsync(_memoryMessages.ToList());
                    }
                    else
                    {
                        SyncDiskMessagesUnsafe(list.ToList());
                    }
                }
            }
        }

        public static void RemoveByTitleAndSync(string title)
        {
            lock (_lock)
            {
                var list = _summaryWindowVisible ? _memoryMessages : LoadFromDiskUnsafe();
                var oldCount = list.Count;

                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(string.IsNullOrWhiteSpace(list[i].Title) ? "新通知" : list[i].Title, title, StringComparison.Ordinal))
                    {
                        var item = list[i];
                        list.RemoveAt(i);

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

                if (oldCount != list.Count)
                {
                    if (_summaryWindowVisible)
                    {
                        QueuePersistAsync(_memoryMessages.ToList());
                    }
                    else
                    {
                        SyncDiskMessagesUnsafe(list.ToList());
                    }
                }
            }
        }

        private static void ReloadMemoryFromDiskUnsafe()
        {
            _memoryMessages.Clear();
            foreach (var item in LoadFromDiskUnsafe())
                _memoryMessages.Add(item);
        }

        private static void ReleaseMemoryCacheUnsafe()
        {
            _memoryMessages.Clear();
            _diskMessages.Clear();
        }

        private static bool MatchesToast(ToastData left, ToastData right)
        {
            if (left == null || right == null)
                return false;

            if (left.NotificationId > 0 && right.NotificationId > 0 && left.NotificationId == right.NotificationId)
                return true;

            if (!string.IsNullOrEmpty(left.Aumid) && !string.IsNullOrEmpty(right.Aumid) &&
                left.Aumid == right.Aumid && left.Title == right.Title && left.Body == right.Body)
                return true;

            return ReferenceEquals(left, right);
        }

        private static void SyncDiskMessagesUnsafe(IEnumerable<ToastData> items)
        {
            _diskMessages.Clear();
            _diskMessages.AddRange(items);
            QueuePersistAsync(_diskMessages.ToList());
        }

        private static void QueuePersistAsync(IEnumerable<ToastData> items)
        {
            var snapshot = items.ToList();
            _ = Task.Run(() => SaveToDiskUnsafe(snapshot));
        }

        private static List<ToastData> LoadFromDiskUnsafe()
        {
            var path = GetStoreFilePath();
            if (!File.Exists(path))
                return new List<ToastData>();

            try
            {
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                    return new List<ToastData>();

                var records = JsonSerializer.Deserialize<List<ToastData>>(json);
                return records ?? new List<ToastData>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载通知缓存失败: {ex.Message}");
                return new List<ToastData>();
            }
        }

        private static void SaveToDiskUnsafe(IEnumerable<ToastData> items)
        {
            try
            {
                var path = GetStoreFilePath();
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var json = JsonSerializer.Serialize(items.ToList(), new JsonSerializerOptions
                {
                    WriteIndented = false
                });

                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"保存通知缓存失败: {ex.Message}");
            }
        }

        private static string GetStoreFilePath()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Notifier", StoreFileName);
        }
    }
}