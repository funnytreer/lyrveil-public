namespace LyricsXQuiet.Core.Playback;

public static class AppleMusicLyricsPositionCompensation
{
    public const int MaximumAdvanceMilliseconds = 3000;

    public static TimeSpan Apply(
        PlaybackSnapshot snapshot,
        TimeSpan position,
        int advanceMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!IsAppleMusic(snapshot.Track.SourceAppUserModelId))
        {
            return position;
        }

        return position + TimeSpan.FromMilliseconds(
            Math.Clamp(advanceMilliseconds, 0, MaximumAdvanceMilliseconds));
    }

    private static bool IsAppleMusic(string sourceAppUserModelId) =>
        sourceAppUserModelId.Contains("applemusic", StringComparison.OrdinalIgnoreCase)
        || sourceAppUserModelId.Contains("apple music", StringComparison.OrdinalIgnoreCase);
}
