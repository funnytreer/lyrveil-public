using System.Net;
using LyricsXQuiet.Core.Playback;

namespace LyricsXQuiet.Platform.Windows.Playback;

public interface INetEaseBridgeSnapshotSource : IAsyncDisposable
{
    event EventHandler<NetEaseBridgeSnapshot?>? SnapshotChanged;

    NetEaseBridgeSnapshot? Current { get; }

    IPEndPoint? LocalEndpoint { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
}
