using LyricsXQuiet.Core.Playback;

namespace LyricsXQuiet.Platform.Windows.Playback;

public static class PlaybackMediaMetadataNormalizer
{
    private static readonly string[] AppleMusicArtistAlbumSeparators = [" — ", " – "];

    public static TrackIdentity CreateTrack(
        string sourceAppUserModelId,
        string? title,
        string? artist,
        string? album,
        TimeSpan duration,
        IEnumerable<string>? genres = null)
    {
        var normalizedArtist = artist?.Trim() ?? string.Empty;
        var normalizedAlbum = album?.Trim() ?? string.Empty;

        if (string.Equals(
                MusicPlayerCatalog.Identify(sourceAppUserModelId)?.Id,
                "applemusic",
                StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(normalizedAlbum)
            && TrySplitAppleMusicArtistAndAlbum(
                normalizedArtist,
                out var splitArtist,
                out var splitAlbum))
        {
            normalizedArtist = splitArtist;
            normalizedAlbum = splitAlbum;
        }

        return new TrackIdentity(
            title?.Trim() ?? string.Empty,
            normalizedArtist,
            normalizedAlbum,
            duration,
            sourceAppUserModelId,
            SourceTrackIdParser.FromGenres(genres ?? []));
    }

    public static bool TrySplitAppleMusicArtistAndAlbum(
        string value,
        out string artist,
        out string album)
    {
        artist = value.Trim();
        album = string.Empty;
        foreach (var separator in AppleMusicArtistAlbumSeparators)
        {
            var separatorIndex = value.IndexOf(separator, StringComparison.Ordinal);
            if (separatorIndex <= 0
                || separatorIndex != value.LastIndexOf(separator, StringComparison.Ordinal))
            {
                continue;
            }

            var left = value[..separatorIndex].Trim();
            var right = value[(separatorIndex + separator.Length)..].Trim();
            if (left.Length == 0 || right.Length == 0)
            {
                continue;
            }

            artist = left;
            album = right;
            return true;
        }

        return false;
    }
}
