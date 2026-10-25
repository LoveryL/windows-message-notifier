using System;
using System.Threading;
using Avalonia;

namespace Notifier
{
    public static class Program
    {
        // 单实例：本程序是常驻托盘工具，跑两份会出现两个托盘图标、
        // 两份通知监听、两份写同一个缓存文件（互相覆盖）。
        // MSIX 下从开始菜单/自启重复激活会真的再起一个进程，所以必须有这道闸。
        private const string SingleInstanceMutexName = @"Local\Notifier.SingleInstance";

        [STAThread]
        public static void Main(string[] args)
        {
            using var mutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);

            if (!createdNew)
            {
                // 已经有一个实例在跑：直接退出，不弹窗、不写日志（避免刷屏）。
                // 已运行实例会继续留在托盘里。
                return;
            }

            // 在 STA 线程上先把数据目录定下来：
            // 打包态要访问 WinRT 的 ApplicationData 拿 LocalFolder 路径，
            // 在这里（任何日志/配置访问之前）预热一次，后面的调用都命中缓存，
            // 不会出现"第一条日志写到别处"这种不一致。
            try { _ = AppEnvironment.DataFolder; } catch { }

            try
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            catch (Exception ex)
            {
                // Ensure any startup exception is written to console for diagnostics
                try { Console.Error.WriteLine(ex.ToString()); } catch { }
                throw;
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
        }

        public static AppBuilder BuildAvaloniaApp()
        {
            // Limit Avalonia to Windows backend only and configure Skia renderer
            //
            // 渲染后端（Avalonia 11.3 的 Win32PlatformOptions）：
            // - RenderingMode：首选 AngleEgl（ANGLE 把 GLES 翻译成 D3D11，走 GPU），
            //   失败时回退 Software（CPU 光栅化）。不写 Wgl/Vulkan：Wgl 依赖厂商驱动、
            //   Vulkan 在本项目上没有收益，只增加初始化失败的面。
            // - CompositionMode：首选 WinUIComposition（Windows.UI.Composition 合成），
            //   它才能配合 AngleEgl 提供透明窗口与高刷新率渲染；依次回退
            //   DirectComposition / RedirectionSurface 保证老系统也能起来。
            //   注意 LowLatencyDxgiSwapChain 延迟最低但不支持透明，本项目弹窗是透明的，不能用。
            // - 这两个数组其实就是 11.3 的默认值，这里显式写出来：
            //   一是表明"必须走 GPU"，二是以后升级 Avalonia 时若默认值变了会被立刻发现。
            return AppBuilder.Configure<App>()
                .UseWin32()
                .UseSkia()
                .With(new Win32PlatformOptions
                {
                    RenderingMode = new[] { Win32RenderingMode.AngleEgl, Win32RenderingMode.Software },
                    CompositionMode = new[]
                    {
                        Win32CompositionMode.WinUIComposition,
                        Win32CompositionMode.DirectComposition,
                        Win32CompositionMode.RedirectionSurface
                    }
                })
                .LogToTrace();
        }
    }
}
