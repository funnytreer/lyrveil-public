using System.Runtime.InteropServices;

namespace LyricsXQuiet.Platform.Windows.Interop;

/// <summary>
/// Gives every Lyrveil window one stable Windows shell identity, regardless of
/// which player-shaped companion shortcut launched the process.
/// </summary>
public static class DesktopAppIdentity
{
    public const string AppUserModelId = "Lyrveil.DesktopLyrics";

    public static bool TryApply()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return SetCurrentProcessExplicitAppUserModelID(AppUserModelId) >= 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
