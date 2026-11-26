using System.Runtime.InteropServices;

namespace LyricsXQuiet.Platform.Windows.Interop;

public static class OverlayWindowInterop
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;

    public static void Configure(nint handle, bool clickThrough)
    {
        var style = GetExtendedStyle(handle) | WsExToolWindow | WsExNoActivate;
        style = clickThrough ? style | WsExTransparent : style & ~WsExTransparent;
        SetExtendedStyle(handle, style);
    }

    public static void SetClickThrough(nint handle, bool enabled)
    {
        var style = GetExtendedStyle(handle);
        style = enabled ? style | WsExTransparent : style & ~WsExTransparent;
        SetExtendedStyle(handle, style);
    }

    public static bool IsCursorInside(nint handle)
    {
        return GetCursorPos(out var point)
            && GetWindowRect(handle, out var rectangle)
            && point.X >= rectangle.Left
            && point.X < rectangle.Right
            && point.Y >= rectangle.Top
            && point.Y < rectangle.Bottom;
    }

    public static bool SetCaptureExclusion(nint handle, bool enabled) =>
        SetWindowDisplayAffinity(handle, enabled ? WdaExcludeFromCapture : WdaNone);

    private static long GetExtendedStyle(nint handle) => IntPtr.Size == 8
        ? GetWindowLongPtr(handle, GwlExStyle).ToInt64()
        : GetWindowLong(handle, GwlExStyle);

    private static void SetExtendedStyle(nint handle, long style)
    {
        if (IntPtr.Size == 8)
        {
            _ = SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
        }
        else
        {
            _ = SetWindowLong(handle, GwlExStyle, unchecked((int)style));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint newLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(nint windowHandle, int index, int newLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint windowHandle, out Rectangle rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint windowHandle, uint affinity);
}
