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

    // ====== 硬件 WMI ======
    public static int Get()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightness");
            foreach (ManagementObject o in s.Get())
                return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return -1;
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
        _lastSimulatedPercent = percent; // 缓存

        float level = percent / 100f;
        var ramp = CreateRamp();

        for (int i = 0; i < 256; i++)
        {
            ushort v = (ushort)(i * level * 257);
            ramp.Red[i] = v;
            ramp.Green[i] = v;
            ramp.Blue[i] = v;
        }

        var dc = GetDC(IntPtr.Zero);
        SetDeviceGammaRamp(dc, ref ramp);
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

    [DllImport("gdi32.dll")] private static extern bool GetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);
    [DllImport("gdi32.dll")] private static extern bool SetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
}