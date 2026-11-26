using System.Reflection;
using System.Runtime.InteropServices;
using System.Diagnostics;
using LyricsXQuiet.Core.Playback;
using LyricsXQuiet.Platform.Windows.Interop;

namespace LyricsXQuiet.Platform.Windows.Companion;

public static class CompanionShortcutService
{
    public sealed record RestoreResult(int RestoredCount, int SkippedCount);

    public static string CreateDesktopShortcut(string playerPath, string? appPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerPath);
        var resolvedPlayerPath = Path.GetFullPath(playerPath);
        if (!File.Exists(resolvedPlayerPath)
            || !string.Equals(Path.GetExtension(resolvedPlayerPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("没有找到有效的播放器程序。", resolvedPlayerPath);
        }

        var supportedPlayer = MusicPlayerCatalog.Identify(Path.GetFileNameWithoutExtension(resolvedPlayerPath));
        if (supportedPlayer is null)
        {
            throw new InvalidOperationException(
                "伴随启动支持 QQ 音乐、网易云音乐、酷狗音乐、Apple Music 和 Spotify。");
        }

        var resolvedAppPath = appPath ?? ResolveAppPath();
        if (!File.Exists(resolvedAppPath))
        {
            throw new FileNotFoundException("没有找到词间应用程序。", resolvedAppPath);
        }

        var playerName = supportedPlayer.DisplayName;
        var shortcutName = SanitizeFileName(GetShortcutDisplayName(supportedPlayer)) + ".lnk";
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var shortcutPath = Path.Combine(desktop, shortcutName);
        BackupOriginalShortcut(shortcutPath, supportedPlayer.Id);
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new PlatformNotSupportedException("当前系统不支持创建 Windows 快捷方式。");
        var shellObject = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("无法启动 Windows 快捷方式服务。");
        object? shortcutObject = null;

        try
        {
            dynamic shell = shellObject;
            shortcutObject = shell.CreateShortcut(shortcutPath);
            dynamic shortcut = shortcutObject;
            shortcut.TargetPath = resolvedAppPath;
            var packagedAppUserModelId = GetPackagedAppUserModelId(
                supportedPlayer,
                resolvedPlayerPath);
            var arguments = packagedAppUserModelId is null
                ? $"--companion \"{resolvedPlayerPath}\""
                : $"--companion-aumid \"{packagedAppUserModelId}\" --companion-player-path \"{resolvedPlayerPath}\"";
            if (string.Equals(supportedPlayer.Id, "netease", StringComparison.OrdinalIgnoreCase))
            {
                arguments += " --enable-netease-progress-bridge";
            }

            shortcut.Arguments = arguments;
            shortcut.WorkingDirectory = Path.GetDirectoryName(resolvedAppPath)!;
            shortcut.IconLocation = $"{resolvedPlayerPath},0";
            shortcut.Description = $"启动 {playerName}";
            shortcut.WindowStyle = 1;
            shortcut.Save();
        }
        finally
        {
            ReleaseComObject(shortcutObject);
            ReleaseComObject(shellObject);
        }

        if (supportedPlayer.Id == "netease")
            PlayerShortcutIdentity.Apply(shortcutPath, PlayerShortcutIdentity.Resolve(resolvedPlayerPath));
        return shortcutPath;
    }

    public static RestoreResult RestoreOriginalShortcuts()
    {
        var restored = 0;
        var skipped = 0;
        foreach (var player in MusicPlayerCatalog.SupportedPlayers)
        {
            var backupPath = GetBackupPath(player.Id);
            if (!File.Exists(backupPath))
            {
                continue;
            }

            var shortcutPath = GetDesktopShortcutPath(player);
            if (File.Exists(shortcutPath) && !IsManagedShortcut(shortcutPath))
            {
                skipped++;
                continue;
            }

            File.Copy(backupPath, shortcutPath, true);
            File.Delete(backupPath);
            restored++;
        }

        return new RestoreResult(restored, skipped);
    }

    private static string GetShortcutDisplayName(MusicPlayerDefinition player) => player.Id switch
    {
        "qqmusic" => "QQ音乐",
        _ => player.DisplayName
    };

    private static void BackupOriginalShortcut(string shortcutPath, string playerId)
    {
        if (!File.Exists(shortcutPath))
        {
            return;
        }

        var backupDirectory = GetBackupDirectory();
        Directory.CreateDirectory(backupDirectory);
        var backupPath = GetBackupPath(playerId);
        if (!File.Exists(backupPath))
        {
            File.Copy(shortcutPath, backupPath);
        }
    }

    private static string GetDesktopShortcutPath(MusicPlayerDefinition player) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            SanitizeFileName(GetShortcutDisplayName(player)) + ".lnk");

    private static string GetBackupDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LyricsXQuiet",
        "ShortcutBackups");

    private static string GetBackupPath(string playerId) =>
        Path.Combine(GetBackupDirectory(), SanitizeFileName(playerId) + ".lnk");

    private static bool IsManagedShortcut(string shortcutPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        var shellObject = shellType is null ? null : Activator.CreateInstance(shellType);
        object? shortcutObject = null;
        try
        {
            if (shellObject is null)
            {
                return false;
            }

            dynamic shell = shellObject;
            shortcutObject = shell.CreateShortcut(shortcutPath);
            dynamic shortcut = shortcutObject;
            var arguments = Convert.ToString(shortcut.Arguments) ?? string.Empty;
            var target = Convert.ToString(shortcut.TargetPath) ?? string.Empty;
            return arguments.Contains("--companion", StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    Path.GetFileName(target),
                    "LyricsXQuiet.App.exe",
                    StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ReleaseComObject(shortcutObject);
            ReleaseComObject(shellObject);
        }
    }

    private static string? GetPackagedAppUserModelId(
        MusicPlayerDefinition player,
        string executablePath)
    {
        if (!executablePath.Contains(
            $"{Path.DirectorySeparatorChar}WindowsApps{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return player.Id switch
        {
            "applemusic" => "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App",
            "spotify" => "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
            _ => null
        };
    }

    internal static string ResolveAppPath()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var nativeProcessPath = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(nativeProcessPath)
                && string.Equals(
                    Path.GetExtension(nativeProcessPath),
                    ".exe",
                    StringComparison.OrdinalIgnoreCase)
                && File.Exists(nativeProcessPath))
            {
                // Environment.ProcessPath can retain a development sandbox's
                // virtual path. MainModule is the executable Windows actually
                // launched, so desktop shortcuts remain valid outside that run.
                return Path.GetFullPath(nativeProcessPath);
            }
        }
        catch
        {
            // Fall through to the managed entry-point paths below.
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath)
            && string.Equals(Path.GetExtension(processPath), ".exe", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        var assemblyPath = Assembly.GetEntryAssembly()?.Location
            ?? throw new InvalidOperationException("无法定位词间应用程序。");
        var appHostPath = Path.ChangeExtension(assemblyPath, ".exe");
        return File.Exists(appHostPath) ? appHostPath : assemblyPath;
    }

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(character =>
            invalidCharacters.Contains(character) ? '_' : character).ToArray());
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }
}
