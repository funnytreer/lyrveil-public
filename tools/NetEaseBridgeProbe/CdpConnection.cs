using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LyricsXQuiet.Tools.NetEaseBridgeProbe;

internal sealed class CdpConnection : IAsyncDisposable
{
    private const int ReceiveBufferSize = 16 * 1024;
    private const int MaximumMessageSize = 8 * 1024 * 1024;

    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private Task? _receiveTask;
    private Exception? _terminalError;
    private long _nextRequestId;
    private int _disposed;

    private CdpConnection()
    {
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _socket.Options.Proxy = null;
    }

    public static async Task<CdpConnection> ConnectAsync(
        Uri loopbackUri,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (loopbackUri.Scheme != Uri.UriSchemeWs || !loopbackUri.IsLoopback)
        {
            throw new ArgumentException("CDP WebSocket 必须是 localhost 的 ws 地址。", nameof(loopbackUri));
        }

        var connection = new CdpConnection();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await connection._socket.ConnectAsync(loopbackUri, timeoutSource.Token);
            connection._receiveTask = connection.ReceiveLoopAsync(connection._lifetime.Token);
            return connection;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            await connection.DisposeAsync();
            throw new ProbeException("连接本机调试页面超时。", exception);
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
            throw new ProbeException("调试页面连接已经中断。", _terminalError);
        }

        var requestId = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion))
        {
            throw new InvalidOperationException("无法登记 CDP 请求。");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
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
                await _socket.SendAsync(payload, WebSocketMessageType.Text, true, timeoutSource.Token);
            }
            finally
            {
                _sendGate.Release();
            }

            return await completion.Task.WaitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new ProbeException($"CDP 命令 {method} 等待响应超时。", exception);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();

        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "probe complete", closeTimeout.Token);
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
            {
                // The peer may already have gone away; disposal still releases the socket.
            }
        }

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (WebSocketException)
            {
            }
        }

        FailPending(new ObjectDisposedException(nameof(CdpConnection)));
        _socket.Dispose();
        _sendGate.Dispose();
        _lifetime.Dispose();
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
                        throw new ProbeException("调试页面关闭了 WebSocket 连接。");
                    }

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        throw new ProbeException("调试页面返回了意外的二进制消息。");
                    }

                    if (message.Length + result.Count > MaximumMessageSize)
                    {
                        throw new ProbeException("调试页面返回的单条消息过大，已停止读取。");
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
        if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id) ||
            !_pending.TryGetValue(id, out var completion))
        {
            return;
        }

        if (root.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : error.GetRawText();
            completion.TrySetException(new ProbeException($"CDP 返回错误：{message}"));
            return;
        }

        if (!root.TryGetProperty("result", out var result))
        {
            completion.TrySetException(new ProbeException("CDP 响应缺少 result 字段。"));
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

    private sealed record CdpRequest(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Parameters);

}
