using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Notifier
{
    /// <summary>
    /// 应用激活工具类：通过 AUMID（AppUserModelId）唤醒/启动发出通知的应用。
    /// 优先使用 WinRT PackageManager + AppListEntry.LaunchAsync；
    /// 失败则回退到 Shell COM IApplicationActivationManager。
    /// </summary>
    public static class AppActivator
    {
        /// <summary>
        /// 通过 AUMID 唤醒/启动对应的应用。
        /// </summary>
        /// <param name="aumid">应用用户模型 ID，例如 "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"</param>
        /// <returns>(是否成功, 说明信息)</returns>
        public static async Task<(bool Success, string Message)> active_app(string aumid)
        {
            if (string.IsNullOrWhiteSpace(aumid))
                return (false, "AUMID 为空，无法激活应用");

            // 方法 1：WinRT PackageManager + AppListEntry.LaunchAsync（推荐，UWP/WinUI 3）
            var winrtResult = await TryLaunchViaWinRT(aumid);
            if (winrtResult.Success)
                return winrtResult;

            // 方法 2：COM IApplicationActivationManager（兼容 .NET Framework / WinForms）
            var comResult = TryLaunchViaCom(aumid);
            if (comResult.Success)
                return comResult;

            return (false, $"无法通过 AUMID 唤醒应用：{aumid}\n" +
                          $"WinRT 错误：{winrtResult.Message}\n" +
                          $"COM 错误：{comResult.Message}");
        }

        #region WinRT 路径（PackageManager + AppListEntry）

        private static async Task<(bool Success, string Message)> TryLaunchViaWinRT(string aumid)
        {
            try
            {
                var packageManager = new Windows.Management.Deployment.PackageManager();
                var packages = packageManager.FindPackagesForUserWithPackageTypes(
                    null,
                    Windows.Management.Deployment.PackageTypes.Main |
                    Windows.Management.Deployment.PackageTypes.Optional);

                foreach (var package in packages)
                {
                    var entries = await package.GetAppListEntriesAsync();
                    foreach (var entry in entries)
                    {
                        if (string.Equals(entry.AppUserModelId, aumid, StringComparison.OrdinalIgnoreCase))
                        {
                            bool launched = await entry.LaunchAsync();
                            if (launched)
                                return (true, $"已通过 WinRT 唤醒应用：{entry.DisplayInfo?.DisplayName ?? aumid}");
                            else
                                return (false, "LaunchAsync 返回 false（用户可能取消了启动）");
                        }
                    }
                }

                return (false, "未在当前用户的已安装包中找到匹配的 AUMID");
            }
            catch (Exception ex)
            {
                return (false, $"WinRT 激活异常：{ex.Message}");
            }
        }

        #endregion

        #region COM 路径（IApplicationActivationManager）

        [ComImport]
        [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IApplicationActivationManager
        {
            IntPtr ActivateApplication(
                [In] string appUserModelId,
                [In] string arguments,
                [In] ActivateOptions options,
                [Out] out uint processId);

            IntPtr ActivateForFile(
                [In] string appUserModelId,
                [In] IntPtr itemArray,
                [In] string verb,
                [Out] out uint processId);

            IntPtr ActivateForProtocol(
                [In] string appUserModelId,
                [In] IntPtr itemArray,
                [Out] out uint processId);
        }

        [ComImport]
        [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
        private class ApplicationActivationManager { }

        [Flags]
        private enum ActivateOptions : uint
        {
            None = 0x00000000,
            DesignMode = 0x00000001,
            NoErrorUI = 0x00000002,
            NoSplashScreen = 0x00000004
        }

        private static (bool Success, string Message) TryLaunchViaCom(string aumid)
        {
            try
            {
                var clsid = new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C");
                var type = Type.GetTypeFromCLSID(clsid);
                if (type == null)
                    return (false, "无法获取 CLSID_ApplicationActivationManager 的类型");

                var activator = (IApplicationActivationManager?)Activator.CreateInstance(type);
                if (activator == null)
                    return (false, "创建 ApplicationActivationManager 实例失败");

                // arguments 传空字符串而非 null，避免 nullable 警告；语义等价
                int hr = (int)activator.ActivateApplication(
                    aumid,
                    string.Empty,
                    ActivateOptions.None,
                    out uint pid);

                if (hr == 0) // S_OK
                    return (true, $"已通过 COM 唤醒应用，进程 PID = {pid}");

                return (false, $"ActivateApplication 返回 HRESULT = 0x{hr:X8}");
            }
            catch (COMException comEx)
            {
                return (false, $"COM 异常：{comEx.Message} (HRESULT 0x{comEx.HResult:X8})");
            }
            catch (Exception ex)
            {
                return (false, $"COM 激活异常：{ex.Message}");
            }
        }

        #endregion
    }
}
