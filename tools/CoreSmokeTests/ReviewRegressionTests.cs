using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using LyricsXQuiet.Core.Lyrics;
using LyricsXQuiet.Core.Lyrics.Providers;
using LyricsXQuiet.Core.Playback;

internal static class ReviewRegressionTests
{
    public static async Task RunAsync()
    {
        var tempBase = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(tempBase, "lyrveil-review-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await CheckPersistenceFailuresAsync(root);
            await CheckSourceResultsAsync(root);
            await CheckSearchDeadlineAsync(root);
            await CheckCachedPresentationAsync(root);
            await CheckAsyncDirectoryAsync(root);
            await CheckWordTimingAsync(root);
            CheckFilterTimeouts();
            Console.WriteLine("Review regressions passed: persistence, async directory, provider failures, versions, deadlines, cached presentation, word durations, filters.");
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.TrimEndingDirectorySeparator(tempBase) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("lyrveil-review-regression-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected test cleanup path.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static readonly TrackIdentity Track = new("Review Song", "Review Artist", "", TimeSpan.FromSeconds(180));
    private static LyricDocument Document => LrcParser.Parse("[00:01.00]First line\n[00:05.00]Second line", "Review Provider");
    private static LyricsCandidate Candidate => new("one", "Review Provider", Track.Title, Track.Artist,
        "Album One", Track.Duration, 0, Document);

    private static async Task CheckPersistenceFailuresAsync(string root)
    {
        var blocker = Path.Combine(root, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "fixture");
        var resolver = new LyricsResolver(new LyricsCache(blocker), [new TestProvider("Review Provider", _ => Task.FromResult<IReadOnlyList<LyricsCandidate>>([Candidate]))]);
        var wasPresented = false;
        var result = await resolver.ResolveWithStatusAsync(Track, documentAvailable: _ => wasPresented = true);
        Check(wasPresented && result.Document?.HasSynchronizedLines == true && result.SaveFailed,
            "Usable lyrics must be presented and returned when cache writes fail.");
        var selected = await resolver.SaveSelectionAsync(Track, Candidate);
        Check(selected.Document is not null && selected.SaveFailed, "Manual selection must distinguish session-only playback.");
        var import = Path.Combine(root, "import.lrc");
        await File.WriteAllTextAsync(import, "[00:01.00]Imported line");
        var imported = await resolver.ImportWithStatusAsync(Track, import);
        Check(imported.Document is not null && imported.SaveFailed, "Import must survive failure to persist the imported document.");

        var cacheRoot = Path.Combine(root, "atomic");
        var cache = new LyricsCache(cacheRoot);
        await cache.SaveAsync(Track, Document, LyricsCacheSelectionKind.Manual);
        var path = Directory.GetFiles(cacheRoot, "*.lrc").Single();
        var before = await File.ReadAllTextAsync(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                await cache.SaveAsync(Track, LrcParser.Parse("[00:01.00]Replacement", "replacement"));
                throw new InvalidOperationException("Replacing an exclusively held destination should fail.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        Check(await File.ReadAllTextAsync(path) == before, "Failed atomic replacement must preserve the previous selection.");
        Check(Directory.GetFiles(cacheRoot, "*.tmp").Length == 0, "Failed writes must clean their temporary files.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => cache.SaveAsync(Track, Document, cancellationToken: cancelled.Token));
        Check(await File.ReadAllTextAsync(path) == before, "Cancelled writes must preserve the previous selection.");
    }

    private static async Task CheckSourceResultsAsync(string root)
    {
        var failed = new TestProvider("Offline source", _ => throw new HttpRequestException("fixture failure"));
        var disabledInner = new TestProvider("Disabled source", _ => throw new InvalidOperationException("Must not be called"));
        using var disabled = new ConfigurableLyricsProvider(disabledInner, () => false);
        var resolver = new LyricsResolver(new LyricsCache(Path.Combine(root, "sources")), [failed, disabled]);
        var status = await resolver.SearchWithStatusAsync(Track);
        Check(status.Candidates.Count == 0 && status.Failures.Count == 1 && status.SuccessfulSources == 0
              && disabledInner.SearchCount == 0, "Disabled sources must not turn an all-failed search into a successful empty search.");
        await ExpectAsync<LyricsSourcesUnavailableException>(() => resolver.ResolveAsync(Track, forceNetwork: true));
        var empty = new LyricsResolver(new LyricsCache(Path.Combine(root, "empty")),
            [new TestProvider("Empty source", _ => Task.FromResult<IReadOnlyList<LyricsCandidate>>([]))]);
        Check(await empty.ResolveAsync(Track) is null, "A successful no-match request must remain distinct from failure.");

        var otherAlbum = Candidate with { Id = "two", Album = "Album Two" };
        var good = new TestProvider("Review Provider", _ => Task.FromResult<IReadOnlyList<LyricsCandidate>>([Candidate, Candidate, otherAlbum]));
        var mixed = new LyricsResolver(new LyricsCache(Path.Combine(root, "versions")), [failed, good]);
        var mixedResult = await mixed.SearchWithStatusAsync(Track);
        Check(mixedResult.Candidates.Count == 2 && mixedResult.Failures.Count == 1,
            "Distinct source IDs/albums must survive deduplication and a different source's failure.");
        Check((await mixed.SearchAsync(Track)).Count == 2, "Partial provider failure must preserve usable candidates.");
        var withoutIds = new LyricsResolver(new LyricsCache(Path.Combine(root, "no-ids")),
            [new TestProvider("Review Provider", _ => Task.FromResult<IReadOnlyList<LyricsCandidate>>(
                [Candidate with { Id = "" }, otherAlbum with { Id = "" }]))]);
        Check((await withoutIds.SearchAsync(Track)).Count == 2, "Metadata fallback deduplication must include album.");

        using var client = new HttpClient(new ResponseHandler(HttpStatusCode.ServiceUnavailable, "{}")) { BaseAddress = new Uri("https://lrclib.net/") };
        using var lrclib = new LrclibProvider(client);
        using var netEase = new NetEaseProvider(client);
        using var kugou = new KugouProvider(client);
        var httpFailures = new LyricsResolver(new LyricsCache(Path.Combine(root, "http")), [lrclib, netEase, kugou]);
        var httpResult = await httpFailures.SearchWithStatusAsync(Track);
        Check(httpResult.Failures.Count == 3 && httpResult.SuccessfulSources == 0,
            "Real source adapters must not convert HTTP 503 into successful no-match results.");
    }

    private static async Task CheckSearchDeadlineAsync(string root)
    {
        var late = new TaskCompletionSource<IReadOnlyList<LyricsCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fast = new TestProvider("Review Provider", _ => Task.FromResult<IReadOnlyList<LyricsCandidate>>([Candidate]));
        var resolver = new LyricsResolver(new LyricsCache(Path.Combine(root, "deadline")),
            [fast, new TestProvider("Slow source", _ => late.Task)], automaticSearchTimeout: TimeSpan.FromMilliseconds(80));
        var result = await resolver.ResolveAsync(Track, forceNetwork: true).WaitAsync(TimeSpan.FromSeconds(3));
        Check(result is not null, "A stalled source must not withhold another source's usable lyrics beyond the total budget.");
        Check(fast.AutomaticCount == 1 && fast.SearchCount == 0, "Automatic matching must use the smaller request path.");
        late.SetResult([]);

        using var cancellation = new CancellationTokenSource();
        var cancellable = new LyricsResolver(new LyricsCache(Path.Combine(root, "cancelled")),
            [new TestProvider("Delayed source", async token => { await Task.Delay(Timeout.Infinite, token); return []; })]);
        var pending = cancellable.ResolveAsync(Track, cancellationToken: cancellation.Token);
        cancellation.Cancel();
        await ExpectAsync<OperationCanceledException>(() => pending);
        Check(!Directory.Exists(Path.Combine(root, "cancelled")), "A cancelled track operation must not publish a cache entry.");
    }

    private static async Task CheckCachedPresentationAsync(string root)
    {
        var cache = new LyricsCache(Path.Combine(root, "cached"));
        await cache.SaveAsync(Track, Document with { Source = "QQ 音乐" });
        var upgrade = new TaskCompletionSource<IReadOnlyList<LyricsCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var visible = new TaskCompletionSource<LyricDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new LyricsResolver(cache, [new TestProvider("酷狗音乐", _ => upgrade.Task)]);
        var pending = resolver.ResolveWithStatusAsync(Track, documentAvailable: doc => visible.TrySetResult(doc));
        Check((await visible.Task.WaitAsync(TimeSpan.FromSeconds(3))).HasSynchronizedLines && !pending.IsCompleted,
            "Cached lyrics must be presented before waiting for an online upgrade.");
        upgrade.SetResult([Candidate with { Provider = "酷狗音乐", Document = Document with { Source = "酷狗音乐" } }]);
        Check((await pending).Document?.Source == "酷狗音乐", "The preferred-provider upgrade must still complete after early cached presentation.");
    }

    private static async Task CheckAsyncDirectoryAsync(string root)
    {
        using var context = new QueuedContext();
        var previous = SynchronizationContext.Current;
        Task save;
        Task<string> open;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            var cache = new LyricsCache(Path.Combine(root, "ui-directory"));
            var large = Document with { Lines = [new LyricLine(TimeSpan.FromSeconds(1), new string('x', 4_000_000), null, [])] };
            save = cache.SaveAsync(Track, large);
            open = cache.EnsureDirectoryAsync(); // Must return control without pumping the UI queue.
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        var all = Task.WhenAll(save, open);
        var deadline = Stopwatch.StartNew();
        while (!all.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            while (context.Queue.TryTake(out var work)) work.Callback(work.State);
            await Task.Delay(5);
        }
        await all.WaitAsync(TimeSpan.FromSeconds(1));
        Check(Directory.Exists(await open), "Opening a directory must complete alongside an asynchronous cache write.");
    }

    private static async Task CheckWordTimingAsync(string root)
    {
        var timed = new LyricDocument([new LyricLine(TimeSpan.FromSeconds(1), "甲乙", "Translation",
            [new(TimeSpan.FromSeconds(1), 0, 1, TimeSpan.FromSeconds(1.5)),
             new(TimeSpan.FromSeconds(4), 1, 1, TimeSpan.FromSeconds(4.5))])], "timing test", "");
        var timeline = new LyricTimeline(timed);
        Check(Math.Abs(timeline.GetFrame(TimeSpan.FromSeconds(2))!.Progress - 0.5) < 0.001,
            "Highlight must hold at the end of a sung word throughout the following pause.");
        Check(timeline.GetFrame(TimeSpan.FromSeconds(5))!.Progress == 1,
            "The last word must finish at its real end, without extending through the line gap.");
        var sustained = timed with { Lines = [new LyricLine(TimeSpan.FromSeconds(1), "甲", null,
            [new(TimeSpan.FromSeconds(1), 0, 1, TimeSpan.FromSeconds(11))])] };
        Check(Math.Abs(new LyricTimeline(sustained).GetFrame(TimeSpan.FromSeconds(6))!.Progress - 0.5) < 0.001,
            "A sustained final word must not be clipped by the old five-second fallback line duration.");
        var cache = new LyricsCache(Path.Combine(root, "word-timing"));
        await cache.SaveAsync(Track, timed);
        var loaded = (await cache.TryLoadAsync(Track))!;
        Check(loaded.Lines[0].Words.SequenceEqual(timed.Lines[0].Words)
              && loaded.Lines[0].Translation == "Translation", "Word durations and translation must survive a cache round trip.");
        var legacy = LrcParser.Parse("[00:01.00]<00:01.00>甲<00:04.00>乙");
        Check(legacy.Lines[0].Words.All(word => word.End is null), "Legacy enhanced LRC without explicit ends must retain interpolation.");
        var offset = LrcParser.Parse("[offset:100]\n[00:01.00]<00:01.00>甲<00:01.500><00:04.00>乙<00:04.500>");
        Check(offset.Lines[0].Words[0].End == TimeSpan.FromSeconds(1.6), "LRC offset must move word ends with their starts.");

        using var client = new HttpClient(new ResponseHandler(HttpStatusCode.OK,
            "{\"code\":200,\"yrc\":{\"lyric\":\"[1000,4000](1000,500,0)甲(4000,500,0)乙\"}}"));
        using var provider = new NetEaseProvider(client);
        var yrc = await provider.FindExactAsync(Track with { SourceAppUserModelId = "cloudmusic.exe", SourceTrackId = "netease:42" });
        Check(yrc?.Lines[0].Words[0].End == TimeSpan.FromSeconds(1.5), "NetEase YRC must preserve its original word duration.");

        using var kugouClient = new HttpClient(new KugouTimingHandler());
        using var kugouProvider = new KugouProvider(kugouClient);
        var krc = (await kugouProvider.SearchAutomaticAsync(Track)).Single().Document;
        Check(krc.Lines[0].Words[0].End == TimeSpan.FromSeconds(1.5)
              && krc.Lines[0].Words[1].End == TimeSpan.FromSeconds(4.5),
            "Kugou KRC must preserve original word durations after decompression.");
    }

    private static void CheckFilterTimeouts()
    {
        const string pattern = "^(a+)+$";
        var document = LrcParser.Parse("[00:01.00]" + new string('a', 8000) + "!");
        var result = LyricFilter.Apply(document, pattern);
        Check(result.Lines[0].Text == document.Lines[0].Text && LyricFilter.FindTimedOutPatternLines(pattern).Contains(1),
            "A pathological rule must be disabled and leave the original line intact.");
        var repeat = Stopwatch.StartNew();
        _ = LyricFilter.Apply(document, pattern);
        Check(repeat.Elapsed < TimeSpan.FromSeconds(1), "A known timed-out rule must not repeatedly block subsequent playback.");
        Check(LyricFilter.FindPatternErrors("valid\n\n[")[0].LineNumber == 3,
            "Cached validation must preserve exact input line numbers.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class TestProvider(string name, Func<CancellationToken, Task<IReadOnlyList<LyricsCandidate>>> search) : ILyricsProvider
    {
        public string Name => name;
        public int SearchCount { get; private set; }
        public int AutomaticCount { get; private set; }
        public Task<LyricDocument?> FindAsync(TrackIdentity track, CancellationToken cancellationToken = default) => Task.FromResult<LyricDocument?>(null);
        public Task<IReadOnlyList<LyricsCandidate>> SearchAsync(TrackIdentity track, CancellationToken cancellationToken = default)
        { SearchCount++; return search(cancellationToken); }
        public Task<IReadOnlyList<LyricsCandidate>> SearchAutomaticAsync(TrackIdentity track, CancellationToken cancellationToken = default)
        { AutomaticCount++; return search(cancellationToken); }
    }

    private sealed class ResponseHandler(HttpStatusCode status, string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
    }

    private sealed class QueuedContext : SynchronizationContext, IDisposable
    {
        public BlockingCollection<(SendOrPostCallback Callback, object? State)> Queue { get; } = new();
        public override void Post(SendOrPostCallback callback, object? state) => Queue.Add((callback, state));
        public void Dispose() => Queue.Dispose();
    }

    private sealed class KugouTimingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string json;
            if (request.RequestUri!.Host == "mobilecdn.kugou.com")
                json = "{\"data\":{\"info\":[{\"hash\":\"fixture\",\"songname\":\"Review Song\",\"singername\":\"Review Artist\",\"duration\":180}]}}";
            else if (request.RequestUri.AbsolutePath == "/search")
                json = "{\"candidates\":[{\"id\":1,\"accesskey\":\"fixture\",\"song\":\"Review Song\",\"singer\":\"Review Artist\",\"duration\":180000}]}";
            else
                json = System.Text.Json.JsonSerializer.Serialize(new { content = EncodeKrc("[1000,4000]<0,500,0>甲<3000,500,0>乙") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
        }

        private static string EncodeKrc(string text)
        {
            using var compressed = new MemoryStream();
            using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                zlib.Write(System.Text.Encoding.UTF8.GetBytes(text));
            byte[] key = [0x40, 0x47, 0x61, 0x77, 0x5e, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2d, 0xce, 0xd2, 0x6e, 0x69];
            var payload = compressed.ToArray();
            var result = new byte[payload.Length + 4];
            "krc1"u8.CopyTo(result);
            for (var index = 0; index < payload.Length; index++) result[index + 4] = (byte)(payload[index] ^ key[index % key.Length]);
            return Convert.ToBase64String(result);
        }
    }
}
