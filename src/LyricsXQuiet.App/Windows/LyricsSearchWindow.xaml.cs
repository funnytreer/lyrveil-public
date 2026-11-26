using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using LyricsXQuiet.App.Services;
using LyricsXQuiet.App.State;
using LyricsXQuiet.Core.Lyrics;
using LyricsXQuiet.Core.Playback;

namespace LyricsXQuiet.App.Windows;

public partial class LyricsSearchWindow : Window
{
    private readonly AppController _controller;
    private readonly string? _trackLyricsOperationKey;
    private CancellationTokenSource? _searchCancellation;
    private LyricsCandidateViewModel[] _rows = [];
    private string _filter = "";

    public LyricsSearchWindow(AppController controller, string? title = null, string? artist = null)
    {
        _controller = controller;
        _trackLyricsOperationKey = controller.CurrentTrack?.LyricsOperationKey;
        InitializeComponent();
        Calibration.Attach(controller);
        LyricsXQuiet.App.Services.ThemeManager.AttachChrome(this);
        var track = controller.CurrentTrack;
        TitleBox.Text = title ?? track?.Title ?? string.Empty;
        ArtistBox.Text = artist ?? track?.Artist ?? string.Empty;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    internal static void Open(AppController controller, Window? owner = null, string? title = null, string? artist = null)
    {
        foreach (var existing in System.Windows.Application.Current.Windows.OfType<LyricsSearchWindow>().ToArray())
        {
            if (existing._controller != controller) continue;
            if (existing._trackLyricsOperationKey == controller.CurrentTrack?.LyricsOperationKey)
            {
                existing.Activate();
                if (title is not null && (existing.TitleBox.Text != title || existing.ArtistBox.Text != artist))
                {
                    existing.TitleBox.Text = title;
                    existing.ArtistBox.Text = artist ?? "";
                    _ = existing.SearchAsync();
                }
                return;
            }
            existing.Close();
        }

        var window = new LyricsSearchWindow(controller, title, artist);
        if (owner?.IsVisible == true) { window.Owner = owner; window.ShowDialog(); }
        else window.Show();
    }

    private void CloseSearch(object sender, RoutedEventArgs args) => Close();

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        Loaded -= OnLoaded;
        if (_controller.CurrentTrack is null)
        {
            StatusText.Text = "请先在受支持的音乐播放器中播放一首歌曲。";
            SearchButton.IsEnabled = false;
            return;
        }

        await SearchAsync();
    }

    private async void OnSearchClick(object sender, RoutedEventArgs args) =>
        await SearchAsync();

    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            StatusText.Text = "请输入歌名。";
            return;
        }

        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var cancellationToken = _searchCancellation.Token;
        SearchButton.IsEnabled = false;
        UseButton.IsEnabled = false;
        _rows = [];
        ApplyFilter();
        PreviewBox.Text = string.Empty;
        StatusText.Text = "正在搜索同步歌词…";

        try
        {
            var currentTrack = _controller.CurrentTrack;
            if (currentTrack is null)
            {
                StatusText.Text = "当前没有可选择歌词的歌曲。";
                return;
            }

            var matchTrack = currentTrack with
            {
                Title = TitleBox.Text.Trim(),
                Artist = ArtistBox.Text.Trim()
            };
            var candidates = await _controller.SearchLyricsAsync(
                TitleBox.Text,
                ArtistBox.Text,
                cancellationToken);
            var rows = candidates.Candidates
                .Select((candidate, index) => new LyricsCandidateViewModel(
                    candidate,
                    matchTrack,
                    index == 0))
                .ToArray();
            _rows = rows;
            ApplyFilter();
            StatusText.Text = LyricsCandidateDisplay.GetSearchStatus(candidates,
                rows.FirstOrDefault()?.RecommendationSummary);
            if (rows.Length > 0)
            {
                ResultsList.SelectedIndex = 0;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            StatusText.Text = "歌词服务暂时不可用，请稍后再试。";
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SearchButton.IsEnabled = true;
            }
        }
    }

    private void FilterSource(object sender, RoutedEventArgs args)
    {
        _filter = (string)((FrameworkElement)sender).Tag;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var visible = _rows.Where(row => _filter.Length == 0 || row.Provider == _filter).ToArray();
        ResultsList.ItemsSource = visible;
        Recommendation.Text = visible.FirstOrDefault()?.RecommendationSummary ?? "此来源暂无候选";
        if (visible.Length > 0) ResultsList.SelectedIndex = 0;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ResultsList.SelectedItem is not LyricsCandidateViewModel row)
        {
            PreviewBox.Text = string.Empty;
            UseButton.IsEnabled = false;
            return;
        }

        PreviewBox.Text = row.Preview;
        UseButton.IsEnabled = true;
    }

    private async void OnUseClick(object sender, RoutedEventArgs args)
    {
        if (ResultsList.SelectedItem is not LyricsCandidateViewModel row)
        {
            return;
        }

        UseButton.IsEnabled = false;
        StatusText.Text = "正在保存所选歌词…";
        try
        {
            var result = _trackLyricsOperationKey is null ? LyricsSelectionResult.NotApplied
                : await _controller.SelectLyricsAsync(row.Candidate, _trackLyricsOperationKey);
            if (result == LyricsSelectionResult.Saved)
            {
                if (_controller.State.CalibrationPrompt.Length == 0) Close();
                else StatusText.Text = "已使用所选歌词。";
            }
            else if (result == LyricsSelectionResult.SessionOnly)
            {
                StatusText.Text = "本次已使用所选歌词，但未能保存。请检查歌词文件夹后重试。";
                UseButton.IsEnabled = true;
            }
            else
            {
                StatusText.Text = "当前歌曲已经改变，请重新打开选择窗口。";
            }
        }
        catch (IOException)
        {
            StatusText.Text = "无法写入歌词文件夹，请检查文件夹权限。";
            UseButton.IsEnabled = true;
        }
        catch (UnauthorizedAccessException)
        {
            StatusText.Text = "没有权限写入歌词文件夹，请在设置中更换位置。";
            UseButton.IsEnabled = true;
        }
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
    }

}
