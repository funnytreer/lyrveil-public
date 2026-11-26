using System.Drawing;
using WinForms = System.Windows.Forms;

namespace LyricsXQuiet.App.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Action _toggleQuickPanel;
    private readonly Action _showTrayMenu;
    private readonly ThemeManager _theme;
    private readonly Icon _darkInk;
    private readonly Icon _lightInk;
    private readonly WinForms.NotifyIcon _notifyIcon;

    public TrayIconService(
        ThemeManager theme,
        Action toggleQuickPanel,
        Action showTrayMenu)
    {
        _toggleQuickPanel = toggleQuickPanel;
        _showTrayMenu = showTrayMenu;

        _theme = theme;
        _darkInk = LoadIcon("tray-dark-ink");
        _lightInk = LoadIcon("tray-light-ink");
        _notifyIcon = new WinForms.NotifyIcon
        {
            Text = "词间 · Lyrveil",
            Icon = _theme.LightTaskbar ? _darkInk : _lightInk,
            Visible = true
        };
        _notifyIcon.MouseUp += OnMouseUp;
        _theme.TaskbarThemeChanged += OnTaskbarThemeChanged;
    }

    private void OnMouseUp(object? sender, WinForms.MouseEventArgs args)
    {
        if (args.Button == WinForms.MouseButtons.Left)
        {
            _toggleQuickPanel();
        }
        else if (args.Button == WinForms.MouseButtons.Right)
        {
            _showTrayMenu();
        }
    }

    private void OnTaskbarThemeChanged(object? sender, EventArgs e) =>
        _notifyIcon.Icon = _theme.LightTaskbar ? _darkInk : _lightInk;

    private static Icon LoadIcon(string name)
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri($"pack://application:,,,/Assets/{name}.ico", UriKind.Absolute))
            ?? throw new InvalidOperationException("无法读取词间应用图标资源。");
        using var stream = resource.Stream;
        using var source = new Icon(stream);
        return (Icon)source.Clone();
    }

    public void Dispose()
    {
        _notifyIcon.MouseUp -= OnMouseUp;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _theme.TaskbarThemeChanged -= OnTaskbarThemeChanged;
        _darkInk.Dispose();
        _lightInk.Dispose();
    }
}
