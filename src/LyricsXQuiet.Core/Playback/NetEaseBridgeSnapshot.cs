namespace LyricsXQuiet.Core.Playback;

public sealed record NetEaseBridgeTrack(
    string Id,
    string Title,
    IReadOnlyList<string> Artists,
    string Album,
    long DurationMs);

public sealed record NetEaseBridgeSnapshot(
    int Version,
    long Sequence,
    DateTimeOffset ObservedAt,
    NetEaseBridgeTrack Track,
    PlaybackState State,
    long PositionMs,
    double Rate,
    bool TimelineFrozen = false);
