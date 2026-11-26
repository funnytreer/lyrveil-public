using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LyricsXQuiet.Platform.Windows.Playback;

/// <summary>
/// Recovers a NetEase CDP endpoint only when the running cloudmusic process was
/// explicitly started with a numeric loopback address and port. It never scans
/// ports and treats ambiguous process command lines as unavailable.
/// </summary>
public static class NetEaseCdpPortDiscovery
{
    private const string ProcessName = "cloudmusic";
    private const string ExecutableName = "cloudmusic.exe";
    private const string AddressOption = "--remote-debugging-address";
    private const string PortOption = "--remote-debugging-port";
    private const string RequiredAddress = "127.0.0.1";
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int MaximumCommandLineBytes = 128 * 1024;

    public static int? TryFindExplicitLoopbackPort()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(ProcessName);
        }
        catch
        {
            return null;
        }

        try
        {
            return TryFindExplicitLoopbackPort(processes);
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public static int? TryFindExplicitLoopbackPort(IEnumerable<Process> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var commandLines = new List<string>();
        foreach (var process in processes)
        {
            try
            {
                if (process.HasExited
                    || !string.Equals(process.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var imagePath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(imagePath)
                    || !string.Equals(
                        Path.GetFileName(imagePath),
                        ExecutableName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var commandLine = TryReadCommandLine(process.Id);
                if (commandLine is null
                    || !TrySplitCommandLine(commandLine, out var arguments)
                    || !CommandTargetsImage(arguments, imagePath))
                {
                    continue;
                }

                commandLines.Add(commandLine);
            }
            catch
            {
                // A protected, exiting, or inaccessible process is not a usable
                // endpoint. Discovery must remain a fail-open optional feature.
            }
        }

        return TryFindExplicitLoopbackPort(commandLines);
    }

    /// <summary>
    /// Exposed for deterministic validation of the strict command-line policy.
    /// Production discovery supplies only command lines read from cloudmusic.
    /// </summary>
    public static int? TryFindExplicitLoopbackPort(IEnumerable<string> commandLines)
    {
        ArgumentNullException.ThrowIfNull(commandLines);
        var ports = new HashSet<int>();
        foreach (var commandLine in commandLines)
        {
            if (TryParseExplicitLoopbackPort(commandLine, out var port))
            {
                ports.Add(port);
                if (ports.Count > 1)
                {
                    return null;
                }
            }
        }

        return ports.Count == 1 ? ports.Single() : null;
    }

    public static bool TryParseExplicitLoopbackPort(string? commandLine, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(commandLine)
            || !TrySplitCommandLine(commandLine, out var arguments))
        {
            return false;
        }

        return TryParseExplicitLoopbackPort(arguments, out port);
    }

    private static bool TryParseExplicitLoopbackPort(
        IReadOnlyList<string> arguments,
        out int port)
    {
        port = 0;
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ports = new HashSet<int>();
        var sawAddress = false;
        var sawPort = false;

        for (var index = 1; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (TryReadOption(arguments, ref index, AddressOption, out var address, out var matchedAddress))
            {
                sawAddress = true;
                if (string.IsNullOrWhiteSpace(address) || address.Any(char.IsControl))
                {
                    return false;
                }

                addresses.Add(address);
            }
            else if (matchedAddress)
            {
                return false;
            }
            else if (TryReadOption(arguments, ref index, PortOption, out var portText, out var matchedPort))
            {
                sawPort = true;
                if (!int.TryParse(
                        portText,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var candidate)
                    || candidate is < 1 or > 65535)
                {
                    return false;
                }

                ports.Add(candidate);
            }
            else if (matchedPort)
            {
                return false;
            }
        }

        if (!sawAddress
            || addresses.Count != 1
            || !addresses.Contains(RequiredAddress)
            || !sawPort
            || ports.Count != 1)
        {
            return false;
        }

        port = ports.Single();
        return true;
    }

    private static bool TryReadOption(
        IReadOnlyList<string> arguments,
        ref int index,
        string option,
        out string value,
        out bool matched)
    {
        value = string.Empty;
        matched = false;
        var argument = arguments[index];
        if (string.Equals(argument, option, StringComparison.OrdinalIgnoreCase))
        {
            matched = true;
            if (index + 1 >= arguments.Count)
            {
                return false;
            }

            value = arguments[++index];
            return true;
        }

        var prefix = option + "=";
        if (!argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        matched = true;
        value = argument[prefix.Length..];
        return value.Length > 0;
    }

    private static bool CommandTargetsImage(IReadOnlyList<string> arguments, string imagePath)
    {
        if (arguments.Count == 0 || string.IsNullOrWhiteSpace(arguments[0]))
        {
            return false;
        }

        var executableArgument = arguments[0];
        if (!Path.IsPathRooted(executableArgument))
        {
            return string.Equals(
                Path.GetFileName(executableArgument),
                ExecutableName,
                StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(executableArgument),
                Path.GetFullPath(imagePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TrySplitCommandLine(string commandLine, out string[] arguments)
    {
        arguments = [];
        if (string.IsNullOrWhiteSpace(commandLine) || commandLine.Any(char.IsControl))
        {
            return false;
        }

        var argumentPointer = CommandLineToArgvW(commandLine, out var argumentCount);
        if (argumentPointer == IntPtr.Zero || argumentCount <= 0 || argumentCount > 256)
        {
            if (argumentPointer != IntPtr.Zero)
            {
                _ = LocalFree(argumentPointer);
            }

            return false;
        }

        try
        {
            var result = new string[argumentCount];
            for (var index = 0; index < argumentCount; index++)
            {
                var valuePointer = Marshal.ReadIntPtr(argumentPointer, index * IntPtr.Size);
                var value = Marshal.PtrToStringUni(valuePointer);
                if (value is null)
                {
                    return false;
                }

                result[index] = value;
            }

            arguments = result;
            return true;
        }
        finally
        {
            _ = LocalFree(argumentPointer);
        }
    }

    internal static string? TryReadCommandLine(int processId)
    {
        using var processHandle = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            processId);
        if (processHandle.IsInvalid)
        {
            return null;
        }

        var status = NtQueryInformationProcess(
            processHandle,
            ProcessCommandLineInformation,
            IntPtr.Zero,
            0,
            out var requiredBytes);
        if (status != StatusInfoLengthMismatch
            || requiredBytes <= Marshal.SizeOf<UnicodeString>()
            || requiredBytes > MaximumCommandLineBytes)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(requiredBytes);
        try
        {
            status = NtQueryInformationProcess(
                processHandle,
                ProcessCommandLineInformation,
                buffer,
                requiredBytes,
                out var returnedBytes);
            if (status < 0
                || returnedBytes <= 0
                || returnedBytes > requiredBytes)
            {
                return null;
            }

            var commandLine = Marshal.PtrToStructure<UnicodeString>(buffer);
            if (commandLine.Buffer == IntPtr.Zero
                || commandLine.Length == 0
                || commandLine.Length % sizeof(char) != 0
                || commandLine.Length > commandLine.MaximumLength)
            {
                return null;
            }

            var bufferStart = buffer.ToInt64();
            var stringStart = commandLine.Buffer.ToInt64();
            var stringOffset = stringStart - bufferStart;
            if (stringOffset < 0
                || stringOffset > returnedBytes
                || commandLine.Length > returnedBytes - stringOffset)
            {
                return null;
            }

            return Marshal.PtrToStringUni(commandLine.Buffer, commandLine.Length / sizeof(char));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct UnicodeString
    {
        public readonly ushort Length;
        public readonly ushort MaximumLength;
        public readonly IntPtr Buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        SafeProcessHandle processHandle,
        int processInformationClass,
        IntPtr processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(
        string commandLine,
        out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
