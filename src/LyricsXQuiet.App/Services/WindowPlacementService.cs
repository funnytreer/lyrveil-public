using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LyricsXQuiet.App.Services;

internal static class WindowPlacementService
{
    internal readonly record struct WindowCenter(int X, int Y);
    internal readonly record struct VerticalDragResult(int Y, bool SnappedToTaskbar);

    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint MonitorDefaultToNull = 0x00000000;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;

    internal static double OverlayWidth(double workWidth) => Math.Min(1280, workWidth * .86);
    internal static double QuickPanelHeight(double workHeight) => Math.Clamp(workHeight - 36, 1, 640);

    public static Rect GetCursorWorkingAreaInDips() => GetWorkingAreaInDips(GetCursorMonitor());

    public static Rect GetWindowWorkingAreaInDips(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        return handle != 0 && GetWindowRect(handle, out var rect)
            ? GetWorkingAreaInDips(MonitorFromRect(ref rect, MonitorDefaultToNearest))
            : GetCursorWorkingAreaInDips();
    }

    private static Rect GetWorkingAreaInDips(nint monitor)
    {
        if (!TryGetMonitorInfo(monitor, out var info))
        {
            return SystemParameters.WorkArea;
        }

        var scale = GetMonitorScale(monitor);
        return new Rect(
            info.WorkArea.Left / scale,
            info.WorkArea.Top / scale,
            info.WorkArea.Width / scale,
            info.WorkArea.Height / scale);
    }

    public static void CenterOnCursorMonitor(Window window, double marginInDips = 20)
    {
        Position(window, (work, width, height, scale) =>
        {
            var margin = (int)Math.Round(marginInDips * scale);
            return (
                Clamp(work.Left + ((work.Width - width) / 2), work.Left + margin, work.Right - width - margin),
                Clamp(work.Top + ((work.Height - height) / 2), work.Top + margin, work.Bottom - height - margin));
        });
    }

    public static void PlaceBottomRightOnCursorMonitor(Window window, double marginInDips = 16)
    {
        Position(window, (work, width, height, scale) =>
        {
            var margin = (int)Math.Round(marginInDips * scale);
            return (
                Clamp(work.Right - width - margin, work.Left + margin, work.Right - width - margin),
                Clamp(work.Bottom - height - margin, work.Top + margin, work.Bottom - height - margin));
        });
    }

    public static void PlaceBottomCenterOnCursorMonitor(
        Window window,
        double verticalPositionRatio,
        double horizontalMarginInDips,
        double? verticalMarginInDips = null)
    {
        Position(window, (work, width, height, scale) =>
        {
            var horizontalMargin = (int)Math.Round(horizontalMarginInDips * scale);
            var verticalMargin = (int)Math.Round(
                (verticalMarginInDips ?? horizontalMarginInDips) * scale);
            var x = work.Left + ((work.Width - width) / 2);
            var y = work.Top
                + (int)Math.Round(work.Height * verticalPositionRatio)
                - (height / 2);
            return (
                Clamp(
                    x,
                    work.Left + horizontalMargin,
                    work.Right - width - horizontalMargin),
                Clamp(
                    y,
                    work.Top + verticalMargin,
                    work.Bottom - height - verticalMargin));
        });
    }

    public static void PlaceBottomCenterOnCurrentMonitor(
        Window window,
        double horizontalMarginInDips = 16,
        double verticalMarginInDips = 0)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || !GetWindowRect(handle, out var currentRect))
        {
            return;
        }

        var monitor = MonitorFromRect(ref currentRect, MonitorDefaultToNearest);
        if (!TryGetMonitorInfo(monitor, out var info))
        {
            return;
        }

        var scale = GetMonitorScale(monitor);
        var horizontalMargin = (int)Math.Round(horizontalMarginInDips * scale);
        var verticalMargin = (int)Math.Round(verticalMarginInDips * scale);
        var x = Clamp(
            info.WorkArea.Left + ((info.WorkArea.Width - currentRect.Width) / 2),
            info.WorkArea.Left + horizontalMargin,
            info.WorkArea.Right - currentRect.Width - horizontalMargin);
        var y = Clamp(
            info.WorkArea.Bottom - currentRect.Height - verticalMargin,
            info.WorkArea.Top + verticalMargin,
            info.WorkArea.Bottom - currentRect.Height - verticalMargin);
        _ = SetWindowPos(
            handle,
            0,
            x,
            y,
            0,
            0,
            SwpNoActivate | SwpNoSize | SwpNoZOrder);
    }

    public static bool IsNearBottomWorkingAreaEdge(
        Window window,
        double maximumDistanceInDips)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || !GetWindowRect(handle, out var currentRect))
        {
            return false;
        }

        var monitor = MonitorFromRect(ref currentRect, MonitorDefaultToNearest);
        if (!TryGetMonitorInfo(monitor, out var info))
        {
            return false;
        }

        var threshold = (int)Math.Round(maximumDistanceInDips * GetMonitorScale(monitor));
        return Math.Abs(currentRect.Bottom - info.WorkArea.Bottom) <= threshold;
    }

    public static void PreserveCenterAfterResize(
        Window window,
        System.Windows.Size previousSize,
        double marginInDips = 16)
    {
        if (previousSize.Width <= 0
            || previousSize.Height <= 0
            || !double.IsFinite(previousSize.Width)
            || !double.IsFinite(previousSize.Height))
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || !GetWindowRect(handle, out var currentRect))
        {
            return;
        }

        var monitor = MonitorFromRect(ref currentRect, MonitorDefaultToNearest);
        if (!TryGetMonitorInfo(monitor, out var info))
        {
            return;
        }

        var scale = GetMonitorScale(monitor);
        var previousWidth = (int)Math.Round(previousSize.Width * scale);
        var previousHeight = (int)Math.Round(previousSize.Height * scale);
        var margin = (int)Math.Round(marginInDips * scale);
        var centeredX = currentRect.Left + ((previousWidth - currentRect.Width) / 2);
        var centeredY = currentRect.Top + ((previousHeight - currentRect.Height) / 2);
        var x = Clamp(
            centeredX,
            info.WorkArea.Left + margin,
            info.WorkArea.Right - currentRect.Width - margin);
        var y = Clamp(
            centeredY,
            info.WorkArea.Top + margin,
            info.WorkArea.Bottom - currentRect.Height - margin);
        _ = SetWindowPos(
            handle,
            0,
            x,
            y,
            0,
            0,
            SwpNoActivate | SwpNoSize | SwpNoZOrder);
    }

    public static bool TryGetWindowCenter(Window window, out WindowCenter center)
    {
        center = default;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || !GetWindowRect(handle, out var rect))
        {
            return false;
        }

        center = new WindowCenter(
            rect.Left + (rect.Width / 2),
            rect.Top + (rect.Height / 2));
        return true;
    }

    public static void PlaceAtCenter(
        Window window,
        WindowCenter center,
        double horizontalMarginInDips = 16,
        double? verticalMarginInDips = null,
        bool allowTaskbarOverlap = false)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || !GetWindowRect(handle, out var currentRect))
        {
            return;
        }

        var centerPoint = new NativePoint { X = center.X, Y = center.Y };
        var monitor = MonitorFromPoint(centerPoint, MonitorDefaultToNearest);
        if (!TryGetMonitorInfo(monitor, out var info))
        {
            return;
        }

        var scale = GetMonitorScale(monitor);
        var horizontalMargin = (int)Math.Round(horizontalMarginInDips * scale);
        var verticalMargin = (int)Math.Round(
            (verticalMarginInDips ?? horizontalMarginInDips) * scale);
        var x = Clamp(
            center.X - (currentRect.Width / 2),
            info.WorkArea.Left + horizontalMargin,
            info.WorkArea.Right - currentRect.Width - horizontalMargin);
        var verticalBounds = allowTaskbarOverlap ? info.MonitorArea : info.WorkArea;
        var y = Clamp(
            center.Y - (currentRect.Height / 2),
            verticalBounds.Top + verticalMargin,
            verticalBounds.Bottom - currentRect.Height - verticalMargin);
        _ = SetWindowPos(
            handle,
            0,
            x,
            y,
            0,
            0,
            SwpNoActivate | SwpNoSize | SwpNoZOrder);
    }

    public static void CenterHorizontallyOnCurrentMonitor(
        Window window,
        double horizontalMarginInDips = 16,
        double? verticalMarginInDips = null,
        bool allowTaskbarOverlap = false)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || !GetWindowRect(handle, out var currentRect))
        {
            return;
        }

        var monitor = MonitorFromRect(ref currentRect, MonitorDefaultToNearest);
        if (!TryGetMonitorInfo(monitor, out var info))
        {
            return;
        }

        var scale = GetMonitorScale(monitor);
        var horizontalMargin = (int)Math.Round(horizontalMarginInDips * scale);
        var verticalMargin = (int)Math.Round(
            (verticalMarginInDips ?? horizontalMarginInDips) * scale);
        var x = Clamp(
            info.WorkArea.Left + ((info.WorkArea.Width - currentRect.Width) / 2),
            info.WorkArea.Left + horizontalMargin,
            info.WorkArea.Right - currentRect.Width - horizontalMargin);
        var verticalBounds = allowTaskbarOverlap ? info.MonitorArea : info.WorkArea;
        var y = Clamp(
            currentRect.Top,
            verticalBounds.Top + verticalMargin,
            verticalBounds.Bottom - currentRect.Height - verticalMargin);
        _ = SetWindowPos(
            handle,
            0,
            x,
            y,
            0,
            0,
            SwpNoActivate | SwpNoSize | SwpNoZOrder);
    }

    public static bool MoveAtCursor(
        Window window,
        System.Windows.Point pointerOffset, bool centerOnly, out bool centered,
        double horizontalMarginInDips = 16,
        double? verticalMarginInDips = null,
        bool taskbarSnapActive = false,
        double taskbarSnapDistanceInDips = 12,
        double taskbarSnapReleaseDistanceInDips = 24)
    {
        centered = false;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0
            || !GetWindowRect(handle, out var currentRect)
            || !GetCursorPos(out var cursor))
        {
            return taskbarSnapActive;
        }

        var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        if (!TryGetMonitorInfo(monitor, out var info))
        {
            return taskbarSnapActive;
        }

        var scale = GetMonitorScale(monitor);
        var horizontalMargin = (int)Math.Round(horizontalMarginInDips * scale);
        var verticalMargin = (int)Math.Round(
            (verticalMarginInDips ?? horizontalMarginInDips) * scale);
        var middle = info.WorkArea.Left + ((info.WorkArea.Width - currentRect.Width) / 2);
        var requestedX = cursor.X - (int)Math.Round(pointerOffset.X * scale);
        centered = centerOnly || Math.Abs(requestedX - middle) <= 12 * scale;
        var x = Clamp(
            centered ? middle : requestedX,
            info.WorkArea.Left + horizontalMargin,
            info.WorkArea.Right - currentRect.Width - horizontalMargin);
        var requestedY = cursor.Y - (int)Math.Round(pointerOffset.Y * scale);
        var verticalResult = CalculateVerticalDragY(
            requestedY,
            currentRect.Height,
            info.MonitorArea.Top,
            info.MonitorArea.Bottom,
            info.WorkArea.Bottom,
            verticalMargin,
            (int)Math.Round(taskbarSnapDistanceInDips * scale),
            (int)Math.Round(taskbarSnapReleaseDistanceInDips * scale),
            taskbarSnapActive);
        _ = SetWindowPos(
            handle,
            0,
            x,
            verticalResult.Y,
            0,
            0,
            SwpNoActivate | SwpNoSize | SwpNoZOrder);
        return verticalResult.SnappedToTaskbar;
    }

    internal static VerticalDragResult CalculateVerticalDragY(
        int requestedY,
        int windowHeight,
        int monitorTop,
        int monitorBottom,
        int workAreaBottom,
        int verticalMargin,
        int snapDistance,
        int snapReleaseDistance,
        bool wasSnappedToTaskbar)
    {
        var minimumY = monitorTop + verticalMargin;
        var maximumY = monitorBottom - windowHeight - verticalMargin;
        var y = Clamp(requestedY, minimumY, maximumY);

        // A bottom taskbar is represented by a work area whose bottom edge is
        // above the physical monitor edge. Side/top taskbars do not create a
        // bottom snap target, so horizontal movement remains unchanged.
        if (workAreaBottom >= monitorBottom)
        {
            return new VerticalDragResult(y, false);
        }

        var snappedY = workAreaBottom - windowHeight;
        if (snappedY < minimumY || snappedY > maximumY)
        {
            return new VerticalDragResult(y, false);
        }

        var threshold = wasSnappedToTaskbar
            ? Math.Max(snapDistance, snapReleaseDistance)
            : snapDistance;
        var distanceToTaskbarTop = Math.Abs((y + windowHeight) - workAreaBottom);
        return distanceToTaskbarTop <= threshold
            ? new VerticalDragResult(snappedY, true)
            : new VerticalDragResult(y, false);
    }

    public static bool IsMeaningfullyVisible(Window window, bool includeTaskbar = false)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || !GetWindowRect(handle, out var windowRect))
        {
            return false;
        }

        var monitor = MonitorFromRect(ref windowRect, MonitorDefaultToNull);
        if (monitor == 0 || !TryGetMonitorInfo(monitor, out var info))
        {
            return false;
        }

        var visibilityBounds = includeTaskbar ? info.MonitorArea : info.WorkArea;
        var intersectionWidth = Math.Max(
            0,
            Math.Min(windowRect.Right, visibilityBounds.Right) - Math.Max(windowRect.Left, visibilityBounds.Left));
        var intersectionHeight = Math.Max(
            0,
            Math.Min(windowRect.Bottom, visibilityBounds.Bottom) - Math.Max(windowRect.Top, visibilityBounds.Top));
        var scale = GetMonitorScale(monitor);
        return intersectionWidth >= 64 * scale && intersectionHeight >= 24 * scale;
    }

    private static void Position(
        Window window,
        Func<NativeRect, int, int, double, (int X, int Y)> calculatePosition)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || !GetWindowRect(handle, out var windowRect))
        {
            return;
        }

        var monitor = GetCursorMonitor();
        if (!TryGetMonitorInfo(monitor, out var info))
        {
            return;
        }

        var (x, y) = calculatePosition(
            info.WorkArea,
            windowRect.Width,
            windowRect.Height,
            GetMonitorScale(monitor));
        _ = SetWindowPos(
            handle,
            0,
            x,
            y,
            0,
            0,
            SwpNoActivate | SwpNoSize | SwpNoZOrder);
    }

    private static nint GetCursorMonitor()
    {
        _ = GetCursorPos(out var point);
        return MonitorFromPoint(point, MonitorDefaultToNearest);
    }

    private static bool TryGetMonitorInfo(nint monitor, out MonitorInfo info)
    {
        info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return monitor != 0 && GetMonitorInfo(monitor, ref info);
    }

    private static double GetMonitorScale(nint monitor)
    {
        try
        {
            return GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0
                ? dpiX / 96d
                : 1d;
        }
        catch (DllNotFoundException)
        {
            return 1d;
        }
        catch (EntryPointNotFoundException)
        {
            return 1d;
        }
    }

    private static int Clamp(int value, int minimum, int maximum)
    {
        if (maximum < minimum)
        {
            return minimum;
        }

        return Math.Clamp(value, minimum, maximum);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref NativeRect rect, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }
}
