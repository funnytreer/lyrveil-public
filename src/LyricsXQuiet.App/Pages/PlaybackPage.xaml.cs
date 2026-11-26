using System.Windows;
using LyricsXQuiet.App.Windows;

namespace LyricsXQuiet.App.Pages;

public partial class PlaybackPage : System.Windows.Controls.UserControl
{
    public PlaybackPage() => InitializeComponent();
    internal void Connect(SettingsWindow host)
    {
        ChooseLocalPlayerButton.Click += host.OnChooseLocalPlayerClick;
        ClearLocalPlayerButton.Click += host.OnClearLocalPlayerClick;
        ManageMonitoredPlayersButton.Click += host.OnManageMonitoredPlayersClick;
        CreateCompanionShortcutsButton.Click += host.OnCreateCompanionShortcutsClick;
        ClearTrackLyricOffsetsButton.Click += host.OnClearTrackLyricOffsetsClick;
    }
}
