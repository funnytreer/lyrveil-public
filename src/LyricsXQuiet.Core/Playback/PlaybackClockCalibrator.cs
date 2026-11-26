namespace LyricsXQuiet.Core.Playback;

public static class PlaybackClockCalibrator
{
    private static readonly TimeSpan MaximumTimelineAge = TimeSpan.FromHours(12);
    private static readonly TimeSpan MaximumPositionOverrun = TimeSpan.FromSeconds(2);

    public static PlaybackSnapshot Reconcile(
        PlaybackSnapshot? previous,
        TrackIdentity track,
        PlaybackState state,
        TimeSpan reportedPosition,
        double? reportedPlaybackRate,
        DateTimeOffset timelineUpdatedAt,
        DateTimeOffset capturedAt)
    {
        var playbackRate = NormalizePlaybackRate(reportedPlaybackRate);
        if (HasUsableTimeline(
            track.Duration,
            reportedPosition,
            timelineUpdatedAt,
            capturedAt))
        {
            var stablePosition = AppleMusicProgressStabilityPolicy.Stabilize(
                previous,
                track,
                state,
                reportedPosition,
                capturedAt);
            var anchorTime = stablePosition == reportedPosition
                ? timelineUpdatedAt
                : capturedAt;
            return new PlaybackSnapshot(
                track,
                state,
                Clamp(stablePosition, track.Duration),
                playbackRate,
                anchorTime,
                PlaybackTimelineKind.Reported);
        }

        var isSameTrack = IsSameTrack(previous?.Track, track);
        var position = state is PlaybackState.Stopped or PlaybackState.Closed
            ? TimeSpan.Zero
            : isSameTrack
                ? previous!.EstimatePosition(capturedAt)
                : TimeSpan.Zero;
        var timelineKind = DetermineFallbackTimelineKind(previous, state, isSameTrack);

        return new PlaybackSnapshot(
            track,
            state,
            Clamp(position, track.Duration),
            playbackRate,
            capturedAt,
            timelineKind);
    }

    public static bool HasUsableTimelineAnchor(
        DateTimeOffset timelineUpdatedAt,
        DateTimeOffset capturedAt)
    {
        if (timelineUpdatedAt > capturedAt)
        {
            return false;
        }

        return capturedAt - timelineUpdatedAt <= MaximumTimelineAge;
    }

    public static bool HasUsableTimeline(
        TimeSpan duration,
        TimeSpan position,
        DateTimeOffset timelineUpdatedAt,
        DateTimeOffset capturedAt)
    {
        if (!HasUsableTimelineAnchor(timelineUpdatedAt, capturedAt)
            || duration <= TimeSpan.Zero
            || position < TimeSpan.Zero)
        {
            return false;
        }

        return position <= duration
            || position - duration <= MaximumPositionOverrun;
    }

    private static double NormalizePlaybackRate(double? reportedPlaybackRate)
    {
        var rate = reportedPlaybackRate ?? 1d;
        return double.IsFinite(rate) && rate > 0 ? rate : 1d;
    }

    private static PlaybackTimelineKind DetermineFallbackTimelineKind(
        PlaybackSnapshot? previous,
        PlaybackState state,
        bool isSameTrack)
    {
        if (state == PlaybackState.Playing)
        {
            return PlaybackTimelineKind.Estimated;
        }

        if (state == PlaybackState.Paused
            && isSameTrack
            && previous?.TimelineKind is PlaybackTimelineKind.Estimated
                or PlaybackTimelineKind.EnhancedEstimated
                or PlaybackTimelineKind.Reported)
        {
            return PlaybackTimelineKind.Estimated;
        }

        return PlaybackTimelineKind.Unavailable;
    }

    private static bool IsSameTrack(TrackIdentity? previous, TrackIdentity current)
    {
        if (previous is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(previous.SourceTrackId)
            && !string.IsNullOrWhiteSpace(current.SourceTrackId))
        {
            return string.Equals(
                previous.SourceTrackId,
                current.SourceTrackId,
                StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(
            TrackIdentity.NormalizeForMatching(previous.Title),
            TrackIdentity.NormalizeForMatching(current.Title),
            StringComparison.Ordinal)
        && string.Equals(
            TrackIdentity.NormalizeForMatching(previous.Artist),
            TrackIdentity.NormalizeForMatching(current.Artist),
            StringComparison.Ordinal)
        && AreDurationsCompatible(previous.Duration, current.Duration)
        && (string.IsNullOrWhiteSpace(previous.Album)
            || string.IsNullOrWhiteSpace(current.Album)
            || string.Equals(
                TrackIdentity.NormalizeForMatching(previous.Album),
                TrackIdentity.NormalizeForMatching(current.Album),
                StringComparison.Ordinal));
    }

    private static bool AreDurationsCompatible(TimeSpan previous, TimeSpan current) =>
        previous <= TimeSpan.Zero
        || current <= TimeSpan.Zero
        || (previous - current).Duration() <= TimeSpan.FromSeconds(3);

    private static TimeSpan Clamp(TimeSpan position, TimeSpan duration)
    {
        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return duration > TimeSpan.Zero && position > duration ? duration : position;
    }
}
