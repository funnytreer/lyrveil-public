namespace LyricsXQuiet.Core.Playback;

public static class NetEaseLyricsPositionCompensation
{
    public const int DefaultDelayMilliseconds = 1000;

    // Device/player output latency can exceed the old 500 ms limit. Keep this
    // bound shared with persistence and the settings UI so a calibrated value
    // is not silently truncated on save or during playback.
    public const int MaximumDelayMilliseconds = 3000;

    public static TimeSpan Apply(
        PlaybackSnapshot snapshot,
        TimeSpan position,
        int delayMilliseconds,
        int offsetMilliseconds = 0)
    {
        var player = MusicPlayerCatalog.Identify(snapshot.Track.SourceAppUserModelId);
        var capability = NetEaseSyncCapabilityClassifier.Classify(
            snapshot.Track,
            snapshot.TimelineKind);
        if (!string.Equals(player?.Id, "netease", StringComparison.OrdinalIgnoreCase)
            || capability is not (NetEaseSyncCapability.EnhancedPreciseTimeline
                or NetEaseSyncCapability.EnhancedEstimatedTimeline))
        {
            return position;
        }

        var delay = TimeSpan.FromMilliseconds(Math.Clamp(
            delayMilliseconds, 0, MaximumDelayMilliseconds));
        var adjustment = TimeSpan.FromMilliseconds(Math.Clamp(offsetMilliseconds,
            PlayerLyricsPositionCompensation.MinimumMilliseconds,
            PlayerLyricsPositionCompensation.MaximumMilliseconds));
        return position - delay + adjustment;
    }
}
