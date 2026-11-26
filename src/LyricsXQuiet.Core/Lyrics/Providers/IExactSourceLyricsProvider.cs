using LyricsXQuiet.Core.Playback;

namespace LyricsXQuiet.Core.Lyrics.Providers;

/// <summary>
/// Resolves lyrics only when the playing application supplied an exact,
/// provider-owned track identifier. Implementations must not fall back to a
/// fuzzy search from this method.
/// </summary>
public interface IExactSourceLyricsProvider : ILyricsProvider
{
    bool CanResolveExact(TrackIdentity track);

    Task<LyricDocument?> FindExactAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default);
}
