using LyricsXQuiet.Core.Lyrics;
using LyricsXQuiet.Core.Lyrics.Providers;
using LyricsXQuiet.Core.Playback;

var root = Path.Combine(Path.GetTempPath(), $"lyrveil-exact-upgrade-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);

try
{
    var baseTrack = new TrackIdentity(
        "干杯时我会想起你",
        "新学校合唱团",
        "干杯时我会想起你",
        TimeSpan.FromSeconds(151),
        "cloudmusic.exe");
    var exactTrack = baseTrack with { SourceTrackId = "netease:1989389607" };
    var badQqDocument = LrcParser.Parse(
        "[00:00.25]举起手中的酒杯\n[00:00.75]今夜不能睡\n[00:07.80]忘掉我所有悲伤",
        "QQ 音乐");
    var exactNetEaseDocument = LrcParser.Parse(
        "[00:00.690]举起手中的酒杯\n[00:04.260]今夜不能睡\n[00:07.680]忘掉我所有悲伤",
        "网易云音乐");

    var automaticDirectory = Path.Combine(root, "automatic");
    var automaticCache = new LyricsCache(automaticDirectory);
    await automaticCache.SaveAsync(baseTrack, badQqDocument);
    var automaticPath = Directory.GetFiles(automaticDirectory, "*.lrc").Single();
    await RemoveSelectionMetadataAsync(automaticPath);

    var exactProvider = new ExactProvider(exactNetEaseDocument);
    var resolver = new LyricsResolver(automaticCache, [exactProvider]);
    var upgraded = await resolver.ResolveAsync(exactTrack);
    Assert(upgraded?.Source == "网易云音乐", "旧 QQ 自动缓存应由网易云精确 ID 歌词升级");
    Assert(exactProvider.ExactRequestCount == 1, "精确升级只应请求一次对应歌曲 ID");
    var upgradedText = await File.ReadAllTextAsync(automaticPath);
    Assert(upgradedText.Contains("[selection:auto]", StringComparison.OrdinalIgnoreCase)
           && upgradedText.Contains("[source:网易云音乐]", StringComparison.OrdinalIgnoreCase)
           && upgradedText.Contains(
               "[source-track-id:netease:1989389607]",
               StringComparison.OrdinalIgnoreCase)
           && upgradedText.Contains("[00:04.260]今夜不能睡", StringComparison.Ordinal),
        "升级后应持久化精确歌词及自动选择元数据");
    var resolvedAgain = await resolver.ResolveAsync(exactTrack);
    Assert(resolvedAgain?.Source.Contains("网易云音乐", StringComparison.Ordinal) == true
           && exactProvider.ExactRequestCount == 1,
        "已缓存当前精确 ID 后不应每次启动重复联网");

    var oldNetEaseDirectory = Path.Combine(root, "old-netease");
    var oldNetEaseCache = new LyricsCache(oldNetEaseDirectory);
    var oldFuzzyNetEaseDocument = LrcParser.Parse(
        "[00:00.20]旧模糊网易歌词\n[00:00.80]时间也不正确",
        "网易云音乐");
    await oldNetEaseCache.SaveAsync(baseTrack, oldFuzzyNetEaseDocument);
    var oldNetEasePath = Directory.GetFiles(oldNetEaseDirectory, "*.lrc").Single();
    await RemoveSelectionMetadataAsync(oldNetEasePath);
    var oldNetEaseProvider = new ExactProvider(exactNetEaseDocument);
    var oldNetEaseResolver = new LyricsResolver(oldNetEaseCache, [oldNetEaseProvider]);
    var upgradedOldNetEase = await oldNetEaseResolver.ResolveAsync(exactTrack);
    Assert(upgradedOldNetEase?.RawText.Contains("[00:04.260]", StringComparison.Ordinal) == true
           && oldNetEaseProvider.ExactRequestCount == 1,
        "旧网易模糊缓存没有精确 source-track-id 时也应只升级一次");

    var failureDirectory = Path.Combine(root, "failure");
    var failureCache = new LyricsCache(failureDirectory);
    await failureCache.SaveAsync(baseTrack, badQqDocument);
    var failingProvider = new ExactProvider(null);
    var failureResolver = new LyricsResolver(failureCache, [failingProvider]);
    var fallback = await failureResolver.ResolveAsync(exactTrack);
    Assert(fallback?.Source.Contains("QQ 音乐", StringComparison.Ordinal) == true,
        "精确歌词请求失败时必须继续使用旧缓存");

    var manualDirectory = Path.Combine(root, "manual");
    var manualCache = new LyricsCache(manualDirectory);
    var manualProvider = new ExactProvider(exactNetEaseDocument);
    var manualResolver = new LyricsResolver(manualCache, [manualProvider]);
    var manualCandidate = new LyricsCandidate(
        "qq:manual",
        "QQ 音乐",
        baseTrack.Title,
        baseTrack.Artist,
        baseTrack.Album,
        baseTrack.Duration,
        0,
        badQqDocument);
    await manualResolver.SaveSelectionAsync(baseTrack, manualCandidate);
    var preservedManual = await manualResolver.ResolveAsync(exactTrack);
    Assert(preservedManual?.Source.Contains("QQ 音乐", StringComparison.Ordinal) == true
           && manualProvider.ExactRequestCount == 0,
        "用户手选歌词不得被精确来源自动覆盖");
    var manualText = await File.ReadAllTextAsync(
        Directory.GetFiles(manualDirectory, "*.lrc").Single());
    Assert(manualText.Contains("[selection:manual]", StringComparison.OrdinalIgnoreCase),
        "手选歌词应持久化 manual 保护标记");

    var importDirectory = Path.Combine(root, "import");
    var importCache = new LyricsCache(importDirectory);
    var importProvider = new ExactProvider(exactNetEaseDocument);
    var importResolver = new LyricsResolver(importCache, [importProvider]);
    var importPath = Path.Combine(root, "user-import.lrc");
    await File.WriteAllTextAsync(importPath, "[00:02.00]用户导入\n[00:06.00]第二句");
    await importResolver.ImportAsync(baseTrack, importPath);
    var preservedImport = await importResolver.ResolveAsync(exactTrack);
    Assert(preservedImport?.Source.Contains("手动导入", StringComparison.Ordinal) == true
           && importProvider.ExactRequestCount == 0,
        "用户导入歌词不得被精确来源自动覆盖");
    var importCacheText = await File.ReadAllTextAsync(
        Directory.GetFiles(importDirectory, "*.lrc").Single());
    Assert(importCacheText.Contains("[selection:import]", StringComparison.OrdinalIgnoreCase),
        "导入歌词应持久化 import 保护标记");

    Console.WriteLine("Lyrics exact-source upgrade smoke tests passed.");
}
finally
{
    if (Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task RemoveSelectionMetadataAsync(string path)
{
    var lines = (await File.ReadAllTextAsync(path))
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Split('\n')
        .Where(line => !line.Trim().StartsWith("[selection:", StringComparison.OrdinalIgnoreCase));
    await File.WriteAllTextAsync(path, string.Join(Environment.NewLine, lines));
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

file sealed class ExactProvider(LyricDocument? exactDocument) : IExactSourceLyricsProvider
{
    public string Name => "网易云音乐";

    public int ExactRequestCount { get; private set; }

    public bool CanResolveExact(TrackIdentity track) =>
        SourceTrackIdParser.TryGetNetEaseSongId(track.SourceTrackId, out _);

    public Task<LyricDocument?> FindExactAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExactRequestCount++;
        return Task.FromResult(exactDocument);
    }

    public Task<LyricDocument?> FindAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("缓存升级不得调用模糊 FindAsync。");

    public Task<IReadOnlyList<LyricsCandidate>> SearchAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("缓存升级不得调用模糊 SearchAsync。");
}
