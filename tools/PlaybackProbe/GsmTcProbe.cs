using System.Collections.Concurrent;
using Windows.Media.Control;

namespace LyricsXQuiet.Tools.PlaybackProbe;

internal sealed class GsmTcProbe : IAsyncDisposable
{
    private readonly GlobalSystemMediaTransportControlsSessionManager _manager;
    private readonly ConcurrentDictionary<string, ObservedSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);
    private bool _started;

    private GsmTcProbe(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        _manager = manager;
    }

    public event EventHandler<ProbeRecord>? RecordProduced;

    public static async Task<GsmTcProbe> CreateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return new GsmTcProbe(manager);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _manager.SessionsChanged += OnSessionsChanged;
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        await ReconcileAsync("initial", cancellationToken);
    }

    public IReadOnlyList<PlaybackSnapshot> GetSnapshots() =>
        _sessions.Values
            .Select(session => session.LatestSnapshot)
            .Where(snapshot => snapshot is not null)
            .Cast<PlaybackSnapshot>()
            .OrderByDescending(snapshot => snapshot.IsCurrent)
            .ThenBy(snapshot => snapshot.SourceAppUserModelId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public PlaybackSnapshot? GetCurrentSnapshot() =>
        GetSnapshots().FirstOrDefault(snapshot => snapshot.IsCurrent);

    private void OnSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args) => QueueReconcile("sessions-changed");

    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args) => QueueReconcile("current-session-changed");

    private void QueueReconcile(string reason)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ReconcileAsync(reason, CancellationToken.None);
            }
            catch (Exception exception)
            {
                Emit(ProbeRecord.Error($"处理 {reason} 失败。", exception));
            }
        });
    }

    private async Task ReconcileAsync(string reason, CancellationToken cancellationToken)
    {
        await _reconcileLock.WaitAsync(cancellationToken);
        try
        {
            var available = _manager.GetSessions().ToArray();
            var availableIds = available
                .Select(session => session.SourceAppUserModelId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in _sessions.ToArray())
            {
                if (!availableIds.Contains(pair.Key) && _sessions.TryRemove(pair.Key, out var removed))
                {
                    await removed.DisposeAsync();
                    Emit(ProbeRecord.Status($"会话已移除：{pair.Key}"));
                }
            }

            foreach (var session in available)
            {
                if (_sessions.ContainsKey(session.SourceAppUserModelId))
                {
                    continue;
                }

                var observed = new ObservedSession(session, IsCurrentSession);
                observed.RecordProduced += OnObservedRecordProduced;
                if (_sessions.TryAdd(session.SourceAppUserModelId, observed))
                {
                    await observed.StartAsync(reason, cancellationToken);
                }
                else
                {
                    await observed.DisposeAsync();
                }
            }

            foreach (var observed in _sessions.Values)
            {
                await observed.RefreshAsync(reason, cancellationToken);
            }

            if (available.Length == 0)
            {
                Emit(ProbeRecord.Status("当前没有 GSMTC 媒体会话。"));
            }
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    private bool IsCurrentSession(GlobalSystemMediaTransportControlsSession session)
    {
        var current = _manager.GetCurrentSession();
        return current is not null && string.Equals(
            current.SourceAppUserModelId,
            session.SourceAppUserModelId,
            StringComparison.OrdinalIgnoreCase);
    }

    private void OnObservedRecordProduced(object? sender, ProbeRecord record) => Emit(record);

    private void Emit(ProbeRecord record) => RecordProduced?.Invoke(this, record);

    public async ValueTask DisposeAsync()
    {
        if (_started)
        {
            _manager.SessionsChanged -= OnSessionsChanged;
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        }

        foreach (var observed in _sessions.Values)
        {
            await observed.DisposeAsync();
        }

        _sessions.Clear();
        _reconcileLock.Dispose();
    }

    private sealed class ObservedSession : IAsyncDisposable
    {
        private readonly GlobalSystemMediaTransportControlsSession _session;
        private readonly Func<GlobalSystemMediaTransportControlsSession, bool> _isCurrent;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);
        private bool _started;

        public ObservedSession(
            GlobalSystemMediaTransportControlsSession session,
            Func<GlobalSystemMediaTransportControlsSession, bool> isCurrent)
        {
            _session = session;
            _isCurrent = isCurrent;
        }

        public PlaybackSnapshot? LatestSnapshot { get; private set; }

        public event EventHandler<ProbeRecord>? RecordProduced;

        public Task StartAsync(string reason, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_started)
            {
                _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
                _started = true;
            }

            return Task.CompletedTask;
        }

        public async Task RefreshAsync(string reason, CancellationToken cancellationToken)
        {
            await _refreshLock.WaitAsync(cancellationToken);
            try
            {
                var capturedAt = DateTimeOffset.Now;
                var media = await _session.TryGetMediaPropertiesAsync();
                cancellationToken.ThrowIfCancellationRequested();
                var playback = _session.GetPlaybackInfo();
                var timeline = _session.GetTimelineProperties();
                var controls = playback.Controls;

                LatestSnapshot = new PlaybackSnapshot(
                    _session.SourceAppUserModelId,
                    _isCurrent(_session),
                    media.Title ?? string.Empty,
                    media.Artist ?? string.Empty,
                    media.AlbumTitle ?? string.Empty,
                    media.TrackNumber,
                    playback.PlaybackStatus,
                    playback.PlaybackRate ?? 1d,
                    timeline.Position,
                    timeline.StartTime,
                    timeline.EndTime,
                    timeline.LastUpdatedTime,
                    capturedAt,
                    controls.IsPlayEnabled,
                    controls.IsPauseEnabled,
                    controls.IsPlaybackPositionEnabled,
                    controls.IsNextEnabled,
                    controls.IsPreviousEnabled);

                RecordProduced?.Invoke(this, ProbeRecord.FromSnapshot(reason, LatestSnapshot));
            }
            catch (Exception exception)
            {
                RecordProduced?.Invoke(this, ProbeRecord.Error(
                    $"读取媒体会话 {_session.SourceAppUserModelId} 失败。",
                    exception));
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private void OnMediaPropertiesChanged(
            GlobalSystemMediaTransportControlsSession sender,
            MediaPropertiesChangedEventArgs args) => QueueRefresh("media-properties-changed");

        private void OnPlaybackInfoChanged(
            GlobalSystemMediaTransportControlsSession sender,
            PlaybackInfoChangedEventArgs args) => QueueRefresh("playback-info-changed");

        private void OnTimelinePropertiesChanged(
            GlobalSystemMediaTransportControlsSession sender,
            TimelinePropertiesChangedEventArgs args) => QueueRefresh("timeline-properties-changed");

        private void QueueRefresh(string reason)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await RefreshAsync(reason, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    RecordProduced?.Invoke(this, ProbeRecord.Error(
                        $"处理 {_session.SourceAppUserModelId} 的 {reason} 失败。",
                        exception));
                }
            });
        }

        public ValueTask DisposeAsync()
        {
            if (_started)
            {
                _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            }

            _refreshLock.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
