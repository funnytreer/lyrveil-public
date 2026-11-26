using LyricsXQuiet.Core.Playback;

namespace LyricsXQuiet.Core.Lyrics.Providers;

public interface ILyricsProvider
{
    string Name { get; }

    bool IsEnabled => true;

    Task<IReadOnlyList<LyricsCandidate>> SearchAutomaticAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default) => SearchAsync(track, cancellationToken);

    Task<LyricDocument?> FindAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LyricsCandidate>> SearchAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default);
}
