using System.Windows;
using LyricsXQuiet.App.Windows;

namespace LyricsXQuiet.App.Pages;

public partial class LyricsPage : System.Windows.Controls.UserControl
{
    private LyricsXQuiet.App.Services.AppController? _controller;

    private async void CheckHealth(object sender, RoutedEventArgs e)
    {
        if (_controller is null || sender is not System.Windows.Controls.Button button) return;
        if (_controller.CurrentTrack is not { IsUsable: true } track)
        { _controller.State.SourceHealth = "播放一首歌曲后可检查四个来源。"; return; }
        button.IsEnabled = false;
        try { await _controller.SearchLyricsAsync(track.Title, track.Artist); }
        finally { button.IsEnabled = true; }
    }

    public LyricsPage() => InitializeComponent();
    internal void Connect(SettingsWindow host)
    {
        OpenLyricsDirectoryButton.Click += host.OnOpenLyricsDirectoryClick;
        ChooseLyricsDirectoryButton.Click += host.OnChooseLyricsDirectoryClick;
        OrganizeLyricsCacheButton.Click += host.OnOrganizeLyricsCacheClick;
        RestoreLyricsCacheButton.Click += host.OnRestoreLyricsCacheClick;
        SearchLyricsButton.Click += host.OnSearchLyricsClick;
        RetryLyricsButton.Click += host.OnRetryLyricsClick;
        ResetLyricsFiltersButton.Click += host.OnResetLyricsFiltersClick;
        Calibration.Attach(host.Controller);
        _controller = host.Controller;
    }
}
