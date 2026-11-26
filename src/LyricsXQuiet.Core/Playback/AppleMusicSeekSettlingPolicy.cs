namespace LyricsXQuiet.Core.Playback;

public static class AppleMusicSeekSettlingPolicy
{
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(360);
    private static readonly TimeSpan DiscontinuityThreshold = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan PositionClusterTolerance = TimeSpan.FromMilliseconds(900);

    public enum PendingCandidateKind
    {
        NotApplicable,
        Older,
        AcceptedPosition,
        PendingPosition,
        ReplacementPosition
    }

    public static bool ShouldSettle(
        PlaybackSnapshot? accepted,
        PlaybackSnapshot? candidate)
    {
        if (accepted is null
            || candidate is null
            || !BelongsToSameAppleMusicSession(accepted, candidate)
            || candidate.TimelineKind != PlaybackTimelineKind.Reported
            || !IsActiveState(accepted.State)
            || !IsActiveState(candidate.State))
        {
            return false;
        }

        var expectedPosition = accepted.EstimatePosition(candidate.CapturedAt);
        return (candidate.Position - expectedPosition).Duration() >= DiscontinuityThreshold;
    }

    public static bool IsOlderThan(
        PlaybackSnapshot? acceptedOrPending,
        PlaybackSnapshot? candidate) =>
        acceptedOrPending is not null
        && candidate is not null
        && BelongsToSameAppleMusicSession(acceptedOrPending, candidate)
        && acceptedOrPending.TimelineKind == PlaybackTimelineKind.Reported
        && candidate.TimelineKind == PlaybackTimelineKind.Reported
        && IsActiveState(acceptedOrPending.State)
        && IsActiveState(candidate.State)
        // CapturedAt is a playback-clock anchor, not an observation sequence. It is only safe to
        // discard a smaller anchor when the player state itself has not changed. A real pause may
        // legitimately reuse an older timeline anchor.
        && acceptedOrPending.State == candidate.State
        && candidate.CapturedAt < acceptedOrPending.CapturedAt;

    public static PendingCandidateKind ClassifyPendingCandidate(
        PlaybackSnapshot? accepted,
        PlaybackSnapshot? pending,
        PlaybackSnapshot? candidate)
    {
        if (!BelongsToSameAppleMusicTimeline(accepted, pending, candidate))
        {
            return PendingCandidateKind.NotApplicable;
        }

        if (IsOlderThan(pending, candidate))
        {
            return PendingCandidateKind.Older;
        }

        if (IsPositionConsistentWith(accepted!, candidate!))
        {
            return PendingCandidateKind.AcceptedPosition;
        }

        return IsPositionConsistentWith(pending!, candidate!)
            ? PendingCandidateKind.PendingPosition
            : PendingCandidateKind.ReplacementPosition;
    }

    private static bool BelongsToSameAppleMusicTimeline(
        PlaybackSnapshot? accepted,
        PlaybackSnapshot? pending,
        PlaybackSnapshot? candidate) =>
        accepted is not null
        && pending is not null
        && candidate is not null
        && BelongsToSameAppleMusicSession(accepted, pending)
        && BelongsToSameAppleMusicSession(accepted, candidate)
        && accepted.TimelineKind == PlaybackTimelineKind.Reported
        && pending.TimelineKind == PlaybackTimelineKind.Reported
        && candidate.TimelineKind == PlaybackTimelineKind.Reported
        && IsActiveState(accepted.State)
        && IsActiveState(pending.State)
        && IsActiveState(candidate.State);

    private static bool IsPositionConsistentWith(
        PlaybackSnapshot reference,
        PlaybackSnapshot candidate)
    {
        var expected = reference.EstimatePosition(candidate.CapturedAt);
        return (candidate.Position - expected).Duration() <= PositionClusterTolerance;
    }

    private static bool IsActiveState(PlaybackState state) =>
        state is PlaybackState.Playing or PlaybackState.Paused;

    private static bool BelongsToSameAppleMusicSession(
        PlaybackSnapshot first,
        PlaybackSnapshot second)
    {
        if (!IsAppleMusic(first.Track.SourceAppUserModelId)
            || !IsAppleMusic(second.Track.SourceAppUserModelId)
            || !string.Equals(
                first.Track.SourceAppUserModelId,
                second.Track.SourceAppUserModelId,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                first.Track.StableKey,
                second.Track.StableKey,
                StringComparison.Ordinal))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(first.Track.SourceTrackId)
            || string.IsNullOrWhiteSpace(second.Track.SourceTrackId)
            || string.Equals(
                first.Track.SourceTrackId,
                second.Track.SourceTrackId,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAppleMusic(string sourceAppUserModelId) =>
        sourceAppUserModelId.Contains("applemusic", StringComparison.OrdinalIgnoreCase)
        || sourceAppUserModelId.Contains("apple music", StringComparison.OrdinalIgnoreCase);
}
