using System.Diagnostics;
using System.Runtime.InteropServices;
using LyricsXQuiet.Core.Settings;
using LyricsXQuiet.Platform.Windows.Companion;

internal static class PlayerMonitorTests
{
    public static void RunFixture(string root)
    {
        using var stop = new ManualResetEvent(false);
        using var exit = new AutoResetEvent(false);
        var ready = Path.Combine(root, "observed");
        PlayerMonitor.Run(new SettingsStore(root), stop, exit, () => File.Exists(ready),
            () => File.WriteAllText(ready, "observed fixture"));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        public uint Size, PageFaults;
        public nuint PeakWorkingSet, WorkingSet, PeakPaged, Paged, PeakNonPaged, NonPaged, Pagefile, PeakPagefile, PrivateUsage, PrivateWorkingSet;
        public ulong SharedCommit;
    }

    [DllImport("psapi.dll")]
    private static extern bool GetProcessMemoryInfo(nint process, ref MemoryCounters counters, uint size);

    public static async Task RunAsync(string tempDirectory)
    {
        var root = Directory.CreateDirectory(Path.Combine(tempDirectory, "monitor")).FullName;
        var store = new SettingsStore(root);
        var settings = new AppSettings
        {
            PlayerFollowMode = PlayerFollowModes.Monitor,
            LocalPlayerExecutablePath = Environment.ProcessPath!,
            AutoRepairNetEaseSync = false
        };
        store.SaveNow(settings);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--monitor-fixture");
        start.ArgumentList.Add(root);
        using var fixture = Process.Start(start)!;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            while (!File.Exists(Path.Combine(root, "observed")))
            {
                if (fixture.HasExited) throw new InvalidOperationException("The isolated monitor exited before finding the fixture player.");
                await Task.Delay(30, deadline.Token);
            }
            if (fixture.Modules.Cast<ProcessModule>().Any(m => m.ModuleName is "PresentationCore.dll" or "PresentationFramework.dll"))
                throw new InvalidOperationException("The monitor loaded the WPF desktop runtime.");
            var bytes = (uint)Marshal.SizeOf<MemoryCounters>();
            var counters = new MemoryCounters { Size = bytes };
            var privateWorkingSet = GetProcessMemoryInfo(fixture.Handle, ref counters, bytes) && counters.PrivateWorkingSet > 0
                ? $", private working set {counters.PrivateWorkingSet / 1048576d:F1} MiB" : "";
            Console.WriteLine($"Isolated monitor: no WPF, working set {fixture.WorkingSet64 / 1048576d:F1} MiB{privateWorkingSet}.");
            settings.PlayerFollowMode = PlayerFollowModes.Shortcut;
            store.SaveNow(settings);
            await fixture.WaitForExitAsync(deadline.Token);
            if (fixture.ExitCode != 0) throw new InvalidOperationException("The monitor did not exit cleanly after a configuration change.");
            Console.WriteLine("Monitor lifecycle passed: current-session process snapshot, atomic settings reload and shortcut-only exit.");
        }
        finally
        {
            if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); }
        }
    }
}
