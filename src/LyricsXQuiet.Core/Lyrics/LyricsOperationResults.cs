namespace LyricsXQuiet.Core.Lyrics;

public enum LyricsProviderFailureKind { Network, Timeout, InvalidResponse }

// Safe diagnostics intentionally contain no request URLs, song data or raw exceptions.
public sealed record LyricsProviderFailure(string Provider, LyricsProviderFailureKind Kind);

public sealed record LyricsSearchResult(
    IReadOnlyList<LyricsCandidate> Candidates,
    IReadOnlyList<LyricsProviderFailure> Failures,
    int SuccessfulSources)
{
    public static LyricsSearchResult Empty { get; } = new([], [], 0);
}

public sealed class LyricsSourcesUnavailableException(
    IReadOnlyList<LyricsProviderFailure> failures)
    : HttpRequestException("歌词来源请求失败，请检查网络或稍后重试。")
{
    public IReadOnlyList<LyricsProviderFailure> Failures { get; } = failures;
}

public sealed record LyricsResolutionResult(LyricDocument? Document, bool SaveFailed = false);

public enum LyricsSelectionResult { NotApplied, Saved, SessionOnly }
