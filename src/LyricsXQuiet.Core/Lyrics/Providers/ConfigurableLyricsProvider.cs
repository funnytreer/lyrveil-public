using LyricsXQuiet.Core.Playback;

namespace LyricsXQuiet.Core.Lyrics.Providers;

public sealed class ConfigurableLyricsProvider : IExactSourceLyricsProvider, IDisposable
{
    private readonly ILyricsProvider _inner;
    private readonly Func<bool> _isEnabled;

    public ConfigurableLyricsProvider(ILyricsProvider inner, Func<bool> isEnabled)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
    }

    public string Name => _inner.Name;

    public bool IsEnabled => _isEnabled();

    public Task<IReadOnlyList<LyricsCandidate>> SearchAutomaticAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default) =>
        IsEnabled ? _inner.SearchAutomaticAsync(track, cancellationToken)
            : Task.FromResult<IReadOnlyList<LyricsCandidate>>([]);

    public bool CanResolveExact(TrackIdentity track) =>
        _isEnabled()
        && _inner is IExactSourceLyricsProvider exactProvider
        && exactProvider.CanResolveExact(track);

    public Task<LyricDocument?> FindExactAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default) =>
        _isEnabled() && _inner is IExactSourceLyricsProvider exactProvider
            ? exactProvider.FindExactAsync(track, cancellationToken)
            : Task.FromResult<LyricDocument?>(null);

    public Task<LyricDocument?> FindAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default) =>
        _isEnabled()
            ? _inner.FindAsync(track, cancellationToken)
            : Task.FromResult<LyricDocument?>(null);

    public Task<IReadOnlyList<LyricsCandidate>> SearchAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default) =>
        _isEnabled()
            ? _inner.SearchAsync(track, cancellationToken)
            : Task.FromResult<IReadOnlyList<LyricsCandidate>>(Array.Empty<LyricsCandidate>());

    public void Dispose()
    {
        if (_inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
