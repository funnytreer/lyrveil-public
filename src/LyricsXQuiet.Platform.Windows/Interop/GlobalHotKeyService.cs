using System.Runtime.InteropServices;
using LyricsXQuiet.Core.Settings;

namespace LyricsXQuiet.Platform.Windows.Interop;

public sealed class GlobalHotKeyService : IDisposable
{
    private readonly nint _window;
    private readonly Dictionary<string, (int Id, string Gesture)> _registered = new();
    private bool _disposed;
    private static readonly Dictionary<string, uint> Keys = new()
    {
        ["Space"]=0x20, ["Tab"]=9, ["Enter"]=13, ["Escape"]=27, ["Backspace"]=8, ["Delete"]=46,
        ["Insert"]=45, ["Home"]=36, ["End"]=35, ["PageUp"]=33, ["PageDown"]=34, ["Up"]=38,
        ["Down"]=40, ["Left"]=37, ["Right"]=39, ["MediaPlayPause"]=179, ["MediaNextTrack"]=176,
        ["MediaPreviousTrack"]=177, ["MediaStop"]=178, ["VolumeUp"]=175, ["VolumeDown"]=174,
        ["VolumeMute"]=173, ["OemPlus"]=187, ["OemMinus"]=189, ["OemComma"]=188, ["OemPeriod"]=190,
        ["OemQuestion"]=191, ["OemSemicolon"]=186, ["OemQuotes"]=222, ["OemOpenBrackets"]=219,
        ["OemCloseBrackets"]=221, ["OemPipe"]=220, ["OemTilde"]=192
    };

    public GlobalHotKeyService(nint window)
    {
        if (window == 0) throw new ArgumentException("需要消息窗口", nameof(window));
        _window = window;
    }

    public bool TryBind(string action, string? gesture, out string message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var index = HotKeyBindings.Actions.ToList().IndexOf(action);
        if (index < 0 || !HotKeyBindings.TryNormalize(gesture, out var normalized))
        {
            message = "普通按键需要搭配 Ctrl、Alt、Shift 或 Win。";
            return false;
        }
        _registered.TryGetValue(action, out var old);
        message = "";
        if (normalized == old.Gesture) return true;
        if (normalized.Length == 0)
        {
            if (_registered.Remove(action)) UnregisterHotKey(_window, old.Id);
            return true;
        }
        if (_registered.Values.Any(v => v.Gesture == normalized))
        {
            message = "词间的另一项操作已使用这个快捷键。";
            return false;
        }
        var parts = normalized.Split('+');
        const uint noRepeat = 0x4000;
        uint modifiers = noRepeat;
        foreach (var part in parts[..^1])
            modifiers |= part switch { "Alt" => 1u, "Ctrl" => 2u, "Shift" => 4u, "Win" => 8u, _ => 0u };
        var key = parts[^1];
        var vk = Keys.TryGetValue(key, out var special) ? special : key.Length == 1 ? key[0] :
            (uint)(0x70 + int.Parse(key.AsSpan(1)) - 1);
        // Keep the old shortcut active until Windows has accepted its replacement.
        var firstId = 1 + index * 2;
        var id = old.Id == firstId ? firstId + 1 : firstId;
        if (!RegisterHotKey(_window, id, modifiers, vk))
        {
            message = "Windows 未接受此快捷键，可能已被系统或其他应用占用。";
            return false;
        }
        if (old.Id != 0) UnregisterHotKey(_window, old.Id);
        _registered[action] = (id, normalized);
        return true;
    }

    public string? ResolveMessage(int id) =>
        _registered.FirstOrDefault(pair => pair.Value.Id == id).Key;

    public void Dispose()
    {
        foreach (var binding in _registered.Values) UnregisterHotKey(_window, binding.Id);
        _registered.Clear();
        _disposed = true;
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}
