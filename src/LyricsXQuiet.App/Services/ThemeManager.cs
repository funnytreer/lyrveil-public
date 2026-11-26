using System.ComponentModel;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Interop;
using LyricsXQuiet.Core.Settings;
using LyricsXQuiet.Platform.Windows.Interop;
using Microsoft.Win32;
using WpfApplication = System.Windows.Application;

namespace LyricsXQuiet.App.Services;

public sealed class ThemeManager : IDisposable
{
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private readonly WpfApplication _app;
    private readonly AppSettings _settings;
    private ResourceDictionary? _palette;
    private bool _dark;
    private bool _disposed;

    public bool LightTaskbar { get; private set; }
    public event EventHandler? TaskbarThemeChanged;

    public ThemeManager(WpfApplication app, AppSettings settings)
    {
        _app = app;
        _settings = settings;
        _palette = app.Resources.MergedDictionaries.FirstOrDefault(d =>
            d.Source?.OriginalString.EndsWith("Themes/ThemeLight.xaml", StringComparison.Ordinal) == true ||
            d.Source?.OriginalString.EndsWith("Themes/ThemeDark.xaml", StringComparison.Ordinal) == true);
        _dark = _palette?.Source.OriginalString.EndsWith("ThemeDark.xaml", StringComparison.Ordinal) == true;
        settings.PropertyChanged += OnSettingsChanged;
        SystemEvents.UserPreferenceChanged += OnSystemThemeChanged;
        Refresh();
    }

    public void ApplyChrome(Window window)
    {
        if (!window.AllowsTransparency)
            DwmWindowHelper.Apply(new WindowInteropHelper(window).Handle, _dark);
    }

    public static void AttachChrome(Window window)
    {
        if (window.ReadLocalValue(Window.FontFamilyProperty) == DependencyProperty.UnsetValue)
            window.SetResourceReference(Window.FontFamilyProperty, "AppFont");
        window.UseLayoutRounding = true;
        window.SnapsToDevicePixels = true;
        void ApplyDpi(double scale)
        {
            System.Windows.Media.TextOptions.SetTextFormattingMode(window, scale >= 1.25
                ? System.Windows.Media.TextFormattingMode.Ideal : System.Windows.Media.TextFormattingMode.Display);
            System.Windows.Media.TextOptions.SetTextRenderingMode(window, scale < 1.25 && !window.AllowsTransparency
                ? System.Windows.Media.TextRenderingMode.ClearType : System.Windows.Media.TextRenderingMode.Auto);
        }
        window.SourceInitialized += (_, _) =>
        {
            ApplyDpi(System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX);
            (WpfApplication.Current as App)?.Theme?.ApplyChrome(window);
        };
        window.DpiChanged += (_, args) => ApplyDpi(args.NewDpi.DpiScaleX);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.UiThemeMode)) RequestRefresh();
    }

    private void OnSystemThemeChanged(object sender, UserPreferenceChangedEventArgs args) => RequestRefresh();

    private void RequestRefresh()
    {
        if (_app.Dispatcher.HasShutdownStarted) return;
        if (_app.Dispatcher.CheckAccess()) Refresh();
        else _app.Dispatcher.InvokeAsync(Refresh);
    }

    private void Refresh()
    {
        if (_disposed) return;
        var dark = _settings.UiThemeMode == "Dark" ||
            (_settings.UiThemeMode == "System" && !UsesLightTheme("AppsUseLightTheme", true));
        if (_palette is null || dark != _dark)
        {
            var path = dark ? "ThemeDark" : "ThemeLight";
            var palette = new ResourceDictionary
            {
                Source = new Uri($"/LyricsXQuiet.App;component/Themes/{path}.xaml", UriKind.Relative)
            };
            var dictionaries = _app.Resources.MergedDictionaries;
            if (_palette is null) dictionaries.Insert(0, palette);
            else dictionaries[dictionaries.IndexOf(_palette)] = palette;
            _palette = palette;
            _dark = dark;
            foreach (Window window in _app.Windows) ApplyChrome(window);
        }
        var lightTaskbar = UsesLightTheme("SystemUsesLightTheme", false);
        if (LightTaskbar == lightTaskbar) return;
        LightTaskbar = lightTaskbar;
        TaskbarThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool UsesLightTheme(string value, bool fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Personalize);
            return key?.GetValue(value) is int setting ? setting != 0 : fallback;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException or IOException)
        {
            return fallback;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnSystemThemeChanged;
        _settings.PropertyChanged -= OnSettingsChanged;
    }
}
