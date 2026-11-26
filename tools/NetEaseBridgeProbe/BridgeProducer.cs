using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;

namespace LyricsXQuiet.Tools.NetEaseBridgeProbe;

internal sealed class BridgeProducer(
    int cdpPort,
    int bridgePort,
    string? targetId,
    TimeSpan timeout)
{
    private const int ProtocolVersion = 1;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(125);
    private static readonly TimeSpan NormalPublishInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromSeconds(4);
    private static readonly byte[] NewLine = [(byte)'\n'];
    private static readonly JsonSerializerOptions WireJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var reconnectDelay = InitialReconnectDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            var connected = false;
            try
            {
                await RunConnectionAsync(
                    () =>
                    {
                        connected = true;
                        reconnectDelay = InitialReconnectDelay;
                    },
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (IsReconnectable(exception))
            {
                await Task.Delay(reconnectDelay, cancellationToken);
                if (!connected)
                {
                    reconnectDelay = TimeSpan.FromMilliseconds(Math.Min(
                        MaximumReconnectDelay.TotalMilliseconds,
                        reconnectDelay.TotalMilliseconds * 2));
                }
            }
        }
    }

    private async Task RunConnectionAsync(
        Action onConnected,
        CancellationToken cancellationToken)
    {
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(timeout);

        using var client = new TcpClient(AddressFamily.InterNetwork)
        {
            NoDelay = true
        };
        await client.ConnectAsync(IPAddress.Loopback, bridgePort, connectTimeout.Token);

        var targets = await DevToolsDiscovery.ListTargetsAsync(
            cdpPort,
            timeout,
            connectTimeout.Token);
        var target = DevToolsDiscovery.SelectTarget(targets, targetId);
        var webSocketUri = DevToolsDiscovery.ValidateLoopbackWebSocket(target, cdpPort);
        await using var connection = await CdpConnection.ConnectAsync(
            webSocketUri,
            timeout,
            connectTimeout.Token);
        onConnected();
        using var stream = client.GetStream();
        await PumpAsync(connection, stream, cancellationToken);
    }

    private async Task PumpAsync(
        CdpConnection connection,
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        ProducerSnapshot? previousSample = null;
        long previousSampleTick = 0;
        long lastPublishedTick = 0;
        long lastValidSampleTick = 0;
        long sequence = 0;

        while (true)
        {
            var iterationStart = Stopwatch.GetTimestamp();
            var probe = await NetEaseCdpProbe.CaptureAsync(connection, timeout, cancellationToken);
            var sampledAtTick = Stopwatch.GetTimestamp();
            if (BridgeSnapshotAdapter.TryCreate(probe, out var snapshot) && snapshot is not null)
            {
                var publishImmediately = previousSample is null
                    || TrackChanged(previousSample, snapshot)
                    || !string.Equals(previousSample.State, snapshot.State, StringComparison.Ordinal)
                    || IsSeek(previousSample, snapshot, previousSampleTick, sampledAtTick);
                var normalAnchorDue = lastPublishedTick == 0
                    || Stopwatch.GetElapsedTime(lastPublishedTick, sampledAtTick)
                        >= NormalPublishInterval;

                if (publishImmediately || normalAnchorDue)
                {
                    var message = new BridgeWireMessage(
                        ProtocolVersion,
                        sequence++,
                        snapshot.ObservedAt,
                        snapshot.Track,
                        snapshot.State,
                        snapshot.PositionMs,
                        snapshot.Rate);
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(message, WireJsonOptions);
                    await stream.WriteAsync(bytes, cancellationToken);
                    await stream.WriteAsync(NewLine, cancellationToken);
                    lastPublishedTick = sampledAtTick;
                }

                previousSample = snapshot;
                previousSampleTick = sampledAtTick;
                lastValidSampleTick = sampledAtTick;
            }
            else if (lastValidSampleTick != 0
                && Stopwatch.GetElapsedTime(lastValidSampleTick, sampledAtTick)
                    >= TimeSpan.FromSeconds(1))
            {
                // Closing both local connections makes the receiver discard the
                // old anchor immediately instead of waiting for freshness expiry.
                throw new ProbeException("网易云页面暂时没有可用的播放快照。");
            }

            var elapsed = Stopwatch.GetElapsedTime(iterationStart);
            if (elapsed < SampleInterval)
            {
                await Task.Delay(SampleInterval - elapsed, cancellationToken);
            }
        }
    }

    private static bool TrackChanged(ProducerSnapshot previous, ProducerSnapshot current) =>
        !string.Equals(previous.Track.Id, current.Track.Id, StringComparison.Ordinal)
        || !string.Equals(previous.Track.Title, current.Track.Title, StringComparison.Ordinal)
        || !previous.Track.Artists.SequenceEqual(current.Track.Artists, StringComparer.Ordinal);

    private static bool IsSeek(
        ProducerSnapshot previous,
        ProducerSnapshot current,
        long previousTick,
        long currentTick)
    {
        if (TrackChanged(previous, current) || previousTick == 0)
        {
            return false;
        }

        var elapsed = Stopwatch.GetElapsedTime(previousTick, currentTick);
        var expectedPosition = previous.PositionMs;
        if (previous.State == "playing")
        {
            expectedPosition += (long)Math.Round(elapsed.TotalMilliseconds * previous.Rate);
        }

        var tolerance = Math.Max(750, elapsed.TotalMilliseconds * 2 + 250);
        return Math.Abs(current.PositionMs - expectedPosition) > tolerance;
    }

    private static bool IsReconnectable(Exception exception) =>
        exception is ProbeException
            or HttpRequestException
            or IOException
            or SocketException
            or WebSocketException
            or JsonException
            or TaskCanceledException;
}
