using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LyricsXQuiet.Tools.NetEaseBridgeProbe;

internal static class DevToolsDiscovery
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<IReadOnlyList<DevToolsTarget>> ListTargetsAsync(
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
            Timeout = timeout
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.GetAsync("json/list", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new ProbeException($"本机调试端口返回了 HTTP {(int)response.StatusCode}，无法读取页面列表。");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && !mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProbeException($"本机调试端口返回了意外的内容类型：{mediaType}。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var targets = await JsonSerializer.DeserializeAsync<List<DevToolsTarget>>(stream, JsonOptions, cancellationToken);
        return targets ?? [];
    }

    public static DevToolsTarget SelectTarget(IReadOnlyList<DevToolsTarget> targets, string? requestedId)
    {
        var pages = targets
            .Where(target => target.Type.Equals("page", StringComparison.OrdinalIgnoreCase) &&
                             !string.IsNullOrWhiteSpace(target.WebSocketDebuggerUrl))
            .ToArray();

        if (requestedId is not null)
        {
            return pages.FirstOrDefault(target => target.Id.Equals(requestedId, StringComparison.Ordinal))
                ?? throw new ProbeException($"没有找到页面 ID“{requestedId}”。可先使用 --list 查看页面。");
        }

        var ranked = pages
            .Select(target => new { Target = target, Score = Score(target) })
            .OrderByDescending(item => item.Score)
            .ToArray();

        if (ranked.Length == 0)
        {
            throw new ProbeException("调试端口中没有可连接的 page 页面。");
        }

        if (ranked[0].Score == 0)
        {
            throw new ProbeException("没有识别到网易云音乐页面。请使用 --list 查看页面，再用 --target 指定。");
        }

        return ranked[0].Target;
    }

    public static Uri ValidateLoopbackWebSocket(DevToolsTarget target, int expectedPort)
    {
        if (!Uri.TryCreate(target.WebSocketDebuggerUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeWs ||
            !uri.IsLoopback ||
            uri.Port != expectedPort)
        {
            throw new ProbeException("页面给出的 WebSocket 地址不是当前 localhost 调试端口，已拒绝连接。");
        }

        return uri;
    }

    private static int Score(DevToolsTarget target)
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
