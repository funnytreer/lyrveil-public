namespace LyricsXQuiet.Core.Playback;

public interface IPlaybackService : IAsyncDisposable
{
    event EventHandler<PlaybackSnapshot?>? SnapshotChanged;

    event EventHandler<PlaybackConnectionStatus>? ConnectionStatusChanged;

    PlaybackSnapshot? Current { get; }

    PlaybackConnectionStatus ConnectionStatus { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
}
