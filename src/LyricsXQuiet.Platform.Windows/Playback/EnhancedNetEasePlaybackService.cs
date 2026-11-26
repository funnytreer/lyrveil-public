using LyricsXQuiet.Core.Playback;
using System.Text;

namespace LyricsXQuiet.Platform.Windows.Playback;

public sealed class EnhancedNetEasePlaybackService : IPlaybackService
{
    private readonly IPlaybackService _nativeService;
    private readonly INetEaseBridgeSnapshotSource _bridgeSource;
    private readonly object _stateLock = new();
    private readonly Queue<PlaybackSnapshot?> _pendingPublications = new();
    private PlaybackSnapshot? _lastAcceptedEnhancedSnapshot;
    private PlaybackSnapshot? _fallbackClock;
    private bool _isPublishing;
    private bool _started;
    private bool _disposed;

    public EnhancedNetEasePlaybackService(
        IPlaybackService nativeService,
        INetEaseBridgeSnapshotSource bridgeSource)
    {
        _nativeService = nativeService ?? throw new ArgumentNullException(nameof(nativeService));
        _bridgeSource = bridgeSource ?? throw new ArgumentNullException(nameof(bridgeSource));
        _nativeService.SnapshotChanged += OnNativeSnapshotChanged;
        _nativeService.ConnectionStatusChanged += OnConnectionStatusChanged;
        _bridgeSource.SnapshotChanged += OnBridgeSnapshotChanged;
    }

    public event EventHandler<PlaybackSnapshot?>? SnapshotChanged;

    public event EventHandler<PlaybackConnectionStatus>? ConnectionStatusChanged;

    public PlaybackSnapshot? Current { get; private set; }

    public PlaybackConnectionStatus ConnectionStatus => _nativeService.ConnectionStatus;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        try
        {
            await _bridgeSource.StartAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _started = false;
            throw;
        }
        catch
        {
            // The optional NetEase bridge must never prevent the native Windows
            // media-session service (and every other supported player) from working.
        }

        try
        {
            await _nativeService.StartAsync(cancellationToken);
            PublishMerged(_nativeService.Current);
        }
        catch
        {
            _started = false;
            await DisposeAsync();
            throw;
        }
    }

    private void OnNativeSnapshotChanged(object? sender, PlaybackSnapshot? snapshot) =>
        PublishMerged(snapshot);

    private void OnBridgeSnapshotChanged(object? sender, NetEaseBridgeSnapshot? snapshot) =>
        PublishMerged(_nativeService.Current);

    private void OnConnectionStatusChanged(
        object? sender,
        PlaybackConnectionStatus status) => ConnectionStatusChanged?.Invoke(this, status);

    private void PublishMerged(PlaybackSnapshot? nativeSnapshot)
    {
        var shouldDrain = false;
        lock (_stateLock)
        {
            var now = DateTimeOffset.Now;
            var next = MergeOrEstimate(nativeSnapshot, now);
            Current = next;
            _pendingPublications.Enqueue(next);
            if (!_isPublishing)
            {
                _isPublishing = true;
                shouldDrain = true;
            }
        }

        if (shouldDrain)
        {
            DrainPublications();
        }
    }

    private void DrainPublications()
    {
        while (true)
        {
            PlaybackSnapshot? next;
            lock (_stateLock)
            {
                if (_pendingPublications.Count == 0)
                {
                    _isPublishing = false;
                    return;
                }

                next = _pendingPublications.Dequeue();
            }

            try
            {
                SnapshotChanged?.Invoke(this, next);
            }
            catch
            {
                lock (_stateLock)
                {
                    _pendingPublications.Clear();
                    _isPublishing = false;
                }

                throw;
            }
        }
    }

    private PlaybackSnapshot? MergeOrEstimate(
        PlaybackSnapshot? nativeSnapshot,
        DateTimeOffset now)
    {
        if (nativeSnapshot is null)
        {
            ClearEnhancedClock();
            return null;
        }

        var merged = NetEaseBridgeSnapshotMerger.Merge(
            nativeSnapshot,
            _bridgeSource.Current,
            now);
        if (!ReferenceEquals(merged, nativeSnapshot))
        {
            _lastAcceptedEnhancedSnapshot = merged;
            _fallbackClock = merged;
            return merged;
        }

        if (_lastAcceptedEnhancedSnapshot is null
            || _fallbackClock is null
            || !CanContinueEnhancedClock(
                _lastAcceptedEnhancedSnapshot.Track,
                nativeSnapshot.Track)
            || nativeSnapshot.State is not (PlaybackState.Playing or PlaybackState.Paused))
        {
            ClearEnhancedClock();
            return nativeSnapshot;
        }

        var position = _fallbackClock.EstimatePosition(now);
        var playbackRate = nativeSnapshot.PlaybackRate > 0
            ? nativeSnapshot.PlaybackRate
            : _fallbackClock.PlaybackRate > 0
                ? _fallbackClock.PlaybackRate
                : 1d;
        var estimated = new PlaybackSnapshot(
            _lastAcceptedEnhancedSnapshot.Track,
            nativeSnapshot.State,
            position,
            playbackRate,
            now,
            PlaybackTimelineKind.EnhancedEstimated);
        _fallbackClock = estimated;
        return estimated;
    }

    private void ClearEnhancedClock()
    {
        _lastAcceptedEnhancedSnapshot = null;
        _fallbackClock = null;
    }

    private static bool CanContinueEnhancedClock(
        TrackIdentity enhancedTrack,
        TrackIdentity nativeTrack)
    {
        if (MusicPlayerCatalog.Identify(nativeTrack.SourceAppUserModelId)?.Id != "netease"
            || !string.Equals(
                Normalize(enhancedTrack.Title),
                Normalize(nativeTrack.Title),
                StringComparison.Ordinal)
            || !string.Equals(
                Normalize(enhancedTrack.Artist),
                Normalize(nativeTrack.Artist),
                StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(enhancedTrack.SourceTrackId)
            && !string.IsNullOrWhiteSpace(nativeTrack.SourceTrackId)
            && !string.Equals(
                enhancedTrack.SourceTrackId,
                nativeTrack.SourceTrackId,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(enhancedTrack.Album)
            && !string.IsNullOrWhiteSpace(nativeTrack.Album)
            && !string.Equals(
                Normalize(enhancedTrack.Album),
                Normalize(nativeTrack.Album),
                StringComparison.Ordinal))
        {
            return false;
        }

        return enhancedTrack.Duration <= TimeSpan.Zero
            || nativeTrack.Duration <= TimeSpan.Zero
            || (enhancedTrack.Duration - nativeTrack.Duration).Duration()
                <= TimeSpan.FromSeconds(3);
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value
            .Trim()
            .ToLowerInvariant()
            .Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _nativeService.SnapshotChanged -= OnNativeSnapshotChanged;
        _nativeService.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _bridgeSource.SnapshotChanged -= OnBridgeSnapshotChanged;
        await _bridgeSource.DisposeAsync();
        await _nativeService.DisposeAsync();
    }
}
