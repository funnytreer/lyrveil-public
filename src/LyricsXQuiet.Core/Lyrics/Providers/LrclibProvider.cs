using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using LyricsXQuiet.Core.Playback;

namespace LyricsXQuiet.Core.Lyrics.Providers;

public sealed class LrclibProvider : ILyricsProvider, IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public LrclibProvider(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient
        {
            BaseAddress = new Uri("https://lrclib.net/"),
            Timeout = TimeSpan.FromSeconds(12)
        };

        if (!_client.DefaultRequestHeaders.UserAgent.Any())
        {
            _client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Lyrveil/0.2 (Windows desktop lyrics; local development build)");
        }
    }

    public string Name => "LRCLIB";

    public async Task<LyricDocument?> FindAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default)
    {
        if (!track.IsUsable)
        {
            return null;
        }

        LrclibRecord? record = null;
        if (!string.IsNullOrWhiteSpace(track.Artist)
            && !string.IsNullOrWhiteSpace(track.Album)
            && track.Duration > TimeSpan.Zero)
        {
            var exactPath = "api/get?" + BuildQuery(new Dictionary<string, string>
            {
                ["track_name"] = track.Title,
                ["artist_name"] = track.Artist,
                ["album_name"] = track.Album,
                ["duration"] = Math.Round(track.Duration.TotalSeconds).ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
            });
            record = await GetRecordAsync(exactPath, cancellationToken);
        }

        if (record is null
            || !LyricsMatchScorer.IsRelevantTitle(record.TrackName, track.Title))
        {
            return (await SearchAsync(track, cancellationToken)).FirstOrDefault()?.Document;
        }

        if (record.Instrumental)
        {
            return new LyricDocument(
                Array.Empty<LyricLine>(),
                Name,
                string.Empty,
                true);
        }

        if (string.IsNullOrWhiteSpace(record.SyncedLyrics))
        {
            return null;
        }

        var document = LrcParser.Parse(record.SyncedLyrics, Name);
        return document.HasSynchronizedLines ? document : null;
    }

    public async Task<IReadOnlyList<LyricsCandidate>> SearchAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default)
    {
        if (!track.IsUsable)
        {
            return Array.Empty<LyricsCandidate>();
        }

        var query = new Dictionary<string, string>
        {
            ["track_name"] = LyricsMatchScorer.GetSearchTitle(track.Title)
        };
        if (!string.IsNullOrWhiteSpace(track.Artist))
        {
            query["artist_name"] = track.Artist;
        }

        using var response = await SendAsync("api/search?" + BuildQuery(query), cancellationToken);
        response.EnsureSuccessStatusCode();

        var results = await response.Content.ReadFromJsonAsync<LrclibRecord[]>(cancellationToken: cancellationToken)
            ?? Array.Empty<LrclibRecord>();

        return results
            .Where(result => LyricsMatchScorer.IsRelevantTitle(result.TrackName, track.Title))
            .Where(result => !result.Instrumental && !string.IsNullOrWhiteSpace(result.SyncedLyrics))
            .OrderBy(result => Score(result, track))
            .Take(24)
            .Select(result => ToCandidate(result, track))
            .Where(candidate => candidate is not null)
            .Cast<LyricsCandidate>()
            .ToArray();
    }

    private LyricsCandidate? ToCandidate(LrclibRecord record, TrackIdentity track)
    {
        var document = LrcParser.Parse(record.SyncedLyrics ?? string.Empty, Name);
        return document.HasSynchronizedLines
            ? new LyricsCandidate(
                $"lrclib:{record.Id}",
                Name,
                record.TrackName,
                record.ArtistName,
                record.AlbumName,
                TimeSpan.FromSeconds(record.Duration),
                Score(record, track),
                document)
            : null;
    }

    private async Task<LrclibRecord?> GetRecordAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(path, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<LrclibRecord>(cancellationToken: cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await _requestLock.WaitAsync(cancellationToken);
        try
        {
            var sinceLastRequest = DateTimeOffset.UtcNow - _lastRequestAt;
            if (sinceLastRequest < TimeSpan.FromMilliseconds(250))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250) - sinceLastRequest, cancellationToken);
            }

            var response = await _client.GetAsync(path, cancellationToken);
            _lastRequestAt = DateTimeOffset.UtcNow;
            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                return response;
            }

            var retryDelay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
            response.Dispose();
            await Task.Delay(retryDelay, cancellationToken);
            var retry = await _client.GetAsync(path, cancellationToken);
            _lastRequestAt = DateTimeOffset.UtcNow;
            return retry;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private static double Score(LrclibRecord result, TrackIdentity track)
        => LyricsMatchScorer.ScoreMetadata(
            result.TrackName,
            result.ArtistName,
            result.AlbumName,
            TimeSpan.FromSeconds(result.Duration),
            track);

    private static string BuildQuery(IReadOnlyDictionary<string, string> values) =>
        string.Join('&', values.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    public void Dispose()
    {
        _requestLock.Dispose();
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private sealed record LrclibRecord(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("trackName")] string TrackName,
        [property: JsonPropertyName("artistName")] string ArtistName,
        [property: JsonPropertyName("albumName")] string AlbumName,
        [property: JsonPropertyName("duration")] double Duration,
        [property: JsonPropertyName("instrumental")] bool Instrumental,
        [property: JsonPropertyName("plainLyrics")] string? PlainLyrics,
        [property: JsonPropertyName("syncedLyrics")] string? SyncedLyrics);
}
