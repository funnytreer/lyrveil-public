using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace LyricsXQuiet.Platform.Windows.Companion;

public static class PlayerMonitorRegistrationService
{
    public const string MonitorMutexName = "Local\\Lyrveil.PlayerMonitor.SingleInstance";
    public const string StopEventName = "Local\\Lyrveil.PlayerMonitor.Stop";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "LyrveilPlayerMonitor";

    public static void Apply(bool enabled)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
            ?? throw new InvalidOperationException("无法打开 Windows 登录启动设置。");

        if (!enabled)
        {
            runKey.DeleteValue(RunValueName, false);
            SignalMonitorToStop();
            return;
        }

        var appPath = ResolveAppPath();
        var command = $"\"{appPath}\" --player-monitor";
        if (!string.Equals(runKey.GetValue(RunValueName) as string, command, StringComparison.OrdinalIgnoreCase))
            runKey.SetValue(RunValueName, command, RegistryValueKind.String);
        ResetStopSignal();
        EnsureMonitorRunning(appPath);
    }

    public static bool IsMonitorRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(MonitorMutexName, out var mutex))
            {
                return false;
            }

            mutex.Dispose();
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    private static void EnsureMonitorRunning(string appPath)
    {
        if (IsMonitorRunning())
        {
            return;
        }

        var startInfo = new ProcessStartInfo(appPath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(appPath)!
        };
        startInfo.ArgumentList.Add("--player-monitor");
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows 没有启动播放器监听器。");
    }

    private static void SignalMonitorToStop()
    {
        using var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
        stopEvent.Set();
    }

    private static void ResetStopSignal()
    {
        using var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
        stopEvent.Reset();
    }

    internal static string ResolveAppPath()
    {
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
}
