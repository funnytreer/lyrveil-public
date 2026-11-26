using System.ComponentModel;
using System.Diagnostics;
using LyricsXQuiet.Platform.Windows.Playback;

namespace LyricsXQuiet.Platform.Windows.Companion;

public sealed record NetEaseProcessInstance(int ProcessId, DateTime StartTimeUtc,
    string ExecutablePath, bool HasEndpoint)
{
    public string Identity => $"{ProcessId}:{StartTimeUtc.Ticks}";
}

public static class NetEaseProcessRecovery
{
    public static NetEaseProcessInstance? FindMainInstance()
    {
        var processes = Process.GetProcessesByName("cloudmusic");
        try
        {
            var candidates = new List<NetEaseProcessInstance>();
            foreach (var process in processes)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    var command = NetEaseCdpPortDiscovery.TryReadCommandLine(process.Id);
                    if (path is null || command is null
                        || !string.Equals(Path.GetFileName(path), "cloudmusic.exe", StringComparison.OrdinalIgnoreCase)
                        || command.Contains("--type=", StringComparison.OrdinalIgnoreCase)) continue;
                    candidates.Add(new(process.Id, process.StartTime.ToUniversalTime(), path,
                        NetEaseCdpPortDiscovery.TryParseExplicitLoopbackPort(command, out _)));
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
            return candidates.Count == 1 ? candidates[0] : null;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public static async Task<bool> StopForRecoveryAsync(NetEaseProcessInstance instance,
        Func<bool> stillSafelyPaused, Action onStopped)
    {
        using var process = Process.GetProcessById(instance.ProcessId);
        if (process.HasExited || process.StartTime.ToUniversalTime() != instance.StartTimeUtc
            || !string.Equals(process.MainModule?.FileName, instance.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(process.ProcessName, "cloudmusic", StringComparison.OrdinalIgnoreCase)) return false;
        var command = NetEaseCdpPortDiscovery.TryReadCommandLine(process.Id);
        if (command is null || NetEaseCdpPortDiscovery.TryParseExplicitLoopbackPort(command, out _)
            || !stillSafelyPaused()) return false;
        // Close-to-tray does not exit NetEase. This opt-in recovery is only used
        // after a sustained, fresh Paused state and immediately starts it again.
        var oldProcesses = Process.GetProcessesByName("cloudmusic");
        try
        {
            // Wait for the old renderers too: the parent can exit first, and the
            // companion launcher would otherwise mistake a closing renderer for
            // an already-running player and never start the replacement.
            var oldPlayerProcesses = oldProcesses.Where(candidate =>
            {
                try { return string.Equals(candidate.MainModule?.FileName, instance.ExecutablePath, StringComparison.OrdinalIgnoreCase); }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { return false; }
            }).ToArray();
            if (!stillSafelyPaused()) return false;
            // Stop only the validated player image, never arbitrary descendants:
            // a player can have launched Lyrveil or another application itself.
            process.Kill();
            onStopped();
            foreach (var candidate in oldPlayerProcesses.Where(candidate => candidate.Id != instance.ProcessId))
            {
                try { if (!candidate.HasExited) candidate.Kill(); }
                catch (InvalidOperationException) { /* Already gone. */ }
            }
            await Task.WhenAll(oldPlayerProcesses.Select(candidate => candidate.WaitForExitAsync()))
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { foreach (var candidate in oldProcesses) candidate.Dispose(); }
        return true;
    }
}
