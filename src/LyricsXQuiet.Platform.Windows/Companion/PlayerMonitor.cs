using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LyricsXQuiet.Core.Playback;
using LyricsXQuiet.Core.Settings;
using Windows.Media.Control;

namespace LyricsXQuiet.Platform.Windows.Companion;

public static class PlayerMonitor
{
    public const string ManualExitEventName = @"Local\Lyrveil.ManualExit";
    public const string MainMutexName = @"Local\Lyrveil.DesktopLyrics.SingleInstance";

    public static void Run()
    {
        using var singleton = new Mutex(false, PlayerMonitorRegistrationService.MonitorMutexName, out var first);
        if (!first) return;
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, PlayerMonitorRegistrationService.StopEventName);
        using var manualExit = new EventWaitHandle(false, EventResetMode.AutoReset, ManualExitEventName);
        Run(new SettingsStore(), stop, manualExit, IsMainRunning, StartMain);
    }

    private static bool IsMainRunning()
    {
        if (!Mutex.TryOpenExisting(MainMutexName, out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    private static void StartMain()
    {
        var path = PlayerMonitorRegistrationService.ResolveAppPath();
        var launch = new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! };
        launch.ArgumentList.Add("--monitor-managed");
        using var started = Process.Start(launch) ?? throw new InvalidOperationException("无法启动词间。");
    }

    internal static void Run(SettingsStore store, WaitHandle stop, WaitHandle manualExit,
        Func<bool> isMainRunning, Action startMain)
    {
        using var changed = new AutoResetEvent(false);
        using var sessions = new AutoResetEvent(false);
        using var cancellation = new CancellationTokenSource();
        if (!File.Exists(store.FilePath)) return;
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(store.FilePath)!, Path.GetFileName(store.FilePath))
        { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        watcher.Changed += (_, _) => Wake(changed);
        watcher.Created += (_, _) => Wake(changed);
        watcher.Deleted += (_, _) => Wake(changed);
        watcher.Renamed += (_, _) => Wake(changed);
        watcher.Error += (_, _) => Wake(changed);
        watcher.EnableRaisingEvents = true;
        var sessionEvents = ListenForSessionsAsync(sessions, cancellation.Token);
        try
        {
            RunLoop(store, stop, manualExit, changed, sessions, isMainRunning, startMain);
        }
        finally
        {
            cancellation.Cancel();
            sessionEvents.GetAwaiter().GetResult();
        }
    }

    private static void RunLoop(SettingsStore store, WaitHandle stop, WaitHandle manualExit,
        WaitHandle changed, WaitHandle sessions, Func<bool> isMainRunning, Action startMain)
    {
        WaitHandle[] events = [stop, manualExit, changed, sessions];
        var policy = new PlayerMonitorLaunchPolicy(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        var lastMaintenance = DateTimeOffset.MinValue;
        bool reload = true;
        bool maintainShortcuts = false;
        string[] all = [];
        HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
        while (!stop.WaitOne(0))
        {
            bool retryRead = false;
            if (reload && store.ReadMonitorConfiguration() is { } settings)
            {
                if (!PlayerFollowModes.UsesMonitor(settings.PlayerFollowMode)) return;
                MusicPlayerCatalog.ConfigureLocalPlayer(settings.LocalPlayerExecutablePath);
                all = MusicPlayerCatalog.DetectablePlayers.Select(p => p.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                selected = MusicPlayerCatalog.SelectForMonitoring(settings.FollowedPlayerIds)
                    .Select(p => p.ProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                maintainShortcuts = NetEaseShortcutMaintenance.IsEnabled(settings.AutoRepairNetEaseSync, settings.PlayerFollowMode, settings.FollowedPlayerIds);
            }
            else if (reload) retryRead = true;
            if (manualExit.WaitOne(0)) policy.SuppressUntilAllPlayersExit();
            var running = PlayerProcessSnapshot.ReadRunning(all);
            if (running is not null)
            {
                var now = DateTimeOffset.UtcNow;
                if (policy.Observe(running.Overlaps(selected), isMainRunning(), now, running.Count > 0)
                    == PlayerMonitorLaunchDecision.StartMainInstance)
                {
                    try
                    {
                        startMain();
                    }
                    catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
                    {
                        policy.ReportLaunchFailure(now);
                    }
                }
            }
            if (maintainShortcuts && DateTimeOffset.UtcNow - lastMaintenance >= TimeSpan.FromSeconds(10))
            {
                lastMaintenance = DateTimeOffset.UtcNow;
                try { NetEaseShortcutMaintenance.Repair(); }
                catch (Exception e) { Trace.TraceWarning("Shortcut maintenance failed: {0}", e); }
            }

            int signal = WaitHandle.WaitAny(events, TimeSpan.FromSeconds(2));
            if (signal == 0) return;
            if (signal == 1) policy.SuppressUntilAllPlayersExit();
            reload = signal == 2 || retryRead;
        }
    }

    private static async Task ListenForSessionsAsync(AutoResetEvent wake, CancellationToken token)
    {
        GlobalSystemMediaTransportControlsSessionManager? manager = null;
        void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => Wake(wake);
        try
        {
            manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(token).ConfigureAwait(false);
            manager.SessionsChanged += OnSessionsChanged;
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException)
        { Trace.TraceInformation("Media session notifications unavailable: {0}", e.Message); }
        finally
        {
            if (manager is not null) manager.SessionsChanged -= OnSessionsChanged;
        }
    }

    private static void Wake(EventWaitHandle signal)
    {
        try { signal.Set(); }
        catch (ObjectDisposedException) { /* A callback was queued just before shutdown. */ }
    }
}
