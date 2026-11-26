using System.Diagnostics;
using LyricsXQuiet.Platform.Windows.Companion;

internal static class NetEaseProcessRecoveryTests
{
    public static async Task<bool> RunFixtureAsync(string[] args)
    {
        if (args.Length == 1 && args[0] == "--recovery-fixture-child")
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            return true;
        }
        if (args.Length != 2 || args[0] != "--recovery-fixture-parent") return false;
        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, Arguments = "--recovery-fixture-child"
        })!;
        await File.WriteAllTextAsync(args[1] + ".tmp", child.Id.ToString());
        File.Move(args[1] + ".tmp", args[1]);
        await Task.Delay(TimeSpan.FromSeconds(30));
        return true;
    }

    public static async Task RunAsync(string root)
    {
        var directory = Path.Combine(root, "recovery-fixture");
        Directory.CreateDirectory(directory);
        var assembly = typeof(NetEaseProcessRecoveryTests).Assembly.Location;
        var sourceDirectory = Path.GetDirectoryName(assembly)!;
        var executable = Path.Combine(directory, "cloudmusic.exe");
        File.Copy(Path.ChangeExtension(assembly, ".exe"), executable);
        foreach (var source in Directory.EnumerateFiles(sourceDirectory).Where(path =>
                     Path.GetExtension(path) is ".dll" or ".json"))
            File.Copy(source, Path.Combine(directory, Path.GetFileName(source)));
        var ready = Path.Combine(directory, "child.id");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--recovery-fixture-parent");
        start.ArgumentList.Add(ready);
        using var parent = Process.Start(start)!;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(ready) && DateTime.UtcNow < deadline) await Task.Delay(20);
            Check(File.Exists(ready), "The isolated recovery process must start its child.");
            using var child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(ready)));
            var instance = new NetEaseProcessInstance(parent.Id, parent.StartTime.ToUniversalTime(), executable, false);
            Check(CompanionProcessLifetime.HasRunningProcess([parent, child]), "Live player processes must prevent duplicate launches.");
            var stopped = false;
            Check(!await NetEaseProcessRecovery.StopForRecoveryAsync(instance, () => false, () => stopped = true)
                  && !stopped && !parent.HasExited && !child.HasExited,
                "Resuming playback immediately before recovery must leave both parent and child running.");
            Check(!await NetEaseProcessRecovery.StopForRecoveryAsync(instance with { StartTimeUtc = instance.StartTimeUtc.AddSeconds(1) },
                    () => true, () => stopped = true) && !stopped,
                "A stale or reused process identity must never be stopped.");
            Check(await NetEaseProcessRecovery.StopForRecoveryAsync(instance, () => true, () => stopped = true)
                  && stopped && parent.HasExited && child.HasExited,
                "Recovery must wait for the old player and all of its renderers before permitting relaunch.");
            Check(!CompanionProcessLifetime.HasRunningProcess([parent, child]),
                "Exited processes whose handles are still open must not prevent the replacement player from starting.");
        }
        finally
        {
            if (!parent.HasExited) { parent.Kill(entireProcessTree: true); await parent.WaitForExitAsync(); }
        }
        Console.WriteLine("NetEase process recovery passed: isolated process tree, last-moment pause check, process identity, renderer exit wait.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
