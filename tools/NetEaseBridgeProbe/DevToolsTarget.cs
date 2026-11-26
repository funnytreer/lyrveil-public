using System.Text.Json.Serialization;

namespace LyricsXQuiet.Tools.NetEaseBridgeProbe;

internal sealed record DevToolsTarget(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("webSocketDebuggerUrl")] string? WebSocketDebuggerUrl);
