using System.Globalization;

namespace LyricsXQuiet.Core.Playback;

public static class SourceTrackIdParser
{
    private const string NetEaseGenrePrefix = "NCM-";
    private const string NetEaseSourcePrefix = "netease:";

    public static string FromGenres(IEnumerable<string>? genres)
    {
        if (genres is null)
        {
            return string.Empty;
        }

        foreach (var genre in genres)
        {
            if (TryParseNetEaseGenre(genre, out var sourceTrackId))
            {
                return sourceTrackId;
            }
        }

        return string.Empty;
    }

    public static bool TryParseNetEaseGenre(
        string? genre,
        out string sourceTrackId)
    {
        sourceTrackId = string.Empty;
        if (!TryParsePositiveId(genre, NetEaseGenrePrefix, out var songId))
        {
            return false;
        }

        sourceTrackId = FormatNetEaseSongId(songId);
        return true;
    }

    public static bool TryGetNetEaseSongId(
        string? sourceTrackId,
        out long songId) =>
        TryParsePositiveId(sourceTrackId, NetEaseSourcePrefix, out songId);

    public static string FormatNetEaseSongId(long songId) =>
        songId > 0
            ? $"{NetEaseSourcePrefix}{songId.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;

    private static bool TryParsePositiveId(
        string? value,
        string prefix,
        out long id)
    {
        id = 0;
        if (string.IsNullOrEmpty(value)
            || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var number = value.AsSpan(prefix.Length);
        if (number.IsEmpty)
        {
            return false;
        }

        foreach (var character in number)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return long.TryParse(
                number,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out id)
            && id > 0;
    }
}
