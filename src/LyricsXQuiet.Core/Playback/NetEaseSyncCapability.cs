namespace LyricsXQuiet.Core.Playback;

public enum NetEaseSyncCapability
{
    NativeLimited,
    EnhancedMetadataOnly,
    PreciseTimeline,
    EnhancedPreciseTimeline,
    EnhancedEstimatedTimeline
}

public static class NetEaseSyncCapabilityClassifier
{
    public static NetEaseSyncCapability Classify(
        TrackIdentity track,
        PlaybackTimelineKind timelineKind)
    {
        var hasEnhancedTrackId = SourceTrackIdParser.TryGetNetEaseSongId(
            track.SourceTrackId,
            out _);
        return (hasEnhancedTrackId, timelineKind) switch
        {
            (true, PlaybackTimelineKind.Reported) =>
                NetEaseSyncCapability.EnhancedPreciseTimeline,
            (true, PlaybackTimelineKind.EnhancedEstimated) =>
                NetEaseSyncCapability.EnhancedEstimatedTimeline,
            (true, _) => NetEaseSyncCapability.EnhancedMetadataOnly,
            (false, PlaybackTimelineKind.Reported) =>
                NetEaseSyncCapability.PreciseTimeline,
            _ => NetEaseSyncCapability.NativeLimited
        };
    }
}
