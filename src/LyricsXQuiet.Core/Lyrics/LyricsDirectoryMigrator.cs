using System.Security.Cryptography;

namespace LyricsXQuiet.Core.Lyrics;

public sealed record LyricsDirectoryMigrationResult(
    string SourceDirectory,
    string DestinationDirectory,
    int FileCount,
    bool Changed,
    bool SourceRemoved,
    string? CleanupError = null,
    bool SourceRetainedForSafety = false);

public static class LyricsDirectoryMigrator
{
    public static LyricsDirectoryMigrationResult Migrate(
        string sourceDirectory,
        string destinationDirectory)
    {
        var source = NormalizeDirectory(sourceDirectory, nameof(sourceDirectory));
        var destination = NormalizeDirectory(destinationDirectory, nameof(destinationDirectory));

        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        {
            LyricsStorage.EnsureOwnedDirectory(destination);
            return new LyricsDirectoryMigrationResult(source, destination, 0, false, true);
        }

        RejectDriveRoot(source, "旧歌词文件夹");
        RejectDriveRoot(destination, "新歌词文件夹");
        if (IsDescendant(destination, source))
        {
            throw new ArgumentException(
                "新歌词文件夹不能放在旧歌词文件夹内部，请选择其他位置。",
                nameof(destinationDirectory));
        }

        if (IsDescendant(source, destination))
        {
            throw new ArgumentException(
                "新歌词文件夹不能包含旧歌词文件夹，请选择一个独立位置。",
                nameof(destinationDirectory));
        }

        if (File.Exists(source))
        {
            throw new IOException("旧歌词文件夹路径现在指向一个文件，无法迁移。");
        }

        if (File.Exists(destination))
        {
            throw new IOException("选择的新位置现在指向一个文件，请重新选择文件夹。");
        }

        if (!Directory.Exists(source))
        {
            LyricsStorage.EnsureOwnedDirectory(destination);
            return new LyricsDirectoryMigrationResult(source, destination, 0, true, true);
        }

        var sourceOwned = LyricsStorage.IsOwnedDirectory(source);
        var tree = ReadSourceTree(source, sourceOwned);
        var destinationExisted = Directory.Exists(destination);
        Directory.CreateDirectory(destination);

        var createdFiles = new List<string>();
        var createdDirectories = new List<string>();
        try
        {
            PreflightDestination(destination, tree);
            foreach (var relativeDirectory in tree.Directories
                         .OrderBy(path => path.Length))
            {
                EnsureDirectory(
                    Path.Combine(destination, relativeDirectory),
                    createdDirectories);
            }

            foreach (var file in tree.Files)
            {
                var targetPath = Path.Combine(destination, file.RelativePath);
                if (File.Exists(targetPath))
                {
                    continue;
                }

                EnsureDirectory(Path.GetDirectoryName(targetPath)!, createdDirectories);
                CopyAndVerify(file.FullPath, targetPath);
                createdFiles.Add(targetPath);
            }

            foreach (var file in tree.Files)
            {
                var targetPath = Path.Combine(destination, file.RelativePath);
                if (!FilesAreEqual(file.FullPath, targetPath))
                {
                    throw new IOException($"迁移后校验失败：{file.RelativePath}");
                }
            }

            LyricsStorage.EnsureOwnedDirectory(destination);
        }
        catch
        {
            RollBackDestination(
                destination,
                destinationExisted,
                createdFiles,
                createdDirectories);
            throw;
        }

        try
        {
            if (!sourceOwned)
            {
                return new LyricsDirectoryMigrationResult(
                    source,
                    destination,
                    tree.Files.Count,
                    true,
                    false,
                    "旧位置不是由词间创建的专用歌词文件夹；为避免误删个人文件，原目录和原文件均已保留。",
                    true);
            }

            DeleteSourceTree(source, destination, tree);
            var sourceRemoved = !Directory.Exists(source);
            return new LyricsDirectoryMigrationResult(
                source,
                destination,
                tree.Files.Count,
                true,
                sourceRemoved,
                sourceRemoved
                    ? null
                    : "旧目录中仍有非歌词文件；词间只清理了已验证的歌词缓存，其余内容已安全保留。",
                !sourceRemoved);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return new LyricsDirectoryMigrationResult(
                source,
                destination,
                tree.Files.Count,
                true,
                false,
                exception.Message);
        }
    }

    private static string NormalizeDirectory(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("文件夹路径不能为空。", parameterName);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void RejectDriveRoot(string path, string description)
    {
        var root = Path.GetPathRoot(path);
        if (!string.IsNullOrWhiteSpace(root)
            && string.Equals(
                Path.TrimEndingDirectorySeparator(path),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{description}不能使用磁盘根目录。");
        }
    }

    private static bool IsDescendant(string path, string potentialParent)
    {
        var relative = Path.GetRelativePath(potentialParent, path);
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, ".", StringComparison.Ordinal)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static SourceTree ReadSourceTree(string source, bool includeEmptyDirectories)
    {
        var directories = new List<string>();
        var files = new List<SourceFile>();
        var pending = new Stack<string>();
        pending.Push(source);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new IOException(
                        $"歌词文件夹中包含链接，无法安全迁移：{Path.GetRelativePath(source, entry)}");
                }

                var relative = Path.GetRelativePath(source, entry);
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (includeEmptyDirectories)
                    {
                        directories.Add(relative);
                    }

                    pending.Push(entry);
                }
                else if (string.Equals(
                             Path.GetExtension(entry),
                             ".lrc",
                             StringComparison.OrdinalIgnoreCase)
                         || includeEmptyDirectories
                         && IsLyricsCleanupManifest(relative))
                {
                    files.Add(new SourceFile(entry, relative));
                    AddParentDirectories(relative, directories);
                }
            }
        }

        return new SourceTree(directories, files);
    }

    private static bool IsLyricsCleanupManifest(string relativePath)
    {
        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 3
            && string.Equals(
                segments[0],
                LyricsCacheOrganizer.RecycleDirectoryName,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                segments[^1],
                LyricsCacheOrganizer.ManifestFileName,
                StringComparison.OrdinalIgnoreCase);
    }

    private static void AddParentDirectories(string relativeFile, ICollection<string> directories)
    {
        var parent = Path.GetDirectoryName(relativeFile);
        while (!string.IsNullOrWhiteSpace(parent) && parent != ".")
        {
            if (!directories.Contains(parent, StringComparer.OrdinalIgnoreCase))
            {
                directories.Add(parent);
            }

            parent = Path.GetDirectoryName(parent);
        }
    }

    private static void PreflightDestination(string destination, SourceTree tree)
    {
        foreach (var relativeDirectory in tree.Directories)
        {
            var targetPath = Path.Combine(destination, relativeDirectory);
            if (File.Exists(targetPath))
            {
                throw new IOException($"新位置存在同名文件，无法合并：{relativeDirectory}");
            }
        }

        foreach (var file in tree.Files)
        {
            var targetPath = Path.Combine(destination, file.RelativePath);
            if (Directory.Exists(targetPath))
            {
                throw new IOException($"新位置存在同名文件夹，无法合并：{file.RelativePath}");
            }

            if (File.Exists(targetPath) && !FilesAreEqual(file.FullPath, targetPath))
            {
                throw new IOException($"新位置存在内容不同的同名文件：{file.RelativePath}");
            }
        }
    }

    private static void EnsureDirectory(string path, ICollection<string> createdDirectories)
    {
        if (Directory.Exists(path))
        {
            return;
        }

        var missing = new Stack<string>();
        var current = path;
        while (!string.IsNullOrWhiteSpace(current) && !Directory.Exists(current))
        {
            missing.Push(current);
            current = Path.GetDirectoryName(current);
        }

        while (missing.Count > 0)
        {
            var directory = missing.Pop();
            Directory.CreateDirectory(directory);
            createdDirectories.Add(directory);
        }
    }

    private static void CopyAndVerify(string sourcePath, string targetPath)
    {
        var targetDirectory = Path.GetDirectoryName(targetPath)!;
        var temporaryPath = Path.Combine(
            targetDirectory,
            $".lyrveil-migrate-{Guid.NewGuid():N}.tmp");

        try
        {
            File.Copy(sourcePath, temporaryPath, false);
            if (!FilesAreEqual(sourcePath, temporaryPath))
            {
                throw new IOException($"复制文件后校验失败：{Path.GetFileName(sourcePath)}");
            }

            File.Move(temporaryPath, targetPath, false);
            try
            {
                File.SetLastWriteTimeUtc(targetPath, File.GetLastWriteTimeUtc(sourcePath));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // File contents are already verified; timestamp preservation is optional.
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                }
            }
        }
    }

    private static bool FilesAreEqual(string firstPath, string secondPath)
    {
        var first = new FileInfo(firstPath);
        var second = new FileInfo(secondPath);
        if (!first.Exists || !second.Exists || first.Length != second.Length)
        {
            return false;
        }

        using var firstStream = first.OpenRead();
        using var secondStream = second.OpenRead();
        var firstHash = SHA256.HashData(firstStream);
        var secondHash = SHA256.HashData(secondStream);
        return CryptographicOperations.FixedTimeEquals(firstHash, secondHash);
    }

    private static void DeleteSourceTree(
        string source,
        string destination,
        SourceTree tree)
    {
        foreach (var file in tree.Files)
        {
            if (!File.Exists(file.FullPath))
            {
                continue;
            }

            var targetPath = Path.Combine(destination, file.RelativePath);
            if (!FilesAreEqual(file.FullPath, targetPath))
            {
                throw new IOException(
                    $"迁移期间文件发生了变化，已保留旧文件：{file.RelativePath}");
            }

            ClearReadOnlyAttribute(file.FullPath);
            File.Delete(file.FullPath);
        }

        foreach (var relativeDirectory in tree.Directories
                     .OrderByDescending(path => path.Length))
        {
            var directory = Path.Combine(source, relativeDirectory);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                ClearReadOnlyAttribute(directory);
                Directory.Delete(directory, false);
            }
        }

        if (Directory.Exists(source))
        {
            var marker = Path.Combine(source, LyricsStorage.OwnershipMarkerFileName);
            if (File.Exists(marker))
            {
                ClearReadOnlyAttribute(marker);
                File.Delete(marker);
            }

            if (!Directory.EnumerateFileSystemEntries(source).Any())
            {
                ClearReadOnlyAttribute(source);
                Directory.Delete(source, false);
            }
        }
    }

    private static void ClearReadOnlyAttribute(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static void RollBackDestination(
        string destination,
        bool destinationExisted,
        IEnumerable<string> createdFiles,
        IEnumerable<string> createdDirectories)
    {
        foreach (var file in createdFiles.Reverse())
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
            }
        }

        foreach (var directory in createdDirectories
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(path => path.Length))
        {
            try
            {
                Directory.Delete(directory, false);
            }
            catch
            {
            }
        }

        if (!destinationExisted)
        {
            try
            {
                Directory.Delete(destination, false);
            }
            catch
            {
            }
        }
    }

    private sealed record SourceFile(string FullPath, string RelativePath);

    private sealed record SourceTree(
        IReadOnlyList<string> Directories,
        IReadOnlyList<SourceFile> Files);
}
