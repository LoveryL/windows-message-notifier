using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

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

        // 只读快照缓存：删除/刷新时 GetAll() 会被频繁调用，
        // 旧实现每次都 _messages.ToArray() 复制整表（消息多时是几千次引用拷贝 + 一次大数组分配）。
        // 用版本号判断是否真的变了，没变就直接返回上次那份不可变快照。
        private static int _version;
        private static int _snapshotVersion = -1;
        private static ToastData[] _snapshot = Array.Empty<ToastData>();
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

            // 面板关闭时立刻落盘一次，保证数据不会因为异常退出而丢失。
            // 走后台：这个方法是从 UI 线程（关闭/失焦）调用的，同步写盘会卡一下界面。
            if (!visible)
                PersistNowBackground();
        }

        public static void Add(ToastData toast)
        {
            lock (_lock)
            {
                EnsureLoadedUnsafe();
                _messages.Add(toast);
                TrimExcessUnsafe();
                InvalidateUnsafe();
            }

            SchedulePersist();
            Changed?.Invoke();
        }

        public static IReadOnlyList<ToastData> GetAll()
        {
            lock (_lock)
            {
                EnsureLoadedUnsafe();

                if (_snapshotVersion != _version)
                {
                    _snapshot = _messages.Count == 0 ? Array.Empty<ToastData>() : _messages.ToArray();
                    _snapshotVersion = _version;
                }

                return _snapshot;
            }
        }

        /// <summary>标记数据已变更（调用方必须持有 _lock）。</summary>
        private static void InvalidateUnsafe() => _version++;

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
                InvalidateUnsafe();
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

        /// <summary>
        /// 立即把内存中的数据写到磁盘（**同步**，仅供退出时使用）。
        /// 不要在 UI 线程调用：JSON 序列化 + 文件写入是同步磁盘 I/O，
        /// 消息多时（几百条）会明显卡住界面。
        /// UI 路径请用 <see cref="PersistNowBackground"/>。
        /// </summary>
        public static void PersistNow()
        {
            List<ToastData>? snapshot;
            lock (_lock)
            {
                if (!_loaded) return;
                snapshot = new List<ToastData>(_messages);
            }

            WriteToDisk(snapshot);
        }

        /// <summary>在后台线程落盘：UI 线程只负责触发，不等待。</summary>
        public static void PersistNowBackground()
        {
            _ = Task.Run(() => PersistNow());
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
                InvalidateUnsafe();
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
            InvalidateUnsafe();
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

                // 文件现在是二进制（加密格式），不再按文本读
                byte[] bytes;
                using (var stream = new FileStream(StoreFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var length = (int)Math.Min(stream.Length, int.MaxValue);
                    bytes = new byte[length];
                    var read = stream.Read(bytes, 0, length);
                    if (read != length) Array.Resize(ref bytes, read);
                }

                if (bytes.Length == 0) return new List<ToastData>();

                // 自动识别：加密格式（有魔数）或旧版明文 JSON
                var json = SecureStore.TryRead(bytes);
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

                    // 落盘前加密：通知内容常含验证码等敏感信息，明文存盘等于对任何能读文件的程序敞开
                    var payload = SecureStore.Wrap(Encoding.UTF8.GetBytes(json));

                    // 先写临时文件再原子替换，避免写一半崩溃导致缓存文件损坏。
                    // 临时文件用 FileShare.None 独占打开，别的进程无法在写入期间把它读走。
                    var temp = StoreFilePath + ".tmp";
                    using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(payload, 0, payload.Length);
                        stream.Flush(true);
                    }

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
            // 打包态（MSIX）写 ApplicationData 的 LocalFolder，未打包写 %LOCALAPPDATA%\Notifier。
            // 不能写程序目录：MSIX 下 WindowsApps 是只读的。
            return Path.Combine(AppEnvironment.DataFolder, StoreFileName);
        }

        /// <summary>
        /// 本地消息文件的加密封装（Windows DPAPI）。
        ///
        /// 为什么需要它：原来 toast-messages.json 是**明文**存的，
        /// 通知的标题和正文常常包含验证码、私信片段、金额等敏感内容，
        /// 任何能读这个文件的程序（或把文件拷走的人）都能直接看到全部内容。
        ///
        /// 现在写入前用 CryptProtectData 加密，读取时 CryptUnprotectData 解密：
        /// - 密钥由 Windows 按**用户账户**派生并托管，不落在本程序的任何文件里；
        /// - 密文绑定本机 + 本用户：文件被拷到别的机器、或用别的账户登录都解不开；
        /// - 额外叠加一层 entropy（随机 32 字节，存在 HKCU），
        ///   就算有人直接调用 DPAPI 解密，缺了 entropy 也解不出来。
        ///
        /// 文件格式：魔数 "NTFM1" + DPAPI 密文（二进制，不再是文本文件）。
        /// 读取时先看魔数：命中就按密文解，没命中就按旧明文 JSON 解析 —— 老文件平滑迁移，
        /// 下一次写入自动升级成加密格式。
        ///
        /// 降级策略：DPAPI 在极少数环境（受损的用户配置文件、服务账户）会失败。
        /// 失败时退回明文写入并记录日志，**保证功能不中断** —— 缓存丢了可以重建，程序起不来不行。
        /// </summary>
        private static class SecureStore
        {
            // NTFM1 = Notifier Message Format 1
            private static readonly byte[] Magic = { (byte)'N', (byte)'T', (byte)'F', (byte)'M', (byte)'1' };

            private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;
            private const string RegSubKey = @"Software\Notifier";
            private const string RegValueName = "StoreEntropy";
            private const string EntropyFileName = "store.key";

            private static readonly object _sync = new();
            private static byte[]? _entropy;
            private static bool _dpapiUsable = true;
            private static int _degradedLogged;

            [StructLayout(LayoutKind.Sequential)]
            private struct DATA_BLOB
            {
                public int cbData;
                public IntPtr pbData;
            }

            [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CryptProtectData(
                ref DATA_BLOB dataIn, string? description, ref DATA_BLOB entropy,
                IntPtr reserved, IntPtr prompt, uint flags, ref DATA_BLOB dataOut);

            [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CryptUnprotectData(
                ref DATA_BLOB dataIn, IntPtr description, ref DATA_BLOB entropy,
                IntPtr reserved, IntPtr prompt, uint flags, ref DATA_BLOB dataOut);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr LocalFree(IntPtr hMem);

            /// <summary>加密并加上魔数头；失败时原样返回明文（降级不中断功能）。</summary>
            public static byte[] Wrap(byte[] plain)
            {
                var cipher = Protect(plain);
                if (cipher == null)
                {
                    LogDegradedOnce();
                    return plain;
                }

                var result = new byte[Magic.Length + cipher.Length];
                Buffer.BlockCopy(Magic, 0, result, 0, Magic.Length);
                Buffer.BlockCopy(cipher, 0, result, Magic.Length, cipher.Length);
                return result;
            }

            /// <summary>按魔数判断格式并还原成 JSON 文本；解密失败返回 null（不把密文当 JSON 喂给解析器）。</summary>
            public static string? TryRead(byte[] fileBytes)
            {
                if (HasMagic(fileBytes))
                {
                    var cipher = new byte[fileBytes.Length - Magic.Length];
                    Buffer.BlockCopy(fileBytes, Magic.Length, cipher, 0, cipher.Length);

                    var plain = Unprotect(cipher);
                    if (plain == null)
                    {
                        try
                        {
                            Logger.Warn("消息缓存解密失败（可能换了用户账户/重装了系统，或注册表里的密钥丢失），已按空列表启动");
                        }
                        catch { }

                        return null;
                    }

                    return Encoding.UTF8.GetString(plain);
                }

                // 没有魔数 = 旧版本的明文文件，直接按 UTF-8 读（下次写入会自动升级为加密格式）
                return Encoding.UTF8.GetString(fileBytes);
            }

            private static bool HasMagic(byte[] bytes)
            {
                if (bytes.Length < Magic.Length) return false;

                for (int i = 0; i < Magic.Length; i++)
                {
                    if (bytes[i] != Magic[i]) return false;
                }

                return true;
            }

            private static byte[]? Protect(byte[] plain)
            {
                if (!_dpapiUsable) return null;

                DATA_BLOB inBlob = default, entBlob = default, outBlob = default;
                try
                {
                    inBlob = AllocBlob(plain);
                    entBlob = AllocBlob(GetEntropy());

                    if (!CryptProtectData(ref inBlob, "Notifier message cache", ref entBlob,
                            IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                    {
                        _dpapiUsable = false;
                        return null;
                    }

                    return ReadBlob(outBlob);
                }
                catch (Exception ex)
                {
                    _dpapiUsable = false;
                    Debug.WriteLine($"DPAPI 加密失败: {ex.Message}");
                    return null;
                }
                finally
                {
                    FreeHGlobalBlob(inBlob);
                    FreeHGlobalBlob(entBlob);
                    LocalFreeBlob(outBlob);
                }
            }

            private static byte[]? Unprotect(byte[] cipher)
            {
                if (!_dpapiUsable) return null;

                DATA_BLOB inBlob = default, entBlob = default, outBlob = default;
                try
                {
                    inBlob = AllocBlob(cipher);
                    entBlob = AllocBlob(GetEntropy());

                    if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob,
                            IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                        return null;

                    return ReadBlob(outBlob);
                }
                catch
                {
                    return null;
                }
                finally
                {
                    FreeHGlobalBlob(inBlob);
                    FreeHGlobalBlob(entBlob);
                    LocalFreeBlob(outBlob);
                }
            }

            /// <summary>
            /// entropy：随机 32 字节，存在**数据目录下的 store.key**。
            ///
            /// 为什么从注册表改成文件：
            /// - 生命周期必须与密文一致。寄存器被清理（profile 重置、MSIX 虚拟化注册表）时，
            ///   entropy 丢失 = 所有历史消息永久解不开；而文件和数据同目录，同生同灭。
            /// - MSIX 打包态下注册表写入可能被虚拟化到私有 hive，行为随 manifest 的
            ///   unvirtualizedResources 声明而变，不可靠。
            /// - 安全性没有下降：entropy 与密文同目录，攻击者要拿也是一起拿走，
            ///   而 DPAPI 本身已绑定本机+本用户，拷到别处照样解不开。
            ///
            /// 迁移：旧版把 entropy 放在 HKCU\Software\Notifier，这里**先读旧值再落文件**，
            /// 保证老用户升级后历史消息仍然能解开（否则升级一次就全变乱码）。
            /// </summary>
            private static byte[] GetEntropy()
            {
                lock (_sync)
                {
                    if (_entropy != null) return _entropy;
                    _entropy = LoadOrCreateEntropy() ?? FallbackEntropy;
                    return _entropy;
                }
            }

            private static byte[]? LoadOrCreateEntropy()
            {
                try
                {
                    var path = Path.Combine(AppEnvironment.DataFolder, EntropyFileName);

                    if (File.Exists(path))
                    {
                        var bytes = File.ReadAllBytes(path);
                        if (bytes.Length >= 16) return bytes;
                    }

                    // 没有文件 → 先看旧版有没有把 entropy 存在注册表里（迁移路径）
                    var migrated = TryReadEntropyFromRegistry();

                    // 都没有 → 生成新的
                    var fresh = migrated ?? RandomNumberGenerator.GetBytes(32);

                    try
                    {
                        var dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        File.WriteAllBytes(path, fresh);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"写入 entropy 失败: {ex.Message}");
                    }

                    return fresh;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"读写 entropy 失败: {ex.Message}");
                    return null;
                }
            }

            /// <summary>旧版 entropy 存在注册表里，读一次用于迁移（读不到返回 null）。</summary>
            private static byte[]? TryReadEntropyFromRegistry()
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RegSubKey);
                    if (key?.GetValue(RegValueName) is byte[] existing && existing.Length >= 16)
                        return existing;
                }
                catch
                {
                    // 读不到就算了（未打包旧版可能从没写过，或权限受限）
                }

                return null;
            }

            private static readonly byte[] FallbackEntropy = Encoding.UTF8.GetBytes("Notifier.MessageCache.Entropy.v1");

            private static DATA_BLOB AllocBlob(byte[] data)
            {
                var blob = default(DATA_BLOB);
                var size = Math.Max(1, data.Length);
                blob.pbData = Marshal.AllocHGlobal(size);
                if (data.Length > 0) Marshal.Copy(data, 0, blob.pbData, data.Length);
                blob.cbData = data.Length;
                return blob;
            }

            private static byte[] ReadBlob(DATA_BLOB blob)
            {
                if (blob.pbData == IntPtr.Zero || blob.cbData <= 0) return Array.Empty<byte>();

                var buffer = new byte[blob.cbData];
                Marshal.Copy(blob.pbData, buffer, 0, blob.cbData);
                return buffer;
            }

            private static void FreeHGlobalBlob(DATA_BLOB blob)
            {
                if (blob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(blob.pbData);
            }

            // CryptProtectData / CryptUnprotectData 的输出必须用 LocalFree 释放
            private static void LocalFreeBlob(DATA_BLOB blob)
            {
                if (blob.pbData != IntPtr.Zero) LocalFree(blob.pbData);
            }

            private static void LogDegradedOnce()
            {
                if (Interlocked.Exchange(ref _degradedLogged, 1) == 1) return;

                try
                {
                    Logger.Warn("消息缓存加密不可用，已退回明文存储（DPAPI 在当前环境初始化失败）");
                }
                catch { }
            }
        }
    }
}
