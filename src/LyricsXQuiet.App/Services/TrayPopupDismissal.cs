using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LyricsXQuiet.App.Services;

internal sealed class TrayPopupDismissal : IDisposable
{
    private readonly Window _window;
    private readonly Action _dismiss;
    private HwndSource? _source;
    private long? _hiddenAt;
    public bool ModalOpen { get; set; }
    public bool JustDismissed => _hiddenAt is long at && Stopwatch.GetElapsedTime(at).TotalMilliseconds < 250;

    public TrayPopupDismissal(Window window, Action dismiss)
    {
        _window = window;
        _dismiss = dismiss;
        window.SourceInitialized += OnHandle;
        window.Deactivated += OnDeactivate;
    }

    public void TakeFocus()
    {
        SetForegroundWindow(new WindowInteropHelper(_window).Handle);
        _window.Activate();
    }

    public void RecordDismissal() => _hiddenAt = Stopwatch.GetTimestamp();

    private void OnHandle(object? sender, EventArgs e)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
        _source?.AddHook(OnMessage);
    }

    private void OnDeactivate(object? sender, EventArgs e)
    {
        if (!ModalOpen && _window.IsVisible) _dismiss();
    }

    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int wmActivateApp = 0x001C;
        if (message == wmActivateApp && wParam == 0) OnDeactivate(null, EventArgs.Empty);
        return 0;
    }

    public void Dispose()
    {
        _source?.RemoveHook(OnMessage);
        _window.Deactivated -= OnDeactivate;
        _window.SourceInitialized -= OnHandle;
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
}
