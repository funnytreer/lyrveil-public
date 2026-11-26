using System.Net.WebSockets;
using System.Text.Encodings.Web;
using System.Text.Json;
using LyricsXQuiet.Tools.NetEaseBridgeProbe;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

ProbeOptions options;
try
{
    options = ProbeOptions.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine();
    ProbeOptions.WriteHelp();
    return 64;
}

if (options.ShowHelp)
{
    ProbeOptions.WriteHelp();
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

try
{
    if (options.Watch)
    {
        var producer = new BridgeProducer(
            options.Port,
            options.BridgePort!.Value,
            options.TargetId,
            options.Timeout);
        await producer.RunAsync(cancellation.Token);
        return 0;
    }

    var targets = await DevToolsDiscovery.ListTargetsAsync(options.Port, options.Timeout, cancellation.Token);
    if (options.ListTargets)
    {
        var displayTargets = targets.Select(target => new
        {
            target.Id,
            target.Type,
            target.Title,
            target.Url,
            Connectable = !string.IsNullOrWhiteSpace(target.WebSocketDebuggerUrl)
        });
        Console.WriteLine(JsonSerializer.Serialize(displayTargets, jsonOptions));
        return 0;
    }

    var target = DevToolsDiscovery.SelectTarget(targets, options.TargetId);
    var webSocketUri = DevToolsDiscovery.ValidateLoopbackWebSocket(target, options.Port);
    await using var connection = await CdpConnection.ConnectAsync(webSocketUri, options.Timeout, cancellation.Token);
    if (options.DiagnoseClock)
    {
        var diagnostics = await NetEaseClockDiagnostics.CaptureAsync(
            connection,
            options.Timeout,
            cancellation.Token);
        Console.WriteLine(JsonSerializer.Serialize(diagnostics, jsonOptions));
        return 0;
    }

    if (options.ValidateSeek)
    {
        var diagnostics = await NetEaseSeekValidation.CaptureAsync(
            connection,
            options.Timeout,
            cancellation.Token);
        Console.WriteLine(JsonSerializer.Serialize(diagnostics, jsonOptions));
        return diagnostics.TryGetProperty("ok", out var validationOk)
            && validationOk.ValueKind == JsonValueKind.True
            ? 0
            : 3;
    }

    var snapshot = await NetEaseCdpProbe.CaptureAsync(connection, options.Timeout, cancellation.Token);
    Console.WriteLine(JsonSerializer.Serialize(snapshot, jsonOptions));
    return snapshot.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True ? 0 : 3;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("探测已取消。");
    return 130;
}
catch (ProbeException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}
catch (HttpRequestException exception)
{
    Console.Error.WriteLine($"无法连接 127.0.0.1:{options.Port} 的调试端口：{exception.Message}");
    return 2;
}
catch (TaskCanceledException exception) when (!cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine($"连接 127.0.0.1:{options.Port} 的调试端口超时：{exception.Message}");
    return 2;
}
catch (WebSocketException exception)
{
    Console.Error.WriteLine($"无法连接本机调试页面：{exception.Message}");
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"探测失败：{exception.Message}");
    return 1;
}
