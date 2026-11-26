using System.Windows;
using System.Windows.Input;
using LyricsXQuiet.App.Services;
using LyricsXQuiet.App.State;
using LyricsXQuiet.App.Windows;
using LyricsXQuiet.Core.Settings;
using WpfButton = System.Windows.Controls.Button;

namespace LyricsXQuiet.App.Pages;

public partial class ShortcutsPage : System.Windows.Controls.UserControl
{
    private static readonly string[] Labels = ["显示 / 隐藏桌面歌词", "锁定 / 解锁穿透", "本曲歌词提前 100 ms",
        "本曲歌词延后 100 ms", "唤出快速调节面板", "打开歌词对比窗"];
    private static readonly string[] Recommended = ["Ctrl+Alt+L", "Ctrl+Alt+K", "Ctrl+Alt+Right", "Ctrl+Alt+Left",
        "Ctrl+Alt+P", "Ctrl+Alt+F"];
    private string? _capture;
    private AppHotKeys? _hotKeys;
    public ShortcutsPage() => InitializeComponent();

    internal void Connect(SettingsWindow host)
    {
        _hotKeys = (System.Windows.Application.Current as App)?.HotKeys;
        Refresh();
        if (_hotKeys?.Errors.Count > 0)
            Feedback.Text = string.Join("　", _hotKeys.Errors.Select(p => $"{Labels[HotKeyBindings.Actions.ToList().IndexOf(p.Key)]}：{p.Value}"));
    }

    private void Refresh()
    {
        var values = ((AppViewModel)DataContext).Settings.HotKeys;
        BindingsPanel.ItemsSource = HotKeyBindings.Actions.Select((action, i) => new
        {
            Action = action, Label = Labels[i], Gesture = values[action].Length == 0 ? "未绑定" : values[action]
        }).ToArray();
    }

    private void ArmCapture(object sender, RoutedEventArgs e)
    {
        var button = (WpfButton)sender;
        button.Focus();
        _capture = (string)button.Tag;
        Feedback.Text = "按下要绑定的组合键，Esc 取消。";
    }

    private void EndCapture(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_capture == (string)((WpfButton)sender).Tag) _capture = null;
    }

    private void CaptureKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_capture is not { } action) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        { _capture = null; Feedback.Text = ""; return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var parts = new List<string>();
        foreach (var (flag, text) in new[] { (ModifierKeys.Control, "Ctrl"), (ModifierKeys.Alt,"Alt"),
                     (ModifierKeys.Shift,"Shift"), (ModifierKeys.Windows,"Win") })
            if (Keyboard.Modifiers.HasFlag(flag)) parts.Add(text);
        parts.Add(key switch { >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(), Key.Back => "Backspace",
            Key.Return => "Enter", Key.Prior => "PageUp", Key.Next => "PageDown", Key.Oem1 => "OemSemicolon",
            Key.Oem2 => "OemQuestion", Key.Oem3 => "OemTilde", Key.Oem4 => "OemOpenBrackets",
            Key.Oem5 => "OemPipe", Key.Oem6 => "OemCloseBrackets", Key.Oem7 => "OemQuotes", _ => key.ToString() });
        if (_hotKeys is null) return;
        if (!_hotKeys.Set(action, string.Join("+", parts), out var reason)) { Feedback.Text = reason; return; }
        _capture = null;
        Feedback.Text = "已保存，快捷键已生效。";
        Refresh();
    }

    private void ClearBinding(object sender, RoutedEventArgs e)
    {
        if (_hotKeys is null) return;
        _hotKeys.Set((string)((WpfButton)sender).Tag, "", out _);
        Feedback.Text = "已解除绑定。";
        Refresh();
    }

    private void Recommend(object sender, RoutedEventArgs e) => ApplyGroup(true);
    private void ClearAll(object sender, RoutedEventArgs e) => ApplyGroup(false);

    private void ApplyGroup(bool recommended)
    {
        if (_hotKeys is null) return;
        var failures = new List<string>();
        for (var i = 0; i < HotKeyBindings.Actions.Count; i++)
            if (!_hotKeys.Set(HotKeyBindings.Actions[i], recommended ? Recommended[i] : "", out var reason))
                failures.Add($"{Labels[i]}：{reason}");
        Feedback.Text = failures.Count > 0 ? string.Join("　", failures) : recommended ? "已启用推荐组合。" : "已清除所有快捷键。";
        Refresh();
    }
}
