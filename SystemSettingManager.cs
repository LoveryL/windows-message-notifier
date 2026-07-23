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

    public SystemSettingsManager()
    {
        try { _audio = AudioNative.Create(); }
        catch { _audio = null; }
    }

    // ====== 音量 ======
    public float GetSystemVolume() => _audio?.GetVolume() ?? 0f;

    /// <summary>level: 0.0 ~ 1.0</summary>
    public void SetSystemVolume(float level) => _audio?.SetVolume(level);

    public void Mute(bool mute) => _audio?.Mute(mute);

    // ====== 亮度 ======
    public BrightnessCapability BrightnessCapability
        => BrightnessManager.Capability;

    /// <summary>硬件亮度（-1 为不支持）</summary>
    public int GetScreenBrightness() => BrightnessManager.Get();

    /// <summary>硬件写亮度</summary>
    public bool TrySetScreenBrightness(int percent) => BrightnessManager.TrySet(percent);

    /// <summary>模拟读</summary>
    public int GetSimulatedBrightness() => BrightnessManager.GetSimulated();

    /// <summary>模拟写（台式机可用）</summary>
    public void SetSimulatedBrightness(int percent) => BrightnessManager.SetSimulated(percent);

    public void Dispose() => _audio?.Dispose();
}