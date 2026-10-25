using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;

namespace Notifier;

/// <summary>
/// 音频控制接口（进程内唯一实现：<see cref="StaAudioController"/>）。
///
/// 为什么必须把 NAudio 的调用固定在一个 STA 线程上：
/// NAudio 的音频端点是 COM 对象，而 COM 对象具有"单元（apartment）亲和性"。
/// 线程池线程默认是 MTA，Avalonia 的 UI 线程是 STA。
/// 在 MTA 上创建端点、再到 STA 上调用（或反过来），接口指针无法跨单元直接使用，于是出现：
///   Unable to cast COM object of type 'System.__ComObject' to interface type
///   'NAudio.CoreAudioApi.Interfaces.IMMDevice' ... 不支持此接口 (0x80004002 E_NOINTERFACE)
/// 只要"创建 + 全部读写"都发生在同一个 STA 线程，这个错误就不会出现。
/// 顺带的好处：UI 线程不再同步等待 COM 调用，音量读取走缓存，拖动滑块不会卡界面。
/// </summary>
internal interface IAudioController : IDisposable
{
    /// <summary>缓存音量 0.0~1.0；端点尚未就绪时返回 -1（调用方据此保持 UI 原值）。</summary>
    float GetVolume();

    void SetVolume(float level);

    void Mute(bool mute);

    bool IsMuted { get; }

    /// <summary>COM 端点当前是否可用（不可用时不值得继续调用）。</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 是否轮询系统音量（仅在音量 UI 可见时打开）。
    /// 面板关着时不做任何 COM 调用，常驻开销为 0。
    /// </summary>
    void SetMonitoring(bool enabled);

    /// <summary>系统音量/静音发生变化时回调（volume 0~1、muted）。回调在音频 STA 线程上，订阅方需自行切回 UI 线程。</summary>
    event Action<float, bool>? StateChanged;
}

/// <summary>
/// 在专用 STA 线程上托管 NAudio 音频端点。
///
/// 设计要点：
/// 1. 专用后台 STA 线程 + Win32 消息泵：COM 对象的创建与全部调用都在这条线程上完成；
/// 2. 命令队列 + 单飞投递：UI 线程只负责入队，绝不阻塞；连续拖动滑块时多次写入在 STA 侧合并；
/// 3. 音量/静音读缓存：GetVolume() 是一次 volatile 读，零 COM、零跨线程等待；
/// 4. 失败自愈：端点创建/调用失败后按退避重试，彻底失败则停止调用并只记录一次日志，避免日志风暴；
/// 5. 退出时走 Shutdown 命令，保证 COM 对象在它所属的那条 STA 线程上释放。
/// </summary>
internal sealed class StaAudioController : IAudioController
{
    private const int MaxAttempts = 4;
    private const int PollIntervalMs = 500;
    private const int PumpWaitMs = 3000;

    private enum Cmd : byte { Initialize, ApplyVolume, ApplyMute, Refresh, Shutdown }

    private readonly object _gate = new();
    private readonly Queue<Cmd> _queue = new();
    private readonly ManualResetEventSlim _pumpReady = new(false);
    private int _scheduled;

    private System.Windows.Forms.Form? _pump;
    private System.Windows.Forms.ApplicationContext? _context;
    private System.Windows.Forms.Timer? _pollTimer;
    private Thread? _thread;

    private MMDevice? _device;
    private AudioEndpointVolume? _endpoint;
    private volatile bool _deviceReady;
    private volatile bool _gaveUp;
    private int _attempts;
    private System.Threading.Timer? _retryTimer;
    private bool _complained;
    private volatile bool _disposed;

    private volatile float _volume;
    private volatile bool _muted;
    private float _targetVolume;
    private bool _targetMuted;

    public event Action<float, bool>? StateChanged;

    public StaAudioController() => Start();

    // ===== 对外 API（任意线程调用） =====

    public bool IsAvailable => _deviceReady;

    public bool IsMuted => _muted;

    public float GetVolume() => _deviceReady ? _volume : -1f;

    public void SetVolume(float level)
    {
        if (_disposed) return;

        _targetVolume = Math.Clamp(level, 0f, 1f);
        Post(Cmd.ApplyVolume);
    }

    public void Mute(bool mute)
    {
        if (_disposed) return;

        _targetMuted = mute;
        Post(Cmd.ApplyMute);
    }

    /// <summary>打开/关闭音量轮询。仅在 STA 线程上生效，跨线程调用只是投递一条命令。</summary>
    public void SetMonitoring(bool enabled)
    {
        if (_disposed) return;

        TryInvokeOnAudioThread(() => SetMonitoringCore(enabled));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _retryTimer?.Dispose(); } catch { }
        _retryTimer = null;

        Post(Cmd.Shutdown);
    }

    // ===== STA 线程 =====

    private void Start()
    {
        _thread = new Thread(ThreadProc)
        {
            IsBackground = true,
            Name = "Notifier.AudioSTA"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // 等消息泵就绪，否则 BeginInvoke 可能抛"句柄尚未创建"
        _pumpReady.Wait(PumpWaitMs);
        Post(Cmd.Initialize);
    }

    private void ThreadProc()
    {
        // .NET 会为 STA 线程自动初始化 COM；这里显式调用一次，确保 apartment 类型明确为 STA
        try { CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED); }
        catch { }

        var context = new System.Windows.Forms.ApplicationContext();
        _context = context;

        var pump = new System.Windows.Forms.Form();
        _pump = pump;
        try { _ = pump.Handle; } catch { }   // 强制创建句柄，BeginInvoke 才可用
        _pumpReady.Set();

        try
        {
            System.Windows.Forms.Application.Run(context);
        }
        catch (Exception ex)
        {
            Logger.Warn($"音频线程消息循环退出: {ex.Message}");
        }
        finally
        {
            try { _pollTimer?.Dispose(); } catch { }
            _pollTimer = null;
            ReleaseDevice();
            try { pump.Dispose(); } catch { }
            _pump = null;
        }
    }

    private void Post(Cmd cmd)
    {
        var pump = _pump;
        lock (_gate) _queue.Enqueue(cmd);

        if (pump == null) return;

        // 单飞：同一时刻只允许一条待处理的泵消息，队列里堆积的命令由 Process 一次跑完
        if (Interlocked.CompareExchange(ref _scheduled, 1, 0) != 0) return;

        try
        {
            pump.BeginInvoke(new Action(Process));
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _scheduled, 0);
            Complain($"投递音频命令失败: {ex.Message}");
        }
    }

    private void TryInvokeOnAudioThread(Action action)
    {
        var pump = _pump;
        if (pump == null) return;

        try { pump.BeginInvoke(action); }
        catch (Exception ex) { Complain($"投递音频命令失败: {ex.Message}"); }
    }

    /// <summary>命令泵：在 STA 线程上串行执行队列里的全部命令。</summary>
    private void Process()
    {
        Volatile.Write(ref _scheduled, 0);

        while (true)
        {
            Cmd cmd;
            lock (_gate)
            {
                if (_queue.Count == 0) return;
                cmd = _queue.Dequeue();
            }

            switch (cmd)
            {
                case Cmd.Initialize:
                case Cmd.Refresh:
                    if (EnsureDevice()) SyncFromDevice();
                    break;

                case Cmd.ApplyVolume:
                    ApplyVolumeCore();
                    break;

                case Cmd.ApplyMute:
                    ApplyMuteCore();
                    break;

                case Cmd.Shutdown:
                    SetMonitoringCore(false);
                    ReleaseDevice();
                    try { _context?.ExitThread(); } catch { }
                    return;
            }
        }
    }

    // ===== COM 操作（只在本 STA 线程上执行） =====

    private bool EnsureDevice()
    {
        if (_deviceReady) return true;
        if (_gaveUp || _disposed) return false;

        try
        {
            var device = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var endpoint = device.AudioEndpointVolume;   // 这一步会 Activate 出 IAudioEndpointVolume
            _device = device;
            _endpoint = endpoint;
            _deviceReady = true;
            _attempts = 0;
            _complained = false;
            Logger.Info("音频端点已就绪（STA 线程）");
            return true;
        }
        catch (Exception ex)
        {
            _attempts++;
            Complain($"获取音频端点失败（第 {_attempts}/{MaxAttempts} 次）: {ex.Message}");

            if (_attempts >= MaxAttempts)
            {
                _gaveUp = true;
                Logger.Warn("音频控制不可用，已停止重试（不影响通知等其它功能）");
            }
            else
            {
                ScheduleRetry();
            }

            return false;
        }
    }

    private void SyncFromDevice()
    {
        var endpoint = _endpoint;
        if (endpoint == null) return;

        try
        {
            var v = endpoint.MasterVolumeLevelScalar;
            var m = endpoint.Mute;
            if (Math.Abs(v - _volume) > 0.0005f || m != _muted)
            {
                _volume = v;
                _muted = m;
                RaiseStateChanged();
            }
        }
        catch (Exception ex)
        {
            HandleComFailure(ex);
        }
    }

    private void ApplyVolumeCore()
    {
        if (!EnsureDevice()) return;

        var endpoint = _endpoint;
        if (endpoint == null) return;

        var target = _targetVolume;
        try
        {
            // 连续拖动滑块会塞进很多次写入：值没变就不打扰 COM
            if (Math.Abs(endpoint.MasterVolumeLevelScalar - target) < 0.0005f) return;

            endpoint.MasterVolumeLevelScalar = target;
            _volume = target;
            RaiseStateChanged();
        }
        catch (Exception ex)
        {
            HandleComFailure(ex);
        }
    }

    private void ApplyMuteCore()
    {
        if (!EnsureDevice()) return;

        var endpoint = _endpoint;
        if (endpoint == null) return;

        try
        {
            if (endpoint.Mute == _targetMuted) return;

            endpoint.Mute = _targetMuted;
            _muted = _targetMuted;
            RaiseStateChanged();
        }
        catch (Exception ex)
        {
            HandleComFailure(ex);
        }
    }

    private void SetMonitoringCore(bool enabled)
    {
        try
        {
            if (enabled)
            {
                if (_pollTimer == null)
                {
                    _pollTimer = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
                    _pollTimer.Tick += (_, _) => SyncFromDevice();
                }
                _pollTimer.Start();
                SyncFromDevice();
            }
            else
            {
                _pollTimer?.Stop();
            }
        }
        catch (Exception ex)
        {
            Complain($"切换音量轮询失败: {ex.Message}");
        }
    }

    private void HandleComFailure(Exception ex)
    {
        Complain($"音频端点调用失败: {ex.Message}");
        ReleaseDevice();

        if (_gaveUp || _disposed) return;
        ScheduleRetry();
    }

    private void ScheduleRetry()
    {
        try
        {
            var delay = 400 * (1 << Math.Min(_attempts, 4));
            _retryTimer?.Dispose();
            _retryTimer = new System.Threading.Timer(_ => Post(Cmd.Initialize), null, delay, Timeout.Infinite);
        }
        catch { }
    }

    private void ReleaseDevice()
    {
        _deviceReady = false;
        try { _endpoint = null; } catch { }

        var device = _device;
        _device = null;
        if (device == null) return;

        try { device.Dispose(); }
        catch (Exception ex) { Complain($"释放音频设备失败: {ex.Message}"); }
    }

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(_volume, _muted); }
        catch { }
    }

    /// <summary>失败只记一次，之后降级为静默：高频路径上的日志本身也是一种开销。</summary>
    private void Complain(string message)
    {
        if (_complained) return;
        _complained = true;
        Logger.Warn(message);
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    private const uint COINIT_APARTMENTTHREADED = 0x2;
}

/// <summary>工厂入口。</summary>
internal static class AudioNative
{
    public static IAudioController Create() => new StaAudioController();
}
