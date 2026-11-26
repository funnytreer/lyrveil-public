using System.Collections.Concurrent;
using LyricsXQuiet.Platform.Windows.Playback;

internal static class NetEaseBridgeDiscoveryTests
{
    public static async Task RunAsync()
    {
        var scans = 0;
        var failures = 0;
        var activeConnections = 0;
        var overlappingConnections = 0;
        var port = 49152;
        var configured = new ConcurrentQueue<int>();
        var firstConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restartedConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var monitor = new NetEaseBridgeDiscoveryMonitor(
            () => Interlocked.Increment(ref scans) switch
            {
                1 => null, // Lyrveil starts before the player.
                2 => throw new IOException("Process is restarting."),
                _ => Volatile.Read(ref port)
            },
            async (discoveredPort, cancellationToken) =>
            {
                if (Interlocked.Increment(ref activeConnections) != 1)
                    Interlocked.Increment(ref overlappingConnections);
                try
                {
                    if (Interlocked.Increment(ref failures) == 1)
                        throw new IOException("Endpoint is still starting.");
                    await Task.Delay(35, cancellationToken);
                    configured.Enqueue(discoveredPort);
                    if (discoveredPort == 49152) firstConnection.TrySetResult();
                    else restartedConnection.TrySetResult();
                }
                finally
                {
                    Interlocked.Decrement(ref activeConnections);
                }
            },
            TimeSpan.FromMilliseconds(10)))
        {
            monitor.Start();
            monitor.Start(); // Starting twice must not create overlapping loops.
            await firstConnection.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Volatile.Write(ref port, 49153);
            await restartedConnection.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var scansAfterStop = Volatile.Read(ref scans);
        await Task.Delay(60);
        Assert(scans == scansAfterStop, "Discovery must stop when Lyrveil shuts down.");
        Assert(overlappingConnections == 0, "Slow connections must not overlap.");
        Assert(configured.Contains(49152) && configured.Contains(49153),
            "Discovery must recover after startup/connection failures and attach to a restarted player's new port.");

        var pendingConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = false;
        var pendingMonitor = new NetEaseBridgeDiscoveryMonitor(() => 49154, async (_, token) =>
        {
            pendingConnection.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancellationObserved = true;
                throw;
            }
        });
        pendingMonitor.Start();
        await pendingConnection.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await pendingMonitor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert(cancellationObserved, "Shutdown must cancel an in-flight connection.");
        Console.WriteLine("NetEase discovery regressions passed: late startup, restart, retry, no overlapping scans, shutdown.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
