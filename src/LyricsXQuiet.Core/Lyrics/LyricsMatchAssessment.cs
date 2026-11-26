namespace LyricsXQuiet.Core.Lyrics;

public sealed record LyricsMatchAssessment(
    bool HasCandidateArtist,
    bool ArtistMatches,
    bool HasComparableAlbum,
    bool AlbumMatches,
    bool HasComparableDuration,
    TimeSpan DurationDifference);
