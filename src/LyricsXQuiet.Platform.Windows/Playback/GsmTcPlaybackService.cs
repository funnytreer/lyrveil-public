using System.Diagnostics;
using System.Runtime.InteropServices;
using LyricsXQuiet.Core.Playback;
using Windows.Media.Control;

namespace LyricsXQuiet.Platform.Windows.Playback;

public sealed class GsmTcPlaybackService : IPlaybackService
{
    private static readonly TimeSpan EstimatedTimelineReconcileInterval =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReportedPlayingReconcileInterval =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleReconcileInterval =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NoSessionReconcileInterval =
        TimeSpan.FromSeconds(10);
    private const long FullSessionRefreshIntervalMilliseconds = 30_000;

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly List<GlobalSystemMediaTransportControlsSession> _observedSessions = [];
    private readonly ProcessAudioActivityMonitor _audioActivityMonitor = new();
    private readonly AppleMusicTimelineQuantizationCompensator
        _appleMusicTimelineCompensator = new();
    private IReadOnlySet<string> _currentlyAudibleProcessNames = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase);
    private IReadOnlySet<string> _recentlyAudibleProcessNames = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private Task? _reconcileTask;
    private long _lastSessionRefreshTick;
    private int _sessionsDirty = 1;
    private int _sessionListChanged = 1;
    private bool _started;
    private bool _disposed;

    public event EventHandler<PlaybackSnapshot?>? SnapshotChanged;

    public event EventHandler<PlaybackConnectionStatus>? ConnectionStatusChanged;

    public PlaybackSnapshot? Current { get; private set; }

    public PlaybackConnectionStatus ConnectionStatus { get; private set; } =
        PlaybackConnectionStatus.Waiting;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        cancellationToken.ThrowIfCancellationRequested();
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        _manager.SessionsChanged += OnSessionsChanged;
        _started = true;
        await SelectCurrentSessionAsync(cancellationToken);
        _reconcileTask = ReconcilePeriodicallyAsync(_lifetimeCancellation.Token);
    }

    public void RefreshConfiguredPlayers()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MarkSessionsDirty(forceSelectedSessionRefresh: true);
        if (_started)
        {
            QueueSelectCurrentSession();
        }
    }

    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args) => QueueSelectCurrentSession();

    private void OnSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args)
    {
        MarkSessionsDirty(forceSelectedSessionRefresh: true);
        QueueSelectCurrentSession();
    }

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args) => QueueRefresh();

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args) => QueueSelectCurrentSession();

    private void OnTimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args) => QueueRefresh();

    private void QueueSelectCurrentSession() => _ = Task.Run(async () =>
    {
        try
        {
            await SelectCurrentSessionAsync(_lifetimeCancellation.Token);
        }
        catch (Exception exception) when (IsExpectedShutdown(exception))
        {
        }
        catch
        {
            // A later event or the low-frequency reconciliation loop will retry.
        }
    });

    private void QueueRefresh() => _ = Task.Run(async () =>
    {
        try
        {
            await RefreshAsync(_lifetimeCancellation.Token);
        }
        catch (Exception exception) when (IsExpectedShutdown(exception))
        {
        }
        catch
        {
            // A later event or the low-frequency reconciliation loop will retry.
        }
    });

    private async Task ReconcilePeriodicallyAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(GetReconcileInterval(), cancellationToken);
                if (Current is null || IsFullSessionRefreshDue())
                {
                    MarkSessionsDirty();
                }

                try
                {
                    await SelectCurrentSessionAsync(cancellationToken);
                }
                catch (Exception exception) when (IsExpectedShutdown(exception))
                {
                    return;
                }
                catch
                {
                    // Keep the service self-healing after a transient WinRT failure.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private TimeSpan GetReconcileInterval()
    {
        var current = Current;
        if (current is null)
        {
            return NoSessionReconcileInterval;
        }

        if (current.TimelineKind != PlaybackTimelineKind.Reported)
        {
            // Players without a usable GSMTC timeline may also publish an incorrect
            // playback state, so keep their lightweight audio fallback responsive.
            return EstimatedTimelineReconcileInterval;
        }

        return current.State == PlaybackState.Playing
            ? ReportedPlayingReconcileInterval
            : IdleReconcileInterval;
    }

    private bool IsFullSessionRefreshDue()
    {
        var lastRefreshTick = Volatile.Read(ref _lastSessionRefreshTick);
        return lastRefreshTick == 0
            || Environment.TickCount64 - lastRefreshTick
                >= FullSessionRefreshIntervalMilliseconds;
    }

    private void MarkSessionsDirty(bool forceSelectedSessionRefresh = false)
    {
        Interlocked.Exchange(ref _sessionsDirty, 1);
        if (forceSelectedSessionRefresh)
        {
            Interlocked.Exchange(ref _sessionListChanged, 1);
        }
    }

    private async Task SelectCurrentSessionAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (_manager is null)
            {
                return;
            }

            var forceSelectedSessionRefresh = RefreshObservedSessionsIfNeededUnsafe();
            var supportedSessions = _observedSessions.ToArray();
            var audioActivity = _audioActivityMonitor.GetActivity(DateTimeOffset.Now);
            _currentlyAudibleProcessNames =
                audioActivity.CurrentlyAudibleProcessNames;
            _recentlyAudibleProcessNames =
                audioActivity.RecentlyAudibleProcessNames;

            var windowsCurrentSession = _manager.GetCurrentSession();
            var next = SelectSupportedSession(
                windowsCurrentSession,
                supportedSessions,
                _recentlyAudibleProcessNames);
            var nextHasHigherPriority = next is not null
                && _session is not null
                && GetSessionScore(
                    next,
                    windowsCurrentSession,
                    supportedSessions,
                    _recentlyAudibleProcessNames)
                    > GetSessionScore(
                        _session,
                        windowsCurrentSession,
                        supportedSessions,
                        _recentlyAudibleProcessNames);
            if (!ReferenceEquals(next, _session)
                && (!HasSameSessionIdentity(next, _session, supportedSessions)
                    || forceSelectedSessionRefresh
                    || nextHasHigherPriority))
            {
                DetachSession();
                _session = next;
                AttachSession();
            }

            await RefreshUnsafeAsync(cancellationToken);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private bool RefreshObservedSessionsIfNeededUnsafe()
    {
        if (Interlocked.Exchange(ref _sessionsDirty, 0) == 0)
        {
            return false;
        }

        var forceSelectedSessionRefresh =
            Interlocked.Exchange(ref _sessionListChanged, 0) != 0;
        try
        {
            var supportedSessions = _manager!.GetSessions()
                .Where(session => MusicPlayerCatalog.IsSupported(session.SourceAppUserModelId))
                .ToArray();
            ReplaceObservedSessions(supportedSessions);
            Volatile.Write(ref _lastSessionRefreshTick, Environment.TickCount64);
            return forceSelectedSessionRefresh;
        }
        catch
        {
            MarkSessionsDirty(forceSelectedSessionRefresh);
            throw;
        }
    }

    private static bool HasSameSessionIdentity(
        GlobalSystemMediaTransportControlsSession? left,
        GlobalSystemMediaTransportControlsSession? right,
        IReadOnlyCollection<GlobalSystemMediaTransportControlsSession> supportedSessions)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        var sourceMatches = string.Equals(
            left.SourceAppUserModelId,
            right.SourceAppUserModelId,
            StringComparison.OrdinalIgnoreCase);
        return GsmTcSessionArbitrationPolicy.IsWindowsCurrentSession(
            new GsmTcCurrentSessionIdentitySignals(
                IsSameNativeSession: false,
                SourceAppUserModelIdMatches: sourceMatches,
                MatchingSourceSessionCount: CountSessionsWithSource(
                    supportedSessions,
                    left.SourceAppUserModelId)));
    }

    private static GlobalSystemMediaTransportControlsSession? SelectSupportedSession(
        GlobalSystemMediaTransportControlsSession? windowsCurrentSession,
        IReadOnlyCollection<GlobalSystemMediaTransportControlsSession> supportedSessions,
        IReadOnlySet<string> audibleProcessNames)
    {
        if (supportedSessions.Count == 0)
        {
            return null;
        }

        return supportedSessions
            .OrderByDescending(session => GetSessionScore(
                session,
                windowsCurrentSession,
                supportedSessions,
                audibleProcessNames))
            .ThenBy(
                session => session.SourceAppUserModelId,
                StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static int GetSessionScore(
        GlobalSystemMediaTransportControlsSession session,
        GlobalSystemMediaTransportControlsSession? windowsCurrentSession,
        IReadOnlyCollection<GlobalSystemMediaTransportControlsSession> supportedSessions,
        IReadOnlySet<string> audibleProcessNames)
    {
        var sourceMatches = windowsCurrentSession is not null
            && string.Equals(
                session.SourceAppUserModelId,
                windowsCurrentSession.SourceAppUserModelId,
                StringComparison.OrdinalIgnoreCase);
        var isWindowsCurrent = GsmTcSessionArbitrationPolicy.IsWindowsCurrentSession(
            new GsmTcCurrentSessionIdentitySignals(
                ReferenceEquals(session, windowsCurrentSession),
                sourceMatches,
                CountSessionsWithSource(
                    supportedSessions,
                    session.SourceAppUserModelId)));
        return GsmTcSessionArbitrationPolicy.GetScore(
            new GsmTcSessionArbitrationSignals(
                GetReportedState(session),
                IsAudiblyPlaying(session, audibleProcessNames),
                HasUsableTimeline(session),
                isWindowsCurrent));
    }

    private static int CountSessionsWithSource(
        IReadOnlyCollection<GlobalSystemMediaTransportControlsSession> sessions,
        string sourceAppUserModelId) => sessions.Count(session => string.Equals(
            session.SourceAppUserModelId,
            sourceAppUserModelId,
            StringComparison.OrdinalIgnoreCase));

    private static bool IsAudiblyPlaying(
        GlobalSystemMediaTransportControlsSession session,
        IReadOnlySet<string> audibleProcessNames)
    {
        var player = MusicPlayerCatalog.Identify(session.SourceAppUserModelId);
        return player is not null && audibleProcessNames.Contains(player.ProcessName);
    }

    private static bool HasUsableTimeline(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var timeline = session.GetTimelineProperties();
            var duration = timeline.EndTime > timeline.StartTime
                ? timeline.EndTime - timeline.StartTime
                : TimeSpan.Zero;
            var position = timeline.Position >= timeline.StartTime
                ? timeline.Position - timeline.StartTime
                : TimeSpan.FromTicks(-1);
            return PlaybackClockCalibrator.HasUsableTimeline(
                duration,
                position,
                timeline.LastUpdatedTime,
                DateTimeOffset.Now);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or UnauthorizedAccessException or COMException)
        {
            return false;
        }
    }

    private static PlaybackState GetReportedState(
        GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return MapState(session.GetPlaybackInfo().PlaybackStatus);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or UnauthorizedAccessException
                or COMException
                or ObjectDisposedException)
        {
            return PlaybackState.Unknown;
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            await RefreshUnsafeAsync(cancellationToken);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task RefreshUnsafeAsync(CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            PublishConnectionStatus(DetectConnectionStatus());
            Current = null;
            SnapshotChanged?.Invoke(this, null);
            return;
        }

        var media = await _session.TryGetMediaPropertiesAsync();
        cancellationToken.ThrowIfCancellationRequested();
        var playback = _session.GetPlaybackInfo();
        var timeline = _session.GetTimelineProperties();
        var capturedAt = DateTimeOffset.Now;
        var duration = timeline.EndTime > timeline.StartTime
            ? timeline.EndTime - timeline.StartTime
            : timeline.EndTime;
        var position = timeline.Position >= timeline.StartTime
            ? timeline.Position - timeline.StartTime
            : TimeSpan.FromTicks(-1);
        var track = PlaybackMediaMetadataNormalizer.CreateTrack(
            _session.SourceAppUserModelId,
            media.Title,
            media.Artist,
            media.AlbumTitle,
            duration,
            media.Genres);

        var state = MapState(playback.PlaybackStatus);
        state = PlaybackStateTrustPolicy.ApplyAudioFallback(
            state,
            MusicPlayerCatalog.Identify(_session.SourceAppUserModelId)?.Id,
            HasUsableTimeline(_session),
            IsAudiblyPlaying(_session, _currentlyAudibleProcessNames));

        position = _appleMusicTimelineCompensator.Compensate(
            track,
            state,
            position,
            timeline.LastUpdatedTime,
            capturedAt);

        Current = PlaybackClockCalibrator.Reconcile(
            Current,
            track,
            state,
            position,
            playback.PlaybackRate,
            timeline.LastUpdatedTime,
            capturedAt);

        PublishConnectionStatus(new PlaybackConnectionStatus(
            PlaybackConnectionState.Connected,
            MusicPlayerCatalog.Identify(_session.SourceAppUserModelId)));
        SnapshotChanged?.Invoke(this, Current);
    }

    private static PlaybackConnectionStatus DetectConnectionStatus()
    {
        foreach (var player in MusicPlayerCatalog.DetectablePlayers)
        {
            if (IsProcessRunning(player.ProcessName))
            {
                return new PlaybackConnectionStatus(
                    PlaybackConnectionState.PlayerNeedsMediaSession,
                    player);
            }
        }

        return PlaybackConnectionStatus.Waiting;
    }

    private static bool IsProcessRunning(string processName)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch
        {
            return false;
        }

        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private void PublishConnectionStatus(PlaybackConnectionStatus status)
    {
        if (status == ConnectionStatus)
        {
            return;
        }

        ConnectionStatus = status;
        ConnectionStatusChanged?.Invoke(this, status);
    }

    private bool IsExpectedShutdown(Exception exception) =>
        exception is ObjectDisposedException
        || exception is OperationCanceledException && _lifetimeCancellation.IsCancellationRequested;

    private void AttachSession()
    {
        if (_session is null)
        {
            return;
        }

        _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
    }

    private void DetachSession()
    {
        if (_session is null)
        {
            return;
        }

        _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
    }

    private void ReplaceObservedSessions(
        IEnumerable<GlobalSystemMediaTransportControlsSession> sessions)
    {
        var replacements = sessions.ToArray();
        if (ObservedSessionsAreSame(replacements))
        {
            return;
        }

        DetachObservedSessions();
        foreach (var session in replacements)
        {
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _observedSessions.Add(session);
        }
    }

    private bool ObservedSessionsAreSame(
        IReadOnlyCollection<GlobalSystemMediaTransportControlsSession> sessions) =>
        sessions.Count == _observedSessions.Count
        && sessions.All(candidate =>
            _observedSessions.Any(observed => ReferenceEquals(candidate, observed)));

    private void DetachObservedSessions()
    {
        foreach (var session in _observedSessions)
        {
            session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }

        _observedSessions.Clear();
    }

    private static PlaybackState MapState(
        GlobalSystemMediaTransportControlsSessionPlaybackStatus status) => status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PlaybackState.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PlaybackState.Paused,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => PlaybackState.Stopped,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => PlaybackState.Closed,
            _ => PlaybackState.Unknown
        };

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();
        if (_reconcileTask is not null)
        {
            try
            {
                await _reconcileTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager.SessionsChanged -= OnSessionsChanged;
        }

        DetachObservedSessions();
        DetachSession();
        _session = null;
        Current = null;
        _lifetimeCancellation.Dispose();
    }
}
