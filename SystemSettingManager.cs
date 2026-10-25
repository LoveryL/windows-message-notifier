using System;

namespace Notifier;

public enum BrightnessCapability
{
    NotSupported,
    SoftwareOnly,
    Hardware
}

public sealed class SystemSettingsManager : IDisposable
{
    // 进程内共享：音频端点（COM）与亮度能力探测（WMI）都只需要做一次。
    private static readonly Lazy<SystemSettingsManager> _shared = new(() => new SystemSettingsManager());
    public static SystemSettingsManager Shared => _shared.Value;

    /// <summary>共享实例是否已被创建（退出时只在真正用过时才释放）。</summary>
    public static bool IsSharedCreated => _shared.IsValueCreated;

    private readonly IAudioController? _audio;
    private bool _disposed;

    // 亮度能力探测要走一次 WMI（几十毫秒），结果不会变。
    // 旧实现在构造函数里同步探测，等于把这段耗时钉死在启动/首次开窗的关键路径上；
    // 现在改为后台预热 + 首次取值时兜底，构造函数立即返回。
    private int _brightnessProbeStarted;

    public SystemSettingsManager()
    {
        try
        {
            // 音频控制器自带 STA 线程，构造只是起线程 + 入队初始化命令，不阻塞调用方
            _audio = AudioNative.Create();
            _audio.StateChanged += OnAudioStateChanged;
            Logger.Info("SystemSettingsManager 初始化完成，音频控制器已加载");
        }
        catch (Exception ex)
        {
            _audio = null;
            Logger.Error("SystemSettingsManager 初始化：音频控制器加载失败", ex);
        }

        WarmUpBrightnessProbe();
    }

    /// <summary>后台预热 WMI 亮度能力（构造时调用一次，线程池上跑，不占 UI 时间）。</summary>
    private void WarmUpBrightnessProbe()
    {
        if (System.Threading.Interlocked.Exchange(ref _brightnessProbeStarted, 1) != 0) return;

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try { _ = BrightnessManager.Capability; }
            catch { }
        });
    }

    // ====== 音量 ======

    /// <summary>音量/静音被系统或其它程序改变时触发（回调在音频 STA 线程，订阅方需自行切回 UI 线程）。</summary>
    public event Action<float, bool>? VolumeChanged;

    /// <summary>音频端点是否可用（不可用时不值得再调音量接口）。</summary>
    public bool IsAudioAvailable => _audio?.IsAvailable ?? false;

    /// <summary>音量 UI 可见时打开轮询，不可见时关闭：面板关着就一次 COM 调用都不做。</summary>
    public void SetVolumeMonitoring(bool enabled)
    {
        try { _audio?.SetMonitoring(enabled); }
        catch (Exception ex) { Logger.Warn($"切换音量轮询失败: {ex.Message}"); }
    }

    /// <summary>当前音量 0.0~1.0；端点未就绪返回 -1（UI 应保持原值而不是跳到 0）。</summary>
    public float GetSystemVolume()
    {
        if (_audio == null) return -1f;
        try { return _audio.GetVolume(); }
        catch (Exception ex) { Logger.Warn($"读取系统音量失败: {ex.Message}"); return -1f; }
    }

    public bool IsSystemMuted() => _audio?.IsMuted ?? false;

    /// <summary>level: 0.0 ~ 1.0</summary>
    public void SetSystemVolume(float level)
    {
        if (_audio == null)
        {
            Logger.Warn("设置系统音量失败：音频控制器不可用");
            return;
        }

        try
        {
            _audio.SetVolume(level);
        }
        catch (Exception ex)
        {
            Logger.Error($"设置系统音量失败，level={level}", ex);
        }
    }

    public void Mute(bool mute)
    {
        if (_audio == null)
        {
            Logger.Warn("设置静音失败：音频控制器不可用");
            return;
        }

        try
        {
            _audio.Mute(mute);
        }
        catch (Exception ex)
        {
            Logger.Error($"设置静音失败，mute={mute}", ex);
        }
    }

    private void OnAudioStateChanged(float volume, bool muted)
    {
        try { VolumeChanged?.Invoke(volume, muted); }
        catch { }
    }

    // ====== 亮度 ======

    /// <summary>硬件亮度能力（首次访问才真正探测，之后走 Lazy 缓存）。</summary>
    public BrightnessCapability BrightnessCapability => BrightnessManager.Capability;

    /// <summary>硬件亮度（-1 为不支持）</summary>
    public int GetScreenBrightness() => BrightnessManager.Get();

    /// <summary>硬件写亮度</summary>
    public bool TrySetScreenBrightness(int percent)
    {
        var ok = BrightnessManager.TrySet(percent);
        if (!ok && BrightnessManager.Capability == BrightnessCapability.Hardware)
            Logger.Warn($"设置硬件亮度失败：{percent}%");

        BrightnessManager.Invalidate(); // 下次读取重新走 WMI
        return ok;
    }

    /// <summary>模拟读</summary>
    public int GetSimulatedBrightness() => BrightnessManager.GetSimulated();

    /// <summary>模拟写（台式机可用）</summary>
    public void SetSimulatedBrightness(int percent)
    {
        try
        {
            BrightnessManager.SetSimulated(percent);
        }
        catch (Exception ex)
        {
            Logger.Error($"SetSimulatedBrightness 失败，percent={percent}", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_audio != null)
                _audio.StateChanged -= OnAudioStateChanged;

            _audio?.Dispose();
            BrightnessManager.RestoreDefaultGamma();
            Logger.Info("SystemSettingsManager 已释放资源");
        }
        catch (Exception ex)
        {
            Logger.Error("SystemSettingsManager 释放资源异常", ex);
        }
    }
}
