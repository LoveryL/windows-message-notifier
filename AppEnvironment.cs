using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Notifier
{
    /// <summary>
    /// 运行环境适配：**打包态（MSIX / AppX）** 与 **未打包（直接跑 exe）** 的差异集中在这里。
    ///
    /// 打包态最主要的差异是"能往哪写"：
    /// - 未打包：<c>%LOCALAPPDATA%\Notifier</c>，随便写；
    /// - 打包态：MSIX 是沙箱化的full-trust 应用，正确做法是写
    ///   <c>ApplicationData.Current.LocalFolder</c>（卸载时由系统清理、升级时保留）。
    ///   虽然 manifest 里声明了 <c>unvirtualizedResources</c> 让文件系统不虚拟化，
    ///   直接写 %LOCALAPPDATA% 也能成功，但那样卸载后会留下垃圾、也不符合商店规范。
    ///
    /// 非打包态调用 ApplicationData.Current 会直接抛异常（没有包标识），
    /// 所以这里全程 try/catch，失败就退回 %LOCALAPPDATA%。
    /// </summary>
    public static class AppEnvironment
    {
        /// <summary>目录名（未打包时使用）。</summary>
        private const string AppFolderName = "Notifier";

        private static int _packaged = -1; // -1 未探测，0 否，1 是
        private static string? _dataFolder;

        /// <summary>当前是否运行在打包态（MSIX/AppX）。结果恒定，只探测一次并缓存。</summary>
        public static bool IsPackaged
        {
            get
            {
                var cached = Volatile.Read(ref _packaged);
                if (cached >= 0) return cached == 1;

                var result = 0;
                try
                {
                    var length = 0u;
                    GetCurrentPackageFullName(ref length, null);
                    var sb = new StringBuilder((int)length);
                    result = GetCurrentPackageFullName(ref length, sb) == 0 ? 1 : 0;
                }
                catch
                {
                    result = 0;
                }

                Volatile.Write(ref _packaged, result);
                return result == 1;
            }
        }

        /// <summary>
        /// 应用数据目录（配置、消息缓存、日志都放这里）。
        /// 打包态下是 ApplicationData 的 LocalFolder，否则是 %LOCALAPPDATA%\Notifier。
        /// </summary>
        public static string DataFolder
        {
            get
            {
                var cached = _dataFolder;
                if (cached != null) return cached;

                cached = ResolveDataFolder();

                try { Directory.CreateDirectory(cached); }
                catch { /* 建目录失败也先返回路径，调用方各有兜底 */ }

                // 从"未打包版"升级到 MSIX 时，把旧位置的数据搬过来，避免用户丢配置/消息
                try { MigrateLegacyData(cached); }
                catch { }

                _dataFolder = cached;
                return cached;
            }
        }

        private static string ResolveDataFolder()
        {
            if (IsPackaged)
            {
                try
                {
                    // 打包态专属目录：卸载即清理、升级保留
                    var path = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
                    if (!string.IsNullOrEmpty(path)) return path;
                }
                catch
                {
                    // 没有包标识 / WinRT 不可用 → 走下面的通用分支
                }
            }

            string dir;
            try
            {
                dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    AppFolderName);
            }
            catch
            {
                dir = Path.Combine(Path.GetTempPath(), AppFolderName);
            }

            return dir;
        }

        /// <summary>
        /// 迁移：打包态首次启动时，旧版（未打包）把数据写在 %LOCALAPPDATA%\Notifier，
        /// 而新位置在 Packages\...\LocalState。这里把已有文件复制过去，
        /// 让用户从旧版升级到 MSIX 时不丢配置和消息。
        /// 只复制不删除源文件（旧版可能还在用），且目标已存在时不覆盖。
        /// </summary>
        private static void MigrateLegacyData(string target)
        {
            if (!IsPackaged) return;

            string legacy;
            try
            {
                legacy = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    AppFolderName);
            }
            catch
            {
                return;
            }

            if (string.Equals(legacy, target, StringComparison.OrdinalIgnoreCase)) return;
            if (!Directory.Exists(legacy)) return;

            // 只搬这几个文件：配置、消息缓存、以及加密用的密钥
            var files = new[] { "config.json", "toast-messages.json", "store.key", "app.log", "app.old.log" };

            foreach (var name in files)
            {
                try
                {
                    var src = Path.Combine(legacy, name);
                    var dst = Path.Combine(target, name);

                    if (!File.Exists(src) || File.Exists(dst)) continue;

                    File.Copy(src, dst, false);
                }
                catch
                {
                    // 单个文件失败不影响其它文件，也不影响启动
                }
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, StringBuilder? packageFullName);
    }
}
