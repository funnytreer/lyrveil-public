using Windows.Media.Control;

namespace LyricsXQuiet.Tools.PlaybackProbe;

internal sealed record PlaybackSnapshot(
    string SourceAppUserModelId,
    bool IsCurrent,
    string Title,
    string Artist,
    string AlbumTitle,
    int TrackNumber,
    GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus,
    double PlaybackRate,
    TimeSpan Position,
    TimeSpan StartTime,
    TimeSpan EndTime,
    DateTimeOffset TimelineUpdatedAt,
    DateTimeOffset CapturedAt,
    bool CanPlay,
    bool CanPause,
    bool CanSeek,
    bool CanNext,
    bool CanPrevious)
{
    public TimeSpan Duration => EndTime > StartTime ? EndTime - StartTime : TimeSpan.Zero;

    public TimeSpan EstimatePosition(DateTimeOffset now)
    {
        if (PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing || PlaybackRate <= 0)
        {
            return Clamp(Position);
        }

        var baseTime = TimelineUpdatedAt == default ? CapturedAt : TimelineUpdatedAt;
        var elapsed = now - baseTime;
        if (elapsed < TimeSpan.Zero || elapsed > TimeSpan.FromHours(12))
        {
            elapsed = now - CapturedAt;
        }

        return Clamp(Position + TimeSpan.FromTicks((long)(elapsed.Ticks * PlaybackRate)));
    }

    private TimeSpan Clamp(TimeSpan value)
    {
        if (value < StartTime)
        {
            return StartTime;
        }

        if (EndTime > StartTime && value > EndTime)
        {
            return EndTime;
        }

        return value;
    }
}

internal sealed record ProbeRecord(
    DateTimeOffset Timestamp,
    string Kind,
    string Reason,
    PlaybackSnapshot? Snapshot,
    string? Message,
    string? ErrorType,
    string? ErrorDetail)
{
    public static ProbeRecord FromSnapshot(string reason, PlaybackSnapshot snapshot) =>
        new(DateTimeOffset.Now, "snapshot", reason, snapshot, null, null, null);

    public static ProbeRecord Clock(PlaybackSnapshot snapshot) =>
        new(DateTimeOffset.Now, "clock", "local-estimate", snapshot, null, null, null);

    public static ProbeRecord Status(string message) =>
        new(DateTimeOffset.Now, "status", "probe", null, message, null, null);

    public static ProbeRecord Error(string message, Exception exception) =>
        new(DateTimeOffset.Now, "error", "probe", null, message, exception.GetType().FullName, exception.Message);
}
