using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LyricsXQuiet.Core.Playback;

namespace LyricsXQuiet.Core.Lyrics.Providers;

/// <summary>
/// Searches Kugou's public-facing client endpoints and converts KRC word timing
/// into Lyrveil's provider-neutral lyric model.
/// </summary>
public sealed partial class KugouProvider : ILyricsProvider, IDisposable
{
    private const int MaximumSearchSongs = 8;
    private const int MaximumCandidates = 16;
    private const int MaximumDecompressedBytes = 4 * 1024 * 1024;

    private static readonly byte[] KrcMagic = Encoding.ASCII.GetBytes("krc1");
    private static readonly byte[] KrcXorKey =
    [
        0x40, 0x47, 0x61, 0x77, 0x5e, 0x32, 0x74, 0x47,
        0x51, 0x36, 0x31, 0x2d, 0xce, 0xd2, 0x6e, 0x69
    ];

    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public KugouProvider(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };

        if (!_client.DefaultRequestHeaders.UserAgent.Any())
        {
            _client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Lyrveil/0.2 (Windows desktop lyrics; local development build)");
        }
    }

    public string Name => "酷狗音乐";

    public async Task<LyricDocument?> FindAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default)
    {
        if (!track.IsUsable)
        {
            return null;
        }

        return (await SearchAsync(track, cancellationToken)).FirstOrDefault()?.Document;
    }

    public Task<IReadOnlyList<LyricsCandidate>> SearchAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default) => SearchCoreAsync(track, MaximumSearchSongs, cancellationToken);

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

        var songs = await SearchSongsAsync(track, cancellationToken);
        if (songs.Count == 0)
        {
            return Array.Empty<LyricsCandidate>();
        }

        var rankedSongs = songs
            .Where(song => !string.IsNullOrWhiteSpace(song.EffectiveHash)
                && LyricsMatchScorer.IsRelevantTitle(song.EffectiveTitle, track.Title))
            .OrderBy(song => Score(
                song.EffectiveTitle,
                song.EffectiveArtist,
                song.EffectiveAlbum,
                song.DurationSeconds,
                track))
            .Take(resultLimit)
            .ToArray();

        using var concurrencyGate = new SemaphoreSlim(3, 3);
        var requests = rankedSongs.Select(song => (Func<Task<IReadOnlyList<LyricsCandidate>>>)(async () =>
        {
            await concurrencyGate.WaitAsync(cancellationToken);
            try
            {
                return await LoadSongCandidatesAsync(song, track, cancellationToken);
            }
            finally
            {
                concurrencyGate.Release();
            }
        }));

        var candidates = await LyricsProviderRequests.CollectAsync(requests, cancellationToken);
        return candidates
            .GroupBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(candidate => candidate.MatchScore).First())
            .OrderBy(candidate => candidate.MatchScore)
            .Take(MaximumCandidates)
            .ToArray();
    }

    private async Task<IReadOnlyList<KugouSong>> SearchSongsAsync(
        TrackIdentity track,
        CancellationToken cancellationToken)
    {
        var keyword = string.Join(' ', new[]
        {
            LyricsMatchScorer.GetSearchTitle(track.Title),
            track.Artist
        }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        var uri = new Uri(
            "http://mobilecdn.kugou.com/api/v3/search/song?" + BuildQuery(new Dictionary<string, string>
            {
                ["format"] = "json",
                ["keyword"] = keyword,
                ["page"] = "1",
                ["pagesize"] = "20",
                ["showtype"] = "1"
            }));

        using var response = await _client.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<KugouSongSearchResponse>(
            cancellationToken: cancellationToken);
        return result?.Data?.Info is { } info
            ? info
            : Array.Empty<KugouSong>();
    }

    private async Task<IReadOnlyList<LyricsCandidate>> LoadSongCandidatesAsync(
        KugouSong song,
        TrackIdentity track,
        CancellationToken cancellationToken)
    {
        var albumAudioId = ReadString(song.AlbumAudioId);
        var uri = new Uri(
            "https://krcs.kugou.com/search?" + BuildQuery(new Dictionary<string, string>
            {
                ["ver"] = "1",
                ["man"] = "yes",
                ["client"] = "mobi",
                ["keyword"] = string.Empty,
                ["duration"] = string.Empty,
                ["hash"] = song.EffectiveHash,
                ["album_audio_id"] = albumAudioId
            }));

        using var response = await _client.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<KugouCandidateSearchResponse>(
            cancellationToken: cancellationToken);
        if (result?.Candidates is not { Count: > 0 })
        {
            return Array.Empty<LyricsCandidate>();
        }

        // A song result occasionally exposes several uploaded KRC files. Keep the
        // two closest variants so manual selection can distinguish live/remaster
        // versions without turning one search into dozens of downloads.
        var selected = result.Candidates
            .Where(candidate => candidate.IsUsable)
            .Where(candidate => LyricsMatchScorer.IsRelevantTitle(
                string.IsNullOrWhiteSpace(candidate.EffectiveTitle)
                    ? song.EffectiveTitle
                    : candidate.EffectiveTitle,
                track.Title))
            .OrderBy(candidate => Score(
                candidate.EffectiveTitle,
                candidate.EffectiveArtist,
                song.EffectiveAlbum,
                candidate.DurationSeconds,
                track))
            .Take(2)
            .ToArray();

        var candidates = new List<LyricsCandidate>(selected.Length);
        foreach (var candidate in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = await DownloadLyricsAsync(candidate, cancellationToken);
            if (document is null || (!document.HasSynchronizedLines && !document.IsInstrumental))
            {
                continue;
            }

            var duration = candidate.DurationSeconds > 0
                ? TimeSpan.FromSeconds(candidate.DurationSeconds)
                : song.DurationSeconds > 0
                    ? TimeSpan.FromSeconds(song.DurationSeconds)
                    : TimeSpan.Zero;
            var title = string.IsNullOrWhiteSpace(candidate.EffectiveTitle)
                ? song.EffectiveTitle
                : candidate.EffectiveTitle;
            var artist = string.IsNullOrWhiteSpace(candidate.EffectiveArtist)
                ? song.EffectiveArtist
                : candidate.EffectiveArtist;

            candidates.Add(new LyricsCandidate(
                $"kugou:{candidate.EffectiveId}",
                Name,
                title,
                artist,
                song.EffectiveAlbum,
                duration,
                Score(title, artist, song.EffectiveAlbum, duration.TotalSeconds, track)
                    - (document.Lines.Any(line => line.Words.Count > 0) ? 1.5 : 0)
                    - (document.Lines.Any(line => !string.IsNullOrWhiteSpace(line.Translation)) ? 1 : 0),
                document));
        }

        return candidates;
    }

    private async Task<LyricDocument?> DownloadLyricsAsync(
        KugouLyricCandidate candidate,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(
            "http://lyrics.kugou.com/download?" + BuildQuery(new Dictionary<string, string>
            {
                ["id"] = candidate.EffectiveId,
                ["accesskey"] = candidate.AccessKey,
                ["fmt"] = "krc",
                ["charset"] = "utf8",
                ["client"] = "pc",
                ["ver"] = "1"
            }));

        using var response = await _client.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<KugouDownloadResponse>(
            cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(result?.Content))
        {
            return null;
        }

        var decrypted = TryDecryptKrc(result.Content);
        return decrypted is null ? null : ParseKrc(decrypted);
    }

    private LyricDocument? ParseKrc(string content)
    {
        var translations = ReadTranslations(content);
        var parsedLines = new List<LyricLine>();

        foreach (var rawLine in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var lineMatch = KrcLineRegex().Match(rawLine);
            if (!lineMatch.Success
                || !long.TryParse(lineMatch.Groups["start"].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var lineStartMilliseconds))
            {
                continue;
            }

            var body = lineMatch.Groups["body"].Value;
            var wordMatches = KrcWordRegex().Matches(body);
            if (wordMatches.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(body))
                {
                    parsedLines.Add(new LyricLine(
                        TimeSpan.FromMilliseconds(Math.Max(0, lineStartMilliseconds)),
                        body,
                        null,
                        Array.Empty<LyricWordTiming>()));
                }

                continue;
            }

            var text = new StringBuilder();
            var words = new List<LyricWordTiming>(wordMatches.Count);
            var prefixLength = wordMatches[0].Index;
            if (prefixLength > 0)
            {
                text.Append(body.AsSpan(0, prefixLength));
            }

            for (var index = 0; index < wordMatches.Count; index++)
            {
                var match = wordMatches[index];
                var fragmentStart = match.Index + match.Length;
                var fragmentEnd = index + 1 < wordMatches.Count
                    ? wordMatches[index + 1].Index
                    : body.Length;
                var fragment = body[fragmentStart..fragmentEnd];
                if (!long.TryParse(match.Groups["offset"].Value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var wordOffsetMilliseconds))
                {
                    text.Append(fragment);
                    continue;
                }

                var characterIndex = text.Length;
                text.Append(fragment);
                if (fragment.Length > 0)
                {
                    _ = long.TryParse(match.Groups["duration"].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var durationMilliseconds);
                    words.Add(new LyricWordTiming(
                        TimeSpan.FromMilliseconds(Math.Max(0, lineStartMilliseconds + wordOffsetMilliseconds)),
                        characterIndex,
                        fragment.Length,
                        durationMilliseconds > 0
                            ? TimeSpan.FromMilliseconds(Math.Max(0,
                                lineStartMilliseconds + wordOffsetMilliseconds + durationMilliseconds)) : null));
                }
            }

            var lineText = text.ToString();
            if (!string.IsNullOrWhiteSpace(lineText))
            {
                parsedLines.Add(new LyricLine(
                    TimeSpan.FromMilliseconds(Math.Max(0, lineStartMilliseconds)),
                    lineText,
                    null,
                    words));
            }
        }

        if (parsedLines.Count == 0)
        {
            return null;
        }

        for (var index = 0; index < Math.Min(parsedLines.Count, translations.Count); index++)
        {
            var translation = translations[index];
            if (!string.IsNullOrWhiteSpace(translation)
                && !string.Equals(translation, parsedLines[index].Text, StringComparison.Ordinal))
            {
                parsedLines[index] = parsedLines[index] with { Translation = translation };
            }
        }

        var lines = parsedLines
            .OrderBy(line => line.Start)
            .GroupBy(line => line.Start)
            .Select(group => group.First())
            .ToArray();
        var isInstrumental = lines.All(line => InstrumentalRegex().IsMatch(line.Text));
        return new LyricDocument(lines, Name, content, isInstrumental);
    }

    private static IReadOnlyList<string> ReadTranslations(string content)
    {
        var match = LanguageHeaderRegex().Match(content);
        if (!match.Success)
        {
            return Array.Empty<string>();
        }

        try
        {
            var bytes = Convert.FromBase64String(match.Groups["value"].Value);
            var language = JsonSerializer.Deserialize<KugouLanguageHeader>(bytes);
            var translation = language?.Content?.FirstOrDefault(item => item.Type == 1);
            return translation?.LyricContent?
                .Select(parts => string.Concat(parts).Trim())
                .ToArray()
                ?? Array.Empty<string>();
        }
        catch (FormatException)
        {
            return Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static string? TryDecryptKrc(string base64Content)
    {
        byte[] encrypted;
        try
        {
            encrypted = Convert.FromBase64String(base64Content);
        }
        catch (FormatException)
        {
            return null;
        }

        if (encrypted.Length <= KrcMagic.Length
            || !encrypted.AsSpan(0, KrcMagic.Length).SequenceEqual(KrcMagic))
        {
            return null;
        }

        var compressed = new byte[encrypted.Length - KrcMagic.Length];
        for (var index = 0; index < compressed.Length; index++)
        {
            compressed[index] = (byte)(encrypted[index + KrcMagic.Length]
                ^ KrcXorKey[index % KrcXorKey.Length]);
        }

        if (TryDecompress(compressed, useZlibWrapper: true, out var text))
        {
            return text;
        }

        // A few older KRC encoders expose a raw DEFLATE stream after the two-byte
        // zlib header. Keep this fallback for those files.
        return compressed.Length > 2
               && TryDecompress(compressed.AsSpan(2).ToArray(), useZlibWrapper: false, out text)
            ? text
            : null;
    }

    private static bool TryDecompress(byte[] compressed, bool useZlibWrapper, out string? text)
    {
        text = null;
        try
        {
            using var input = new MemoryStream(compressed, writable: false);
            using Stream decoder = useZlibWrapper
                ? new ZLibStream(input, CompressionMode.Decompress)
                : new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var read = decoder.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > MaximumDecompressedBytes)
                {
                    return false;
                }

                output.Write(buffer, 0, read);
            }

            text = Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
            return !string.IsNullOrWhiteSpace(text);
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static double Score(
        string title,
        string artist,
        string album,
        double durationSeconds,
        TrackIdentity track)
        => LyricsMatchScorer.ScoreMetadata(
            title,
            artist,
            album,
            durationSeconds > 0 ? TimeSpan.FromSeconds(durationSeconds) : TimeSpan.Zero,
            track);

    private static double TextPenalty(string candidate, string expected, double mismatchPenalty)
    {
        var normalizedCandidate = Normalize(candidate);
        var normalizedExpected = Normalize(expected);
        if (normalizedCandidate.Length == 0 || normalizedExpected.Length == 0)
        {
            return mismatchPenalty;
        }

        if (string.Equals(normalizedCandidate, normalizedExpected, StringComparison.Ordinal))
        {
            return 0;
        }

        if (normalizedCandidate.Contains(normalizedExpected, StringComparison.Ordinal)
            || normalizedExpected.Contains(normalizedCandidate, StringComparison.Ordinal))
        {
            return mismatchPenalty * 0.35;
        }

        return mismatchPenalty;
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

    private static string BuildQuery(IReadOnlyDictionary<string, string> values) =>
        string.Join('&', values.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    private static string ReadString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        _ => string.Empty
    };

    private static double ReadDouble(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDouble(out var number) => number,
        JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var number) => number,
        _ => 0
    };

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    [GeneratedRegex(@"^\[(?<start>-?\d+),(?<duration>\d+)\](?<body>.*)$", RegexOptions.Compiled)]
    private static partial Regex KrcLineRegex();

    [GeneratedRegex(@"<(?<offset>-?\d+),(?<duration>\d+),(?<flag>-?\d+)>", RegexOptions.Compiled)]
    private static partial Regex KrcWordRegex();

    [GeneratedRegex(@"\[language:(?<value>[^\]\r\n]+)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LanguageHeaderRegex();

    [GeneratedRegex(
        @"^\s*(?:纯音乐(?:[，,。！!\s]*(?:请欣赏)?)?|此歌曲为没有填词的纯音乐|instrumental)\s*[，,。！!]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex InstrumentalRegex();

    private sealed class KugouSongSearchResponse
    {
        [JsonPropertyName("data")]
        public KugouSongSearchData? Data { get; init; }
    }

    private sealed class KugouSongSearchData
    {
        [JsonPropertyName("info")]
        public List<KugouSong> Info { get; init; } = [];
    }

    private sealed class KugouSong
    {
        [JsonPropertyName("hash")]
        public string? Hash { get; init; }

        [JsonPropertyName("FileHash")]
        public string? FileHash { get; init; }

        [JsonPropertyName("songname")]
        public string? SongName { get; init; }

        [JsonPropertyName("songname_original")]
        public string? OriginalSongName { get; init; }

        [JsonPropertyName("singername")]
        public string? SingerName { get; init; }

        [JsonPropertyName("filename")]
        public string? FileName { get; init; }

        [JsonPropertyName("album_name")]
        public string? AlbumName { get; init; }

        [JsonPropertyName("duration")]
        public JsonElement Duration { get; init; }

        [JsonPropertyName("album_audio_id")]
        public JsonElement AlbumAudioId { get; init; }

        public string EffectiveHash => Hash ?? FileHash ?? string.Empty;

        public string EffectiveTitle => SongName
            ?? OriginalSongName
            ?? SplitFileName().Title;

        public string EffectiveArtist => SingerName ?? SplitFileName().Artist;

        public string EffectiveAlbum => AlbumName ?? string.Empty;

        public double DurationSeconds => ReadDouble(Duration);

        private (string Artist, string Title) SplitFileName()
        {
            var parts = (FileName ?? string.Empty).Split(" - ", 2, StringSplitOptions.TrimEntries);
            return parts.Length == 2 ? (parts[0], parts[1]) : (string.Empty, FileName ?? string.Empty);
        }
    }

    private sealed class KugouCandidateSearchResponse
    {
        [JsonPropertyName("candidates")]
        public List<KugouLyricCandidate> Candidates { get; init; } = [];
    }

    private sealed class KugouLyricCandidate
    {
        [JsonPropertyName("id")]
        public JsonElement Id { get; init; }

        [JsonPropertyName("accesskey")]
        public string AccessKey { get; init; } = string.Empty;

        [JsonPropertyName("song")]
        public string? Song { get; init; }

        [JsonPropertyName("singer")]
        public string? Singer { get; init; }

        [JsonPropertyName("duration")]
        public JsonElement Duration { get; init; }

        public string EffectiveId => ReadString(Id);

        public string EffectiveTitle => Song ?? string.Empty;

        public string EffectiveArtist => Singer ?? string.Empty;

        public double DurationSeconds => ReadDouble(Duration) / 1000d;

        public bool IsUsable => !string.IsNullOrWhiteSpace(EffectiveId)
            && !string.IsNullOrWhiteSpace(AccessKey);
    }

    private sealed class KugouDownloadResponse
    {
        [JsonPropertyName("content")]
        public string? Content { get; init; }
    }

    private sealed class KugouLanguageHeader
    {
        [JsonPropertyName("content")]
        public List<KugouLanguageItem>? Content { get; init; }
    }

    private sealed class KugouLanguageItem
    {
        [JsonPropertyName("type")]
        public int Type { get; init; }

        [JsonPropertyName("lyricContent")]
        public List<List<string>>? LyricContent { get; init; }
    }
}
