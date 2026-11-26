using System.ComponentModel;
using System.Diagnostics;
using LyricsXQuiet.Core.Playback;

namespace LyricsXQuiet.Platform.Windows.Companion;

public static class CompanionUpdateDetectionService
{
    private static readonly IReadOnlyDictionary<string, string[]> KnownUpdaterTokens =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["qqmusic"] = ["qqmusicupdate", "qqmusicupdater", "qqmusicsetup", "qqmusicinstaller", "qmupdate"],
            ["netease"] =
                ["cloudmusicupdate", "cloudmusicupdater", "cloudmusicsetup", "cloudmusicinstaller", "orpheusinstall", "appupdate"],
            ["applemusic"] = ["applemusicupdate", "applemusicupdater", "applemusicsetup", "applemusicinstaller"],
            ["spotify"] = ["spotifyupdate", "spotifyupdater", "spotifysetup", "spotifyinstaller", "spotifylauncher"]
        };

    private static readonly string[] GenericUpdaterTokens =
        ["update", "updater", "setup", "install", "patch", "repair", "launcher"];

    public static bool IsUpdateActivityPresent(
        MusicPlayerDefinition player,
        string? playerExecutablePath)
    {
        KnownUpdaterTokens.TryGetValue(player.Id, out var knownTokens);
        var relatedDirectory = ResolveRelatedDirectory(playerExecutablePath);

        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var normalizedName = Normalize(process.ProcessName);
                    if (knownTokens?.Any(token => normalizedName.Contains(token, StringComparison.Ordinal)) == true)
                    {
                        return true;
                    }

                    if (relatedDirectory is null
                        || !GenericUpdaterTokens.Any(token =>
                            normalizedName.Contains(token, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    var processPath = TryGetProcessPath(process);
                    if (processPath is not null
                        && IsWithinDirectory(processPath, relatedDirectory))
                    {
                        return true;
                    }
                }
                catch (Exception exception) when (
                    exception is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                }
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        return false;
    }

    private static string? ResolveRelatedDirectory(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        try
        {
            var executableDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
            if (string.IsNullOrWhiteSpace(executableDirectory))
            {
                return null;
            }

            return executableDirectory;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var normalizedDirectory = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static string Normalize(string value) => new(
        value.Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
}
