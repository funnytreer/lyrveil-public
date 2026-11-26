using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using LyricsXQuiet.App.State;
using LyricsXQuiet.Core.Playback;
using LyricsXQuiet.Core.Settings;
using LyricsXQuiet.Platform.Windows.Companion;

namespace LyricsXQuiet.App;

public partial class App
{
    private readonly DispatcherTimer _netEaseRecoveryTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly NetEaseRecoveryPolicy _netEaseRecoveryPolicy = new();
    private Task _netEaseRecoveryTask = Task.CompletedTask;
    private AppState? _netEaseRecoveryState;
    private DateTimeOffset _lastShortcutMaintenance;
    private bool _recoveringNetEase;

    private static bool NeedsPlayerMonitor(AppSettings settings) =>
        PlayerFollowModes.UsesMonitor(settings.PlayerFollowMode);

    private void StartNetEaseRecovery(AppState state)
    {
        _netEaseRecoveryState = state;
        _netEaseRecoveryTimer.Tick += OnNetEaseRecoveryTimer;
        _netEaseRecoveryTimer.Start();
    }

    private void OnNetEaseRecoveryTimer(object? sender, EventArgs args)
    {
        if (_netEaseRecoveryTask.IsCompleted && !_shuttingDown)
            _netEaseRecoveryTask = CheckNetEaseRecoveryAsync();
    }

    private bool CanRecoverPausedNetEase()
    {
        return !_shuttingDown && NetEaseShortcutMaintenance.IsEnabled(_settings)
            && NetEaseRecoveryPolicy.IsSafelyPaused(_nativePlaybackService?.Current, DateTimeOffset.Now);
    }

    private void SetNetEaseRecoveryStatus(string value)
    {
        if (_netEaseRecoveryState is not null) _netEaseRecoveryState.NetEaseRecoveryStatus = value;
    }

    private async Task CheckNetEaseRecoveryAsync()
    {
        if (!NetEaseShortcutMaintenance.IsEnabled(_settings))
        {
            _netEaseRecoveryPolicy.Reset();
            SetNetEaseRecoveryStatus("");
            return;
        }
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastShortcutMaintenance >= TimeSpan.FromSeconds(10))
            {
                _lastShortcutMaintenance = now;
                await Task.Run(NetEaseShortcutMaintenance.Repair);
            }
            var instance = await Task.Run(NetEaseProcessRecovery.FindMainInstance);
            if (_shuttingDown || !NetEaseShortcutMaintenance.IsEnabled(_settings)) return;
            var updating = instance is { HasEndpoint: false } && CanRecoverPausedNetEase()
                && await Task.Run(() => CompanionUpdateDetectionService.IsUpdateActivityPresent(
                    MusicPlayerCatalog.SupportedPlayers.Single(player => player.Id == "netease"),
                    instance.ExecutablePath));
            var native = _nativePlaybackService?.Current;
            var nativePrecise = native?.TimelineKind == PlaybackTimelineKind.Reported
                && MusicPlayerCatalog.Identify(native.Track.SourceAppUserModelId)?.Id == "netease";
            var shouldRecover = _netEaseRecoveryPolicy.Observe(instance?.Identity,
                instance?.HasEndpoint == true || nativePrecise, CanRecoverPausedNetEase(), updating, DateTimeOffset.UtcNow);
            if (instance is null || instance.HasEndpoint || nativePrecise)
            {
                SetNetEaseRecoveryStatus("");
                return;
            }
            if (!shouldRecover)
            {
                SetNetEaseRecoveryStatus(_netEaseRecoveryPolicy.Attempted
                    ? "自动恢复未成功，已停止重复重启"
                    : updating ? "等待网易云更新完成后恢复同步"
                    : CanRecoverPausedNetEase() ? "网易云已暂停，即将自动恢复同步"
                    : "网易云精确进度未连接，暂停后自动恢复");
                return;
            }

            await _companionLaunchGate.WaitAsync();
            _recoveringNetEase = true;
            try
            {
                if (!CanRecoverPausedNetEase())
                {
                    _netEaseRecoveryPolicy.Reset();
                    return;
                }
                SetNetEaseRecoveryStatus("正在重新打开网易云并恢复同步…");
                WriteNetEaseRecoveryLog($"Recovering player {instance.ProcessId}; Lyrveil {Environment.ProcessId}.");
                var stopped = false;
                try
                {
                    await NetEaseProcessRecovery.StopForRecoveryAsync(instance,
                        CanRecoverPausedNetEase, () =>
                        {
                            stopped = true;
                            WriteNetEaseRecoveryLog("Paused player stopped; waiting for old renderers.");
                        });
                }
                finally
                {
                    // Once stopped, always attempt to return the player, including
                    // when exit waiting fails or Lyrveil is closing concurrently.
                    if (stopped && CompanionLaunchRequest.TryCreate(instance.ExecutablePath, true, out var request))
                    {
                        var result = await LaunchValidatedCompanionAsync(request!, CancellationToken.None);
                        WriteNetEaseRecoveryLog($"Replacement launch: {result}.");
                    }
                }
                if (!stopped) _netEaseRecoveryPolicy.Reset();
            }
            finally
            {
                _recoveringNetEase = false;
                _companionLaunchGate.Release();
            }
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("NetEase automatic recovery failed: {0}", exception);
            WriteNetEaseRecoveryLog(exception.ToString());
            SetNetEaseRecoveryStatus("网易云自动恢复未完成，可在设置中查看连接状态");
        }
    }

    private async Task StopNetEaseRecoveryAsync()
    {
        _netEaseRecoveryTimer.Stop();
        _netEaseRecoveryTimer.Tick -= OnNetEaseRecoveryTimer;
        await _netEaseRecoveryTask;
    }

    private static void WriteNetEaseRecoveryLog(string message)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LyricsXQuiet");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "netease-recovery.log");
            if (File.Exists(path) && new FileInfo(path).Length > 65536) File.WriteAllText(path, "");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
