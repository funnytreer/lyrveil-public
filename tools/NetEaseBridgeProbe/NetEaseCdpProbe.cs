using System.Text.Json;
using LyricsXQuiet.Platform.Windows.Playback;

namespace LyricsXQuiet.Tools.NetEaseBridgeProbe;

internal static class NetEaseCdpProbe
{
    public static async Task<JsonElement> CaptureAsync(
        CdpConnection connection,
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
            var description = exceptionDetails.TryGetProperty("text", out var text)
                ? text.GetString()
                : exceptionDetails.GetRawText();
            throw new ProbeException($"网易云页面执行探测表达式失败：{description}");
        }

        if (!response.TryGetProperty("result", out var remoteObject))
        {
            throw new ProbeException("Runtime.evaluate 响应缺少远程对象。");
        }

        if (remoteObject.TryGetProperty("subtype", out var subtype) && subtype.GetString() == "error")
        {
            var description = remoteObject.TryGetProperty("description", out var descriptionElement)
                ? descriptionElement.GetString()
                : "未知页面错误";
            throw new ProbeException($"网易云页面返回错误：{description}");
        }

        if (!remoteObject.TryGetProperty("value", out var value))
        {
            throw new ProbeException("网易云页面没有返回可序列化的探测结果。");
        }

        return value.Clone();
    }
}
