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
    // 每次构造都要走一遍 NAudio 的 MMDeviceEnumerator（枚举音频端点 + COM 初始化），
    // 旧实现每打开一次面板就 new 一个，改为进程内共享，只在应用退出时释放。
    private static readonly Lazy<SystemSettingsManager> _shared = new(() => new SystemSettingsManager());
    public static SystemSettingsManager Shared => _shared.Value;

    /// <summary>共享实例是否已被创建（退出时只在真正用过时才释放）。</summary>
    public static bool IsSharedCreated => _shared.IsValueCreated;

    private readonly IAudioController? _audio;
    private bool _disposed;

    // 亮度能力探测要走一次 WMI，结果不会变，构造时探测一次即可
    private readonly BrightnessCapability _brightnessCapability;

    public SystemSettingsManager()
    {
        try
        {
            _audio = AudioNative.Create();
            Logger.Info("SystemSettingsManager 初始化完成，音频控制器已加载");
        }
        catch (Exception ex)
        {
            _audio = null;
            Logger.Error("SystemSettingsManager 初始化：音频控制器加载失败", ex);
        }

        _brightnessCapability = BrightnessManager.Capability;
    }

    // ====== 音量 ======
    public float GetSystemVolume()
    {
        // 这里原本每次调用都写两条日志，属于高频路径，日志留在真正需要的地方（异常分支）
        if (_audio == null) return 0f;
        return _audio.GetVolume();
    }

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

    // ====== 亮度 ======
    public BrightnessCapability BrightnessCapability => _brightnessCapability;

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
