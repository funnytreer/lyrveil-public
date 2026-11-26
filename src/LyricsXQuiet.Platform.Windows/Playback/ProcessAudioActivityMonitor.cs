using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LyricsXQuiet.Platform.Windows.Playback;

internal sealed class ProcessAudioActivityMonitor
{
    private const float AudiblePeakThreshold = 0.0001f;
    private static readonly TimeSpan AudibleHoldDuration = TimeSpan.FromSeconds(2);
    private readonly ProcessAudioActivityTracker _tracker =
        new(AudibleHoldDuration);

    public ProcessAudioActivitySnapshot GetActivity(DateTimeOffset now) =>
        _tracker.Update(SampleAudibleProcessNames(), now);

    private static IReadOnlySet<string> SampleAudibleProcessNames()
        => GetActivitySamples()
            .Where(sample => sample.IsActive && sample.Peak > AudiblePeakThreshold)
            .Select(sample => sample.ProcessName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<ProcessAudioActivitySample> GetActivitySamples()
    {
        var samples = new List<ProcessAudioActivitySample>();
        IMMDeviceEnumerator? deviceEnumerator = null;
        IMMDeviceCollection? devices = null;

        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(
                new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"),
                throwOnError: true);
            deviceEnumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType!)!;
            ThrowIfFailed(deviceEnumerator.EnumAudioEndpoints(
                EDataFlow.Render,
                DeviceState.Active,
                out devices));
            ThrowIfFailed(devices.GetCount(out var deviceCount));
            for (var deviceIndex = 0; deviceIndex < deviceCount; deviceIndex++)
            {
                IMMDevice? device = null;
                try
                {
                    ThrowIfFailed(devices.Item(deviceIndex, out device));
                    SampleDevice(device, samples);
                }
                finally
                {
                    ReleaseComObject(device);
                }
            }
        }
        catch (Exception exception) when (
            exception is COMException
                or InvalidCastException
                or UnauthorizedAccessException)
        {
            // GSMTC remains the primary signal. Audio metering is only a fallback for
            // players that publish metadata while incorrectly reporting Paused.
        }
        finally
        {
            ReleaseComObject(devices);
            ReleaseComObject(deviceEnumerator);
        }

        return samples;
    }

    private static void SampleDevice(
        IMMDevice device,
        ICollection<ProcessAudioActivitySample> samples)
    {
        object? managerObject = null;
        IAudioSessionEnumerator? sessionEnumerator = null;
        try
        {
            var managerInterfaceId = typeof(IAudioSessionManager2).GUID;
            ThrowIfFailed(device.Activate(
                ref managerInterfaceId,
                ClsContext.All,
                IntPtr.Zero,
                out managerObject));
            var manager = (IAudioSessionManager2)managerObject;
            ThrowIfFailed(manager.GetSessionEnumerator(out sessionEnumerator));
            ThrowIfFailed(sessionEnumerator.GetCount(out var sessionCount));

            for (var index = 0; index < sessionCount; index++)
            {
                IAudioSessionControl? session = null;
                try
                {
                    if (sessionEnumerator.GetSession(index, out session) < 0
                        || session is not IAudioSessionControl2 session2
                        || session.GetState(out var state) < 0
                        || session2.GetProcessId(out var processId) < 0
                        || processId == 0)
                    {
                        continue;
                    }

                    var peak = 0f;
                    if (session is IAudioMeterInformation meter)
                    {
                        _ = meter.GetPeakValue(out peak);
                    }

                    try
                    {
                        using var process = Process.GetProcessById((int)processId);
                        if (!string.IsNullOrWhiteSpace(process.ProcessName))
                        {
                            samples.Add(new ProcessAudioActivitySample(
                                process.ProcessName,
                                state == AudioSessionState.Active,
                                peak));
                        }
                    }
                    catch (Exception exception) when (
                        exception is ArgumentException
                            or InvalidOperationException
                            or System.ComponentModel.Win32Exception)
                    {
                    }
                }
                finally
                {
                    ReleaseComObject(session);
                }
            }
        }
        finally
        {
            ReleaseComObject(sessionEnumerator);
            ReleaseComObject(managerObject);
        }
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }

    private enum EDataFlow
    {
        Render,
        Capture,
        All
    }

    private enum ERole
    {
        Console,
        Multimedia,
        Communications
    }

    [Flags]
    private enum DeviceState : uint
    {
        Active = 0x1,
        Disabled = 0x2,
        NotPresent = 0x4,
        Unplugged = 0x8,
        All = Active | Disabled | NotPresent | Unplugged
    }

    private enum AudioSessionState
    {
        Inactive,
        Active,
        Expired
    }

    [Flags]
    private enum ClsContext : uint
    {
        InProcessServer = 0x1,
        InProcessHandler = 0x2,
        LocalServer = 0x4,
        RemoteServer = 0x10,
        All = InProcessServer | InProcessHandler | LocalServer | RemoteServer
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(
            EDataFlow dataFlow,
            DeviceState stateMask,
            out IMMDeviceCollection devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out int deviceCount);

        [PreserveSig]
        int Item(int deviceIndex, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid interfaceId,
            ClsContext classContext,
            IntPtr activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig]
        int GetAudioSessionControl(ref Guid audioSessionGuid, uint streamFlags, out IntPtr sessionControl);

        [PreserveSig]
        int GetSimpleAudioVolume(ref Guid audioSessionGuid, uint streamFlags, out IntPtr audioVolume);

        [PreserveSig]
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig]
        int GetCount(out int sessionCount);

        [PreserveSig]
        int GetSession(int sessionIndex, out IAudioSessionControl sessionControl);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig]
        int GetState(out AudioSessionState state);

        [PreserveSig]
        int GetDisplayName(out IntPtr displayName);

        [PreserveSig]
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

        [PreserveSig]
        int GetIconPath(out IntPtr iconPath);

        [PreserveSig]
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

        [PreserveSig]
        int GetGroupingParam(out Guid groupingId);

        [PreserveSig]
        int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

        [PreserveSig]
        int RegisterAudioSessionNotification(IntPtr client);

        [PreserveSig]
        int UnregisterAudioSessionNotification(IntPtr client);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2 : IAudioSessionControl
    {
        [PreserveSig]
        int GetSessionIdentifier(out IntPtr sessionIdentifier);

        [PreserveSig]
        int GetSessionInstanceIdentifier(out IntPtr sessionInstanceIdentifier);

        [PreserveSig]
        int GetProcessId(out uint processId);

        [PreserveSig]
        int IsSystemSoundsSession();

        [PreserveSig]
        int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport]
    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig]
        int GetPeakValue(out float peak);

        [PreserveSig]
        int GetMeteringChannelCount(out int channelCount);

        [PreserveSig]
        int GetChannelsPeakValues(int channelCount, [Out] float[] peakValues);

        [PreserveSig]
        int QueryHardwareSupport(out uint hardwareSupportMask);
    }
}

internal sealed record ProcessAudioActivitySample(
    string ProcessName,
    bool IsActive,
    float Peak);

internal sealed class ProcessAudioActivityTracker(TimeSpan holdDuration)
{
    private readonly Dictionary<string, DateTimeOffset> _lastAudibleAt =
        new(StringComparer.OrdinalIgnoreCase);

    public ProcessAudioActivitySnapshot Update(
        IReadOnlySet<string> currentlyAudibleProcessNames,
        DateTimeOffset now)
    {
        foreach (var processName in currentlyAudibleProcessNames)
        {
            _lastAudibleAt[processName] = now;
        }

        foreach (var staleProcessName in _lastAudibleAt
                     .Where(pair => now - pair.Value > holdDuration)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _lastAudibleAt.Remove(staleProcessName);
        }

        return new ProcessAudioActivitySnapshot(
            currentlyAudibleProcessNames.ToHashSet(StringComparer.OrdinalIgnoreCase),
            _lastAudibleAt.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }
}

internal sealed record ProcessAudioActivitySnapshot(
    IReadOnlySet<string> CurrentlyAudibleProcessNames,
    IReadOnlySet<string> RecentlyAudibleProcessNames);
