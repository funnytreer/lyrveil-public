using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using LyricsXQuiet.Core.Playback;
using Microsoft.Win32.SafeHandles;

namespace LyricsXQuiet.Platform.Windows.Companion;

public sealed record CompanionLaunchRequest(
    string PlayerPath,
    bool EnableNetEaseProgressBridge)
{
    public static bool TryCreate(
        string? playerPath,
        bool enableNetEaseProgressBridge,
        out CompanionLaunchRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(playerPath)
            || playerPath.Any(char.IsControl))
        {
            return false;
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(playerPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!File.Exists(normalizedPath)
            || !string.Equals(
                Path.GetExtension(normalizedPath),
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var executableName = Path.GetFileNameWithoutExtension(normalizedPath);
        var player = MusicPlayerCatalog.Identify(executableName);
        if (player is null
            || !string.Equals(
                executableName,
                player.ProcessName,
                StringComparison.OrdinalIgnoreCase)
            || (enableNetEaseProgressBridge
                && !string.Equals(player.Id, "netease", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        request = new CompanionLaunchRequest(
            normalizedPath,
            enableNetEaseProgressBridge);
        return true;
    }
}

public enum CompanionLaunchResult : byte
{
    Started = 1,
    AlreadyRunning = 2,
    Rejected = 3,
    Failed = 4
}

/// <summary>
/// A deliberately narrow, current-user-only IPC channel. It accepts one fixed
/// command shape and never transports command lines, shell verbs or arguments.
/// </summary>
public sealed class SingleInstanceCompanionChannel : IAsyncDisposable
{
    private const uint ProtocolMagic = 0x3152594C; // "LYR1" in little endian.
    private const byte ProtocolVersion = 1;
    private const byte LaunchCompanionCommand = 1;
    private const int RequestHeaderLength = 12;
    private const int ResponseLength = 8;
    private const int MaximumPathBytes = 4096;
    private static readonly TimeSpan ClientRequestTimeout = TimeSpan.FromSeconds(3);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly string _pipeName;
    private readonly Func<CompanionLaunchRequest, CancellationToken, Task<CompanionLaunchResult>>
        _handler;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _lifecycleLock = new();
    private Task? _serverTask;
    private bool _disposed;

    public SingleInstanceCompanionChannel(
        string pipeName,
        Func<CompanionLaunchRequest, CancellationToken, Task<CompanionLaunchResult>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public static string GetCurrentSessionPipeName()
    {
        using var process = Process.GetCurrentProcess();
        return $"Lyrveil.DesktopLyrics.Command.v1.S{process.SessionId}";
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lifecycleLock)
        {
            _serverTask ??= RunServerAsync(_lifetimeCancellation.Token);
        }
    }

    public static async Task<CompanionLaunchResult> SendAsync(
        string pipeName,
        CompanionLaunchRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(request);
        if (!CompanionLaunchRequest.TryCreate(
                request.PlayerPath,
                request.EnableNetEaseProgressBridge,
                out var validated))
        {
            return CompanionLaunchResult.Rejected;
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(timeoutCancellation.Token);
        await WriteRequestAsync(client, validated!, timeoutCancellation.Token);
        var response = new byte[ResponseLength];
        await client.ReadExactlyAsync(response, timeoutCancellation.Token);
        return ParseResponse(response);
    }

    private async Task RunServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                await HandleClientAsync(server, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // A per-client timeout must never fault the long-lived accept loop.
            }
            catch (IOException)
            {
                // A malformed or abruptly closed client must not stop the server.
            }
            catch (UnauthorizedAccessException)
            {
                // Current-user isolation is mandatory; retry rather than weaken it.
                await DelayAfterFailureAsync(cancellationToken);
            }
        }
    }

    private async Task HandleClientAsync(
        NamedPipeServerStream server,
        CancellationToken cancellationToken)
    {
        using var clientTimeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        clientTimeoutCancellation.CancelAfter(ClientRequestTimeout);
        var clientToken = clientTimeoutCancellation.Token;
        var result = CompanionLaunchResult.Rejected;
        try
        {
            // CurrentUserOnly prevents cross-user clients. The executable check also
            // prevents another process owned by the same user from using this pipe as
            // a supported-player launcher.
            if (!IsTrustedAppClient(server))
            {
                var rejectedResponse = CreateResponse(CompanionLaunchResult.Rejected);
                await server.WriteAsync(rejectedResponse, clientToken);
                await server.FlushAsync(clientToken);
                return;
            }

            var request = await ReadAndValidateRequestAsync(server, clientToken);
            if (request is not null)
            {
                result = await _handler(request, clientToken);
            }
        }
        catch (Exception exception) when (
            exception is DecoderFallbackException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            result = CompanionLaunchResult.Rejected;
        }
        catch (OperationCanceledException) when (
            clientToken.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            // A connected client gets a short, fixed request budget. Disconnect it
            // without a response so the server can accept the next app instance.
            return;
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            result = CompanionLaunchResult.Failed;
        }

        var response = CreateResponse(result);
        try
        {
            await server.WriteAsync(response, clientToken);
            await server.FlushAsync(clientToken);
        }
        catch (OperationCanceledException) when (
            clientToken.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            // The request budget can expire after the handler has completed but
            // before the reply is flushed. Drop this client and accept the next one.
        }
    }

    private static bool IsTrustedAppClient(NamedPipeServerStream server)
    {
        try
        {
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var clientProcessId)
                || clientProcessId == 0
                || clientProcessId > int.MaxValue
                || string.IsNullOrWhiteSpace(Environment.ProcessPath))
            {
                return false;
            }

            using var clientProcess = Process.GetProcessById((int)clientProcessId);
            var clientPath = clientProcess.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(clientPath))
            {
                return false;
            }

            var ownPath = Path.GetFullPath(Environment.ProcessPath);
            var normalizedClientPath = Path.GetFullPath(clientPath);
            return string.Equals(
                ownPath,
                normalizedClientPath,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Identity verification is mandatory. Any inaccessible or unstable
            // client process is rejected rather than being treated as trusted.
            return false;
        }
    }

    private static async Task<CompanionLaunchRequest?> ReadAndValidateRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[RequestHeaderLength];
        await stream.ReadExactlyAsync(header, cancellationToken);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != ProtocolMagic
            || header[4] != ProtocolVersion
            || header[5] != LaunchCompanionCommand
            || (header[6] & ~1) != 0
            || header[7] != 0)
        {
            return null;
        }

        var pathLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        if (pathLength is <= 0 or > MaximumPathBytes)
        {
            return null;
        }

        var pathBytes = new byte[pathLength];
        await stream.ReadExactlyAsync(pathBytes, cancellationToken);
        var suppliedPath = StrictUtf8.GetString(pathBytes);
        if (!CompanionLaunchRequest.TryCreate(
                suppliedPath,
                (header[6] & 1) != 0,
                out var request)
            || !string.Equals(suppliedPath, request!.PlayerPath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return request;
    }

    private static async Task WriteRequestAsync(
        Stream stream,
        CompanionLaunchRequest request,
        CancellationToken cancellationToken)
    {
        var pathBytes = StrictUtf8.GetBytes(request.PlayerPath);
        if (pathBytes.Length is <= 0 or > MaximumPathBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        var payload = new byte[RequestHeaderLength + pathBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, ProtocolMagic);
        payload[4] = ProtocolVersion;
        payload[5] = LaunchCompanionCommand;
        payload[6] = request.EnableNetEaseProgressBridge ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(
            payload.AsSpan(8),
            pathBytes.Length);
        pathBytes.CopyTo(payload.AsSpan(RequestHeaderLength));
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static byte[] CreateResponse(CompanionLaunchResult result)
    {
        var response = new byte[ResponseLength];
        BinaryPrimitives.WriteUInt32LittleEndian(response, ProtocolMagic);
        response[4] = ProtocolVersion;
        response[5] = (byte)result;
        return response;
    }

    private static CompanionLaunchResult ParseResponse(ReadOnlySpan<byte> response)
    {
        if (response.Length != ResponseLength
            || BinaryPrimitives.ReadUInt32LittleEndian(response) != ProtocolMagic
            || response[4] != ProtocolVersion
            || response[6] != 0
            || response[7] != 0
            || !Enum.IsDefined((CompanionLaunchResult)response[5]))
        {
            throw new InvalidDataException("The main instance returned an invalid response.");
        }

        return (CompanionLaunchResult)response[5];
    }

    private static async Task DelayAfterFailureAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);

    public async ValueTask DisposeAsync()
    {
        Task? serverTask;
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetimeCancellation.Cancel();
            serverTask = _serverTask;
        }

        if (serverTask is not null)
        {
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
            }
        }

        _lifetimeCancellation.Dispose();
    }
}
