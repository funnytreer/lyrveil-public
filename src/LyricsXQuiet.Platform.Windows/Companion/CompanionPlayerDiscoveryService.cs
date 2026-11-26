using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;
using LyricsXQuiet.Core.Playback;

namespace LyricsXQuiet.Platform.Windows.Companion;

public sealed record CompanionPlayerInstallation(
    MusicPlayerDefinition Player,
    string? ExecutablePath)
{
    public bool IsDetected => !string.IsNullOrWhiteSpace(ExecutablePath);
}

public static class CompanionPlayerDiscoveryService
{
    public static IReadOnlyList<CompanionPlayerInstallation> Discover()
    {
        return MusicPlayerCatalog.SupportedPlayers
            .Select(player => new CompanionPlayerInstallation(player, FindExecutable(player)))
            .ToArray();
    }

    public static bool IsMatchingPlayer(string path, MusicPlayerDefinition player)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        return string.Equals(
            MusicPlayerCatalog.Identify(Path.GetFileNameWithoutExtension(path))?.Id,
            player.Id,
            StringComparison.OrdinalIgnoreCase);
    }

    public static string? FindExecutable(MusicPlayerDefinition player)
    {
        foreach (var candidate in GetRunningProcessPaths(player)
            .Concat(GetRegistryPaths(player))
            .Concat(GetKnownPaths(player))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsMatchingPlayer(candidate, player))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static IEnumerable<string> GetRunningProcessPaths(MusicPlayerDefinition player)
    {
        var processes = Process.GetProcessesByName(player.ProcessName);
        try
        {
            foreach (var process in processes)
            {
                string? path = null;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch (Exception exception) when (
                    exception is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                }

                if (!string.IsNullOrWhiteSpace(path))
                {
                    yield return path;
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
    }

    private static IEnumerable<string> GetRegistryPaths(MusicPlayerDefinition player)
    {
        var executableName = player.ProcessName + ".exe";
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                RegistryKey? baseKey = null;
                RegistryKey? appPathKey = null;
                try
                {
                    baseKey = RegistryKey.OpenBaseKey(hive, view);
                    appPathKey = baseKey.OpenSubKey(
                        $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{executableName}");
                    if (appPathKey?.GetValue(null) is string value)
                    {
                        var path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            yield return path;
                        }
                    }
                }
                finally
                {
                    appPathKey?.Dispose();
                    baseKey?.Dispose();
                }
            }
        }
    }

    private static IEnumerable<string> GetKnownPaths(MusicPlayerDefinition player)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var relativePaths = player.Id switch
        {
            "qqmusic" => new[]
            {
                @"Tencent\QQMusic\QQMusic.exe",
                @"QQMusic\QQMusic.exe",
                @"Programs\QQMusic\QQMusic.exe"
            },
            "netease" => new[]
            {
                @"NetEase\CloudMusic\cloudmusic.exe",
                @"Netease\CloudMusic\cloudmusic.exe",
                @"Programs\NetEase\CloudMusic\cloudmusic.exe"
            },
            "kugou" => new[]
            {
                @"KuGou\KGMusic\KuGou.exe",
                @"KGMusic\KuGou.exe",
                @"Programs\KuGou\KuGou.exe",
                @"Programs\KGMusic\KuGou.exe"
            },
            "spotify" => new[]
            {
                @"Spotify\Spotify.exe",
                @"Programs\Spotify\Spotify.exe",
                @"Microsoft\WindowsApps\Spotify.exe"
            },
            "applemusic" => new[]
            {
                @"Programs\Apple Music\AppleMusic.exe",
                @"Microsoft\WindowsApps\AppleMusic.exe"
            },
            _ => Array.Empty<string>()
        };

        foreach (var root in new[] { programFiles, programFilesX86, localAppData, roamingAppData }
            .Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            foreach (var relativePath in relativePaths)
            {
                yield return Path.Combine(root, relativePath);
            }
        }
    }
}
