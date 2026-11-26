using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LyricsXQuiet.Core.Playback;

namespace LyricsXQuiet.Core.Lyrics.Providers;

public sealed class NetEaseProvider : IExactSourceLyricsProvider, IDisposable
{
    private const string SearchEndpoint = "https://music.163.com/api/search/pc";
    private const string LyricsEndpoint = "https://interface3.music.163.com/eapi/song/lyric/v1";
    private const string LyricsApiPath = "/api/song/lyric/v1";
    private const string EapiKey = "e82ckenh8dichen8";
    private const int ResultLimit = 8;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);
    private static readonly Uri Referrer = new("https://music.163.com/");
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
        + "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
    private const string MobileUserAgent =
        "Mozilla/5.0 (Linux; Android 9; PCT-AL10) AppleWebKit/537.36 "
        + "(KHTML, like Gecko) Chrome/70.0.3538.64 HuaweiBrowser/10.0.3.311 Mobile Safari/537.36";

    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public NetEaseProvider(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient { Timeout = RequestTimeout };
        if (!_client.DefaultRequestHeaders.UserAgent.Any())
        {
            _client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Lyrveil/0.2 (Windows desktop lyrics; NetEase provider)");
        }
    }

    public string Name => "网易云音乐";

    public bool CanResolveExact(TrackIdentity track) =>
        SourceTrackIdParser.TryGetNetEaseSongId(track.SourceTrackId, out _);

    public Task<LyricDocument?> FindExactAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default) =>
        TryFetchExactDocumentAsync(track, cancellationToken);

    public async Task<LyricDocument?> FindAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default)
    {
        if (!track.IsUsable)
        {
            return null;
        }

        var exactDocument = await TryFetchExactDocumentAsync(track, cancellationToken);
        if (exactDocument is { HasSynchronizedLines: true } or { IsInstrumental: true })
        {
            return exactDocument;
        }

        var songs = await SearchSongsAsync(track, cancellationToken);
        foreach (var song in songs
                     .Where(song => LyricsMatchScorer.IsRelevantTitle(song.DisplayTitle, track.Title))
                     .OrderBy(song => Score(song, track))
                     .Take(ResultLimit))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var document = await FetchDocumentAsync(song.Id, cancellationToken);
                if (document is { HasSynchronizedLines: true })
                {
                    return document;
                }

                // `nolyric` is also returned for some incomplete uploads. Only
                // treat it as a real instrumental marker when the search result
                // itself is a close match for the playing track.
                if (document?.IsInstrumental == true && IsConfidentMatch(song, track))
                {
                    return document;
                }
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A slow candidate is a miss; try the next search result.
            }
            catch (HttpRequestException)
            {
                // One failed lyric request must not discard the remaining candidates.
            }
            catch (JsonException)
            {
                // Ignore a malformed response and continue with the next candidate.
            }
        }

        return null;
    }

    public Task<IReadOnlyList<LyricsCandidate>> SearchAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default) => SearchCoreAsync(track, ResultLimit, cancellationToken);

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

        var exactCandidate = await TryCreateExactCandidateAsync(track, cancellationToken);
        if (exactCandidate is not null)
        {
            return [exactCandidate];
        }

        var songs = (await SearchSongsAsync(track, cancellationToken))
            .Where(song => LyricsMatchScorer.IsRelevantTitle(song.DisplayTitle, track.Title))
            .OrderBy(song => Score(song, track))
            .Take(resultLimit)
            .ToArray();
        var candidates = await LyricsProviderRequests.CollectAsync(
            songs.Select(song => (Func<Task<IReadOnlyList<LyricsCandidate>>>)(async () =>
            {
                var candidate = await CreateCandidateAsync(song, track, cancellationToken);
                return candidate is null ? [] : [candidate];
            })), cancellationToken);
        return candidates
            .Where(candidate => candidate is not null)
            .Cast<LyricsCandidate>()
            .OrderBy(candidate => candidate.MatchScore)
            .ToArray();
    }

    private async Task<LyricDocument?> TryFetchExactDocumentAsync(
        TrackIdentity track,
        CancellationToken cancellationToken)
    {
        if (!SourceTrackIdParser.TryGetNetEaseSongId(track.SourceTrackId, out var songId))
        {
            return null;
        }

        try
        {
            return await FetchDocumentAsync(songId, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<LyricsCandidate?> TryCreateExactCandidateAsync(
        TrackIdentity track,
        CancellationToken cancellationToken)
    {
        if (!SourceTrackIdParser.TryGetNetEaseSongId(track.SourceTrackId, out var songId))
        {
            return null;
        }

        var document = await TryFetchExactDocumentAsync(track, cancellationToken);
        if (document is not { HasSynchronizedLines: true } and not { IsInstrumental: true })
        {
            return null;
        }

        return new LyricsCandidate(
            SourceTrackIdParser.FormatNetEaseSongId(songId),
            Name,
            track.Title,
            track.Artist,
            track.Album,
            track.Duration,
            0,
            document);
    }

    private async Task<LyricsCandidate?> CreateCandidateAsync(
        NetEaseSong song,
        TrackIdentity track,
        CancellationToken cancellationToken)
    {
        var document = await FetchDocumentAsync(song.Id, cancellationToken);
        if (document is not { HasSynchronizedLines: true } || document.IsInstrumental)
        {
            return null;
        }

        var qualityBonus = document.Lines.Any(line => line.Words.Count > 0) ? 4d : 0d;
        qualityBonus += document.Lines.Any(line => !string.IsNullOrWhiteSpace(line.Translation)) ? 2d : 0d;
        return new LyricsCandidate(
            $"netease:{song.Id}",
            Name,
            song.DisplayTitle,
            song.Artist,
            song.Album,
            song.Duration,
            Math.Max(0, Score(song, track) - qualityBonus),
            document);
    }

    private async Task<IReadOnlyList<NetEaseSong>> SearchSongsAsync(
        TrackIdentity track,
        CancellationToken cancellationToken)
    {
        var searchTitle = LyricsMatchScorer.GetSearchTitle(track.Title);
        var searchTerm = string.IsNullOrWhiteSpace(track.Artist)
            ? searchTitle
            : $"{searchTitle} {track.Artist}";
        var query = BuildQuery(new Dictionary<string, string>
        {
            ["s"] = searchTerm,
            ["offset"] = "0",
            ["limit"] = "12",
            ["type"] = "1"
        });

        using var firstRequest = CreateSearchRequest($"{SearchEndpoint}?{query}");
        using var firstResponse = await _client.SendAsync(
            firstRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        firstResponse.EnsureSuccessStatusCode();

        var firstJson = await firstResponse.Content.ReadAsStringAsync(cancellationToken);
        var cookie = ReadResponseCookie(firstResponse);
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return ParseSearchResults(firstJson);
        }

        using var secondRequest = CreateSearchRequest($"{SearchEndpoint}?{query}");
        secondRequest.Headers.TryAddWithoutValidation("Cookie", cookie);
        using var secondResponse = await _client.SendAsync(
            secondRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!secondResponse.IsSuccessStatusCode)
        {
            return ParseSearchResults(firstJson);
        }

        var secondJson = await secondResponse.Content.ReadAsStringAsync(cancellationToken);
        var secondResults = ParseSearchResults(secondJson);
        return secondResults.Count > 0 ? secondResults : ParseSearchResults(firstJson);
    }

    private static HttpRequestMessage CreateSearchRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(Array.Empty<byte>())
        };
        request.Headers.Referrer = Referrer;
        request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
        return request;
    }

    private async Task<LyricDocument?> FetchDocumentAsync(
        long songId,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        NetEaseLyricsPayload? payload = null;
        try
        {
            payload = await FetchEapiLyricsAsync(songId, timeout.Token);
        }
        catch (HttpRequestException)
        {
            // The unencrypted endpoint below is less capable, but remains a useful fallback.
        }
        catch (JsonException)
        {
        }

        payload ??= await FetchPublicLyricsAsync(songId, timeout.Token);
        if (payload is null || payload.Uncollected)
        {
            return null;
        }

        if (payload.NoLyrics)
        {
            return new LyricDocument(Array.Empty<LyricLine>(), Name, string.Empty, true);
        }

        return NetEaseLyricsParser.Parse(
            payload.Lrc,
            payload.KLyric,
            payload.Yrc,
            payload.TranslatedLrc,
            Name);
    }

    private async Task<NetEaseLyricsPayload?> FetchEapiLyricsAsync(
        long songId,
        CancellationToken cancellationToken)
    {
        var header = CreateEapiHeader();
        var headerJson = JsonSerializer.Serialize(header);
        var payload = new Dictionary<string, string>
        {
            ["id"] = songId.ToString(CultureInfo.InvariantCulture),
            ["cp"] = "false",
            ["lv"] = "0",
            ["kv"] = "0",
            ["tv"] = "0",
            ["rv"] = "0",
            ["yv"] = "0",
            ["ytv"] = "0",
            ["yrv"] = "0",
            ["csrf_token"] = string.Empty,
            ["header"] = headerJson
        };
        var payloadJson = JsonSerializer.Serialize(payload);
        var encryptedParams = EncryptEapiPayload(LyricsApiPath, payloadJson);

        using var request = new HttpRequestMessage(HttpMethod.Post, LyricsEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["params"] = encryptedParams
            })
        };
        request.Headers.Referrer = Referrer;
        request.Headers.UserAgent.ParseAdd(MobileUserAgent);
        request.Headers.TryAddWithoutValidation(
            "Cookie",
            string.Join("; ", header.Select(pair => $"{pair.Key}={pair.Value}")));

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseLyricsPayload(json);
    }

    private async Task<NetEaseLyricsPayload?> FetchPublicLyricsAsync(
        long songId,
        CancellationToken cancellationToken)
    {
        var query = BuildQuery(new Dictionary<string, string>
        {
            ["os"] = "pc",
            ["id"] = songId.ToString(CultureInfo.InvariantCulture),
            ["lv"] = "-1",
            ["kv"] = "-1",
            ["tv"] = "-1",
            ["rv"] = "-1",
            ["yv"] = "-1",
            ["ytv"] = "-1",
            ["yrv"] = "-1"
        });
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://music.163.com/api/song/lyric?{query}");
        request.Headers.Referrer = Referrer;
        request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseLyricsPayload(json);
    }

    private static IReadOnlyList<NetEaseSong> ParseSearchResults(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<NetEaseSong>();
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("songs", out var songs)
            || songs.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<NetEaseSong>();
        }

        var parsed = new List<NetEaseSong>();
        foreach (var song in songs.EnumerateArray())
        {
            var id = GetInt64(song, "id");
            var title = GetString(song, "name");
            if (id <= 0 || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var durationMilliseconds = GetInt64(song, "duration", "dt");
            var artists = GetNamedArray(song, "artists", "ar");
            var album = GetNamedObject(song, "album", "al");
            var aliases = GetStringArray(song, "alias", "alia", "tns");
            parsed.Add(new NetEaseSong(
                id,
                title,
                aliases,
                string.Join(" / ", artists),
                album,
                TimeSpan.FromMilliseconds(Math.Max(0, durationMilliseconds))));
        }

        return parsed;
    }

    private static NetEaseLyricsPayload? ParseLyricsPayload(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var code = GetInt64(root, "code");
        if (code != 0 && code != 200)
        {
            return null;
        }

        return new NetEaseLyricsPayload(
            GetNestedString(root, "lrc", "lyric"),
            GetNestedString(root, "klyric", "lyric"),
            GetNestedString(root, "yrc", "lyric"),
            GetNestedString(root, "tlyric", "lyric")
                ?? GetNestedString(root, "ytlrc", "lyric"),
            GetBoolean(root, "nolyric", "pureMusic"),
            GetBoolean(root, "uncollected"));
    }

    private static Dictionary<string, string> CreateEapiHeader()
    {
        var now = DateTimeOffset.UtcNow;
        return new Dictionary<string, string>
        {
            ["__csrf"] = string.Empty,
            ["appver"] = "8.0.0",
            ["buildver"] = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["channel"] = string.Empty,
            ["deviceId"] = string.Empty,
            ["mobilename"] = string.Empty,
            ["resolution"] = "1920x1080",
            ["os"] = "android",
            ["osver"] = string.Empty,
            ["requestId"] = FormattableString.Invariant(
                $"{now.ToUnixTimeMilliseconds()}_{RandomNumberGenerator.GetInt32(0, 1000):0000}"),
            ["versioncode"] = "140",
            ["MUSIC_U"] = string.Empty
        };
    }

    private static string EncryptEapiPayload(string path, string payloadJson)
    {
        var digestInput = $"nobody{path}use{payloadJson}md5forencrypt";
        var digest = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(digestInput)))
            .ToLowerInvariant();
        var plainText = $"{path}-36cd479b6b5-{payloadJson}-36cd479b6b5-{digest}";

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = Encoding.UTF8.GetBytes(EapiKey);
        using var encryptor = aes.CreateEncryptor();
        var input = Encoding.UTF8.GetBytes(plainText);
        var encrypted = encryptor.TransformFinalBlock(input, 0, input.Length);
        return Convert.ToHexString(encrypted);
    }

    private static string ReadResponseCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers))
        {
            return string.Empty;
        }

        return string.Join("; ", headers
            .Select(header => header.Split(';', 2)[0].Trim())
            .Where(cookie => !string.IsNullOrWhiteSpace(cookie)));
    }

    private static double Score(NetEaseSong song, TrackIdentity track)
    {
        var score = LyricsMatchScorer.ScoreMetadata(
            song.DisplayTitle,
            song.Artist,
            song.Album,
            song.Duration,
            track);
        foreach (var alias in song.Aliases)
        {
            score = Math.Min(
                score,
                LyricsMatchScorer.ScoreMetadata(
                    $"{song.Title} ({alias})",
                    song.Artist,
                    song.Album,
                    song.Duration,
                    track) + 4);
        }

        return score;
    }

    private static bool IsConfidentMatch(NetEaseSong song, TrackIdentity track)
    {
        var titleMatches = TextPenalty(song.Title, track.Title) <= 8;
        var artistMatches = string.IsNullOrWhiteSpace(track.Artist)
            || TextPenalty(song.Artist, track.Artist) <= 8;
        var durationMatches = track.Duration <= TimeSpan.Zero
            || song.Duration <= TimeSpan.Zero
            || Math.Abs((song.Duration - track.Duration).TotalSeconds) <= 8;
        return titleMatches && artistMatches && durationMatches;
    }

    private static double TextPenalty(string actual, string expected)
    {
        var normalizedActual = Normalize(actual);
        var normalizedExpected = Normalize(expected);
        if (normalizedExpected.Length == 0 || normalizedActual == normalizedExpected)
        {
            return 0;
        }

        if (normalizedActual.Contains(normalizedExpected, StringComparison.Ordinal)
            || normalizedExpected.Contains(normalizedActual, StringComparison.Ordinal))
        {
            return 8;
        }

        var distance = LevenshteinDistance(normalizedActual, normalizedExpected);
        return 15d + (35d * distance / Math.Max(normalizedActual.Length, normalizedExpected.Length));
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static int LevenshteinDistance(string left, string right)
    {
        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitution = left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1;
                current[rightIndex] = Math.Min(
                    Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1),
                    previous[rightIndex - 1] + substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static string BuildQuery(IReadOnlyDictionary<string, string> values) =>
        string.Join('&', values.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    private static string GetString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String)
            {
                return property.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static long GetInt64(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
            {
                return number;
            }

            if (property.ValueKind == JsonValueKind.String
                && long.TryParse(
                    property.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out number))
            {
                return number;
            }
        }

        return 0;
    }

    private static IReadOnlyList<string> GetNamedArray(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property)
                || property.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            return property.EnumerateArray()
                .Select(item => GetString(item, "name"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
        }

        return Array.Empty<string>();
    }

    private static string GetNamedObject(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.Object)
            {
                return GetString(property, "name");
            }
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property)
                || property.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            return property.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
        }

        return Array.Empty<string>();
    }

    private static string? GetNestedString(JsonElement element, string objectName, string propertyName)
    {
        if (!element.TryGetProperty(objectName, out var nested)
            || nested.ValueKind != JsonValueKind.Object
            || !nested.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static bool GetBoolean(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out var property)
                && property.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return property.GetBoolean();
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private sealed record NetEaseSong(
        long Id,
        string Title,
        IReadOnlyList<string> Aliases,
        string Artist,
        string Album,
        TimeSpan Duration)
    {
        public string DisplayTitle => Aliases.Count == 0
            ? Title
            : $"{Title}（{Aliases[0]}）";
    }

    private sealed record NetEaseLyricsPayload(
        string? Lrc,
        string? KLyric,
        string? Yrc,
        string? TranslatedLrc,
        bool NoLyrics,
        bool Uncollected);
}
