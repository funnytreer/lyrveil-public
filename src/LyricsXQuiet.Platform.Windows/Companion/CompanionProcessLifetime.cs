using System.ComponentModel;
using System.Diagnostics;

namespace LyricsXQuiet.Platform.Windows.Companion;

public static class CompanionProcessLifetime
{
    public static bool HasRunningProcess(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            try { if (!process.HasExited) return true; }
            catch (InvalidOperationException) { /* Exited during inspection. */ }
            catch (Win32Exception) { return true; } // Inaccessible is not proof of exit.
        }
        return false;
    }
}
