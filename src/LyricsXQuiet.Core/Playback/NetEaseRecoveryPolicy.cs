namespace LyricsXQuiet.Core.Playback;

/// <summary>Debounces a paused recovery and prevents restart loops after a failed attempt.</summary>
public sealed class NetEaseRecoveryPolicy
{
    public static readonly TimeSpan PauseGrace = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan ClosedGrace = TimeSpan.FromSeconds(15);
    private string? _instance;
    private DateTimeOffset? _pausedSince;
    private DateTimeOffset? _absentSince;
    public bool Attempted { get; private set; }

    public static bool IsSafelyPaused(PlaybackSnapshot? snapshot, DateTimeOffset now)
    {
        var age = snapshot is null ? TimeSpan.MaxValue : now - snapshot.CapturedAt;
        return snapshot is { State: PlaybackState.Paused }
            && snapshot.TimelineKind != PlaybackTimelineKind.Reported
            && MusicPlayerCatalog.Identify(snapshot.Track.SourceAppUserModelId)?.Id == "netease"
            && age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(5);
    }

    public bool Observe(string? instance, bool hasEndpoint, bool safelyPaused,
        bool updateInProgress, DateTimeOffset now)
    {
        if (instance is null)
        {
            _instance = null;
            _pausedSince = null;
            _absentSince ??= now;
            if (now - _absentSince >= ClosedGrace) Reset();
            return false;
        }
        _absentSince = null;
        if (hasEndpoint)
        {
            Reset();
            return false;
        }
        if (_instance != instance)
        {
            _instance = instance;
            _pausedSince = null;
        }
        if (Attempted) return false;
        if (!safelyPaused || updateInProgress)
        {
            _pausedSince = null;
            return false;
        }
        _pausedSince ??= now;
        if (now - _pausedSince < PauseGrace) return false;
        Attempted = true;
        _pausedSince = null;
        return true;
    }

    public void Reset()
    {
        _instance = null;
        _pausedSince = null;
        _absentSince = null;
        Attempted = false;
    }
}
