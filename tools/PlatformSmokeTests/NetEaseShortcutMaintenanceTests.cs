using System.Runtime.InteropServices;
using LyricsXQuiet.Core.Settings;
using LyricsXQuiet.Platform.Windows.Companion;

internal static class NetEaseShortcutMaintenanceTests
{
    public static void Run(string parent)
    {
        var root = Path.Combine(parent, "shortcut-maintenance");
        var desktop = Path.Combine(root, "desktop");
        var taskbar = Path.Combine(root, "taskbar");
        var backups = Path.Combine(root, "backups");
        Directory.CreateDirectory(desktop);
        Directory.CreateDirectory(taskbar);
        var player = Path.Combine(root, "cloudmusic.exe");
        var app = Path.Combine(root, "LyricsXQuiet.App.exe");
        var other = Path.Combine(root, "other.exe");
        File.WriteAllText(player, "fixture - never executed");
        File.WriteAllText(app, "fixture - never executed");
        File.WriteAllText(other, "fixture - never executed");
        var official = Path.Combine(desktop, "网易云音乐.lnk");
        var pinned = Path.Combine(taskbar, "网易云音乐.lnk");
        var custom = Path.Combine(taskbar, "CloudMusic.lnk");
        var pinnedCombo = Path.Combine(taskbar, NetEaseShortcutMaintenance.DedicatedShortcutName);
        const string playerId = "Tests.NetEase.Player";
        int Repair() => NetEaseShortcutMaintenance.RepairForPaths(player, app, desktop, taskbar, backups, playerId);
        WriteLink(official, player, "");
        WriteLink(pinned, player, "");
        WriteLink(custom, player, "--user-custom-option");
        WriteLink(pinnedCombo, app, $"--companion \"{player}\" --enable-netease-progress-bridge");
        var originalPinned = File.ReadAllBytes(pinned);
        Check(Repair() == 4,
            "Maintenance must create the distinct entry, upgrade the legacy pinned combo and repair ordinary shortcuts.");
        foreach (var path in new[] { official, pinned, pinnedCombo, Path.Combine(desktop, NetEaseShortcutMaintenance.DedicatedShortcutName) })
        {
            var link = ReadLink(path);
            Check(link.Target == app && link.Arguments == $"--companion \"{player}\" --enable-netease-progress-bridge",
                "Every repaired entry must launch Lyrveil with the exact NetEase bridge request.");
            Check(ReadAppId(path) == playerId, "The shortcut's persisted taskbar identity must be the player's.");
        }
        Check(ReadLink(custom).Arguments == "--user-custom-option", "Customized player shortcuts must remain unchanged.");
        Check(Directory.EnumerateFiles(backups, "*.lnk").Any(path => File.ReadAllBytes(path).SequenceEqual(originalPinned)),
            "The original pinned entry must be backed up before modification.");
        var savedTime = File.GetLastWriteTimeUtc(pinned);
        Check(Repair() == 0
              && File.GetLastWriteTimeUtc(pinned) == savedTime, "Healthy shortcuts must not be rewritten on each scan.");

        // Simulate two independent updater runs recreating the taskbar entry.
        for (var update = 0; update < 2; update++)
        {
            File.Delete(pinned);
            WriteLink(pinned, player, "");
            Check(Repair() == 1 && ReadLink(pinned).Target == app && ReadAppId(pinned) == playerId,
                "Each later update must restore the grouped player identity and companion launch.");
        }
        WriteLink(official, other, "");
        Repair();
        Check(ReadLink(official).Target == other, "A user-retargeted entry must not be overwritten.");
        var settings = new AppSettings { PlayerFollowMode = PlayerFollowModes.Combined };
        Check(!NetEaseShortcutMaintenance.IsEnabled(settings), "Recovery must require an explicit opt-in.");
        settings.AutoRepairNetEaseSync = true;
        Check(NetEaseShortcutMaintenance.IsEnabled(settings), "Opted-in combined mode must maintain NetEase.");
        settings.PlayerFollowMode = PlayerFollowModes.Monitor;
        Check(!NetEaseShortcutMaintenance.IsEnabled(settings), "Disabling companion shortcuts must stop rewriting entries.");
        settings.PlayerFollowMode = PlayerFollowModes.Combined;
        settings.FollowedPlayerIds = "qqmusic;spotify";
        Check(!NetEaseShortcutMaintenance.IsEnabled(settings), "Excluding NetEase must stop its maintenance.");
        Console.WriteLine("NetEase shortcut maintenance passed: taskbar, repeated updates, backups, opt-in, custom-entry preservation.");
    }

    private static (string Target, string Arguments) ReadLink(string path) => WithLink(path, link =>
        ((string)link.TargetPath, (string)link.Arguments));

    private static string? ReadAppId(string path)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        dynamic folder = shell.NameSpace(Path.GetDirectoryName(path));
        dynamic item = folder.ParseName(Path.GetFileName(path));
        try { return item.ExtendedProperty("System.AppUserModel.ID") as string; }
        finally
        {
            Marshal.ReleaseComObject(item);
            Marshal.ReleaseComObject(folder);
            Marshal.ReleaseComObject(shell);
        }
    }

    private static void WriteLink(string path, string target, string arguments) => WithLink(path, link =>
    {
        link.TargetPath = target;
        link.Arguments = arguments;
        link.Save();
        return true;
    });

    private static T WithLink<T>(string path, Func<dynamic, T> action)
    {
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        object? link = null;
        try { link = ((dynamic)shell).CreateShortcut(path); return action(link); }
        finally
        {
            if (link is not null) Marshal.FinalReleaseComObject(link);
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
