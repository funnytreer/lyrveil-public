namespace LyricsXQuiet.Core.Lyrics;

public enum LyricsCacheSelectionKind
{
    Automatic,
    Manual,
    Imported
}

public sealed record LyricsCacheEntry(
    LyricDocument Document,
    LyricsCacheSelectionKind SelectionKind,
    string DeclaredSource,
    string SourceTrackId,
    string RankingPolicy = "");
