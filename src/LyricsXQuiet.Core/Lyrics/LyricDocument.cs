namespace LyricsXQuiet.Core.Lyrics;

public sealed record LyricWordTiming(
    TimeSpan Start,
    int StartCharacter,
    int Length,
    TimeSpan? End = null);

public sealed record LyricLine(
    TimeSpan Start,
    string Text,
    string? Translation,
    IReadOnlyList<LyricWordTiming> Words);

public sealed record LyricDocument(
    IReadOnlyList<LyricLine> Lines,
    string Source,
    string RawText,
    bool IsInstrumental = false)
{
    public bool HasSynchronizedLines => Lines.Count > 0;
}

public enum LyricFrameKind
{
    Lyrics,
    TrackInfo
}

public sealed record LyricFrame(
    string CurrentText,
    string SecondaryText,
    double Progress,
    bool HasWordTiming,
    int LineIndex,
    LyricFrameKind Kind = LyricFrameKind.Lyrics);
