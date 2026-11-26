using System.Security.Cryptography;
using System.Text;
using LyricsXQuiet.Core.Playback;

namespace LyricsXQuiet.Core.Lyrics;

public sealed class LyricsCache
{
    private static readonly HashSet<string> AutomaticLegacySources = new(
        ["QQ 音乐", "网易云音乐", "酷狗音乐", "LRCLIB"],
        StringComparer.OrdinalIgnoreCase);

    private readonly Func<string> _directoryProvider;
    private readonly SemaphoreSlim _ioGate = new(1, 1);

    public LyricsCache(string? directory = null)
        : this(() => directory ?? LyricsStorage.DefaultDirectory)
    {
    }

    public LyricsCache(Func<string> directoryProvider)
    {
        _directoryProvider = directoryProvider ?? throw new ArgumentNullException(nameof(directoryProvider));
    }

    public string DirectoryPath => Path.GetFullPath(_directoryProvider());

    public async Task<string> EnsureDirectoryAsync(CancellationToken cancellationToken = default)
    {
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = DirectoryPath;
            LyricsStorage.EnsureOwnedDirectory(directory);
            return directory;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task<LyricDocument?> TryLoadAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default) =>
        (await TryLoadEntryAsync(track, cancellationToken))?.Document;

    public async Task<LyricsCacheEntry?> TryLoadEntryAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default)
    {
        await _ioGate.WaitAsync(cancellationToken);
        try
        {
            var rememberedSelection = await TryLoadRememberedSelectionAsync(
                track,
                cancellationToken);
            if (rememberedSelection is not null)
            {
                return rememberedSelection;
            }

            foreach (var path in GetCandidatePaths(track).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var entry = await TryReadEntryAsync(
                    path,
                    track,
                    ignoreDuration: false,
                    cancellationToken);
                if (entry is not null)
                {
                    return entry;
                }
            }

            return null;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private async Task<LyricsCacheEntry?> TryLoadRememberedSelectionAsync(
        TrackIdentity track,
        CancellationToken cancellationToken)
    {
        var directory = DirectoryPath;
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var title = SanitizeFileName(track.Title, "未知歌曲");
        var artist = SanitizeFileName(track.Artist, "未知歌手");
        var filePrefix = $"{title} - {artist} [";
        var candidates = new List<RememberedSelectionCandidate>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.lrc"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Path.GetFileName(path).StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string content;
                try
                {
                    content = await File.ReadAllTextAsync(path, cancellationToken);
                }
                catch (IOException)
                {
                    continue;
                }

                var declaredSource = ReadMetadataTag(content, "source") ?? string.Empty;
                var selectionKind = ReadSelectionKind(content, declaredSource);
                if (selectionKind == LyricsCacheSelectionKind.Automatic
                    || !IsCacheRelevant(content, track, ignoreDuration: true))
                {
                    continue;
                }

                var document = LrcParser.Parse(content, "本地缓存");
                if (!document.HasSynchronizedLines && !document.IsInstrumental)
                {
                    continue;
                }

                var duration = TryReadDuration(content);
                var sourceTrackId = ReadMetadataTag(content, "source-track-id") ?? string.Empty;
                candidates.Add(new RememberedSelectionCandidate(
                    new LyricsCacheEntry(
                        document,
                        selectionKind,
                        declaredSource,
                        sourceTrackId,
                        ReadMetadataTag(content, "ranking-policy") ?? string.Empty),
                    duration,
                    sourceTrackId,
                    File.GetLastWriteTimeUtc(path)));
            }
        }
        catch (IOException)
        {
            return null;
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(track.SourceTrackId))
        {
            var sourceIdMatch = candidates
                .Where(candidate => string.Equals(
                    candidate.SourceTrackId,
                    track.SourceTrackId,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candidate => candidate.LastWriteTimeUtc)
                .FirstOrDefault();
            if (sourceIdMatch is not null)
            {
                return sourceIdMatch.Entry;
            }
        }

        if (track.Duration > TimeSpan.Zero)
        {
            var durationMatch = candidates
                .Where(candidate => candidate.Duration is not null
                    && Math.Abs(
                        candidate.Duration.Value.TotalMilliseconds
                        - track.Duration.TotalMilliseconds) <= 1_500)
                .OrderByDescending(candidate => candidate.LastWriteTimeUtc)
                .FirstOrDefault();
            if (durationMatch is not null)
            {
                return durationMatch.Entry;
            }
        }

        // When exactly one version has ever been chosen manually for this
        // title/artist/album identity, keep that explicit choice even if the
        // player briefly pairs the song with a stale duration from another
        // track. Multiple manual versions remain duration/ID-disambiguated.
        return candidates.Count == 1 ? candidates[0].Entry : null;
    }

    private static async Task<LyricsCacheEntry?> TryReadEntryAsync(
        string path,
        TrackIdentity track,
        bool ignoreDuration,
        CancellationToken cancellationToken)
    {
        try
        {
            var content = await File.ReadAllTextAsync(path, cancellationToken);
            if (!IsCacheRelevant(content, track, ignoreDuration))
            {
                return null;
            }

            var document = LrcParser.Parse(content, "本地缓存");
            if (!document.HasSynchronizedLines && !document.IsInstrumental)
            {
                return null;
            }

            var declaredSource = ReadMetadataTag(content, "source") ?? string.Empty;
            return new LyricsCacheEntry(
                document,
                ReadSelectionKind(content, declaredSource),
                declaredSource,
                ReadMetadataTag(content, "source-track-id") ?? string.Empty,
                ReadMetadataTag(content, "ranking-policy") ?? string.Empty);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public async Task SaveAsync(
        TrackIdentity track,
        LyricDocument document,
        LyricsCacheSelectionKind selectionKind = LyricsCacheSelectionKind.Automatic,
        CancellationToken cancellationToken = default,
        string rankingPolicy = "")
    {
        await _ioGate.WaitAsync(cancellationToken);
        try
        {
            var directory = DirectoryPath;
            LyricsStorage.EnsureOwnedDirectory(directory);
            var path = GetPath(track);
            var temporaryPath = Path.Combine(directory, $".lyrveil-{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllTextAsync(
                    temporaryPath,
                    AddCacheMetadata(track, document, selectionKind, rankingPolicy),
                    Encoding.UTF8,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                // The temporary file is on the same volume. Failed/cancelled writes
                // never truncate the previous selection or publish a partial lyric.
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task<LyricDocument?> ImportAsync(
        TrackIdentity track,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var document = await ReadImportAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        await SaveAsync(
            track,
            document,
            LyricsCacheSelectionKind.Imported,
            cancellationToken);
        return document;
    }

    public static async Task<LyricDocument?> ReadImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var content = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var document = LrcParser.Parse(content, "手动导入");
        return document.HasSynchronizedLines || document.IsInstrumental ? document : null;
    }

    public async Task<LyricsDirectoryMigrationResult> RelocateAsync(
        string destinationDirectory,
        Func<string, Task> commitDirectoryAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commitDirectoryAsync);
        await _ioGate.WaitAsync(cancellationToken);
        try
        {
            var sourceDirectory = DirectoryPath;
            var result = await Task.Run(
                () => LyricsDirectoryMigrator.Migrate(sourceDirectory, destinationDirectory),
                cancellationToken);
            if (result.Changed)
            {
                await commitDirectoryAsync(result.DestinationDirectory);
            }

            return result;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task<LyricsCacheCleanupPlan> ScanCleanupAsync(
        CancellationToken cancellationToken = default)
    {
        await _ioGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(
                () => LyricsCacheOrganizer.Scan(DirectoryPath),
                cancellationToken);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task<LyricsCacheCleanupResult> ApplyCleanupAsync(
        LyricsCacheCleanupPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await _ioGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(
                () => LyricsCacheOrganizer.Apply(DirectoryPath, plan),
                cancellationToken);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task<LyricsCacheRestorePlan?> GetLatestCleanupBackupAsync(
        CancellationToken cancellationToken = default)
    {
        await _ioGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(
                () => LyricsCacheOrganizer.GetLatestRestorePlan(DirectoryPath),
                cancellationToken);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task<LyricsCacheRestoreResult> RestoreCleanupBackupAsync(
        LyricsCacheRestorePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await _ioGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(
                () => LyricsCacheOrganizer.Restore(DirectoryPath, plan),
                cancellationToken);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private string GetPath(TrackIdentity track)
    {
        var hash = GetHash(LyricsCacheIdentity.GetPrimaryKey(track))[..10];
        var title = SanitizeFileName(track.Title, "未知歌曲");
        var artist = SanitizeFileName(track.Artist, "未知歌手");
        return Path.Combine(DirectoryPath, $"{title} - {artist} [{hash}].lrc");
    }

    private IEnumerable<string> GetCandidatePaths(TrackIdentity track)
    {
        var sanitizedTitle = SanitizeFileName(track.Title, "未知歌曲");
        var sanitizedArtist = SanitizeFileName(track.Artist, "未知歌手");
        foreach (var key in LyricsCacheIdentity.GetCompatibleKeys(track))
        {
            yield return Path.Combine(
                DirectoryPath,
                $"{sanitizedTitle} - {sanitizedArtist} [{GetHash(key)[..10]}].lrc");
        }

        // v1 readable cache path and the original LocalAppData cache remain
        // read-only fallbacks so an upgrade never strands existing lyrics.
        yield return Path.Combine(
            DirectoryPath,
            $"{sanitizedTitle} - {sanitizedArtist} [{GetHash(track.StableKey)[..10]}].lrc");
        yield return GetLegacyPath(track.StableKey);

        if (!IsAppleMusicTrack(track))
        {
            yield break;
        }

        var legacyArtists = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            track.Artist
        };
        if (!string.IsNullOrWhiteSpace(track.Album))
        {
            legacyArtists.Add($"{track.Artist} — {track.Album}");
        }

        var legacyDurations = new HashSet<double>
        {
            0,
            Math.Round(track.Duration.TotalSeconds)
        };
        foreach (var legacyArtist in legacyArtists)
        {
            foreach (var legacyDuration in legacyDurations)
            {
                var title = SanitizeFileName(track.Title, "未知歌曲");
                var artist = SanitizeFileName(legacyArtist, "未知歌手");
                var legacyTrack = track with
                {
                    Artist = legacyArtist,
                    Album = string.Empty,
                    Duration = TimeSpan.FromSeconds(legacyDuration)
                };
                foreach (var key in LyricsCacheIdentity.GetCompatibleKeys(legacyTrack))
                {
                    yield return Path.Combine(
                        DirectoryPath,
                        $"{title} - {artist} [{GetHash(key)[..10]}].lrc");
                }

                var legacyStableKey = string.Join('|',
                    TrackIdentity.NormalizeForMatching(track.Title),
                    TrackIdentity.NormalizeForMatching(legacyArtist),
                    legacyDuration);
                var hash = GetHash(legacyStableKey);
                yield return Path.Combine(
                    DirectoryPath,
                    $"{title} - {artist} [{hash[..10]}].lrc");
                yield return GetLegacyPath(legacyStableKey);
            }
        }
    }

    private static bool IsAppleMusicTrack(TrackIdentity track) =>
        track.SourceAppUserModelId.Contains("applemusic", StringComparison.OrdinalIgnoreCase)
        || track.SourceAppUserModelId.Contains("apple music", StringComparison.OrdinalIgnoreCase);

    private static string GetHash(string stableKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(stableKey));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private string GetLegacyPath(string stableKey) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LyricsXQuiet",
            "Lyrics",
            $"{GetHash(stableKey)[..20]}.lrc");

    private static string SanitizeFileName(string value, string fallback)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string(value
            .Trim()
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = fallback;
        }

        return cleaned.Length <= 70 ? cleaned : cleaned[..70].TrimEnd();
    }

    private static string AddCacheMetadata(
        TrackIdentity track,
        LyricDocument document,
        LyricsCacheSelectionKind selectionKind,
        string rankingPolicy)
    {
        // Always persist the provider-neutral model. This preserves KRC/YRC word
        // timings while guaranteeing that the next run can parse the .lrc file.
        var content = LrcSerializer.Serialize(document);

        var source = new string(document.Source
            .Where(character => character is not ('\r' or '\n' or ']'))
            .ToArray())
            .Trim();
        if (!string.IsNullOrWhiteSpace(source))
        {
            content = $"[source:{source}]{Environment.NewLine}{content}";
        }

        content = AddIdentityTag(content, "al", track.Album);
        content = AddIdentityTag(content, "ar", track.Artist);
        content = AddIdentityTag(content, "ti", track.Title);
        if (track.Duration > TimeSpan.Zero)
        {
            var durationMilliseconds = Math.Round(track.Duration.TotalMilliseconds)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            content = $"[duration-ms:{durationMilliseconds}]"
                + Environment.NewLine
                + content;
        }

        content = $"[cache-key-version:2]{Environment.NewLine}{content}";

        var selection = selectionKind switch
        {
            LyricsCacheSelectionKind.Manual => "manual",
            LyricsCacheSelectionKind.Imported => "import",
            _ => "auto"
        };
        if (!string.IsNullOrWhiteSpace(track.SourceTrackId))
        {
            var sourceTrackId = new string(track.SourceTrackId
                .Where(character => character is not ('\r' or '\n' or ']'))
                .ToArray())
                .Trim();
            if (!string.IsNullOrWhiteSpace(sourceTrackId))
            {
                content = $"[source-track-id:{sourceTrackId}]"
                    + Environment.NewLine
                    + content;
            }
        }

        if (selectionKind == LyricsCacheSelectionKind.Automatic
            && !string.IsNullOrWhiteSpace(rankingPolicy))
        {
            var sanitizedPolicy = new string(rankingPolicy
                .Where(character => character is not ('\r' or '\n' or ']'))
                .ToArray())
                .Trim();
            if (!string.IsNullOrWhiteSpace(sanitizedPolicy))
            {
                content = $"[ranking-policy:{sanitizedPolicy}]"
                    + Environment.NewLine
                    + content;
            }
        }

        return $"[selection:{selection}]{Environment.NewLine}{content}";
    }

    private static string AddIdentityTag(string content, string name, string? value)
    {
        var sanitized = new string((value ?? string.Empty)
            .Where(character => character is not ('\r' or '\n' or ']'))
            .ToArray())
            .Trim();
        return string.IsNullOrWhiteSpace(sanitized)
            ? content
            : $"[{name}:{sanitized}]{Environment.NewLine}{content}";
    }

    private static LyricsCacheSelectionKind ReadSelectionKind(
        string content,
        string declaredSource)
    {
        var selection = ReadMetadataTag(content, "selection");
        if (string.Equals(selection, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return LyricsCacheSelectionKind.Automatic;
        }

        if (string.Equals(selection, "import", StringComparison.OrdinalIgnoreCase))
        {
            return LyricsCacheSelectionKind.Imported;
        }

        if (string.Equals(selection, "manual", StringComparison.OrdinalIgnoreCase))
        {
            return LyricsCacheSelectionKind.Manual;
        }

        // Before selection metadata existed, network-provider files were all
        // automatic cache entries. Unknown/local sources are treated as user
        // choices so an upgrade can never silently overwrite them.
        if (AutomaticLegacySources.Contains(declaredSource))
        {
            return LyricsCacheSelectionKind.Automatic;
        }

        return string.Equals(declaredSource, "手动导入", StringComparison.OrdinalIgnoreCase)
            ? LyricsCacheSelectionKind.Imported
            : LyricsCacheSelectionKind.Manual;
    }

    private static bool IsCacheRelevant(
        string content,
        TrackIdentity track,
        bool ignoreDuration = false)
    {
        var cachedTitle = ReadMetadataTag(content, "ti");
        if (!IdentityPartMatches(cachedTitle, track.Title))
        {
            return false;
        }

        var cachedArtist = ReadMetadataTag(content, "ar");
        if (!ArtistMatches(cachedArtist, track))
        {
            return false;
        }

        var cachedAlbum = ReadMetadataTag(content, "al");
        if (!IdentityPartMatches(cachedAlbum, track.Album))
        {
            return false;
        }

        var durationText = ReadMetadataTag(content, "duration-ms");
        return ignoreDuration
            || string.IsNullOrWhiteSpace(durationText)
            || track.Duration <= TimeSpan.Zero
            || !long.TryParse(
                durationText,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var cachedDurationMilliseconds)
            || Math.Abs(cachedDurationMilliseconds - track.Duration.TotalMilliseconds) <= 1_500;
    }

    private static TimeSpan? TryReadDuration(string content)
    {
        var text = ReadMetadataTag(content, "duration-ms");
        return long.TryParse(
            text,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var milliseconds)
            && milliseconds > 0
                ? TimeSpan.FromMilliseconds(milliseconds)
                : null;
    }

    private sealed record RememberedSelectionCandidate(
        LyricsCacheEntry Entry,
        TimeSpan? Duration,
        string SourceTrackId,
        DateTime LastWriteTimeUtc);

    private static bool IdentityPartMatches(string? cached, string requested) =>
        string.IsNullOrWhiteSpace(cached)
        || string.IsNullOrWhiteSpace(requested)
        || string.Equals(
            TrackIdentity.NormalizeForMatching(cached),
            TrackIdentity.NormalizeForMatching(requested),
            StringComparison.Ordinal);

    private static bool ArtistMatches(string? cachedArtist, TrackIdentity track)
    {
        if (IdentityPartMatches(cachedArtist, track.Artist))
        {
            return true;
        }

        // Older Apple Music metadata sometimes placed "artist — album" in
        // the artist field. Keep that one known migration path without
        // weakening release checks for other players.
        return IsAppleMusicTrack(track)
            && !string.IsNullOrWhiteSpace(cachedArtist)
            && !string.IsNullOrWhiteSpace(track.Album)
            && string.Equals(
                TrackIdentity.NormalizeForMatching(cachedArtist),
                TrackIdentity.NormalizeForMatching($"{track.Artist} — {track.Album}"),
                StringComparison.Ordinal);
    }

    private static string? ReadMetadataTag(string content, string name)
    {
        var prefix = $"[{name}:";
        foreach (var rawLine in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && line.EndsWith(']'))
            {
                return line[prefix.Length..^1].Trim();
            }
        }

        return null;
    }

}
