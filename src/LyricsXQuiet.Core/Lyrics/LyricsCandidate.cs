namespace LyricsXQuiet.Core.Lyrics;

public sealed record LyricsCandidate(
    string Id,
    string Provider,
    string Title,
    string Artist,
    string Album,
    TimeSpan Duration,
    double MatchScore,
    LyricDocument Document);
