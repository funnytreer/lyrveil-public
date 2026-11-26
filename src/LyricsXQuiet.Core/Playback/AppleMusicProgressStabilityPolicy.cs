namespace LyricsXQuiet.Core.Playback;

public static class AppleMusicProgressStabilityPolicy
{
    private static readonly TimeSpan MaximumQuantizationRegression =
        TimeSpan.FromMilliseconds(1100);

    public static TimeSpan Stabilize(
        PlaybackSnapshot? previous,
        TrackIdentity track,
        PlaybackState state,
        TimeSpan reportedPosition,
        DateTimeOffset capturedAt)
    {
        if (previous is null
            || state != PlaybackState.Playing
            || previous.State != PlaybackState.Playing
            || previous.TimelineKind != PlaybackTimelineKind.Reported
            || previous.Track.StableKey != track.StableKey
            || !IsAppleMusic(track.SourceAppUserModelId))
        {
            return reportedPosition;
        }

        var expectedPosition = previous.EstimatePosition(capturedAt);
        var regression = expectedPosition - reportedPosition;
        return regression > TimeSpan.Zero
            && regression <= MaximumQuantizationRegression
                ? expectedPosition
                : reportedPosition;
    }

    private static bool IsAppleMusic(string sourceAppUserModelId) =>
        sourceAppUserModelId.Contains("applemusic", StringComparison.OrdinalIgnoreCase)
        || sourceAppUserModelId.Contains("apple music", StringComparison.OrdinalIgnoreCase);
}
