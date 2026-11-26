using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LyricsXQuiet.Core.Playback;

namespace LyricsXQuiet.Core.Lyrics;

public enum LyricsCacheCleanupReason
{
    SupersededByExplicitSelection,
    ExactDuplicate,
    UnreadableAutomaticCache
}

public sealed record LyricsCacheCleanupItem(
    string FileName,
    long Length,
    DateTime LastWriteTimeUtc,
    string ContentHash,
    LyricsCacheCleanupReason Reason)
{
    public string ReasonText => Reason switch
    {
        LyricsCacheCleanupReason.SupersededByExplicitSelection => "已被手选或导入的歌词替代",
        LyricsCacheCleanupReason.ExactDuplicate => "歌词内容完全重复",
        _ => "无法读取的自动缓存"
    };
}

public sealed record LyricsCacheCleanupPlan(
    int ScannedFileCount,
    int ManagedFileCount,
    IReadOnlyList<LyricsCacheCleanupItem> Items)
{
    public long TotalBytes => Items.Sum(item => item.Length);
}

public sealed record LyricsCacheCleanupResult(
    int MovedFileCount,
    int SkippedFileCount,
    long MovedBytes,
    string? BackupDirectory);

public sealed record LyricsCacheRestorePlan(
    string BatchDirectory,
    DateTime CreatedUtc,
    IReadOnlyList<LyricsCacheRestoreItem> Items)
{
    public long TotalBytes => Items.Sum(item => item.Length);
}

public sealed record LyricsCacheRestoreItem(
    string FileName,
    long Length,
    LyricsCacheCleanupReason Reason);

public sealed record LyricsCacheRestoreResult(
    int RestoredFileCount,
    int ConflictFileCount,
    int MissingFileCount);

internal static partial class LyricsCacheOrganizer
{
    internal const string RecycleDirectoryName = ".lyrveil-recycle";
    internal const string ManifestFileName = "manifest.json";

    private const int ManifestSchemaVersion = 1;
    private const long MaximumManagedFileSize = 8 * 1024 * 1024;
    public static LyricsCacheCleanupPlan Scan(string directory)
    {
        var root = RequireOwnedDirectory(directory);
        var files = Directory.EnumerateFiles(root, "*.lrc", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var entries = new List<CacheFile>();
        foreach (var path in files)
        {
            var entry = TryReadManagedFile(path);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        var candidates = new Dictionary<string, LyricsCacheCleanupReason>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries.Where(entry =>
                     entry.SelectionKind == LyricsCacheSelectionKind.Automatic
                     && !entry.IsReadable))
        {
            candidates[entry.Path] = LyricsCacheCleanupReason.UnreadableAutomaticCache;
        }

        foreach (var identityGroup in entries
                     .Where(entry => entry.IsReadable && !string.IsNullOrWhiteSpace(entry.IdentityKey))
                     .GroupBy(entry => entry.IdentityKey, StringComparer.Ordinal))
        {
            var explicitSelections = identityGroup
                .Where(entry => entry.SelectionKind != LyricsCacheSelectionKind.Automatic)
                .ToArray();
            var automaticEntries = identityGroup
                .Where(entry => entry.SelectionKind == LyricsCacheSelectionKind.Automatic)
                .ToArray();

            if (explicitSelections.Length == 1)
            {
                foreach (var automatic in automaticEntries)
                {
                    candidates[automatic.Path] =
                        LyricsCacheCleanupReason.SupersededByExplicitSelection;
                }
            }
            else if (explicitSelections.Length > 1)
            {
                foreach (var automatic in automaticEntries.Where(automatic =>
                             explicitSelections.Any(explicitSelection =>
                                 RecordingsAreCompatible(automatic, explicitSelection))))
                {
                    candidates[automatic.Path] =
                        LyricsCacheCleanupReason.SupersededByExplicitSelection;
                }
            }

            foreach (var contentGroup in identityGroup
                         .Where(entry => !string.IsNullOrWhiteSpace(entry.LyricContentHash))
                         .GroupBy(entry => entry.LyricContentHash, StringComparer.Ordinal))
            {
                var keepers = new List<CacheFile>();
                foreach (var entry in contentGroup
                             .OrderByDescending(GetKeeperScore)
                             .ThenByDescending(entry => entry.LastWriteTimeUtc))
                {
                    if (entry.SelectionKind != LyricsCacheSelectionKind.Automatic)
                    {
                        keepers.Add(entry);
                        continue;
                    }

                    if (candidates.ContainsKey(entry.Path))
                    {
                        continue;
                    }

                    if (keepers.Any(keeper => RecordingsAreCompatible(entry, keeper)))
                    {
                        candidates[entry.Path] = LyricsCacheCleanupReason.ExactDuplicate;
                    }
                    else
                    {
                        keepers.Add(entry);
                    }
                }
            }
        }

        var items = entries
            .Where(entry => candidates.TryGetValue(entry.Path, out _))
            .Select(entry => new LyricsCacheCleanupItem(
                Path.GetFileName(entry.Path),
                entry.Length,
                entry.LastWriteTimeUtc,
                entry.FullContentHash,
                candidates[entry.Path]))
            .OrderBy(item => item.FileName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return new LyricsCacheCleanupPlan(files.Length, entries.Count, items);
    }

    public static LyricsCacheCleanupResult Apply(
        string directory,
        LyricsCacheCleanupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var root = RequireOwnedDirectory(directory);
        if (plan.Items.Count == 0)
        {
            return new LyricsCacheCleanupResult(0, 0, 0, null);
        }

        var recycleRoot = Path.Combine(root, RecycleDirectoryName);
        RejectReparsePoint(recycleRoot);
        Directory.CreateDirectory(recycleRoot);
        TryMarkHidden(recycleRoot);
        var batchDirectory = Path.Combine(
            recycleRoot,
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(batchDirectory);

        var manifestEntries = plan.Items.Select(item => new CleanupManifestEntry(
            item.FileName,
            item.FileName,
            item.Length,
            item.ContentHash,
            item.Reason)).ToArray();
        var manifest = new CleanupManifest(
            ManifestSchemaVersion,
            DateTime.UtcNow,
            manifestEntries);
        WriteManifest(batchDirectory, manifest);

        var moved = 0;
        var skipped = 0;
        long movedBytes = 0;
        foreach (var item in plan.Items)
        {
            var source = ResolveDirectChild(root, item.FileName);
            var destination = ResolveDirectChild(batchDirectory, item.FileName);
            if (!File.Exists(source)
                || File.Exists(destination)
                || !FingerprintMatches(source, item))
            {
                skipped++;
                continue;
            }

            try
            {
                File.Move(source, destination, false);
                moved++;
                movedBytes += item.Length;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }

        if (moved == 0)
        {
            TryDeleteBatch(batchDirectory);
            return new LyricsCacheCleanupResult(0, skipped, 0, null);
        }

        return new LyricsCacheCleanupResult(moved, skipped, movedBytes, batchDirectory);
    }

    public static LyricsCacheRestorePlan? GetLatestRestorePlan(string directory)
    {
        var root = RequireOwnedDirectory(directory);
        var recycleRoot = Path.Combine(root, RecycleDirectoryName);
        if (!Directory.Exists(recycleRoot))
        {
            return null;
        }

        RejectReparsePoint(recycleRoot);

        foreach (var batchDirectory in Directory.EnumerateDirectories(recycleRoot)
                     .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (IsReparsePoint(batchDirectory))
            {
                continue;
            }

            var manifest = TryReadManifest(batchDirectory);
            if (manifest is null)
            {
                continue;
            }

            var items = manifest.Entries
                .Where(entry => IsSafeFileName(entry.OriginalFileName)
                    && IsSafeFileName(entry.BackupFileName))
                .Where(entry => File.Exists(Path.Combine(batchDirectory, entry.BackupFileName)))
                .Select(entry => new LyricsCacheRestoreItem(
                    entry.OriginalFileName,
                    entry.Length,
                    entry.Reason))
                .ToArray();
            if (items.Length > 0)
            {
                return new LyricsCacheRestorePlan(
                    batchDirectory,
                    manifest.CreatedUtc,
                    items);
            }
        }

        return null;
    }

    public static LyricsCacheRestoreResult Restore(
        string directory,
        LyricsCacheRestorePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var root = RequireOwnedDirectory(directory);
        var recycleRoot = Path.Combine(root, RecycleDirectoryName);
        RejectReparsePoint(recycleRoot);
        var batchDirectory = Path.GetFullPath(plan.BatchDirectory);
        if (!IsDescendantOf(batchDirectory, recycleRoot))
        {
            throw new IOException("恢复清单不属于当前歌词文件夹。");
        }

        RejectReparsePoint(batchDirectory);

        var manifest = TryReadManifest(batchDirectory)
            ?? throw new IOException("恢复清单已损坏或不存在。");
        var restored = 0;
        var conflicts = 0;
        var missing = 0;
        foreach (var entry in manifest.Entries)
        {
            if (!IsSafeFileName(entry.OriginalFileName)
                || !IsSafeFileName(entry.BackupFileName))
            {
                missing++;
                continue;
            }

            var source = ResolveDirectChild(batchDirectory, entry.BackupFileName);
            var destination = ResolveDirectChild(root, entry.OriginalFileName);
            if (!File.Exists(source))
            {
                missing++;
                continue;
            }

            if (!BackupFingerprintMatches(source, entry))
            {
                missing++;
                continue;
            }

            if (File.Exists(destination))
            {
                conflicts++;
                continue;
            }

            try
            {
                File.Move(source, destination, false);
                restored++;
            }
            catch (IOException)
            {
                conflicts++;
            }
            catch (UnauthorizedAccessException)
            {
                conflicts++;
            }
        }

        if (!manifest.Entries.Any(entry =>
                IsSafeFileName(entry.BackupFileName)
                && File.Exists(Path.Combine(batchDirectory, entry.BackupFileName))))
        {
            TryDeleteBatch(batchDirectory);
        }

        return new LyricsCacheRestoreResult(restored, conflicts, missing);
    }

    private static CacheFile? TryReadManagedFile(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists
            || file.Length <= 0
            || file.Length > MaximumManagedFileSize
            || file.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || !ManagedCacheFileNameRegex().IsMatch(file.Name))
        {
            return null;
        }

        string content;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
            content = Encoding.UTF8.GetString(bytes);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var selectionText = ReadMetadataTag(content, "selection");
        var source = ReadMetadataTag(content, "source") ?? string.Empty;
        var selectionKind = ParseSelectionKind(selectionText, source);
        if (selectionKind is null)
        {
            return null;
        }

        var (fallbackTitle, fallbackArtist) = ReadIdentityFromFileName(file.Name);
        var title = ReadMetadataTag(content, "ti") ?? fallbackTitle;
        var artist = ReadMetadataTag(content, "ar") ?? fallbackArtist;
        var album = ReadMetadataTag(content, "al") ?? string.Empty;
        var identityKey = string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)
            ? string.Empty
            : string.Join('|',
                TrackIdentity.NormalizeForMatching(title),
                TrackIdentity.NormalizeForMatching(artist),
                TrackIdentity.NormalizeForMatching(album));
        var duration = TryReadPositiveLong(content, "duration-ms");
        var sourceTrackId = ReadMetadataTag(content, "source-track-id") ?? string.Empty;
        var document = LrcParser.Parse(content, source);
        var readable = document.HasSynchronizedLines || document.IsInstrumental;
        var lyricHash = readable
            ? HashText(LrcSerializer.Serialize(document))
            : string.Empty;
        return new CacheFile(
            path,
            file.Length,
            file.LastWriteTimeUtc,
            Convert.ToHexString(SHA256.HashData(bytes)),
            selectionKind.Value,
            identityKey,
            duration,
            sourceTrackId,
            readable,
            lyricHash,
            !string.IsNullOrWhiteSpace(ReadMetadataTag(content, "cache-key-version")));
    }

    private static LyricsCacheSelectionKind? ParseSelectionKind(
        string? selection,
        string source)
    {
        if (string.Equals(selection, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return LyricsCacheSelectionKind.Automatic;
        }

        if (string.Equals(selection, "manual", StringComparison.OrdinalIgnoreCase))
        {
            return LyricsCacheSelectionKind.Manual;
        }

        if (string.Equals(selection, "import", StringComparison.OrdinalIgnoreCase))
        {
            return LyricsCacheSelectionKind.Imported;
        }

        if (!string.IsNullOrWhiteSpace(selection))
        {
            return null;
        }

        // Provider-only legacy files predate the selection tag. They may have
        // been chosen manually, so cleanup deliberately leaves them untouched.
        return string.Equals(source, "手动导入", StringComparison.OrdinalIgnoreCase)
            ? LyricsCacheSelectionKind.Imported
            : null;
    }

    private static bool RecordingsAreCompatible(CacheFile first, CacheFile second)
    {
        if (!string.IsNullOrWhiteSpace(first.SourceTrackId)
            && !string.IsNullOrWhiteSpace(second.SourceTrackId))
        {
            return string.Equals(
                first.SourceTrackId,
                second.SourceTrackId,
                StringComparison.OrdinalIgnoreCase);
        }

        if (first.DurationMilliseconds is not null
            && second.DurationMilliseconds is not null)
        {
            return Math.Abs(
                first.DurationMilliseconds.Value
                - second.DurationMilliseconds.Value) <= 1_500;
        }

        return first.DurationMilliseconds is null
            && second.DurationMilliseconds is null;
    }

    private static int GetKeeperScore(CacheFile entry)
    {
        var score = entry.SelectionKind switch
        {
            LyricsCacheSelectionKind.Imported => 100,
            LyricsCacheSelectionKind.Manual => 90,
            _ => 0
        };
        if (!string.IsNullOrWhiteSpace(entry.SourceTrackId))
        {
            score += 10;
        }

        if (entry.HasCacheVersion)
        {
            score += 5;
        }

        if (entry.DurationMilliseconds is not null)
        {
            score += 2;
        }

        return score;
    }

    private static bool FingerprintMatches(
        string path,
        LyricsCacheCleanupItem item)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists
                || file.Length != item.Length
                || file.LastWriteTimeUtc != item.LastWriteTimeUtc)
            {
                return false;
            }

            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            using var stream = file.OpenRead();
            return string.Equals(
                Convert.ToHexString(SHA256.HashData(stream)),
                item.ContentHash,
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool BackupFingerprintMatches(
        string path,
        CleanupManifestEntry entry)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists
                || file.Length != entry.Length
                || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            using var stream = file.OpenRead();
            return string.Equals(
                Convert.ToHexString(SHA256.HashData(stream)),
                entry.ContentHash,
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string RequireOwnedDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (Directory.Exists(root) && !LyricsStorage.IsOwnedDirectory(root))
        {
            throw new IOException("只能整理词间专用的歌词文件夹。");
        }

        LyricsStorage.EnsureOwnedDirectory(root);
        return root;
    }

    private static string ResolveDirectChild(string parent, string fileName)
    {
        if (!IsSafeFileName(fileName))
        {
            throw new IOException("整理清单包含不安全的文件名。");
        }

        var resolvedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var resolved = Path.GetFullPath(Path.Combine(resolvedParent, fileName));
        if (!string.Equals(
                Path.GetDirectoryName(resolved),
                resolvedParent,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("整理清单超出了允许的文件夹范围。");
        }

        return resolved;
    }

    private static bool IsSafeFileName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
        && !fileName.Contains(Path.DirectorySeparatorChar)
        && !fileName.Contains(Path.AltDirectorySeparatorChar);

    private static bool IsDescendantOf(string path, string parent)
    {
        var resolvedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var resolvedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var relative = Path.GetRelativePath(resolvedParent, resolvedPath);
        return !Path.IsPathRooted(relative)
            && relative != "."
            && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static void RejectReparsePoint(string path)
    {
        if (Directory.Exists(path) && IsReparsePoint(path))
        {
            throw new IOException("整理备份文件夹不能是链接或重解析点。");
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void WriteManifest(string batchDirectory, CleanupManifest manifest)
    {
        var path = Path.Combine(batchDirectory, ManifestFileName);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(manifest, ManifestJsonOptions),
            new UTF8Encoding(false));
        File.Move(temporaryPath, path, false);
    }

    private static CleanupManifest? TryReadManifest(string batchDirectory)
    {
        try
        {
            var manifestPath = Path.Combine(batchDirectory, ManifestFileName);
            if (!File.Exists(manifestPath)
                || new FileInfo(manifestPath).Length > 1024 * 1024)
            {
                return null;
            }

            var manifest = JsonSerializer.Deserialize<CleanupManifest>(
                File.ReadAllText(manifestPath),
                ManifestJsonOptions);
            return manifest is { SchemaVersion: ManifestSchemaVersion }
                ? manifest
                : null;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException)
        {
            return null;
        }
    }

    private static void TryDeleteBatch(string batchDirectory)
    {
        try
        {
            if (Directory.Exists(batchDirectory))
            {
                Directory.Delete(batchDirectory, true);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryMarkHidden(string directory)
    {
        try
        {
            File.SetAttributes(
                directory,
                File.GetAttributes(directory) | FileAttributes.Hidden);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static (string Title, string Artist) ReadIdentityFromFileName(string fileName)
    {
        var match = ManagedCacheFileNameRegex().Match(fileName);
        var stem = match.Success ? match.Groups["stem"].Value : string.Empty;
        var separator = stem.LastIndexOf(" - ", StringComparison.Ordinal);
        return separator <= 0 || separator >= stem.Length - 3
            ? (string.Empty, string.Empty)
            : (stem[..separator].Trim(), stem[(separator + 3)..].Trim());
    }

    private static string? ReadMetadataTag(string content, string name)
    {
        var prefix = $"[{name}:";
        foreach (var rawLine in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && line.EndsWith(']'))
            {
                return line[prefix.Length..^1].Trim();
            }
        }

        return null;
    }

    private static long? TryReadPositiveLong(string content, string tag)
    {
        var text = ReadMetadataTag(content, tag);
        return long.TryParse(
            text,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            && value > 0
                ? value
                : null;
    }

    private static string HashText(string text) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal))));

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false
    };

    private sealed record CacheFile(
        string Path,
        long Length,
        DateTime LastWriteTimeUtc,
        string FullContentHash,
        LyricsCacheSelectionKind SelectionKind,
        string IdentityKey,
        long? DurationMilliseconds,
        string SourceTrackId,
        bool IsReadable,
        string LyricContentHash,
        bool HasCacheVersion);

    private sealed record CleanupManifest(
        int SchemaVersion,
        DateTime CreatedUtc,
        IReadOnlyList<CleanupManifestEntry> Entries);

    private sealed record CleanupManifestEntry(
        string OriginalFileName,
        string BackupFileName,
        long Length,
        string ContentHash,
        LyricsCacheCleanupReason Reason);

    [GeneratedRegex(@"^(?<stem>.+) \[[0-9a-fA-F]{10,20}\]\.lrc$", RegexOptions.CultureInvariant)]
    private static partial Regex ManagedCacheFileNameRegex();
}
