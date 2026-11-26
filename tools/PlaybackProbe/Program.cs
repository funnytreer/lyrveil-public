using LyricsXQuiet.Tools.PlaybackProbe;

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

await using var output = await ProbeOutput.CreateAsync(options.OutputPath);

try
{
    await using var probe = await GsmTcProbe.CreateAsync(cancellation.Token);
    if (!options.Once)
    {
        probe.RecordProduced += (_, record) => output.Write(record, options.JsonOnly);
    }

    await probe.StartAsync(cancellation.Token);

    if (options.Once)
    {
        foreach (var snapshot in probe.GetSnapshots())
        {
            output.Write(ProbeRecord.FromSnapshot("once", snapshot), options.JsonOnly);
        }

        if (probe.GetSnapshots().Count == 0)
        {
            output.Write(ProbeRecord.Status("没有发现 GSMTC 媒体会话。请先在音乐软件中开始播放。"), options.JsonOnly);
        }

        return 0;
    }

    output.Write(ProbeRecord.Status("正在监听。按 Ctrl+C 退出。"), options.JsonOnly);

    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
    while (await timer.WaitForNextTickAsync(cancellation.Token))
    {
        var current = probe.GetCurrentSnapshot();
        if (current is not null)
        {
            output.WriteClock(current, options.JsonOnly);
        }
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    output.Write(ProbeRecord.Status("监听已停止。"), options.JsonOnly);
}
catch (UnauthorizedAccessException exception)
{
    output.Write(ProbeRecord.Error(
        "Windows 拒绝访问系统媒体会话。请确认当前用户会话可访问媒体控件。",
        exception), options.JsonOnly);
    return 2;
}
catch (Exception exception)
{
    output.Write(ProbeRecord.Error("PlaybackProbe 启动失败。", exception), options.JsonOnly);
    return 1;
}

return 0;
