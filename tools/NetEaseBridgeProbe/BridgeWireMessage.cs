namespace LyricsXQuiet.Tools.NetEaseBridgeProbe;

internal sealed record BridgeWireTrack(
    string Id,
    string Title,
    IReadOnlyList<string> Artists,
    string Album,
    long DurationMs);

internal sealed record BridgeWireMessage(
    int Version,
    long Seq,
    DateTimeOffset ObservedAt,
    BridgeWireTrack Track,
    string State,
    long PositionMs,
    double Rate);

internal sealed record ProducerSnapshot(
    DateTimeOffset ObservedAt,
    BridgeWireTrack Track,
    string State,
    long PositionMs,
    double Rate);
