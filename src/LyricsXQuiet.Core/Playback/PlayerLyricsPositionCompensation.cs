namespace LyricsXQuiet.Core.Playback;

/// <summary>
/// Applies only the current player's saved calibration. Positive offsets
/// advance lyrics; negative offsets delay them. NetEase also has an editable
/// default delay, so its zero offset retains that baseline.
/// </summary>
public static class PlayerLyricsPositionCompensation
{
    public const int MinimumMilliseconds = -3000;
    public const int MaximumMilliseconds = 3000;

    public static TimeSpan Apply(
        PlaybackSnapshot snapshot,
        TimeSpan position,
        int offsetMilliseconds,
        int netEaseSyncDelayMilliseconds = NetEaseLyricsPositionCompensation.DefaultDelayMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var offset = Math.Clamp(offsetMilliseconds, MinimumMilliseconds, MaximumMilliseconds);
        return MusicPlayerCatalog.Identify(snapshot.Track.SourceAppUserModelId)?.Id switch
        {
            "netease" => NetEaseLyricsPositionCompensation.Apply(
                snapshot, position, netEaseSyncDelayMilliseconds, offset),
            "applemusic" => AppleMusicLyricsPositionCompensation.Apply(snapshot, position, offset),
            "qqmusic" or "kugou" or "spotify" => position + TimeSpan.FromMilliseconds(offset),
            _ => position
        };
    }
}
