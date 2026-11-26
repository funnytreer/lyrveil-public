using System.Globalization;

namespace LyricsXQuiet.Tools.NetEaseBridgeProbe;

internal sealed record ProbeOptions(
    int Port,
    int? BridgePort,
    string? TargetId,
    bool ListTargets,
    bool DiagnoseClock,
    bool ValidateSeek,
    bool Watch,
    TimeSpan Timeout,
    bool ShowHelp)
{
    public static ProbeOptions Parse(string[] args)
    {
        var port = 0;
        int? bridgePort = null;
        string? targetId = null;
        var listTargets = false;
        var diagnoseClock = false;
        var validateSeek = false;
        var watch = false;
        var timeout = TimeSpan.FromSeconds(8);
        var showHelp = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "-h":
                case "--help":
                    showHelp = true;
                    break;
                case "-p":
                case "--port":
                    port = ParsePort(ReadValue(args, ref index, args[index]));
                    break;
                case "--target":
                    targetId = ReadValue(args, ref index, args[index]).Trim();
                    if (targetId.Length == 0)
                    {
                        throw new ArgumentException("--target 不能是空值。");
                    }

                    break;
                case "--bridge-port":
                    bridgePort = ParsePort(ReadValue(args, ref index, args[index]));
                    break;
                case "--list":
                    listTargets = true;
                    break;
                case "--diagnose-clock":
                    diagnoseClock = true;
                    break;
                case "--validate-seek":
                    validateSeek = true;
                    break;
                case "--watch":
                    watch = true;
                    break;
                case "--timeout":
                    timeout = ParseTimeout(ReadValue(args, ref index, args[index]));
                    break;
                default:
                    throw new ArgumentException($"未知参数：{args[index]}");
            }
        }

        if (!showHelp && port == 0)
        {
            throw new ArgumentException("必须用 --port 指定网易云音乐本机调试端口。");
        }

        if (!showHelp && watch && bridgePort is null)
        {
            throw new ArgumentException("使用 --watch 时必须用 --bridge-port 指定词间本机接收端口。");
        }

        if (!showHelp && !watch && bridgePort is not null)
        {
            throw new ArgumentException("--bridge-port 只能与 --watch 一起使用。");
        }

        if (!showHelp && watch && (listTargets || diagnoseClock || validateSeek))
        {
            throw new ArgumentException("--watch 不能与 --list、--diagnose-clock 或 --validate-seek 同时使用。");
        }

        if (!showHelp && new[] { listTargets, diagnoseClock, validateSeek }.Count(value => value) > 1)
        {
            throw new ArgumentException("--list、--diagnose-clock 和 --validate-seek 不能同时使用。");
        }

        if (!showHelp && watch && bridgePort == port)
        {
            throw new ArgumentException("词间接收端口不能与网易云 CDP 端口相同。");
        }

        return new ProbeOptions(
            port,
            bridgePort,
            targetId,
            listTargets,
            diagnoseClock,
            validateSeek,
            watch,
            timeout,
            showHelp);
    }

    public static void WriteHelp()
    {
        Console.WriteLine("网易云播放进度桥只读探针");
        Console.WriteLine();
        Console.WriteLine("用法：");
        Console.WriteLine("  NetEaseBridgeProbe --port <端口> [--target <页面ID>] [--timeout <秒>]");
        Console.WriteLine("  NetEaseBridgeProbe --port <端口> --list");
        Console.WriteLine("  NetEaseBridgeProbe --port <端口> --diagnose-clock");
        Console.WriteLine("  NetEaseBridgeProbe --port <端口> --validate-seek");
        Console.WriteLine("  NetEaseBridgeProbe --port <CDP端口> --watch --bridge-port <词间端口>");
        Console.WriteLine();
        Console.WriteLine("说明：");
        Console.WriteLine("  仅连接 127.0.0.1 上已有的 Chrome DevTools Protocol 端口。");
        Console.WriteLine("  默认模式不会启动、关闭或修改网易云音乐，也不会写入日志。");
        Console.WriteLine("  --validate-seek 仅在当前已暂停时临时播放并测试三次跳转，随后恢复原位置和暂停状态。");
        Console.WriteLine("  --watch 连续向词间的 localhost 接收端发送进度，断线后自动重连。");
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length)
        {
            throw new ArgumentException($"{option} 后缺少值。");
        }

        return args[index];
    }

    private static int ParsePort(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
        {
            throw new ArgumentException("端口必须是 1 到 65535 之间的整数。");
        }

        return port;
    }

    private static TimeSpan ParseTimeout(string value)
    {
        if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) ||
            seconds is < 1 or > 60)
        {
            throw new ArgumentException("超时秒数必须在 1 到 60 之间。");
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
