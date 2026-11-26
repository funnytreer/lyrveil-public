using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace LyricsXQuiet.Platform.Windows.Interop;

internal static class PlayerShortcutIdentity
{
    private static readonly object CacheLock = new();
    private static string? cachedPlayerPath, cachedId;
    private static long nextLookup;

    // AppsFolder supplies the same installed application's identity even when
    // its Start menu shortcut has no explicit System.AppUserModel.ID.
    internal static string? Resolve(string playerPath)
    {
        lock (CacheLock)
        {
            if (string.Equals(cachedPlayerPath, playerPath, StringComparison.OrdinalIgnoreCase)
                && Environment.TickCount64 < nextLookup) return cachedId;
            cachedPlayerPath = playerPath;
            cachedId = null;
            nextLookup = Environment.TickCount64 + (long)TimeSpan.FromMinutes(5).TotalMilliseconds;
            object? shell = null, folder = null, items = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true)!);
                folder = ((dynamic)shell!).NameSpace("shell:AppsFolder");
                if (folder is null) return null;
                items = ((dynamic)folder).Items();
                foreach (dynamic item in (dynamic)items!)
                {
                    try
                    {
                        string? target = item.ExtendedProperty("System.Link.TargetParsingPath") as string;
                        if (!string.Equals(target, playerPath, StringComparison.OrdinalIgnoreCase)) continue;
                        string? id = item.ExtendedProperty("System.AppUserModel.ID") as string;
                        if (!string.IsNullOrWhiteSpace(id)) return cachedId = id;
                    }
                    finally { Release(item); }
                }
            }
            catch (COMException ex) { Trace.TraceWarning("Looking up the player taskbar identity: {0}", ex.Message); }
            finally { Release(items); Release(folder); Release(shell); }
            return null;
        }
    }

    internal static bool Apply(string path, string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return false;
        var link = new ShellLink();
        try
        {
            var file = (IPersistFile)link;
            file.Load(path, 2 /* STGM_READWRITE */);
            var store = (IPropertyStore)link;
            var key = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
            store.GetValue(ref key, out var current);
            try
            {
                if (current.Type == (ushort)VarEnum.VT_LPWSTR
                    && Marshal.PtrToStringUni(current.Pointer) == appId) return false;
            }
            finally { PropVariantClear(ref current); }
            var value = new PropVariant { Type = (ushort)VarEnum.VT_LPWSTR, Pointer = Marshal.StringToCoTaskMemUni(appId) };
            try { store.SetValue(ref key, ref value); }
            finally { PropVariantClear(ref value); }
            store.Commit();
            file.Save(path, true);
            SHChangeNotify(0x2000 /* SHCNE_UPDATEITEM */, 0x5 /* SHCNF_PATHW */, path, IntPtr.Zero);
            return true;
        }
        finally { Release(link); }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid format, uint id)
    {
        private readonly Guid formatId = format;
        private readonly uint propertyId = id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        private ushort reserved1, reserved2, reserved3;
        public IntPtr Pointer;
        private IntPtr unionPadding;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void SHChangeNotify(int eventId, uint flags, string path, IntPtr unused);
}
