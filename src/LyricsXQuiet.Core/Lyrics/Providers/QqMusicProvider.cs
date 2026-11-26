using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LyricsXQuiet.Core.Playback;

namespace LyricsXQuiet.Core.Lyrics.Providers;

/// <summary>
/// Retrieves ordinary synchronized LRC lyrics from QQ Music's web endpoints.
/// Encrypted QRC payloads are intentionally ignored; the provider never attempts to
/// decrypt or execute data returned by the service.
/// </summary>
public sealed class QqMusicProvider : ILyricsProvider, IDisposable
{
    private const int MaximumSearchResults = 12;
    private static readonly Uri ModernSearchEndpoint = new("https://u.y.qq.com/cgi-bin/musicu.fcg");
    private static readonly Uri LegacySearchEndpoint = new("https://c.y.qq.com/soso/fcgi-bin/client_search_cp");
    private static readonly Uri LyricsEndpoint = new("https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg");

    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public QqMusicProvider(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };

        if (!_client.DefaultRequestHeaders.UserAgent.Any())
        {
            _client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Lyrveil/0.2 (Windows desktop lyrics; QQ Music web provider)");
        }
    }

    public string Name => "QQ 音乐";

    public async Task<LyricDocument?> FindAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default)
    {
        if (!track.IsUsable)
        {
            return null;
        }

        var songs = (await SearchSongsAsync(track, cancellationToken))
            .Where(song => LyricsMatchScorer.IsRelevantTitle(song.Title, track.Title))
            .OrderBy(song => Score(song, track))
            .Take(8)
            .ToArray();
        Exception? lastFailure = null;
        var completedRequests = 0;
        foreach (var song in songs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var document = await GetLyricsAsync(song, cancellationToken);
                completedRequests++;
                if (document is not null)
                {
                    return document;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException exception)
            {
                lastFailure = exception;
            }
            catch (TaskCanceledException exception)
            {
                lastFailure = exception;
            }
        }

        ThrowIfEveryLyricsRequestFailed(songs.Length, completedRequests, lastFailure);
        return null;
    }

    public Task<IReadOnlyList<LyricsCandidate>> SearchAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default) => SearchCoreAsync(track, MaximumSearchResults, cancellationToken);

    public Task<IReadOnlyList<LyricsCandidate>> SearchAutomaticAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default) => SearchCoreAsync(track, 3, cancellationToken);

    private async Task<IReadOnlyList<LyricsCandidate>> SearchCoreAsync(
        TrackIdentity track, int resultLimit, CancellationToken cancellationToken)
    {
        if (!track.IsUsable)
        {
            return Array.Empty<LyricsCandidate>();
        }

        var songs = (await SearchSongsAsync(track, cancellationToken))
            .Where(song => LyricsMatchScorer.IsRelevantTitle(song.Title, track.Title))
            .OrderBy(song => Score(song, track))
            .Take(resultLimit)
            .ToArray();
        var candidates = new List<LyricsCandidate>(songs.Length);
        Exception? lastFailure = null;
        var completedRequests = 0;

        foreach (var song in songs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var document = await GetLyricsAsync(song, cancellationToken);
                completedRequests++;
                if (document is null)
                {
                    continue;
                }

                candidates.Add(new LyricsCandidate(
                    $"qqmusic:{song.Mid}",
                    Name,
                    song.Title,
                    song.Artist,
                    song.Album,
                    song.Duration,
                    Score(song, track),
                    document));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException exception)
            {
                lastFailure = exception;
            }
            catch (TaskCanceledException exception)
            {
                lastFailure = exception;
            }
        }

        ThrowIfEveryLyricsRequestFailed(songs.Length, completedRequests, lastFailure);
        return candidates
            .OrderBy(candidate => candidate.MatchScore)
            .ThenBy(candidate => candidate.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<QqMusicSong>> SearchSongsAsync(
        TrackIdentity track,
        CancellationToken cancellationToken)
    {
        var query = string.Join(' ', new[] { LyricsMatchScorer.GetSearchTitle(track.Title), track.Artist }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()));

        Exception? modernFailure = null;
        try
        {
            var modern = await SearchModernAsync(query, cancellationToken);
            if (modern.Count > 0)
            {
                return modern;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            modernFailure = exception;
        }

        try
        {
            return await SearchLegacyAsync(query, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException legacyFailure) when (modernFailure is not null)
        {
            throw new HttpRequestException(
                "QQ Music's search endpoints are temporarily unavailable.",
                new AggregateException(modernFailure, legacyFailure));
        }
        catch (TaskCanceledException legacyFailure) when (modernFailure is not null)
        {
            throw new HttpRequestException(
                "QQ Music's search endpoints timed out.",
                new AggregateException(modernFailure, legacyFailure));
        }
        catch (JsonException) when (modernFailure is JsonException)
        {
            return Array.Empty<QqMusicSong>();
        }
    }

    private async Task<IReadOnlyList<QqMusicSong>> SearchModernAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            comm = new
            {
                ct = 24,
                cv = 0,
                format = "json",
                inCharset = "utf-8",
                outCharset = "utf-8"
            },
            req_1 = new
            {
                method = "DoSearchForQQMusicDesktop",
                module = "music.search.SearchCgiService",
                param = new
                {
                    remoteplace = "txt.yqq.song",
                    search_type = 0,
                    query,
                    page_num = 1,
                    num_per_page = MaximumSearchResults
                }
            }
        });

        using var request = CreateRequest(HttpMethod.Post, ModernSearchEndpoint);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await ReadJsonAsync(response, cancellationToken);

        if (TryNavigate(document.RootElement, out var list, "req_1", "data", "body", "song", "list")
            || TryNavigate(
                document.RootElement,
                out list,
                "music.search.SearchCgiService",
                "data",
                "body",
                "song",
                "list"))
        {
            return ParseSongList(list);
        }

        return Array.Empty<QqMusicSong>();
    }

    private async Task<IReadOnlyList<QqMusicSong>> SearchLegacyAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>
        {
            ["format"] = "json",
            ["n"] = MaximumSearchResults.ToString(CultureInfo.InvariantCulture),
            ["p"] = "1",
            ["w"] = query,
            ["cr"] = "1",
            ["g_tk"] = "5381",
            ["inCharset"] = "utf8",
            ["outCharset"] = "utf-8",
            ["platform"] = "yqq.json",
            ["needNewCode"] = "0"
        };
        var uri = new UriBuilder(LegacySearchEndpoint)
        {
            Query = BuildQuery(parameters)
        }.Uri;

        using var request = CreateRequest(HttpMethod.Get, uri);
        using var response = await SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await ReadJsonAsync(response, cancellationToken);
        return TryNavigate(document.RootElement, out var list, "data", "song", "list")
            ? ParseSongList(list)
            : Array.Empty<QqMusicSong>();
    }

    private async Task<LyricDocument?> GetLyricsAsync(
        QqMusicSong song,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>
        {
            ["format"] = "json",
            ["nobase64"] = "1",
            ["g_tk"] = "5381",
            ["songmid"] = song.Mid,
            ["loginUin"] = "0",
            ["hostUin"] = "0",
            ["inCharset"] = "utf8",
            ["outCharset"] = "utf-8",
            ["notice"] = "0",
            ["platform"] = "yqq.json",
            ["needNewCode"] = "0"
        };
        var uri = new UriBuilder(LyricsEndpoint)
        {
            Query = BuildQuery(parameters)
        }.Uri;

        using var request = CreateRequest(HttpMethod.Get, uri, song.Mid);
        using var response = await SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await ReadJsonAsync(response, cancellationToken);
        var root = document.RootElement;
        if (ReadInteger(root, "retcode", "code") is not 0)
        {
            return null;
        }

        var lyric = DecodeText(ReadString(root, "lyric"));
        if (string.IsNullOrWhiteSpace(lyric))
        {
            return null;
        }

        var translation = DecodeText(ReadString(root, "trans", "trans_lrc"));
        var combined = string.IsNullOrWhiteSpace(translation)
            ? lyric
            : $"{lyric.TrimEnd()}\n{translation.Trim()}";
        var parsed = LrcParser.Parse(combined, Name);
        return parsed.HasSynchronizedLines || parsed.IsInstrumental ? parsed : null;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, string? songMid = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Referrer = songMid is null
            ? new Uri("https://y.qq.com/")
            : new Uri($"https://y.qq.com/n/ryqq/songDetail/{Uri.EscapeDataString(songMid)}");
        request.Headers.TryAddWithoutValidation("Origin", "https://y.qq.com");
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            var sinceLastRequest = DateTimeOffset.UtcNow - _lastRequestAt;
            var minimumInterval = TimeSpan.FromMilliseconds(120);
            if (sinceLastRequest < minimumInterval)
            {
                await Task.Delay(minimumInterval - sinceLastRequest, cancellationToken);
            }

            var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            _lastRequestAt = DateTimeOffset.UtcNow;
            return response;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var json = ExtractJson(content);
        return JsonDocument.Parse(json);
    }

    private static string ExtractJson(string content)
    {
        var trimmed = content.Trim().TrimStart('\uFEFF');
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            return trimmed;
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start
            ? trimmed[start..(end + 1)]
            : trimmed;
    }

    private static IReadOnlyList<QqMusicSong> ParseSongList(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<QqMusicSong>();
        }

        var songs = new List<QqMusicSong>();
        foreach (var item in list.EnumerateArray())
        {
            AddSong(item, songs);
            if (item.TryGetProperty("grp", out var group) && group.ValueKind == JsonValueKind.Array)
            {
                foreach (var alternate in group.EnumerateArray())
                {
                    AddSong(alternate, songs);
                }
            }
        }

        return songs
            .Where(song => !string.IsNullOrWhiteSpace(song.Mid))
            .GroupBy(song => song.Mid, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(MaximumSearchResults * 2)
            .ToArray();
    }

    private static void AddSong(JsonElement item, ICollection<QqMusicSong> songs)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var mid = ReadString(item, "mid", "songmid");
        var title = ReadString(item, "name", "title", "songname");
        if (string.IsNullOrWhiteSpace(mid) || string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        var artist = ReadArtists(item);
        var album = ReadAlbum(item);
        var seconds = ReadDouble(item, "interval", "duration");
        songs.Add(new QqMusicSong(
            mid,
            WebUtility.HtmlDecode(title).Trim(),
            WebUtility.HtmlDecode(artist).Trim(),
            WebUtility.HtmlDecode(album).Trim(),
            seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero));
    }

    private static string ReadArtists(JsonElement item)
    {
        if (item.TryGetProperty("singer", out var singers) && singers.ValueKind == JsonValueKind.Array)
        {
            return string.Join('/', singers.EnumerateArray()
                .Select(singer => ReadString(singer, "name", "title"))
                .Where(name => !string.IsNullOrWhiteSpace(name)));
        }

        return ReadString(item, "singername", "fsinger");
    }

    private static string ReadAlbum(JsonElement item)
    {
        if (item.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object)
        {
            return ReadString(album, "name", "title");
        }

        return ReadString(item, "albumname");
    }

    private static bool TryNavigate(JsonElement root, out JsonElement result, params string[] path)
    {
        result = root;
        foreach (var segment in path)
        {
            if (result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty(segment, out result))
            {
                result = default;
                return false;
            }
        }

        return true;
    }

    private static string ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                return property.GetString() ?? string.Empty;
            }

            if (property.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                return property.ToString();
            }
        }

        return string.Empty;
    }

    private static int ReadInteger(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var property))
            {
                continue;
            }

            if (property.TryGetInt32(out var number))
            {
                return number;
            }

            if (int.TryParse(property.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                return number;
            }
        }

        return 0;
    }

    private static double ReadDouble(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var property))
            {
                continue;
            }

            if (property.TryGetDouble(out var number))
            {
                return number;
            }

            if (double.TryParse(property.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                return number;
            }
        }

        return 0;
    }

    private static string DecodeText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decoded = WebUtility.HtmlDecode(value).TrimStart('\uFEFF');
        if (decoded.Contains('[') || decoded.Contains('\n') || decoded.Contains('\r'))
        {
            return decoded;
        }

        try
        {
            var bytes = Convert.FromBase64String(decoded);
            var base64Decoded = WebUtility.HtmlDecode(Encoding.UTF8.GetString(bytes)).TrimStart('\uFEFF');
            return base64Decoded.Contains('[') || base64Decoded.Contains('\n')
                ? base64Decoded
                : string.Empty;
        }
        catch (FormatException)
        {
            // Encrypted QRC is not ordinary base64 LRC and is deliberately unsupported.
            return string.Empty;
        }
    }

    private static double Score(QqMusicSong song, TrackIdentity track)
        => LyricsMatchScorer.ScoreMetadata(
            song.Title,
            song.Artist,
            song.Album,
            song.Duration,
            track);

    private static string BuildQuery(IReadOnlyDictionary<string, string> values) =>
        string.Join('&', values.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    private static void ThrowIfEveryLyricsRequestFailed(
        int attempted,
        int completed,
        Exception? lastFailure)
    {
        if (attempted == 0 || completed > 0 || lastFailure is null)
        {
            return;
        }

        if (lastFailure is TaskCanceledException taskCanceled)
        {
            throw taskCanceled;
        }

        throw new HttpRequestException(
            "QQ Music's lyric endpoint is temporarily unavailable.",
            lastFailure);
    }

    public void Dispose()
    {
        _requestGate.Dispose();
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private sealed record QqMusicSong(
        string Mid,
        string Title,
        string Artist,
        string Album,
        TimeSpan Duration);
}
