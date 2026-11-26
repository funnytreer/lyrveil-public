using LyricsXQuiet.Core.Playback;

namespace LyricsXQuiet.Core.Lyrics;

/// <summary>
/// Builds a storage identity for one concrete recording. This deliberately
/// stays separate from <see cref="TrackIdentity.StableKey"/>, which is used by
/// the live playback pipeline and must tolerate metadata arriving in stages.
/// </summary>
public static class LyricsCacheIdentity
{
    private const string KeyPrefix = "lyrics-cache:v2";

    public static string GetPrimaryKey(TrackIdentity track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return BuildKey(track, track.Album, track.Duration);
    }

    /// <summary>
    /// Returns less-specific v2 keys only as compatibility fallbacks for a
    /// file saved before album or duration metadata became available. The
    /// fully-qualified key is always first and is the only key used for new
    /// writes.
    /// </summary>
    public static IEnumerable<string> GetCompatibleKeys(TrackIdentity track)
    {
        ArgumentNullException.ThrowIfNull(track);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (keys.Add(GetPrimaryKey(track)))
        {
            yield return GetPrimaryKey(track);
        }

        if (!string.IsNullOrWhiteSpace(track.Album))
        {
            var withoutAlbum = BuildKey(track, string.Empty, track.Duration);
            if (keys.Add(withoutAlbum))
            {
                yield return withoutAlbum;
            }
        }

        if (track.Duration > TimeSpan.Zero)
        {
            var withoutDuration = BuildKey(track, track.Album, TimeSpan.Zero);
            if (keys.Add(withoutDuration))
            {
                yield return withoutDuration;
            }
        }

        if (!string.IsNullOrWhiteSpace(track.Album) && track.Duration > TimeSpan.Zero)
        {
            var metadataOnly = BuildKey(track, string.Empty, TimeSpan.Zero);
            if (keys.Add(metadataOnly))
            {
                yield return metadataOnly;
            }
        }
    }

    private static string BuildKey(
        TrackIdentity track,
        string album,
        TimeSpan duration) => string.Join('|',
        KeyPrefix,
        TrackIdentity.NormalizeForMatching(track.Title),
        TrackIdentity.NormalizeForMatching(track.Artist),
        TrackIdentity.NormalizeForMatching(album),
        // Different players may report the same recording a few milliseconds
        // apart. A whole-second identity avoids duplicate files for that
        // transport noise while album/title version markers still separate
        // Live, Remix, Demo and other releases.
        duration > TimeSpan.Zero
            ? Math.Round(
                duration.TotalSeconds,
                MidpointRounding.AwayFromZero).ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            : "0");
}
