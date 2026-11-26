using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LyricsXQuiet.Core.Playback;

namespace LyricsXQuiet.Platform.Windows.Playback;

public sealed class LoopbackNetEaseBridgeSnapshotSource : INetEaseBridgeSnapshotSource
{
    public const int MaximumMessageBytes = 16 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) }
    };

    private readonly int _requestedPort;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _stateLock = new();
    private TcpListener? _listener;
    private Task? _acceptTask;
    private bool _started;
    private bool _disposed;

    public LoopbackNetEaseBridgeSnapshotSource(int port = 0)
    {
        if (port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        _requestedPort = port;
    }

    public event EventHandler<NetEaseBridgeSnapshot?>? SnapshotChanged;

    public NetEaseBridgeSnapshot? Current { get; private set; }

    public IPEndPoint? LocalEndpoint => _listener?.LocalEndpoint as IPEndPoint;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_started)
        {
            return Task.CompletedTask;
        }

        _listener = new TcpListener(IPAddress.Loopback, _requestedPort);
        _listener.Start(backlog: 1);
        _started = true;
        _acceptTask = AcceptLoopAsync(_lifetimeCancellation.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            var acceptedLoopbackClient = false;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                if (client.Client.RemoteEndPoint is not IPEndPoint remote
                    || !IPAddress.IsLoopback(remote.Address))
                {
                    client.Dispose();
                    continue;
                }

                acceptedLoopbackClient = true;
                await ReadClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (
                exception is IOException
                    or SocketException
                    or JsonException
                    or DecoderFallbackException
                    or InvalidDataException)
            {
                // Reject this local client and wait for a new bridge connection.
            }
            finally
            {
                client?.Dispose();
                if (acceptedLoopbackClient)
                {
                    Publish(null);
                }
            }
        }
    }

    private async Task ReadClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        client.NoDelay = true;
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        var message = new List<byte>(1024);
        long previousSequence = -1;
        try
        {
            using var stream = client.GetStream();
            while (true)
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (bytesRead == 0)
                {
                    return;
                }

                for (var index = 0; index < bytesRead; index++)
                {
                    var value = buffer[index];
                    if (value == (byte)'\n')
                    {
                        if (message.Count > 0 && message[^1] == (byte)'\r')
                        {
                            message.RemoveAt(message.Count - 1);
                        }

                        if (message.Count == 0)
                        {
                            throw new InvalidDataException("Empty bridge message.");
                        }

                        var snapshot = ParseAndValidate(message, previousSequence);
                        previousSequence = snapshot.Sequence;
                        Publish(snapshot);
                        message.Clear();
                        continue;
                    }

                    message.Add(value);
                    if (message.Count > MaximumMessageBytes)
                    {
                        throw new InvalidDataException("Bridge message is too large.");
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static NetEaseBridgeSnapshot ParseAndValidate(
        IReadOnlyCollection<byte> message,
        long previousSequence)
    {
        var json = StrictUtf8.GetString(message.ToArray());
        var wire = JsonSerializer.Deserialize<BridgeWireSnapshot>(json, JsonOptions)
            ?? throw new InvalidDataException("Bridge message is null.");
        if (wire.Track is null || wire.Track.Artists is null)
        {
            throw new InvalidDataException("Bridge track is incomplete.");
        }

        EnsureText(wire.Track.Id, 32, nameof(wire.Track.Id));
        EnsureText(wire.Track.Title, 512, nameof(wire.Track.Title));
        EnsureText(wire.Track.Album, 512, nameof(wire.Track.Album), allowEmpty: true);
        if (wire.Track.Artists.Length is 0 or > 16)
        {
            throw new InvalidDataException("Bridge artist count is invalid.");
        }

        foreach (var artist in wire.Track.Artists)
        {
            EnsureText(artist, 256, "artist");
        }

        var snapshot = new NetEaseBridgeSnapshot(
            wire.Version,
            wire.Seq,
            wire.ObservedAt,
            new NetEaseBridgeTrack(
                wire.Track.Id,
                wire.Track.Title,
                Array.AsReadOnly(wire.Track.Artists),
                wire.Track.Album,
                wire.Track.DurationMs),
            wire.State,
            wire.PositionMs,
            wire.Rate,
            wire.TimelineFrozen);

        if (snapshot.Sequence <= previousSequence
            || !NetEaseBridgeSnapshotMerger.IsUsable(
                snapshot,
                DateTimeOffset.Now,
                NetEaseBridgeSnapshotMerger.DefaultMaximumAge))
        {
            throw new InvalidDataException("Bridge snapshot failed validation.");
        }

        return snapshot;
    }

    private static void EnsureText(
        string? value,
        int maximumLength,
        string field,
        bool allowEmpty = false)
    {
        if (value is null
            || (!allowEmpty && string.IsNullOrWhiteSpace(value))
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new InvalidDataException($"Bridge field {field} is invalid.");
        }
    }

    private void Publish(NetEaseBridgeSnapshot? snapshot)
    {
        lock (_stateLock)
        {
            Current = snapshot;
        }

        SnapshotChanged?.Invoke(this, snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();
        _listener?.Stop();
        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _lifetimeCancellation.Dispose();
    }

    private sealed record BridgeWireSnapshot(
        [property: JsonRequired] int Version,
        [property: JsonRequired] long Seq,
        [property: JsonRequired] DateTimeOffset ObservedAt,
        [property: JsonRequired] BridgeWireTrack Track,
        [property: JsonRequired] PlaybackState State,
        [property: JsonRequired] long PositionMs,
        [property: JsonRequired] double Rate,
        bool TimelineFrozen = false);

    private sealed record BridgeWireTrack(
        [property: JsonRequired] string Id,
        [property: JsonRequired] string Title,
        [property: JsonRequired] string[] Artists,
        [property: JsonRequired] string Album,
        [property: JsonRequired] long DurationMs);
}
