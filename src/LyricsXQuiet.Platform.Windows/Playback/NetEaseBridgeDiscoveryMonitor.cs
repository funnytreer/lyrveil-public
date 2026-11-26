namespace LyricsXQuiet.Platform.Windows.Playback;

/// <summary>
/// Rediscovers an explicitly enabled loopback endpoint after NetEase starts or
/// restarts. Discovery never launches or restarts the player itself.
/// </summary>
public sealed class NetEaseBridgeDiscoveryMonitor : IAsyncDisposable
{
    private readonly Func<int?> _discoverPort;
    private readonly Func<int, CancellationToken, Task> _configurePort;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private Task? _worker;
    private bool _disposed;

    public NetEaseBridgeDiscoveryMonitor(
        Func<int?> discoverPort,
        Func<int, CancellationToken, Task> configurePort,
        TimeSpan? interval = null)
    {
        _discoverPort = discoverPort ?? throw new ArgumentNullException(nameof(discoverPort));
        _configurePort = configurePort ?? throw new ArgumentNullException(nameof(configurePort));
        _interval = interval ?? TimeSpan.FromSeconds(3);
        if (_interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Process inspection can be slow. Keep it off the UI thread and do
            // not overlap scans when a previous one is still in progress.
            _worker ??= Task.Run(() => ObserveAsync(_lifetime.Token));
        }
    }

    private async Task ObserveAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var port = _discoverPort();
                cancellationToken.ThrowIfCancellationRequested();
                if (port is not null)
                    await _configurePort(port.Value, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // A transient inspection/attachment failure must not permanently
                // disable discovery or interrupt native media-session playback.
            }

            try
            {
                await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? worker;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            worker = _worker;
        }
        if (worker is not null) await worker.ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
