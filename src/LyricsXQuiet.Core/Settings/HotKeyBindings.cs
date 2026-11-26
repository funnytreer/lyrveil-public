namespace LyricsXQuiet.Core.Settings;

public static class HotKeyBindings
{
    public static IReadOnlyList<string> Actions { get; } = Array.AsReadOnly(new[]
    {
        "ToggleLyrics", "ToggleLock", "OffsetForward", "OffsetBackward", "ToggleQuickPanel", "OpenSearch"
    });

    private static readonly string[] Modifiers = ["Ctrl", "Alt", "Shift", "Win"];
    private static readonly string[] MediaKeys = ["MediaPlayPause", "MediaNextTrack", "MediaPreviousTrack",
        "MediaStop", "VolumeUp", "VolumeDown", "VolumeMute"];
    private static readonly string[] OtherKeys = ["Space", "Tab", "Enter", "Escape", "Backspace", "Delete",
        "Insert", "Home", "End", "PageUp", "PageDown", "Up", "Down", "Left", "Right",
        "OemPlus", "OemMinus", "OemComma", "OemPeriod", "OemQuestion", "OemSemicolon",
        "OemQuotes", "OemOpenBrackets", "OemCloseBrackets", "OemPipe", "OemTilde"];

    public static bool TryNormalize(string? gesture, out string result)
    {
        result = string.Empty;
        if (string.IsNullOrWhiteSpace(gesture)) return true;
        var parts = gesture.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts[..^1])
        {
            if (!Modifiers.Contains(part, StringComparer.OrdinalIgnoreCase) || !modifiers.Add(part)) return false;
        }

        var key = parts[^1].ToUpperInvariant();
        var media = MediaKeys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (media is not null) key = media;
        else if (!IsFunctionKey(key))
        {
            if (modifiers.Count == 0) return false;
            if (key.Length != 1 || !char.IsAsciiLetterOrDigit(key[0]))
            {
                var namedKey = OtherKeys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (namedKey is null) return false;
                key = namedKey;
            }
        }
        result = string.Join("+", Modifiers.Where(modifiers.Contains).Append(key));
        return true;
    }

    private static bool IsFunctionKey(string key) =>
        key.Length >= 2 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out var number)
        && number is >= 1 and <= 24 && key == $"F{number}";

    internal static Dictionary<string, string> Normalize(IReadOnlyDictionary<string, string>? bindings)
    {
        var result = new Dictionary<string, string>();
        var used = new HashSet<string>();
        foreach (var action in Actions)
        {
            result[action] = bindings is not null && bindings.TryGetValue(action, out var text)
                && TryNormalize(text, out var gesture) && used.Add(gesture) ? gesture : string.Empty;
        }
        return result;
    }
}
