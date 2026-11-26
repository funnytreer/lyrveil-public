namespace LyricsXQuiet.Core.Playback;

public enum PlaybackState
{
    Unknown,
    Closed,
    Paused,
    Playing,
    Stopped
}

public enum PlaybackTimelineKind
{
    Unavailable,
    Estimated,
    Reported,
    EnhancedEstimated
}

public sealed record PlaybackSnapshot(
    TrackIdentity Track,
    PlaybackState State,
    TimeSpan Position,
    double PlaybackRate,
    DateTimeOffset CapturedAt,
    PlaybackTimelineKind TimelineKind = PlaybackTimelineKind.Unavailable)
{
    public TimeSpan EstimatePosition(DateTimeOffset now)
    {
        var position = Position;
        if (State == PlaybackState.Playing && PlaybackRate > 0)
        {
            position += TimeSpan.FromTicks((long)((now - CapturedAt).Ticks * PlaybackRate));
        }

        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (Track.Duration > TimeSpan.Zero && position > Track.Duration)
        {
            return Track.Duration;
        }

        return position;
    }
}
