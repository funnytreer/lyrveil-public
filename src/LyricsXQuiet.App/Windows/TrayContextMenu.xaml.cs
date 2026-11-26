using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LyricsXQuiet.Core.Settings;

namespace LyricsXQuiet.App.Windows;

public partial class TrayContextMenu : Window, IDisposable
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExAppWindow = 0x00040000L;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private readonly Action _showQuickPanel;
    private readonly Action _showSettings;
    private readonly Action _exit;
    private readonly LyricsXQuiet.App.Services.TrayPopupDismissal _dismissal;
    private bool _allowClose;
    private bool _disposed;

    public TrayContextMenu(
        AppSettings settings,
        Action showQuickPanel,
        Action showSettings,
        Action exit)
    {
        _showQuickPanel = showQuickPanel;
        _showSettings = showSettings;
        _exit = exit;
        InitializeComponent();
        LyricsXQuiet.App.Services.ThemeManager.AttachChrome(this);
        _dismissal = new(this, CloseMenu);
        DataContext = settings;
        Closing += OnClosing;
    }

    public void ShowAtCursor()
    {
        if (_disposed || _dismissal.JustDismissed) return;
        if (IsVisible) { CloseMenu(); return; }

        ApplyTheme();
        Opacity = 0;
        if (!IsVisible)
        {
            Show();
        }

        ApplyToolWindowStyle();
        _dismissal.TakeFocus();
        UpdateLayout();
        RepositionInsideWorkingArea();
        Opacity = 1;
    }

    public void CloseMenu()
    {
        if (IsVisible)
        {
            _dismissal.RecordDismissal();
            Hide();
        }
    }

    protected override void OnSourceInitialized(EventArgs args)
    {
        base.OnSourceInitialized(args);
        ApplyToolWindowStyle();
    }

    private void ApplyToolWindowStyle()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        var extendedStyle = GetExtendedStyle(handle);
        var toolWindowStyle = (extendedStyle | WsExToolWindow) & ~WsExAppWindow;
        if (toolWindowStyle != extendedStyle)
        {
            SetExtendedStyle(handle, toolWindowStyle);
        }

        _ = SetWindowPos(
            handle,
            nint.Zero,
            0,
            0,
            0,
            0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs args)
    {
        if (args.Key == Key.Escape)
        {
            CloseMenu();
            args.Handled = true;
            return;
        }

        if (args.Key is not (Key.Down or Key.Up))
        {
            return;
        }

        var direction = args.Key == Key.Down
            ? FocusNavigationDirection.Next
            : FocusNavigationDirection.Previous;
        var moved = Keyboard.FocusedElement is UIElement focused
            && focused.MoveFocus(new TraversalRequest(direction));
        if (!moved)
        {
            MoveFocus(new TraversalRequest(
                args.Key == Key.Down
                    ? FocusNavigationDirection.First
                    : FocusNavigationDirection.Last));
        }

        args.Handled = true;
    }

    private void OnQuickPanelClick(object sender, RoutedEventArgs args)
    {
        CloseMenu();
        _showQuickPanel();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs args)
    {
        CloseMenu();
        _showSettings();
    }

    private void OnExitClick(object sender, RoutedEventArgs args)
    {
        CloseMenu();
        _exit();
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        CloseMenu();
    }

    private void ApplyTheme()
    {
        if (SystemParameters.HighContrast)
        {
            SetBrush("Surface.Card", System.Windows.SystemColors.MenuColor);
            SetBrush("Border.Hairline", System.Windows.SystemColors.ActiveBorderColor);
            SetBrush("Text.Primary", System.Windows.SystemColors.MenuTextColor);
            SetBrush("Text.Secondary", System.Windows.SystemColors.MenuTextColor);
            SetBrush("Surface.Subtle", System.Windows.SystemColors.HighlightColor);
            SetBrush("Accent.Soft", System.Windows.SystemColors.HighlightColor);
            SetBrush("Accent.Primary", System.Windows.SystemColors.HighlightTextColor);
            SetBrush("Danger.Text", System.Windows.SystemColors.HighlightTextColor);
            SetBrush("Danger.Soft", System.Windows.SystemColors.HighlightColor);
            return;
        }

        foreach (var key in new[] { "Surface.Card", "Border.Hairline", "Text.Primary", "Text.Secondary", "Surface.Subtle", "Accent.Soft", "Accent.Primary", "Danger.Text", "Danger.Soft" })
            Resources.Remove(key);
    }

    private void RepositionInsideWorkingArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero
            || !GetCursorPos(out var cursor)
            || !GetWindowRect(handle, out var windowRect))
        {
            return;
        }

        var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        if (monitor == nint.Zero)
        {
            return;
        }

        var monitorInfo = new MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        var width = windowRect.Right - windowRect.Left;
        var height = windowRect.Bottom - windowRect.Top;
        var dpi = GetDpiForWindow(handle);
        var margin = Math.Max(6, (int)Math.Round(8d * Math.Max(96u, dpi) / 96d));
        var gap = Math.Max(4, (int)Math.Round(6d * Math.Max(96u, dpi) / 96d));
        var work = monitorInfo.WorkArea;

        int left;
        if (cursor.X <= work.Left)
        {
            left = work.Left + margin;
        }
        else if (cursor.X >= work.Right)
        {
            left = work.Right - width - margin;
        }
        else if (cursor.X + width - gap <= work.Right - margin)
        {
            left = cursor.X - gap;
        }
        else
        {
            left = cursor.X - width + gap;
        }

        int top;
        if (cursor.Y >= work.Bottom)
        {
            top = work.Bottom - height - margin;
        }
        else if (cursor.Y <= work.Top)
        {
            top = work.Top + margin;
        }
        else if (cursor.Y - height - gap >= work.Top + margin)
        {
            top = cursor.Y - height - gap;
        }
        else
        {
            top = cursor.Y + gap;
        }

        var minimumLeft = work.Left + margin;
        var maximumLeft = Math.Max(minimumLeft, work.Right - width - margin);
        var minimumTop = work.Top + margin;
        var maximumTop = Math.Max(minimumTop, work.Bottom - height - margin);
        left = Math.Clamp(left, minimumLeft, maximumLeft);
        top = Math.Clamp(top, minimumTop, maximumTop);
        _ = SetWindowPos(
            handle,
            nint.Zero,
            left,
            top,
            0,
            0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private void SetBrush(string key, System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Resources[key] = brush;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _dismissal.Dispose();
        _disposed = true;
        _allowClose = true;
        DataContext = null;
        Close();
    }

    private static long GetExtendedStyle(nint handle) => IntPtr.Size == 8
        ? GetWindowLongPtr(handle, GwlExStyle).ToInt64()
        : GetWindowLong(handle, GwlExStyle);

    private static void SetExtendedStyle(nint handle, long style)
    {
        if (IntPtr.Size == 8)
        {
            _ = SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
            return;
        }

        _ = SetWindowLong(handle, GwlExStyle, unchecked((int)style));
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
    private static extern bool GetCursorPos(out Point cursor);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rect rectangle);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
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
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect MonitorArea;
        public Rect WorkArea;
        public uint Flags;
    }
}
