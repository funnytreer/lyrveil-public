using System.Runtime.InteropServices;

namespace LyricsXQuiet.Platform.Windows.Interop;

public static class DisplayMetrics
{
    public static int GetRefreshRate(nint window) => Math.Clamp(ReadMode(window).Frequency, 60, 240);

    public static (int Width, int Height, int Frequency) ReadMode(nint window)
    {
        const int nearestMonitor = 2, currentMode = -1;
        if (window == 0) return default;
        var monitor = MonitorFromWindow(window, nearestMonitor);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var mode = new DisplayMode { Size = (ushort)Marshal.SizeOf<DisplayMode>() };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info) ||
            !EnumDisplaySettingsW(info.Device, currentMode, ref mode)) return default;
        return (mode.Width, mode.Height, mode.Frequency);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public System.Drawing.Rectangle MonitorBounds, WorkBounds;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    // Only these two fields of the native fixed-size DEVMODEW are needed.
    [StructLayout(LayoutKind.Explicit, Size = 220)]
    private struct DisplayMode
    {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(172)] public int Width;
        [FieldOffset(176)] public int Height;
        [FieldOffset(184)] public int Frequency;
    }

    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, int flags);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string device, int modeNumber, ref DisplayMode mode);
}
