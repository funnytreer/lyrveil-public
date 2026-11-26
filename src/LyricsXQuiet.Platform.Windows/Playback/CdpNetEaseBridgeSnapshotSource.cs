using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using LyricsXQuiet.Core.Playback;

namespace LyricsXQuiet.Platform.Windows.Playback;

/// <summary>
/// Reads NetEase Cloud Music playback anchors from a Chromium DevTools endpoint
/// exposed on localhost. The source never sends playback-control commands.
/// </summary>
public sealed class CdpNetEaseBridgeSnapshotSource : INetEaseBridgeSnapshotSource
{
    private static readonly TimeSpan ObservationInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan AnchorInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SeekThreshold = TimeSpan.FromMilliseconds(650);
    private const int InvalidCapturesBeforeUnavailable = 3;

    private readonly int _port;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _lifecycleLock = new();
    private readonly object _stateLock = new();
    private Task? _workerTask;
    private NetEaseBridgeSnapshot? _current;
    private string? _lastSuccessfulTargetId;
    private long _sequence = -1;
    private bool _started;
    private bool _disposed;

    public CdpNetEaseBridgeSnapshotSource(int port)
    {
        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        _port = port;
        LocalEndpoint = new IPEndPoint(IPAddress.Loopback, port);
    }

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

    public IPEndPoint LocalEndpoint { get; }

    /// <summary>
    /// Starts a resilient background connection loop. A missing or unavailable
    /// CDP endpoint is treated as an offline source and never fails application startup.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return Task.CompletedTask;
            }

            _started = true;
            _workerTask = RunAsync(_lifetimeCancellation.Token);
        }

        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndObserveAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                Publish(null);
            }

            try
            {
                await Task.Delay(ReconnectDelay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task ConnectAndObserveAsync(CancellationToken cancellationToken)
    {
        var targets = await CdpNetEaseDiscovery.ListTargetsAsync(
            _port,
            DiscoveryTimeout,
            cancellationToken);
        var candidates = CdpNetEaseDiscovery.OrderTargets(
            targets,
            _lastSuccessfulTargetId);
        if (candidates.Count == 0)
        {
            throw new CdpNetEaseBridgeException(
                "No NetEase page was found on the local CDP endpoint.");
        }

        Exception? lastFailure = null;
        foreach (var target in candidates)
        {
            try
            {
                await ConnectAndObserveTargetAsync(target, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException exception)
            {
                lastFailure = exception;
            }
            catch (Exception exception) when (
                exception is WebSocketException
                    or CdpNetEaseBridgeException
                    or HttpRequestException
                    or ObjectDisposedException)
            {
                lastFailure = exception;
            }
        }

        throw lastFailure is null
            ? new CdpNetEaseBridgeException(
                "No NetEase CDP page exposed usable playback information.")
            : new CdpNetEaseBridgeException(
                "No NetEase CDP page exposed usable playback information.",
                lastFailure);
    }

    private async Task ConnectAndObserveTargetAsync(
        CdpNetEaseTarget target,
        CancellationToken cancellationToken)
    {
        var webSocketUri = CdpNetEaseDiscovery.ValidateLoopbackWebSocket(target, _port);

        await using var connection = await CdpNetEaseConnection.ConnectAsync(
            webSocketUri,
            DiscoveryTimeout,
            cancellationToken);

        NetEaseBridgeSnapshot? lastPublished = null;
        var lastPublishedTimestamp = 0L;
        var invalidCaptures = 0;
        var producedUsableSnapshot = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var iterationStarted = Stopwatch.GetTimestamp();
                var probe = await CdpNetEaseProbe.CaptureAsync(
                    connection,
                    CommandTimeout,
                    cancellationToken);

                if (CdpNetEaseSnapshotAdapter.TryCreate(
                        probe,
                        Interlocked.Read(ref _sequence) + 1,
                        out var candidate))
                {
                    invalidCaptures = 0;
                    producedUsableSnapshot = true;
                    _lastSuccessfulTargetId = target.Id;
                    var shouldPublish = IsUrgent(candidate!, lastPublished)
                        || lastPublishedTimestamp == 0
                        || Stopwatch.GetElapsedTime(lastPublishedTimestamp) >= AnchorInterval;
                    if (shouldPublish)
                    {
                        var snapshot = candidate! with
                        {
                            Sequence = Interlocked.Increment(ref _sequence)
                        };
                        Publish(snapshot);
                        lastPublished = snapshot;
                        lastPublishedTimestamp = Stopwatch.GetTimestamp();
                    }
                }
                else
                {
                    invalidCaptures++;

                    // A CDP endpoint can expose several NetEase windows. Some of
                    // them have a plausible title and URL but do not host the
                    // actual playback module. Do not stay attached to such a page:
                    // try the next ranked target in the same discovery result. A
                    // page that was already usable gets a longer recovery window
                    // so a brief reload cannot cause target flapping.
                    if (CdpNetEaseTargetProbePolicy.ShouldTryNextTarget(
                            probe,
                            producedUsableSnapshot,
                            invalidCaptures))
                    {
                        return;
                    }

                    // NetEase briefly exposes an incomplete combination of metadata,
                    // state and playId during some seeks. A single 100 ms sample must
                    // not revoke the exact frozen seek anchor: doing so starts the
                    // fallback clock before audio has resumed and makes lyrics appear
                    // to jump ahead. A real CDP disconnect still reaches the outer
                    // exception path and publishes null immediately.
                    if (ShouldPublishUnavailable(invalidCaptures))
                    {
                        lastPublished = null;
                        lastPublishedTimestamp = 0;
                        Publish(null);
                    }

                    // A page reload replaces the cached read-only helper. If a build
                    // changes its module graph without navigating, periodically remove
                    // only our own helper so that the next sample can discover it again.
                    if (invalidCaptures % 8 == 0)
                    {
                        await CdpNetEaseProbe.ResetOwnHelperAsync(
                            connection,
                            CommandTimeout,
                            cancellationToken);
                    }
                }

                var remaining = ObservationInterval - Stopwatch.GetElapsedTime(iterationStarted);
                if (invalidCaptures == 0 && lastPublishedTimestamp != 0)
                {
                    var untilAnchor = AnchorInterval
                        - Stopwatch.GetElapsedTime(lastPublishedTimestamp);
                    if (untilAnchor < remaining)
                    {
                        remaining = untilAnchor;
                    }
                }

                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, cancellationToken);
                }
            }
        }
        finally
        {
            // Deactivate the page-local callbacks while this connection is still
            // usable. Failure is harmless: the next installation also retires old helpers.
            using var resetTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
            try
            {
                await CdpNetEaseProbe.ResetOwnHelperAsync(
                    connection,
                    TimeSpan.FromMilliseconds(750),
                    resetTimeout.Token);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException
                    or WebSocketException
                    or CdpNetEaseBridgeException
                    or ObjectDisposedException)
            {
            }
        }
    }

    private static bool ShouldPublishUnavailable(int consecutiveInvalidCaptures) =>
        consecutiveInvalidCaptures >= InvalidCapturesBeforeUnavailable;

    private static bool IsUrgent(
        NetEaseBridgeSnapshot next,
        NetEaseBridgeSnapshot? previous)
    {
        if (previous is null
            || next.State != previous.State
            || !string.Equals(next.Track.Id, previous.Track.Id, StringComparison.Ordinal)
            || !string.Equals(next.Track.Title, previous.Track.Title, StringComparison.Ordinal)
            || !string.Equals(next.Track.Album, previous.Track.Album, StringComparison.Ordinal)
            || next.Track.DurationMs != previous.Track.DurationMs
            || !next.Track.Artists.SequenceEqual(previous.Track.Artists, StringComparer.Ordinal)
            || next.TimelineFrozen != previous.TimelineFrozen
            || Math.Abs(next.Rate - previous.Rate) > 0.001d)
        {
            return true;
        }

        var elapsedMs = Math.Max(0d, (next.ObservedAt - previous.ObservedAt).TotalMilliseconds);
        var expectedPosition = previous.PositionMs;
        if (previous.State == PlaybackState.Playing && previous.Rate > 0)
        {
            expectedPosition += (long)Math.Round(elapsedMs * previous.Rate);
        }

        return Math.Abs(next.PositionMs - expectedPosition) >= SeekThreshold.TotalMilliseconds;
    }

    private void Publish(NetEaseBridgeSnapshot? snapshot)
    {
        bool changed;
        lock (_stateLock)
        {
            changed = !ReferenceEquals(_current, snapshot);
            if (snapshot is null && _current is null)
            {
                changed = false;
            }

            _current = snapshot;
        }

        if (!changed)
        {
            return;
        }

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
                // A consumer must not terminate the source's reconnect loop.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? workerTask;
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetimeCancellation.Cancel();
            workerTask = _workerTask;
        }

        if (workerTask is not null)
        {
            try
            {
                await workerTask;
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
            }
        }

        lock (_stateLock)
        {
            _current = null;
        }

        _lifetimeCancellation.Dispose();
    }
}

internal sealed record CdpNetEaseTarget(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("webSocketDebuggerUrl")] string? WebSocketDebuggerUrl);

internal static class CdpNetEaseDiscovery
{
    private const long MaximumTargetListBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<IReadOnlyList<CdpNetEaseTarget>> ListTargetsAsync(
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = timeout
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute),
            Timeout = timeout,
            MaxResponseContentBufferSize = MaximumTargetListBytes
        };
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.GetAsync(
            "json/list",
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new CdpNetEaseBridgeException("The local CDP endpoint rejected target discovery.");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null
            && !mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new CdpNetEaseBridgeException("The local CDP endpoint returned an unexpected content type.");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.LongLength > MaximumTargetListBytes)
        {
            throw new CdpNetEaseBridgeException("The local CDP target list is too large.");
        }

        return JsonSerializer.Deserialize<List<CdpNetEaseTarget>>(bytes, JsonOptions) ?? [];
    }

    public static CdpNetEaseTarget SelectTarget(IReadOnlyList<CdpNetEaseTarget> targets)
    {
        var target = OrderTargets(targets, preferredTargetId: null).FirstOrDefault();

        if (target is null)
        {
            throw new CdpNetEaseBridgeException("No NetEase page was found on the local CDP endpoint.");
        }

        return target;
    }

    public static IReadOnlyList<CdpNetEaseTarget> OrderTargets(
        IReadOnlyList<CdpNetEaseTarget> targets,
        string? preferredTargetId)
    {
        ArgumentNullException.ThrowIfNull(targets);

        return targets
            .Where(item => item.Type.Equals("page", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(item.WebSocketDebuggerUrl))
            .Select((item, index) => new
            {
                Target = item,
                Score = Score(item),
                IsPreferred = !string.IsNullOrWhiteSpace(preferredTargetId)
                    && string.Equals(item.Id, preferredTargetId, StringComparison.Ordinal),
                Index = index
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.IsPreferred)
            .ThenByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .Select(item => item.Target)
            .ToArray();
    }

    public static Uri ValidateLoopbackWebSocket(CdpNetEaseTarget target, int expectedPort)
    {
        if (!Uri.TryCreate(target.WebSocketDebuggerUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeWs
            || !uri.IsLoopback
            || uri.Port != expectedPort
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new CdpNetEaseBridgeException("The CDP target did not provide a loopback WebSocket.");
        }

        // Use a numeric loopback address so no DNS or proxy path is involved.
        return new UriBuilder(uri) { Host = IPAddress.Loopback.ToString() }.Uri;
    }

    private static int Score(CdpNetEaseTarget target)
    {
        var text = $"{target.Title}\n{target.Url}";
        var score = 0;
        score += Contains(text, "网易云") ? 8 : 0;
        score += Contains(text, "cloudmusic") ? 8 : 0;
        score += Contains(text, "music.163.com") ? 6 : 0;
        score += Contains(text, "orpheus") ? 5 : 0;
        return score;
    }

    private static bool Contains(string text, string value) =>
        text.Contains(value, StringComparison.OrdinalIgnoreCase);
}

internal static class CdpNetEaseTargetProbePolicy
{
    private const int InitialInvalidCapturesBeforeFallback = 8;
    private const int PreviouslyUsableHardFailuresBeforeFallback = 8;

    public static bool ShouldTryNextTarget(
        JsonElement probe,
        bool producedUsableSnapshot,
        int consecutiveInvalidCaptures)
    {
        if (consecutiveInvalidCaptures <= 0)
        {
            return false;
        }

        var playbackModuleUnavailable = IsPlaybackModuleUnavailable(probe);
        if (!producedUsableSnapshot)
        {
            return playbackModuleUnavailable
                || consecutiveInvalidCaptures >= InitialInvalidCapturesBeforeFallback;
        }

        return playbackModuleUnavailable
            && consecutiveInvalidCaptures >= PreviouslyUsableHardFailuresBeforeFallback;
    }

    private static bool IsPlaybackModuleUnavailable(JsonElement probe)
    {
        if (!probe.TryGetProperty("ok", out var ok)
            || ok.ValueKind != JsonValueKind.False
            || !probe.TryGetProperty("error", out var error)
            || error.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var message = error.GetString() ?? string.Empty;
        return message.Contains("module runtime is unavailable", StringComparison.OrdinalIgnoreCase)
            || message.Contains("playback modules are unavailable", StringComparison.OrdinalIgnoreCase)
            || message.Contains("live playback subscription is unavailable", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class CdpNetEaseConnection : IAsyncDisposable
{
    private const int ReceiveBufferSize = 16 * 1024;
    private const int MaximumMessageSize = 8 * 1024 * 1024;

    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private Task? _receiveTask;
    private Exception? _terminalError;
    private long _nextRequestId;
    private int _disposed;

    private CdpNetEaseConnection()
    {
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _socket.Options.Proxy = null;
    }

    public static async Task<CdpNetEaseConnection> ConnectAsync(
        Uri loopbackUri,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (loopbackUri.Scheme != Uri.UriSchemeWs || !loopbackUri.IsLoopback)
        {
            throw new ArgumentException("The CDP WebSocket must be a loopback ws URI.", nameof(loopbackUri));
        }

        var connection = new CdpNetEaseConnection();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await connection._socket.ConnectAsync(loopbackUri, timeoutSource.Token);
            connection._receiveTask = connection.ReceiveLoopAsync(
                connection._lifetimeCancellation.Token);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<JsonElement> SendCommandAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_terminalError is not null)
        {
            throw new CdpNetEaseBridgeException("The CDP connection is no longer available.", _terminalError);
        }

        var requestId = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion))
        {
            throw new InvalidOperationException("Unable to register a CDP request.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        timeoutSource.CancelAfter(timeout);

        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new CdpRequest(
                    requestId,
                    method,
                    parameters is null ? null : JsonSerializer.SerializeToElement(parameters)));

            await _sendGate.WaitAsync(timeoutSource.Token);
            try
            {
                await _socket.SendAsync(
                    payload,
                    WebSocketMessageType.Text,
                    true,
                    timeoutSource.Token);
            }
            finally
            {
                _sendGate.Release();
            }

            return await completion.Task.WaitAsync(timeoutSource.Token);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        throw new CdpNetEaseBridgeException("The CDP page closed its WebSocket.");
                    }

                    if (result.MessageType != WebSocketMessageType.Text
                        || message.Length + result.Count > MaximumMessageSize)
                    {
                        throw new CdpNetEaseBridgeException("The CDP page returned an invalid message.");
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                Dispatch(message.GetBuffer().AsSpan(0, checked((int)message.Length)));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _terminalError = exception;
            FailPending(exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Dispatch(ReadOnlySpan<byte> utf8Message)
    {
        using var document = JsonDocument.Parse(utf8Message.ToArray());
        var root = document.RootElement;
        if (!root.TryGetProperty("id", out var idElement)
            || !idElement.TryGetInt64(out var id)
            || !_pending.TryGetValue(id, out var completion))
        {
            return;
        }

        if (root.TryGetProperty("error", out var error))
        {
            completion.TrySetException(new CdpNetEaseBridgeException(
                error.TryGetProperty("message", out var message)
                    ? message.GetString() ?? "CDP returned an error."
                    : "CDP returned an error."));
            return;
        }

        if (!root.TryGetProperty("result", out var result))
        {
            completion.TrySetException(new CdpNetEaseBridgeException(
                "The CDP response did not contain a result."));
            return;
        }

        completion.TrySetResult(result.Clone());
    }

    private void FailPending(Exception exception)
    {
        foreach (var completion in _pending.Values)
        {
            completion.TrySetException(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetimeCancellation.Cancel();
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "bridge complete",
                    closeTimeout.Token);
            }
            catch (Exception exception) when (
                exception is WebSocketException or OperationCanceledException)
            {
            }
        }

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask;
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
            }
            catch (WebSocketException)
            {
            }
        }

        FailPending(new ObjectDisposedException(nameof(CdpNetEaseConnection)));
        _socket.Dispose();
        _sendGate.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private sealed record CdpRequest(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        JsonElement? Parameters);
}

internal static class CdpNetEaseProbe
{
    public static async Task<JsonElement> CaptureAsync(
        CdpNetEaseConnection connection,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var response = await connection.SendCommandAsync(
            "Runtime.evaluate",
            new
            {
                expression = CdpNetEaseProbeScript.Expression,
                awaitPromise = true,
                returnByValue = true,
                userGesture = false,
                silent = true
            },
            timeout,
            cancellationToken);

        if (response.TryGetProperty("exceptionDetails", out var exceptionDetails))
        {
            throw new CdpNetEaseBridgeException(
                exceptionDetails.TryGetProperty("text", out var text)
                    ? text.GetString() ?? "The probe expression failed."
                    : "The probe expression failed.");
        }

        if (!response.TryGetProperty("result", out var remoteObject)
            || !remoteObject.TryGetProperty("value", out var value))
        {
            throw new CdpNetEaseBridgeException("The probe did not return a serializable result.");
        }

        return value.Clone();
    }

    public static async Task ResetOwnHelperAsync(
        CdpNetEaseConnection connection,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await connection.SendCommandAsync(
            "Runtime.evaluate",
            new
            {
                expression = CdpNetEaseProbeScript.ResetExpression,
                awaitPromise = false,
                returnByValue = true,
                userGesture = false,
                silent = true
            },
            timeout,
            cancellationToken);
    }
}

internal static class CdpNetEaseSnapshotAdapter
{
    private static readonly char[] ArtistSeparators =
        ['/', '\\', ',', '，', '、', ';', '；', '&', '＆', '·', '・', '|'];

    public static bool TryCreate(
        JsonElement probe,
        long sequence,
        out NetEaseBridgeSnapshot? snapshot)
    {
        snapshot = null;
        if (!probe.TryGetProperty("ok", out var ok)
            || ok.ValueKind != JsonValueKind.True
            || !probe.TryGetProperty("track", out var track)
            || track.ValueKind != JsonValueKind.Object
            || !probe.TryGetProperty("playback", out var playback)
            || playback.ValueKind != JsonValueKind.Object
            || !TryReadScalar(track, "id", out var id)
            || !long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId)
            || numericId <= 0
            || !TryReadScalar(track, "title", out var title)
            || !TryReadScalar(track, "artist", out var artist)
            || !IsValidText(title, 512)
            || !IsValidText(artist, 4096)
            || !TryReadLong(playback, "positionMs", out var positionMs)
            || !TryReadLong(playback, "durationMs", out var durationMs)
            || positionMs < 0
            || durationMs <= 0
            || durationMs > TimeSpan.FromHours(24).TotalMilliseconds
            || positionMs - durationMs > TimeSpan.FromSeconds(2).TotalMilliseconds
            || !TryReadState(playback, out var state))
        {
            return false;
        }

        var artists = artist
            .Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();
        var album = TryReadScalar(track, "album", out var albumValue)
            ? albumValue
            : string.Empty;
        if (artists.Length == 0
            || !IsValidText(album, 512, allowEmpty: true)
            || artists.Any(value => !IsValidText(value, 256)))
        {
            return false;
        }

        var observedAt = DateTimeOffset.UtcNow;
        var timelineFrozen = playback.TryGetProperty("timelineFrozen", out var frozen)
            && frozen.ValueKind == JsonValueKind.True;
        var timelineEventType = TryReadScalar(
            playback,
            "timelineEventType",
            out var eventTypeValue)
            ? eventTypeValue
            : string.Empty;
        var timelineEventAgeMs = TryReadLong(
            playback,
            "timelineEventAgeMs",
            out var eventAgeValue)
            ? Math.Clamp(eventAgeValue, 0, 500)
            : 0;
        var anchoredPositionMs = state == PlaybackState.Playing
            && !timelineFrozen
            && string.Equals(
                timelineEventType,
                "playprogress",
                StringComparison.OrdinalIgnoreCase)
                ? Math.Min(positionMs + timelineEventAgeMs, durationMs)
                : Math.Min(positionMs, durationMs);
        snapshot = new NetEaseBridgeSnapshot(
            NetEaseBridgeSnapshotMerger.CurrentProtocolVersion,
            sequence,
            observedAt,
            new NetEaseBridgeTrack(
                numericId.ToString(CultureInfo.InvariantCulture),
                title.Trim(),
                Array.AsReadOnly(artists),
                album.Trim(),
                durationMs),
            state,
            anchoredPositionMs,
            1d,
            timelineFrozen);
        return NetEaseBridgeSnapshotMerger.IsUsable(
            snapshot,
            observedAt,
            NetEaseBridgeSnapshotMerger.DefaultMaximumAge);
    }

    private static bool TryReadScalar(
        JsonElement container,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!container.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        value = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            _ => string.Empty
        };
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryReadLong(
        JsonElement container,
        string propertyName,
        out long value)
    {
        value = 0;
        if (!container.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            if (property.TryGetInt64(out value))
            {
                return true;
            }

            if (property.TryGetDouble(out var number)
                && double.IsFinite(number)
                && number is >= long.MinValue and <= long.MaxValue)
            {
                value = checked((long)Math.Round(number));
                return true;
            }
        }

        return property.ValueKind == JsonValueKind.String
            && long.TryParse(
                property.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value);
    }

    private static bool TryReadState(JsonElement playback, out PlaybackState state)
    {
        state = PlaybackState.Unknown;
        if (playback.TryGetProperty("isPlaying", out var isPlaying)
            && isPlaying.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            state = isPlaying.GetBoolean()
                ? PlaybackState.Playing
                : ReadInactiveState(playback);
            return true;
        }

        if (!playback.TryGetProperty("status", out var status))
        {
            return false;
        }

        if (status.ValueKind == JsonValueKind.String)
        {
            var normalized = status.GetString()?.Trim().ToLowerInvariant() ?? string.Empty;
            if (normalized.Contains("playing", StringComparison.Ordinal)
                || normalized is "play" or "started")
            {
                state = PlaybackState.Playing;
            }
            else if (normalized.Contains("pause", StringComparison.Ordinal))
            {
                state = PlaybackState.Paused;
            }
            else if (normalized.Contains("stop", StringComparison.Ordinal)
                || normalized.Contains("ended", StringComparison.Ordinal))
            {
                state = PlaybackState.Stopped;
            }
        }
        else if (status.ValueKind == JsonValueKind.Number
            && status.TryGetInt32(out var numericState))
        {
            // The page-level Redux state uses Pause=1 and Playing=2.
            // The probe normally returns a normalized string, but keep the
            // numeric compatibility path consistent for older page builds.
            state = numericState switch
            {
                2 => PlaybackState.Playing,
                1 => PlaybackState.Paused,
                -1 or 0 => PlaybackState.Stopped,
                _ => PlaybackState.Unknown
            };
        }

        return state is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Stopped;
    }

    private static PlaybackState ReadInactiveState(JsonElement playback)
    {
        if (playback.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.String)
        {
            var value = status.GetString() ?? string.Empty;
            if (value.Contains("stop", StringComparison.OrdinalIgnoreCase)
                || value.Contains("ended", StringComparison.OrdinalIgnoreCase))
            {
                return PlaybackState.Stopped;
            }
        }

        return PlaybackState.Paused;
    }

    private static bool IsValidText(string value, int maximumLength, bool allowEmpty = false) =>
        value.Length <= maximumLength
        && (allowEmpty || !string.IsNullOrWhiteSpace(value))
        && !value.Any(char.IsControl);
}

internal sealed class CdpNetEaseBridgeException : Exception
{
    public CdpNetEaseBridgeException(string message)
        : base(message)
    {
    }

    public CdpNetEaseBridgeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
