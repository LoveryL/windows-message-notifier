using System;
using System.Management;
using System.Runtime.InteropServices;

namespace Notifier;

internal static class BrightnessManager
{
    public static BrightnessCapability Capability => _cap.Value;
    private static readonly Lazy<BrightnessCapability> _cap = new(Probe);

    // 缓存最后一次设置的亮度值
    private static int _lastSimulatedPercent = 70;
    private static int _lastAppliedPercent = -1;
    private static bool _hasGammaOverride;
    private static readonly object _gammaLock = new();

    // 硬件亮度读取缓存：一次 WMI 查询要几十毫秒，而亮度值变化很慢，
    // 1 秒内复用上次结果；写入亮度后由 Invalidate() 主动失效。
    private const int BrightnessCacheTtlMs = 1000;
    private static readonly object _cacheLock = new();
    private static int _cachedBrightness = -1;
    private static long _cacheStampMs = long.MinValue;

    // ====== 硬件 WMI ======
    public static int Get()
    {
        var now = Environment.TickCount64;
        lock (_cacheLock)
        {
            if (_cachedBrightness >= 0 && now - _cacheStampMs < BrightnessCacheTtlMs)
                return _cachedBrightness;
        }

        int value = -1;
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightness");
            foreach (ManagementObject o in s.Get())
            {
                value = Convert.ToInt32(o["CurrentBrightness"]);
                break;
            }
        }
        catch { }

        lock (_cacheLock)
        {
            _cachedBrightness = value;
            _cacheStampMs = now;
        }

        return value;
    }

    /// <summary>亮度被外部/本程序改动后调用，让下一次 Get() 重新查询 WMI。</summary>
    public static void Invalidate()
    {
        lock (_cacheLock)
        {
            _cachedBrightness = -1;
            _cacheStampMs = long.MinValue;
        }
    }

    public static bool TrySet(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject o in s.Get())
            {
                o.InvokeMethod("WmiSetBrightness", new object[] { uint.MaxValue, (byte)percent });
                return true;
            }
        }
        catch { }
        return false;
    }

    // ====== 模拟 Gamma ======
    public static int GetSimulated()
    {
        //优先返回缓存值
        return _lastSimulatedPercent;
    }

    /// <summary>
    /// 设置 GPU Gamma 模拟亮度（percent: 5~100）
    /// </summary>
    public static void SetSimulated(int percent)
    {
        percent = Math.Clamp(percent, 5, 100);
        _lastSimulatedPercent = percent;

        // 拖动滑块时会连续触发相同数值，重复设置 Gamma 只是白白让 GPU 重刷一次。
        // 亮度设置可能来自去抖后的后台线程，这里串行化保护状态字段。
        lock (_gammaLock)
        {
            if (_lastAppliedPercent == percent && _hasGammaOverride)
                return;
        }

        float level = percent / 100f;
        var ramp = CreateRamp();

        for (int i = 0; i < 256; i++)
        {
            ushort v = (ushort)(i * level * 257);
            ramp.Red[i] = v;
            ramp.Green[i] = v;
            ramp.Blue[i] = v;
        }

        IntPtr dc = IntPtr.Zero;
        try
        {
            dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero)
            {
                Logger.Error("GetDC(IntPtr.Zero) 返回 NULL，无法设置 Gamma");
                return;
            }

            bool ok = SetDeviceGammaRamp(dc, ref ramp);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                Logger.Error($"SetDeviceGammaRamp 失败，亮度={percent}%，Win32ErrorCode={err}");
                return;
            }

            lock (_gammaLock)
            {
                _hasGammaOverride = true;
                _lastAppliedPercent = percent;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"SetSimulated 发生异常，亮度={percent}%", ex);
        }
        finally
        {
            if (dc != IntPtr.Zero)
                ReleaseDC(IntPtr.Zero, dc);
        }
    }

    public static void RestoreDefaultGamma()
    {
        lock (_gammaLock)
        {
            if (!_hasGammaOverride)
                return;
        }

        var baseRamp = CreateRamp();
        for (int i = 0; i < 256; i++)
        {
            baseRamp.Red[i] = (ushort)(i * 257);
            baseRamp.Green[i] = (ushort)(i * 257);
            baseRamp.Blue[i] = (ushort)(i * 257);
        }

        IntPtr dc = IntPtr.Zero;
        try
        {
            dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero)
                return;

            SetDeviceGammaRamp(dc, ref baseRamp);
            lock (_gammaLock)
            {
                _hasGammaOverride = false;
                _lastAppliedPercent = -1;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("恢复 Gamma 失败", ex);
        }
        finally
        {
            if (dc != IntPtr.Zero)
                ReleaseDC(IntPtr.Zero, dc);
        }
    }

    private static BrightnessCapability Probe()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightness");
            return s.Get().Count > 0 ? BrightnessCapability.Hardware : BrightnessCapability.NotSupported;
        }
        catch { return BrightnessCapability.NotSupported; }
    }

    private static RAMP CreateRamp() => new()
    {
        Red = new ushort[256],
        Green = new ushort[256],
        Blue = new ushort[256]
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct RAMP
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Red;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Green;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Blue;
    }
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool GetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);
    [DllImport("gdi32.dll")] private static extern bool SetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
}