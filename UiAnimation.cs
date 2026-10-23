using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Notifier
{
    /// <summary>
    /// 常用缓动函数。全部是纯静态函数，调用无分配。
    /// 入参 t 为线性进度 [0,1]，返回值为"视觉进度"（允许 &gt;1，用于过冲回弹）。
    /// </summary>
    internal static class Easing
    {
        public static double Linear(double t) => t;

        /// <summary>先快后慢的自然减速，最通用。</summary>
        public static double OutCubic(double t) => 1 - Math.Pow(1 - t, 3);

        public static double OutQuad(double t) => 1 - (1 - t) * (1 - t);

        public static double OutSine(double t) => Math.Sin(t * Math.PI / 2);

        public static double InQuad(double t) => t * t;

        public static double InCubic(double t) => t * t * t;

        /// <summary>
        /// 末端轻微过冲后再回弹，就是"Q 弹"的手感。
        /// overshoot 越大回弹越明显（1.7 为标准值）。
        /// </summary>
        public static double OutBack(double t, double overshoot = 1.70158)
        {
            double c1 = overshoot;
            double c3 = c1 + 1;
            double u = t - 1;
            return 1 + c3 * u * u * u + c1 * u * u;
        }

        /// <summary>弱过冲版，适合位移很大的场景（如 420px 的侧边面板）。</summary>
        public static double OutBackSoft(double t) => OutBack(t, 0.55);
    }

    /// <summary>
    /// UI 补间动画引擎。
    ///
    /// 旧实现是"固定帧数 + Task.Delay(3~15ms)"：
    ///   1) Task.Delay 在 Windows 上的实际分辨率约 15ms，写 3ms 也只会 15ms 一跳，
    ///      于是 5 帧 × 3ms 的"动画"实际只有 75ms 且只有 5 帧，看起来又硬又卡；
    ///   2) 总时长不可控，机器越卡动画越慢；
    ///   3) 每帧都 new 一次画笔/变换对象，白白制造垃圾。
    ///
    /// 现在改为"时间驱动"：用 Stopwatch 计算真实进度，DispatcherTimer 在 UI 线程按
    /// ~60fps 驱动，时长精确可控，帧率与渲染节奏一致，且动画过程中零分配。
    /// </summary>
    internal static class UiTween
    {
        // 默认 ~60fps；实际帧率由设置决定（每次动画启动时读一次，不在每帧读取）。
        private const int DefaultFrameRate = 60;

        private static TimeSpan CurrentFrameInterval()
        {
            int fps = DefaultFrameRate;
            try
            {
                var configured = App.setting?.FrameRate ?? 0;
                if (configured >= 15 && configured <= 240) fps = configured;
            }
            catch { }

            return TimeSpan.FromMilliseconds(1000.0 / fps);
        }

        /// <summary>
        /// 在 UI 线程上执行一段补间。
        /// </summary>
        /// <param name="durationMs">动画总时长（毫秒），精确可控</param>
        /// <param name="apply">每帧回调，入参是线性进度 [0,1]，由调用方自行选用缓动函数</param>
        public static Task RunAsync(int durationMs, Action<double> apply)
        {
            if (apply == null) throw new ArgumentNullException(nameof(apply));

            if (durationMs <= 0)
            {
                apply(1.0);
                return Task.CompletedTask;
            }

            // 动画必须在 UI 线程跑；若调用方在后台线程，转投一次再继续
            if (!Dispatcher.UIThread.CheckAccess())
            {
                var bridge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        await RunAsync(durationMs, apply);
                        bridge.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        bridge.TrySetException(ex);
                    }
                });
                return bridge.Task;
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sw = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = CurrentFrameInterval() };

            void OnTick(object? sender, EventArgs e)
            {
                double t = sw.Elapsed.TotalMilliseconds / durationMs;

                if (t >= 1.0)
                {
                    timer.Tick -= OnTick;
                    timer.Stop();
                    try { apply(1.0); }
                    catch (Exception ex) { Debug.WriteLine($"[UiTween] apply 异常: {ex.Message}"); }
                    tcs.TrySetResult();
                    return;
                }

                try
                {
                    apply(t);
                }
                catch (Exception ex)
                {
                    // 中途目标被销毁（如窗口已关闭）时直接收敛，不让异常冒泡到 UI 循环
                    Debug.WriteLine($"[UiTween] apply 异常: {ex.Message}");
                    timer.Tick -= OnTick;
                    timer.Stop();
                    tcs.TrySetResult();
                }
            }

            // 先应用起始状态，避免第一帧出现"跳变"
            try { apply(0.0); } catch { }

            timer.Tick += OnTick;
            timer.Start();
            return tcs.Task;
        }
    }
}
