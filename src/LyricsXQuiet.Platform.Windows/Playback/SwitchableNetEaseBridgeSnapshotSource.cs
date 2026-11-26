using System.Net;
using LyricsXQuiet.Core.Playback;

namespace LyricsXQuiet.Platform.Windows.Playback;

/// <summary>
/// Keeps the playback service stable while allowing a newly launched NetEase
/// process to supply a different, short-lived local CDP endpoint.
/// </summary>
public sealed class SwitchableNetEaseBridgeSnapshotSource : INetEaseBridgeSnapshotSource
{
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private readonly object _stateLock = new();
    private INetEaseBridgeSnapshotSource? _inner;
    private NetEaseBridgeSnapshot? _current;
    private bool _started;
    private bool _disposed;

    public event EventHandler<NetEaseBridgeSnapshot?>? SnapshotChanged;

    public NetEaseBridgeSnapshot? Current
    {
        get
        {
            lock (_stateLock)
            {
                return _current;
            }
        }
    }

    public IPEndPoint? LocalEndpoint
    {
        get
        {
            lock (_stateLock)
            {
                return _inner?.LocalEndpoint;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        await _switchGate.WaitAsync(cancellationToken);
        try
        {
            if (_started)
            {
                return;
            }

            _started = true;
            if (_inner is not null)
            {
                await _inner.StartAsync(cancellationToken);
            }
        }
        finally
        {
            _switchGate.Release();
        }
    }

    public async Task ConfigurePortAsync(
        int port,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        await _switchGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_inner?.LocalEndpoint?.Port == port)
            {
                return;
            }

            var replacement = new CdpNetEaseBridgeSnapshotSource(port);
            replacement.SnapshotChanged += OnInnerSnapshotChanged;
            if (_started)
            {
                await replacement.StartAsync(cancellationToken);
            }

            INetEaseBridgeSnapshotSource? previous;
            lock (_stateLock)
            {
                previous = _inner;
                _inner = replacement;
                _current = null;
            }

            if (previous is not null)
            {
                previous.SnapshotChanged -= OnInnerSnapshotChanged;
                await previous.DisposeAsync();
            }

            RaiseSnapshotChanged(null);
        }
        finally
        {
            _switchGate.Release();
        }
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _switchGate.WaitAsync(cancellationToken);
        try
        {
            INetEaseBridgeSnapshotSource? previous;
            lock (_stateLock)
            {
                previous = _inner;
                _inner = null;
                _current = null;
            }

            if (previous is not null)
            {
                previous.SnapshotChanged -= OnInnerSnapshotChanged;
                await previous.DisposeAsync();
            }

            RaiseSnapshotChanged(null);
        }
        finally
        {
            _switchGate.Release();
        }
    }

    private void OnInnerSnapshotChanged(object? sender, NetEaseBridgeSnapshot? snapshot)
    {
        lock (_stateLock)
        {
            if (!ReferenceEquals(sender, _inner))
            {
                return;
            }

            _current = snapshot;
        }

        RaiseSnapshotChanged(snapshot);
    }

    private void RaiseSnapshotChanged(NetEaseBridgeSnapshot? snapshot)
    {
        var handlers = SnapshotChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<NetEaseBridgeSnapshot?> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, snapshot);
            }
            catch
            {
                // A consumer must not break source switching or reconnect behavior.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _switchGate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            INetEaseBridgeSnapshotSource? previous;
            lock (_stateLock)
            {
                previous = _inner;
                _inner = null;
                _current = null;
            }

            if (previous is not null)
            {
                previous.SnapshotChanged -= OnInnerSnapshotChanged;
                await previous.DisposeAsync();
            }
        }
        finally
        {
            _switchGate.Release();
        }
    }
}
