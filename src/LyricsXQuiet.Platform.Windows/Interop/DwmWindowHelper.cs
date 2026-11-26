using System.Runtime.InteropServices;

namespace LyricsXQuiet.Platform.Windows.Interop;

public static class DwmWindowHelper
{
    public static void Apply(nint window, bool dark)
    {
        if (window == 0 || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        const int useImmersiveDarkMode = 20, cornerPreference = 33;
        int darkMode = dark ? 1 : 0, round = 2;
        DwmSetWindowAttribute(window, useImmersiveDarkMode, ref darkMode, sizeof(int));
        DwmSetWindowAttribute(window, cornerPreference, ref round, sizeof(int));

        // DWM expects COLORREF (0x00BBGGRR) for the native caption and its text.
        const int captionColor = 35, textColor = 36;
        int caption = dark ? 0x001A1714 : 0x00F7F8F6;
        int text = dark ? 0x00EEF0EB : 0x00171914;
        DwmSetWindowAttribute(window, captionColor, ref caption, sizeof(int));
        DwmSetWindowAttribute(window, textColor, ref text, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
