using System;
using System.Collections.Generic;
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
    /// 演化过程：
    /// 1) 最早是"固定帧数 + Task.Delay(3~15ms)"：Windows 上 Task.Delay 的实际分辨率约 15ms，
    ///    写 3ms 也只会 15ms 一跳，5 帧 × 3ms 的"动画"实际只有 75ms 且只有 5 帧，又硬又卡；
    /// 2) 上一版改成"时间驱动 + 每条动画一个 DispatcherTimer"：时长精确了，但同时跑 N 条动画
    ///    就有 N 个定时器，各自唤醒、彼此不同步，弹窗滑入和边框变色会互相错帧；
    /// 3) 现在这一版：全局一个 Normal 优先级的定时器统一驱动所有补间。
    ///    - 每帧只唤醒一次，所有动画共享同一个时间基准，永远不会互相错帧；
    ///    - 空闲（无动画）时定时器自动停止，常驻开销为 0；
    ///    - 用共享 Stopwatch + 预分配对象，动画过程中零分配。
    ///
    /// 为什么用 Normal 而不是 Render 优先级（重要）：
    /// 曾经用过 DispatcherPriority.Render，想让动画值赶在这一帧渲染之前生效。
    /// 但 Avalonia 的合成器在没有脏区域要画时不会驱动 Render 优先级的任务，
    /// 于是出现"窗口刚 Show、合成器还没为它排渲染 → Render 定时器不触发 →
    /// 入场动画第一帧都没跑 → RootGrid.Opacity 永远是 0 → 弹窗看起来就是不显示"，
    /// 而且不报错、不崩、日志里什么都没有。Normal 优先级由 UI 线程无条件调度，不会有这个坑。
    ///
    /// 另外加了停滞兜底：万一 UI 线程被长时间占用（或未来又踩到类似的调度坑），
    /// 补间最多只会迟到 StallGuardMs，之后必定收敛到终态，绝不会卡在中间值上。
    /// </summary>
    internal static class UiTween
    {
        private const int DefaultFrameRate = 60;
        private const int MinFrameMs = 4;        // 高刷屏上限（250fps 足够，再快只是白烧 CPU）
        private const int MaxFrameMs = 33;       // 低配下限（30fps），避免慢机器上疯狂唤醒
        private const int StallGuardMs = 1500;   // 停滞兜底：迟到这么久就直接收敛到终态

        private static readonly List<Tween> _running = new(4);
        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static DispatcherTimer? _timer;

        private static double CurrentFrameMs()
        {
            int fps = DefaultFrameRate;
            try
            {
                var configured = App.setting?.FrameRate ?? 0;
                if (configured >= 15 && configured <= 240) fps = configured;
            }
            catch { }

            var ms = 1000.0 / fps;
            if (ms < MinFrameMs) ms = MinFrameMs;
            if (ms > MaxFrameMs) ms = MaxFrameMs;
            return ms;
        }

        /// <summary>当前是否有动画在跑（用于调试/跳过重绘密集操作）。</summary>
        public static bool IsAnimating => _running.Count > 0;

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

            var tween = new Tween(durationMs, apply, _clock.Elapsed.TotalMilliseconds);

            // 先应用起始状态，避免第一帧出现"跳变"
            tween.Apply(0.0);

            _running.Add(tween);
            EnsureTimer();
            return tween.Completion;
        }

        private static void EnsureTimer()
        {
            if (_timer != null)
            {
                if (!_timer.IsEnabled) _timer.Start();
                return;
            }

            // 用默认优先级（Normal）：Render 优先级在合成器空闲时可能得不到调度，
            // 会让动画永远停在起始值（表现为"窗口显示了但看不见"）。
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(CurrentFrameMs())
            };
            _timer.Tick += OnFrame;
            _timer.Start();
        }

        private static void StopTimer()
        {
            if (_timer == null) return;
            _timer.Stop();
        }

        private static void OnFrame(object? sender, EventArgs e)
        {
            double now = _clock.Elapsed.TotalMilliseconds;

            for (int i = _running.Count - 1; i >= 0; i--)
            {
                var tween = _running[i];
                tween.Step(now);
                if (tween.IsFinished) _running.RemoveAt(i);
            }

            if (_running.Count == 0) StopTimer();
        }

        private sealed class Tween
        {
            private readonly int _durationMs;
            private readonly Action<double> _apply;
            private readonly double _startMs;
            private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Tween(int durationMs, Action<double> apply, double startMs)
            {
                _durationMs = durationMs;
                _apply = apply;
                _startMs = startMs;
            }

            public bool IsFinished { get; private set; }

            public Task Completion => _tcs.Task;

            public void Step(double nowMs)
            {
                if (IsFinished) return;

                double elapsed = nowMs - _startMs;

                // 停滞兜底：UI 线程可能被别的事占住很久（或定时器没被调度），
                // 与其让控件停在半透明/半位移的中间态，不如直接收敛到终态。
                if (elapsed >= _durationMs + StallGuardMs)
                {
                    Apply(1.0);
                    IsFinished = true;
                    _tcs.TrySetResult();
                    return;
                }

                double t = elapsed / _durationMs;
                if (t >= 1.0)
                {
                    Apply(1.0);
                    IsFinished = true;
                    _tcs.TrySetResult();
                    return;
                }

                Apply(t);
            }

            public void Apply(double t)
            {
                try
                {
                    _apply(t);
                }
                catch (Exception ex)
                {
                    // 中途目标被销毁（如窗口已关闭）时直接收敛，不让异常冒泡到 UI 循环
                    Debug.WriteLine($"[UiTween] apply 异常: {ex.Message}");
                    if (!IsFinished)
                    {
                        IsFinished = true;
                        _tcs.TrySetResult();
                    }
                }
            }
        }
    }
}
