using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LyricsXQuiet.Platform.Windows.Companion;

public static class PlayerProcessSnapshot
{
    private const int NoMoreFiles = 18;
    private static readonly uint Session = GetCurrentSession();

    public static HashSet<string>? ReadRunning(IEnumerable<string> names)
    {
        var executables = names.ToDictionary(name => name + ".exe", StringComparer.OrdinalIgnoreCase);
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Executable = "" };
        if (snapshot.IsInvalid || !Process32FirstW(snapshot, ref entry)) return null;
        do
        {
            if (executables.TryGetValue(entry.Executable, out var name) &&
                ProcessIdToSessionId(entry.ProcessId, out var session) && session == Session)
                running.Add(name);
        } while (Process32NextW(snapshot, ref entry));
        return Marshal.GetLastWin32Error() == NoMoreFiles ? running : null;
    }

    private static uint GetCurrentSession() =>
        ProcessIdToSessionId((uint)Environment.ProcessId, out var session) ? session : uint.MaxValue;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nuint DefaultHeap;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")]
    private static extern bool ProcessIdToSessionId(uint processId, out uint session);
}
