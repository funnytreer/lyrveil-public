namespace LyricsXQuiet.Core.Playback;

/// <summary>
/// Decides when Core Audio may repair an unreliable media-session state.
/// Explicit pause reports from well-behaved/timeline-capable players must win
/// over the short audio-tail hold, otherwise lyrics linger after pausing.
/// </summary>
public static class PlaybackStateTrustPolicy
{
    public static PlaybackState ApplyAudioFallback(
        PlaybackState reportedState,
        string? playerId,
        bool hasUsableTimeline,
        bool isCurrentlyAudible)
    {
        if (reportedState == PlaybackState.Playing || !isCurrentlyAudible)
        {
            return reportedState;
        }

        if (reportedState == PlaybackState.Unknown)
        {
            return PlaybackState.Playing;
        }

        if (hasUsableTimeline)
        {
            return reportedState;
        }

        return playerId is "netease" or "kugou"
            ? PlaybackState.Playing
            : reportedState;
    }
}
