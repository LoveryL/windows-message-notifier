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
    private readonly IAudioController? _audio;
    private bool _disposed;

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
    }

    // ====== 音量 ======
    public float GetSystemVolume()
    {
        var v = _audio?.GetVolume() ?? 0f;
        Logger.Debug($"读取系统音量：{v:F2}");
        return v;
    }

    /// <summary>level: 0.0 ~ 1.0</summary>
    public void SetSystemVolume(float level)
    {
        Logger.Info($"设置系统音量：{level:F2}");
        try
        {
            _audio?.SetVolume(level);
        }
        catch (Exception ex)
        {
            Logger.Error($"设置系统音量失败，level={level}", ex);
        }
    }

    public void Mute(bool mute)
    {
        Logger.Info($"设置静音：{mute}");
        try
        {
            _audio?.Mute(mute);
        }
        catch (Exception ex)
        {
            Logger.Error($"设置静音失败，mute={mute}", ex);
        }
    }

    // ====== 亮度 ======
    public BrightnessCapability BrightnessCapability
    {
        get
        {
            var cap = BrightnessManager.Capability;
            Logger.Debug($"读取亮度能力：{cap}");
            return cap;
        }
    }

    /// <summary>硬件亮度（-1 为不支持）</summary>
    public int GetScreenBrightness()
    {
        var b = BrightnessManager.Get();
        Logger.Debug($"读取硬件亮度：{b}");
        return b;
    }

    /// <summary>硬件写亮度</summary>
    public bool TrySetScreenBrightness(int percent)
    {
        Logger.Info($"设置硬件亮度：{percent}%");
        var ok = BrightnessManager.TrySet(percent);
        if (!ok) Logger.Warn($"设置硬件亮度失败（不支持？）：{percent}%");
        return ok;
    }

    /// <summary>模拟读</summary>
    public int GetSimulatedBrightness()
    {
        var b = BrightnessManager.GetSimulated();
        Logger.Debug($"读取模拟亮度：{b}");
        return b;
    }

    /// <summary>模拟写（台式机可用）</summary>
    public void SetSimulatedBrightness(int percent)
    {
        Logger.Info($"设置模拟亮度：{percent}%");
        try
        {
            BrightnessManager.SetSimulated(percent);
        }
        catch (Exception ex)
        {
            Logger.Error($"设置模拟亮度失败，percent={percent}", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _audio?.Dispose();
            Logger.Info("SystemSettingsManager 已释放资源");
        }
        catch (Exception ex)
        {
            Logger.Error("SystemSettingsManager 释放资源异常", ex);
        }
    }
}
