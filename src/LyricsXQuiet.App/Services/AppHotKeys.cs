using System.Windows.Interop;
using LyricsXQuiet.Core.Settings;
using LyricsXQuiet.Platform.Windows.Interop;

namespace LyricsXQuiet.App.Services;

public sealed class AppHotKeys : IDisposable
{
    private readonly HwndSource _source;
    private readonly GlobalHotKeyService _keys;
    private readonly AppSettings _settings;
    private readonly Action<string> _execute;
    public Dictionary<string, string> Errors { get; } = new();

    public AppHotKeys(AppSettings settings, Action<string> execute)
    {
        _settings = settings;
        _execute = execute;
        _source = new HwndSource(new HwndSourceParameters("Lyrveil shortcuts") { ParentWindow = -3, WindowStyle = 0 });
        _keys = new GlobalHotKeyService(_source.Handle);
        _source.AddHook(OnMessage);
        foreach (var (action, gesture) in settings.HotKeys)
            if (!_keys.TryBind(action, gesture, out var reason)) Errors[action] = reason;
    }

    public bool Set(string action, string gesture, out string reason)
    {
        if (!HotKeyBindings.TryNormalize(gesture, out var normalized))
        {
            reason = "普通按键需搭配修饰键。也可以单独使用功能键或媒体键。";
            return false;
        }
        var bindings = _settings.HotKeys;
        if (normalized.Length != 0 && bindings.Any(p => p.Key != action && p.Value == normalized))
        {
            reason = "词间的另一项操作已使用这个快捷键。";
            return false;
        }
        if (!_keys.TryBind(action, normalized, out reason)) return false;
        bindings[action] = normalized;
        _settings.HotKeys = bindings;
        Errors.Remove(action);
        return true;
    }

    private nint OnMessage(nint hwnd, int message, nint id, nint data, ref bool handled)
    {
        if (message == 0x0312 && _keys.ResolveMessage((int)id) is { } action)
        {
            handled = true;
            _source.Dispatcher.InvokeAsync(() => _execute(action));
        }
        return 0;
    }

    public void Dispose()
    {
        _source.RemoveHook(OnMessage);
        _keys.Dispose();
        _source.Dispose();
    }
}
