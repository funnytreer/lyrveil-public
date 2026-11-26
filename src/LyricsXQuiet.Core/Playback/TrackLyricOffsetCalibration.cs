namespace LyricsXQuiet.Core.Playback;

/// <summary>
/// Builds versioned keys and applies a user adjustment that belongs to one
/// recording, independently of the global offset and player-specific delays.
/// </summary>
public static class TrackLyricOffsetCalibration
{
    public const int MinimumMilliseconds = -10_000;
    public const int MaximumMilliseconds = 10_000;

    private const string KeyPrefix = "track-lyric-offset:v1";

    public static string GetPrimaryKey(TrackIdentity track)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (!string.IsNullOrWhiteSpace(track.SourceTrackId))
        {
            var playerId = MusicPlayerCatalog.Identify(track.SourceAppUserModelId)?.Id
                ?? "unknown";
            var sourceTrackId = Uri.EscapeDataString(
                track.SourceTrackId.Trim().ToLowerInvariant());
            return $"{KeyPrefix}|source|{playerId}|{sourceTrackId}";
        }

        return GetMetadataKey(track);
    }

    public static string GetMetadataKey(TrackIdentity track)
    {
        ArgumentNullException.ThrowIfNull(track);

        return string.Join('|',
            KeyPrefix,
            "metadata",
            track.StableKey,
            TrackIdentity.NormalizeForMatching(track.Album));
    }

    public static int Resolve(
        TrackIdentity track,
        IReadOnlyDictionary<string, int>? offsetsMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (offsetsMilliseconds is null || offsetsMilliseconds.Count == 0)
        {
            return 0;
        }

        var primaryKey = GetPrimaryKey(track);
        if (offsetsMilliseconds.TryGetValue(primaryKey, out var exactOffset))
        {
            return Clamp(exactOffset);
        }

        var metadataKey = GetMetadataKey(track);
        return !string.Equals(primaryKey, metadataKey, StringComparison.Ordinal)
               && offsetsMilliseconds.TryGetValue(metadataKey, out var metadataOffset)
            ? Clamp(metadataOffset)
            : 0;
    }

    public static TimeSpan Apply(TimeSpan position, int adjustmentMilliseconds) =>
        position + TimeSpan.FromMilliseconds(Clamp(adjustmentMilliseconds));

    public static int Clamp(int value) =>
        Math.Clamp(value, MinimumMilliseconds, MaximumMilliseconds);
}
