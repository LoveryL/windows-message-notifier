using System;
using NAudio.CoreAudioApi;

namespace Notifier;

/// <summary>
/// 内部音频接口
/// </summary>
internal interface IAudioController : IDisposable
{
    float GetVolume();
    void SetVolume(float level);
    void Mute(bool mute);
}

/// <summary>
/// NAudio 底层适配
/// </summary>
internal sealed class NaudioAdapter : IAudioController
{
    private readonly MMDevice? _device;
    private bool _disposed;

    public NaudioAdapter()
    {
        try
        {
            _device = new MMDeviceEnumerator()
                .GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (Exception ex)
        {
            Logger.Warn($"未能获取默认音频端点，音频控制不可用: {ex.Message}");
            _device = null;
        }
    }

    public float GetVolume()
    {
        if (_device == null || _disposed)
            return 0f;

        try
        {
            return _device.AudioEndpointVolume.MasterVolumeLevelScalar;
        }
        catch (Exception ex)
        {
            Logger.Warn($"读取系统音量失败: {ex.Message}");
            return 0f;
        }
    }

    public void SetVolume(float level)
    {
        if (_device == null || _disposed)
            return;

        var safeLevel = Math.Clamp(level, 0f, 1f);
        try
        {
            _device.AudioEndpointVolume.MasterVolumeLevelScalar = safeLevel;
        }
        catch (Exception ex)
        {
            Logger.Warn($"设置系统音量失败，level={safeLevel:F2}: {ex.Message}");
        }
    }

    public void Mute(bool mute)
    {
        if (_device == null || _disposed)
            return;

        try
        {
            _device.AudioEndpointVolume.Mute = mute;
        }
        catch (Exception ex)
        {
            Logger.Warn($"设置静音失败，mute={mute}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _device?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Warn($"释放音频设备失败: {ex.Message}");
        }
    }
}

/// <summary>
/// 工厂入口
/// </summary>
internal static class AudioNative
{
    public static IAudioController Create() => new NaudioAdapter();
}