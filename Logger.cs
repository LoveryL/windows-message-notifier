using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Notifier
{
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static readonly string LogPath = ResolveLogPath();

        private static string ResolveLogPath()
        {
            string dir;
            try
            {
                // 打包态（AppX/MSIX）走 WinRT 专属目录
                if (IsPackaged())
                {
                    dir = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
                }
                else
                {
                    // 未打包：%LocalAppData%\Notifier
                    dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Notifier");
                }
            }
            catch
            {
                // 极端兜底：临时目录
                dir = Path.Combine(Path.GetTempPath(), "Notifier");
            }

            try { Directory.CreateDirectory(dir); } catch { }
            return Path.Combine(dir, "app.log");
        }

        private static bool IsPackaged()
        {
            try
            {
                uint len = 0;
                GetCurrentPackageFullName(ref len, null);
                var sb = new System.Text.StringBuilder((int)len);
                return GetCurrentPackageFullName(ref len, sb) == 0;
            }
            catch { return false; }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, System.Text.StringBuilder? packageFullName);

        public static void Debug(string msg) => Write("DEBUG", msg);
public static void Info(string msg) => Write("INFO", msg);
public static void Warn(string msg) => Write("WARN", msg);
public static void Error(string msg, Exception? ex = null)
    => Write("ERROR", ex == null ? msg : $"{msg} | {ex.GetType().Name}: {ex.Message}");

        private static void Write(string level, string msg)
        {
            try
            {
                lock (_lock)
                {
                    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {msg}{Environment.NewLine}";
                    File.AppendAllText(LogPath, line);
                }
            }
            catch {  }
        }
    }
}