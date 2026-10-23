using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Notifier
{
    /// <summary>
    /// 通知消息存储。
    /// 
    /// 优化要点（旧实现的主要性能问题：每次读/写都要整文件反序列化 + 整文件覆写）：
    /// 1. 内存列表是唯一真相源，磁盘只作为持久化介质：加载只做一次（可后台预热），写盘合并成一次延迟落盘；
    /// 2. 所有同步磁盘 I/O、WinRT 调用都移出锁外，锁内只做 O(1)/O(n) 的内存操作；
    /// 3. UnreadCount 从"读文件 + JSON 反序列化"降级为"读一个 int"，UI 可以放心高频访问；
    /// 4. 写盘 debounce（500ms）+ 单飞，不再每条通知一个 Task.Run 抢同一个文件；
    /// 5. 复用 JsonSerializerOptions（每次 new 会重建元数据缓存，代价很高）并缓存文件路径；
    /// 6. 消息条数上限，避免长期运行后 JSON 无限膨胀。
    /// </summary>
    public static class ToastMessageStore
    {
        private const string StoreFileName = "toast-messages.json";
        // 上限与落盘延迟改为从设置读取（每条通知才读一次字典，开销可忽略；
        // 用户改了"落盘延迟"后下一次写入即生效）

        private static readonly object _lock = new();
        private static readonly object _ioLock = new();
        private static readonly List<ToastData> _messages = new();
        private static readonly string StoreFilePath = ResolveStoreFilePath();
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = false,
            DefaultBufferSize = 8192
        };

        private static bool _loaded;
        private static bool _summaryWindowVisible;
        private static System.Threading.Timer? _persistTimer;
        private static int _persistScheduled;

        /// <summary>消息集合发生变化（通知 UI 合并刷新，避免每条通知多次重建列表）。</summary>
        public static event Action? Changed;

        public static bool IsSummaryWindowVisible
        {
            get { lock (_lock) return _summaryWindowVisible; }
        }

        /// <summary>
        /// 旧实现会在这里读文件 + 反序列化，托盘/UI 每访问一次就是一次磁盘 I/O。
        /// 现在是纯内存计数。
        /// </summary>
        public static int UnreadCount
        {
            get { lock (_lock) { EnsureLoadedUnsafe(); return _messages.Count; } }
        }

        public static ToastNotificationListener? Listener { get; set; }

        /// <summary>后台预热：把首次读盘的开销从 UI 线程挪走。</summary>
        public static void WarmUp()
        {
            _ = Task.Run(() =>
            {
                lock (_lock)
                {
                    EnsureLoadedUnsafe();
                }
            });
        }

        public static void SetSummaryWindowVisible(bool visible)
        {
            lock (_lock)
            {
                if (_summaryWindowVisible == visible)
                    return;

                _summaryWindowVisible = visible;
                EnsureLoadedUnsafe();
            }

            // 面板关闭时立刻落盘一次，保证数据不会因为异常退出而丢失
            if (!visible)
                PersistNow();
        }

        public static void Add(ToastData toast)
        {
            lock (_lock)
            {
                EnsureLoadedUnsafe();
                _messages.Add(toast);
                TrimExcessUnsafe();
            }

            SchedulePersist();
            Changed?.Invoke();
        }

        public static IReadOnlyList<ToastData> GetAll()
        {
            lock (_lock)
            {
                EnsureLoadedUnsafe();
                return _messages.Count == 0 ? Array.Empty<ToastData>() : _messages.ToArray();
            }
        }

        /// <summary>
        /// 清空全部消息。
        /// </summary>
        /// <param name="removeFromNotificationCenter">
        /// 是否逐条从系统通知中心删除。调用方若已经做过 ClearAllNotificationsAsync，
        /// 传 false 可以避免 N 次无意义的跨进程调用。
        /// </param>
        public static void Clear(bool removeFromNotificationCenter = true)
        {
            uint[] ids;
            lock (_lock)
            {
                EnsureLoadedUnsafe();
                ids = removeFromNotificationCenter ? CollectIdsUnsafe(_messages) : Array.Empty<uint>();
                _messages.Clear();
            }

            if (ids.Length > 0)
                RemoveFromNotificationCenter(ids);

            SchedulePersist();
            Changed?.Invoke();
        }

        public static void RemoveAndSync(ToastData data)
        {
            RemoveCore(m => MatchesToast(m, data));
        }

        public static void RemoveByTitleAndSync(string title)
        {
            RemoveCore(m => string.Equals(
                string.IsNullOrWhiteSpace(m.Title) ? "新通知" : m.Title,
                title,
                StringComparison.Ordinal));
        }

        /// <summary>立即把内存中的数据写到磁盘（退出/关闭面板时调用）。</summary>
        public static void PersistNow()
        {
            List<ToastData> snapshot;
            lock (_lock)
            {
                if (!_loaded) return;
                snapshot = new List<ToastData>(_messages);
            }

            WriteToDisk(snapshot);
        }

        private static void RemoveCore(Func<ToastData, bool> predicate)
        {
            List<ToastData> removed = new();
            lock (_lock)
            {
                EnsureLoadedUnsafe();

                for (int i = _messages.Count - 1; i >= 0; i--)
                {
                    if (!predicate(_messages[i])) continue;
                    removed.Add(_messages[i]);
                    _messages.RemoveAt(i);
                }

                if (removed.Count == 0) return;
            }

            // WinRT 调用（通知中心删除）放在锁外，避免拖住其它线程
            RemoveFromNotificationCenter(CollectIds(removed));

            SchedulePersist();
            Changed?.Invoke();
        }

        /// <summary>消息上限（设置驱动）。每条通知才读一次，开销可忽略。</summary>
        private static int MaxMessages
        {
            get
            {
                try { return Math.Clamp(App.setting.MaxMessages, 20, 10000); }
                catch { return 500; }
            }
        }

        /// <summary>落盘延迟（设置驱动）。改完下一次写入即生效。</summary>
        private static int PersistDelayMs
        {
            get
            {
                try { return Math.Clamp(App.setting.PersistDelayMs, 50, 10000); }
                catch { return 500; }
            }
        }

        private static void TrimExcessUnsafe()
        {
            var max = MaxMessages;
            if (_messages.Count <= max) return;
            _messages.RemoveRange(0, _messages.Count - max);
        }

        private static uint[] CollectIdsUnsafe(List<ToastData> list)
        {
            var ids = new List<uint>(list.Count);
            foreach (var item in list)
            {
                if (item.NotificationId > 0) ids.Add(item.NotificationId);
            }
            return ids.ToArray();
        }

        private static uint[] CollectIds(List<ToastData> list) => CollectIdsUnsafe(list);

        private static void RemoveFromNotificationCenter(uint[] ids)
        {
            if (ids.Length == 0) return;

            var listener = Listener;
            if (listener == null) return;

            _ = Task.Run(() =>
            {
                foreach (var id in ids)
                {
                    try { listener.RemoveNotificationById(id); }
                    catch (Exception ex) { Debug.WriteLine($"从通知中心删除通知失败: {ex.Message}"); }
                }
            });
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

        private static void EnsureLoadedUnsafe()
        {
            if (_loaded) return;
            _loaded = true;

            var records = LoadFromDisk();
            _messages.Clear();
            _messages.AddRange(records);
            TrimExcessUnsafe();
        }

        private static void SchedulePersist()
        {
            // 已经有一个待执行的落盘任务就复用它（500ms 内的多次变更合并成一次写）
            if (Interlocked.Exchange(ref _persistScheduled, 1) == 1) return;

            _persistTimer ??= new System.Threading.Timer(_ =>
            {
                Volatile.Write(ref _persistScheduled, 0);
                PersistNow();
            }, null, Timeout.Infinite, Timeout.Infinite);

            _persistTimer.Change(PersistDelayMs, Timeout.Infinite);
        }

        private static List<ToastData> LoadFromDisk()
        {
            try
            {
                if (!File.Exists(StoreFilePath)) return new List<ToastData>();

                var json = File.ReadAllText(StoreFilePath);
                if (string.IsNullOrWhiteSpace(json)) return new List<ToastData>();

                return JsonSerializer.Deserialize<List<ToastData>>(json, SerializerOptions) ?? new List<ToastData>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载通知缓存失败: {ex.Message}");
                return new List<ToastData>();
            }
        }

        private static void WriteToDisk(List<ToastData> items)
        {
            lock (_ioLock)
            {
                try
                {
                    var directory = Path.GetDirectoryName(StoreFilePath);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                    var json = JsonSerializer.Serialize(items, SerializerOptions);

                    // 先写临时文件再原子替换，避免写一半崩溃导致缓存文件损坏
                    var temp = StoreFilePath + ".tmp";
                    File.WriteAllText(temp, json);
                    File.Move(temp, StoreFilePath, true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"保存通知缓存失败: {ex.Message}");
                }
            }
        }

        private static string ResolveStoreFilePath()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Notifier", StoreFileName);
        }
    }
}
