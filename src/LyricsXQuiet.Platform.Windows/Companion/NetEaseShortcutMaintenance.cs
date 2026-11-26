using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LyricsXQuiet.Core.Playback;
using LyricsXQuiet.Core.Settings;
using LyricsXQuiet.Platform.Windows.Interop;

namespace LyricsXQuiet.Platform.Windows.Companion;

public static class NetEaseShortcutMaintenance
{
    public const string DedicatedShortcutName = "网易云音乐 · 词间.lnk";

    public static bool IsEnabled(AppSettings? settings) => settings is not null
        && IsEnabled(settings.AutoRepairNetEaseSync, settings.PlayerFollowMode, settings.FollowedPlayerIds);

    internal static bool IsEnabled(bool repair, string mode, string ids) => repair && PlayerFollowModes.UsesShortcut(mode)
        && MusicPlayerCatalog.SelectForMonitoring(ids).Any(player => player.Id == "netease");

    public static int Repair()
    {
        using var mutex = new Mutex(false, "Local\\Lyrveil.NetEaseShortcutMaintenance");
        try { if (!mutex.WaitOne(0)) return 0; }
        catch (AbandonedMutexException) { /* Ownership was acquired. */ }
        try
        {
            var player = MusicPlayerCatalog.SupportedPlayers.Single(item => item.Id == "netease");
            var playerPath = CompanionPlayerDiscoveryService.FindExecutable(player);
            if (playerPath is null) return 0;
            return RepairForPaths(playerPath, CompanionShortcutService.ResolveAppPath(),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LyricsXQuiet", "ShortcutMaintenanceBackups"));
        }
        finally { mutex.ReleaseMutex(); }
    }

    // Explicit directories also let regression tests exercise real .lnk files in an isolated folder.
    public static int RepairForPaths(string playerPath, string appPath, string desktop,
        string taskbar, string backupDirectory) =>
        RepairForPaths(playerPath, appPath, desktop, taskbar, backupDirectory,
            PlayerShortcutIdentity.Resolve(Path.GetFullPath(playerPath)));

    internal static int RepairForPaths(string playerPath, string appPath, string desktop,
        string taskbar, string backupDirectory, string? playerAppId)
    {
        playerPath = Path.GetFullPath(playerPath);
        appPath = Path.GetFullPath(appPath);
        if (!File.Exists(playerPath) || !File.Exists(appPath)
            || !string.Equals(Path.GetFileName(playerPath), "cloudmusic.exe", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(appPath), "LyricsXQuiet.App.exe", StringComparison.OrdinalIgnoreCase))
            return 0;
        var desiredArguments = $"--companion \"{playerPath}\" --enable-netease-progress-bridge";
        var dedicated = Path.Combine(desktop, DedicatedShortcutName);
        var candidates = new[] { dedicated, Path.Combine(desktop, "网易云音乐.lnk") }
            .Concat(Directory.Exists(taskbar)
                ? Directory.EnumerateFiles(taskbar, "*.lnk").Where(path =>
                    string.Equals(Path.GetFileName(path), DedicatedShortcutName, StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileNameWithoutExtension(path) is "网易云音乐" or "CloudMusic" or "NetEase Cloud Music")
                : []);
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new PlatformNotSupportedException("无法访问 Windows 快捷方式服务。");
        var shellObject = Activator.CreateInstance(shellType)!;
        var repaired = 0;
        try
        {
            dynamic shell = shellObject;
            foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                object? linkObject = null;
                try
                {
                    var exists = File.Exists(path);
                    if (!exists && path != dedicated) continue;
                    linkObject = shell.CreateShortcut(path);
                    dynamic link = linkObject;
                    var target = Convert.ToString(link.TargetPath) ?? "";
                    var arguments = Convert.ToString(link.Arguments) ?? "";
                    var managed = string.Equals(target, appPath, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(arguments, desiredArguments, StringComparison.OrdinalIgnoreCase);
                    if (managed)
                    {
                        if (PlayerShortcutIdentity.Apply(path, playerAppId)) repaired++;
                        continue;
                    }
                    // Only repair the ordinary player entry. Preserve user-customized commands,
                    // unrelated launchers and shared/public desktop shortcuts.
                    if (exists && (!string.Equals(target, playerPath, StringComparison.OrdinalIgnoreCase)
                                   || !string.IsNullOrWhiteSpace(arguments))) continue;
                    if (exists)
                    {
                        Directory.CreateDirectory(backupDirectory);
                        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                            Path.GetFullPath(path).ToUpperInvariant())));
                        var backup = Path.Combine(backupDirectory, key + ".lnk");
                        if (!File.Exists(backup))
                        {
                            File.Copy(path, backup);
                            File.WriteAllText(Path.Combine(backupDirectory, key + ".json"),
                                JsonSerializer.Serialize(new { OriginalPath = path, BackupPath = backup }));
                        }
                    }
                    link.TargetPath = appPath;
                    link.Arguments = desiredArguments;
                    link.WorkingDirectory = Path.GetDirectoryName(appPath)!;
                    link.IconLocation = $"{playerPath},0";
                    link.Description = "网易云音乐与词间：精确歌词同步";
                    link.Save();
                    PlayerShortcutIdentity.Apply(path, playerAppId);
                    repaired++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or COMException)
                {
                    System.Diagnostics.Trace.TraceWarning("Could not maintain NetEase shortcut {0}: {1}", path, exception.Message);
                }
                finally { if (linkObject is not null) Marshal.FinalReleaseComObject(linkObject); }
            }
        }
        finally { Marshal.FinalReleaseComObject(shellObject); }
        return repaired;
    }
}
