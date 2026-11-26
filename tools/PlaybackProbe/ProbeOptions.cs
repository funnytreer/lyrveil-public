namespace LyricsXQuiet.Tools.PlaybackProbe;

internal sealed record ProbeOptions(bool Once, bool JsonOnly, string? OutputPath, bool ShowHelp)
{
    public static ProbeOptions Parse(string[] args)
    {
        var once = false;
        var jsonOnly = false;
        var showHelp = false;
        string? outputPath = null;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--once":
                    once = true;
                    break;
                case "--json":
                    jsonOnly = true;
                    break;
                case "--output":
                    if (index + 1 >= args.Length)
                    {
                        throw new ArgumentException("--output 后面需要一个文件路径。");
                    }

                    outputPath = args[++index];
                    break;
                case "--help":
                case "-h":
                case "/?":
                    showHelp = true;
                    break;
                default:
                    throw new ArgumentException($"未知参数：{args[index]}");
            }
        }

        return new ProbeOptions(once, jsonOnly, outputPath, showHelp);
    }

    public static void WriteHelp()
    {
        Console.WriteLine(
            """
            PlaybackProbe — Windows GSMTC 播放器兼容性探针

            用法：
              PlaybackProbe                 持续监听并显示当前播放器时间线
              PlaybackProbe --once          输出当前所有媒体会话后退出
              PlaybackProbe --json          只向终端输出 JSON Lines
              PlaybackProbe --output PATH   同时把完整事件写入 JSONL 文件

            选项：
              --once       单次快照模式
              --json       终端输出机器可读 JSON Lines
              --output     保存完整事件日志，便于填写兼容性矩阵
              --help, -h   显示帮助
            """);
    }
}
