using System.Runtime.CompilerServices;
using LyricsXQuiet.Platform.Windows.Companion;

namespace LyricsXQuiet.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--player-monitor", StringComparer.OrdinalIgnoreCase))
        {
            // WinRT and the wait loop use an MTA thread. No WPF types are loaded on this path.
            Task.Run(PlayerMonitor.Run).GetAwaiter().GetResult();
            return;
        }
        StartDesktop();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StartDesktop() => App.Main();
}
