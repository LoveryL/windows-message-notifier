using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;


namespace Notifier
{
    /// <summary>
    /// 高性能日志：调用线程只负责"格式化 + 入队"，真正的磁盘写入由后台线程批量完成。
    /// 旧实现每写一行都要 File.AppendAllText（打开/写/关闭一次文件句柄），
    /// 当调用点在 UI 线程（如每条通知到达时）会造成明显卡顿，这里把它彻底移出关键路径。
    /// </summary>
    public static class Logger
    {
        private const int MaxPendingLines = 1024;          // 队列上限，防止异常刷日志时把内存打满
        private const long MaxLogFileBytes = 2L * 1024 * 1024; // 超过 2MB 轮转到 app.old.log
        private const int FlushIntervalMs = 1000;          // 兜底定时 flush，保证低频日志也能落盘
        private const int WriterBufferBytes = 64 * 1024;

        private static readonly object _sync = new();
        private static readonly object _ioLock = new();
        private static readonly Queue<string> _pending = new();
        private static readonly string LogPath = ResolveLogPath();

        private static StreamWriter? _writer;
        private static FileStream? _stream;
        private static bool _flushScheduled;
        private static int _dropped;
        private static System.Threading.Timer? _flushTimer;
        private static readonly WaitCallback FlushCallback = _ => Flush();

        // 日志开关由设置控制，缓存成字段：Write 是高频调用点，不该每次查设置字典
        private static volatile bool _enabled = true;
        private static bool _subscribed;

        private static void EnsureSubscription()
        {
            if (_subscribed) return;

            try
            {
                var s = App.setting;
                if (s == null) return; // 设置中心尚未就绪，下次再试（此时保持默认开启）

                _subscribed = true;
                _enabled = s.LogEnabled;

                s.ValueChanged += key =>
                {
                    if (string.Equals(key, Settings_Manager.KLogEnabled, StringComparison.Ordinal))
                        _enabled = s.LogEnabled;
                };
            }
            catch { }
        }

        public static void Debug(string msg) => Write("DEBUG", msg);
        public static void Info(string msg) => Write("INFO", msg);
        public static void Warn(string msg) => Write("WARN", msg);
        public static void Error(string msg, Exception? ex = null)
            => Write("ERROR", ex == null ? msg : $"{msg} | {ex.GetType().Name}: {ex.Message}");

        private static void Write(string level, string msg)
        {
            EnsureSubscription();
            if (!_enabled) return;

            string line;
            try
            {
                line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {msg}";
            }
            catch
            {
                return;
            }

            bool needSchedule;
            lock (_sync)
            {
                if (_pending.Count >= MaxPendingLines)
                {
                    _dropped++;
                    return;
                }

                _pending.Enqueue(line);

                // 兜底 flush 用"单次"定时器：有日志就续 1 秒，空闲时不再每秒空转唤醒一次。
                // 常驻托盘程序 99% 的时间是空闲的，周期定时器纯属浪费。
                _flushTimer ??= new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
                _flushTimer.Change(FlushIntervalMs, Timeout.Infinite);

                needSchedule = !_flushScheduled;
                if (needSchedule)
                    _flushScheduled = true;
            }

            // 已有 flush 在途时不再派生新的后台任务，避免并发写同一文件
            if (needSchedule)
                ThreadPool.UnsafeQueueUserWorkItem(FlushCallback, null);
        }

        /// <summary>立即把队列里尚未落盘的日志写出（应用退出时调用）。</summary>
        public static void Flush()
        {
            List<string> batch;
            int dropped;
            lock (_sync)
            {
                if (_pending.Count == 0)
                {
                    _flushScheduled = false;
                    return;
                }

                batch = new List<string>(_pending);
                _pending.Clear();
                dropped = _dropped;
                _dropped = 0;
                _flushScheduled = false;
            }

            lock (_ioLock)
            {
                try
                {
                    EnsureWriter();
                    if (_writer == null) return;

                    if (dropped > 0)
                        _writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [WARN] 日志队列已满，已丢弃 {dropped} 条日志");

                    for (int i = 0; i < batch.Count; i++)
                        _writer.WriteLine(batch[i]);

                    _writer.Flush();
                    RotateIfNeeded();
                }
                catch
                {
                    // 日志失败不能影响主流程
                }
            }
        }

        private static void EnsureWriter()
        {
            if (_writer != null) return;

            try
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                _stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.Read, WriterBufferBytes);
                _writer = new StreamWriter(_stream, new UTF8Encoding(false), WriterBufferBytes) { AutoFlush = false };
            }
            catch
            {
                _writer = null;
                _stream = null;
            }
        }

        private static void RotateIfNeeded()
        {
            try
            {
                if (_stream == null || _writer == null || _stream.Length < MaxLogFileBytes) return;

                _writer.Flush();
                _writer.Dispose();
                _writer = null;
                _stream.Dispose();
                _stream = null;

                var backup = Path.Combine(Path.GetDirectoryName(LogPath) ?? ".", "app.old.log");
                try { File.Copy(LogPath, backup, true); } catch { }
                try { File.Delete(LogPath); } catch { }

                EnsureWriter();
            }
            catch
            {
                // 忽略轮转失败
            }
        }

        private static string ResolveLogPath()
        {
            string dir;
            try
            {
                // 打包态（MSIX）与未打包统一由 AppEnvironment 决定，避免两处各写一套
                dir = AppEnvironment.DataFolder;
            }
            catch
            {
                // 极端兜底：临时目录
                dir = Path.Combine(Path.GetTempPath(), "Notifier");
            }

            try { Directory.CreateDirectory(dir); } catch { }
            return Path.Combine(dir, "app.log");
        }
    }
}
