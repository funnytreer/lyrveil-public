using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using LyricsXQuiet.App.Services;
using LyricsXQuiet.App.State;
using LyricsXQuiet.Core.Lyrics;
using LyricsXQuiet.Core.Playback;
using LyricsXQuiet.Core.Settings;

namespace LyricsXQuiet.App.Windows;

public partial class QuickPanelWindow : Window
{
    private const double WorkAreaMargin = 16;
    private const double MinimumLyricsPickerHeight = 88;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExAppWindow = 0x00040000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private readonly AppController _controller;
    private readonly Action _openSettings;
    private readonly TrayPopupDismissal _dismissal;
    private string _sourceFilter = "";
    private LyricsCandidateViewModel[] _visibleSearchResults = [];
    private string? _lastTitle, _lastArtist;
    private AppSettings Settings => ((AppViewModel)DataContext).Settings;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _selectionCancellation;
    private LyricsCandidateViewModel[]? _cachedCandidates;
    private string _cachedCandidateStatus = string.Empty;
    private string? _candidateTrackKey;
    private int _pickerGeneration;
    private bool _applyingCandidate;
    private bool _allowClose;
    private bool _repositionPending;
    private double _collapsedPanelHeight;

    public QuickPanelWindow(
        AppViewModel viewModel,
        AppController controller,
        Action openSettings)
    {
        DataContext = viewModel;
        _controller = controller;
        _openSettings = openSettings;
        InitializeComponent();
        LyricsXQuiet.App.Services.ThemeManager.AttachChrome(this);
        Calibration.Attach(controller);
        _dismissal = new TrayPopupDismissal(this, HidePanel);
        QueryTitle.Text = controller.CurrentTrack?.Title ?? "";
        QueryArtist.Text = controller.CurrentTrack?.Artist ?? "";
        UpdateScope();
        RefreshPresets();
        _controller.CurrentTrackChanged += OnCurrentTrackChanged;
        viewModel.Settings.PropertyChanged += OnSettingsPropertyChanged;
        Closing += OnClosing;
        SizeChanged += OnSizeChanged;
        UpdatePlayerOffsetControls(_controller.CurrentTrack);
        UpdateTrackCalibrationControls(_controller.CurrentTrack);
    }

    public void ShowPanel()
    {
        UpdatePlayerOffsetControls(_controller.CurrentTrack);
        UpdateTrackCalibrationControls(_controller.CurrentTrack);
        UpdateWorkingAreaConstraint();
        Show();
        ApplyToolWindowStyle();
        _dismissal.TakeFocus();
        var fade = new System.Windows.Media.Animation.DoubleAnimation(.7, 1, TimeSpan.FromMilliseconds(120));
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(fade,
            LyricsXQuiet.Platform.Windows.Interop.DisplayMetrics.GetRefreshRate(new WindowInteropHelper(this).Handle));
        BeginAnimation(OpacityProperty, fade);
        UpdateLayout();
        ScheduleRepositionPanel();
    }

    public void TogglePanel()
    {
        if (IsVisible)
        {
            HidePanel();
            return;
        }

        if (!_dismissal.JustDismissed) ShowPanel();
    }

    public void HidePanel()
    {
        if (!IsVisible) return;
        _dismissal.RecordDismissal();
        CollapseLyricsPicker();
        Hide();
    }

    public void ClosePermanently()
    {
        CancelPickerOperations(clearCache: true);
        _controller.CurrentTrackChanged -= OnCurrentTrackChanged;
        if (DataContext is AppViewModel viewModel)
        {
            viewModel.Settings.PropertyChanged -= OnSettingsPropertyChanged;
        }
        _dismissal.Dispose();
        _allowClose = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        HidePanel();
    }

    protected override void OnSourceInitialized(EventArgs args)
    {
        base.OnSourceInitialized(args);
        ApplyToolWindowStyle();
    }

    private void ApplyToolWindowStyle()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        var extendedStyle = GetExtendedStyle(handle);
        var toolWindowStyle = (extendedStyle | WsExToolWindow) & ~WsExAppWindow;
        if (toolWindowStyle != extendedStyle)
        {
            SetExtendedStyle(handle, toolWindowStyle);
        }

        _ = SetWindowPos(
            handle,
            nint.Zero,
            0,
            0,
            0,
            0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private void OnHideClick(object sender, RoutedEventArgs args)
    {
        HidePanel();
    }

    private void OnOffsetBackClick(object sender, RoutedEventArgs args) =>
        AdjustPlayerOffset(-100);

    private void OnFontSmallerClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is AppViewModel viewModel)
        {
            viewModel.Settings.FontSize -= 1;
        }
    }

    private void OnFontLargerClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is AppViewModel viewModel)
        {
            viewModel.Settings.FontSize += 1;
        }
    }

    private void OnOffsetForwardClick(object sender, RoutedEventArgs args) =>
        AdjustPlayerOffset(100);

    private void AdjustPlayerOffset(int changeMilliseconds)
    {
        if (_controller.CurrentTrack is not { IsUsable: true } track
            || DataContext is not AppViewModel viewModel)
        {
            return;
        }

        var current = viewModel.Settings.GetPlayerLyricOffsetMilliseconds(track.SourceAppUserModelId);
        viewModel.Settings.SetPlayerLyricOffsetMilliseconds(
            track.SourceAppUserModelId, current + changeMilliseconds);
    }

    private void UpdatePlayerOffsetControls(TrackIdentity? track)
    {
        var player = MusicPlayerCatalog.Identify(track?.SourceAppUserModelId);
        var supported = track is { IsUsable: true }
            && MusicPlayerCatalog.SupportedPlayers.Any(item => item.Id == player?.Id);
        PlayerOffsetPanel.IsEnabled = supported;
        PlayerScope.ToolTip = player?.DisplayName ?? "未连接播放器";
        PlayerOffsetSourceText.Text = player?.Id switch
        {
            "netease" => "网易云",
            "qqmusic" => "QQ",
            "kugou" => "酷狗",
            "applemusic" => "Apple",
            "spotify" => "Spotify",
            null => "播放器",
            _ => "本地"
        };
        if (!supported || DataContext is not AppViewModel viewModel)
        {
            PlayerOffsetValueText.Text = "—";
            return;
        }

        var value = viewModel.Settings.GetPlayerLyricOffsetMilliseconds(track!.SourceAppUserModelId);
        PlayerOffsetValueText.Text = $"{value:+0;-0;0} ms";
        PlayerOffsetPanel.ToolTip = player!.Id == "netease"
            ? $"0 ms 使用默认补偿：延后 {viewModel.Settings.NetEaseSyncDelayMilliseconds} ms。负值继续延后，正值提前；默认补偿可在完整设置中修改。"
            : "只调整当前播放器，切换播放器时自动使用各自保存的数值。负值延后，正值提前。";
        PlayerOffsetBackButton.IsEnabled = value > (player!.Id == "applemusic"
            ? 0 : PlayerLyricsPositionCompensation.MinimumMilliseconds);
        PlayerOffsetForwardButton.IsEnabled = value < PlayerLyricsPositionCompensation.MaximumMilliseconds;
    }

    private void OnTrackCalibrationBackClick(object sender, RoutedEventArgs args) =>
        AdjustTrackCalibration(-100);

    private void OnTrackCalibrationForwardClick(object sender, RoutedEventArgs args) =>
        AdjustTrackCalibration(100);

    private void OnTrackCalibrationResetClick(object sender, RoutedEventArgs args)
    {
        var track = _controller.CurrentTrack;
        if (track is not { IsUsable: true } || DataContext is not AppViewModel viewModel)
        {
            return;
        }

        viewModel.Settings.ClearTrackLyricOffset(track);
        UpdateTrackCalibrationControls(track);
    }

    private void AdjustTrackCalibration(int changeMilliseconds)
    {
        var track = _controller.CurrentTrack;
        if (track is not { IsUsable: true } || DataContext is not AppViewModel viewModel)
        {
            return;
        }

        var current = viewModel.Settings.GetTrackLyricOffsetMilliseconds(track);
        viewModel.Settings.SetTrackLyricOffsetMilliseconds(track, current + changeMilliseconds);
        UpdateTrackCalibrationControls(track);
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        RefreshPresets();
        if (args.PropertyName is nameof(AppSettings.NetEaseSyncDelayMilliseconds)
            or nameof(AppSettings.NetEaseOffsetMilliseconds)
            or nameof(AppSettings.AppleMusicSyncAdvanceMilliseconds)
            or nameof(AppSettings.QqMusicOffsetMilliseconds)
            or nameof(AppSettings.KugouOffsetMilliseconds)
            or nameof(AppSettings.SpotifyOffsetMilliseconds))
        {
            UpdatePlayerOffsetControls(_controller.CurrentTrack);
        }

        if (args.PropertyName == nameof(AppSettings.TrackLyricOffsetsMilliseconds))
        {
            UpdateTrackCalibrationControls(_controller.CurrentTrack);
        }
    }

    private void UpdateTrackCalibrationControls(TrackIdentity? track)
    {
        if (TrackCalibrationPanel is null || TrackCalibrationValueText is null)
        {
            return;
        }

        var hasTrack = track is { IsUsable: true };
        TrackCalibrationPanel.IsEnabled = hasTrack;
        if (!hasTrack || DataContext is not AppViewModel viewModel)
        {
            TrackCalibrationValueText.Text = "无歌曲";
            return;
        }

        var value = viewModel.Settings.GetTrackLyricOffsetMilliseconds(track!);
        TrackCalibrationValueText.Text = $"{value} ms";
    }

    private async void OnLyricsPickerToggleClick(object sender, RoutedEventArgs args)
    {
        ScheduleRepositionPanel();
        if (LyricsPickerToggle.IsChecked != true)
        {
            CancelPickerOperations(clearCache: false);
            return;
        }

        await SearchCurrentLyricsAsync();
    }

    private async Task SearchCurrentLyricsAsync()
    {
        CancelSearch();

        var track = _controller.CurrentTrack;
        if (track is not { IsUsable: true })
        {
            _candidateTrackKey = null;
            _cachedCandidates = null;
            SetCandidates(null);
            LyricsPickerStatusText.Text = "请先在受支持的音乐播放器中播放一首歌曲。";
            return;
        }

        var expectedTrackKey = track.LyricsOperationKey;
        var title = string.IsNullOrWhiteSpace(QueryTitle.Text) ? track.Title : QueryTitle.Text.Trim();
        var artist = QueryArtist.Text.Trim();
        if (_lastTitle != title || _lastArtist != artist) _cachedCandidates = null;
        (_lastTitle, _lastArtist) = (title, artist);
        if (string.Equals(_candidateTrackKey, expectedTrackKey, StringComparison.Ordinal)
            && _cachedCandidates is not null)
        {
            SetCandidates(_cachedCandidates);
            LyricsPickerStatusText.Text = _cachedCandidateStatus;
            LyricsCandidatesList.IsEnabled = true;
            return;
        }

        _candidateTrackKey = null;
        _cachedCandidates = null;
        SetCandidates(null);
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        var generation = ++_pickerGeneration;
        LyricsCandidatesList.IsEnabled = false;
        LyricsPickerStatusText.Text = "正在搜索匹配的歌词版本…";

        try
        {
            var candidates = await _controller.SearchLyricsAsync(
                title,
                artist,
                cancellation.Token);
            if (cancellation.IsCancellationRequested
                || generation != _pickerGeneration
                || LyricsPickerToggle.IsChecked != true
                || !string.Equals(
                    _controller.CurrentTrack?.LyricsOperationKey,
                    expectedTrackKey,
                    StringComparison.Ordinal))
            {
                return;
            }

            var rows = candidates.Candidates
                .Select((candidate, index) => new LyricsCandidateViewModel(candidate, track, index == 0))
                .ToArray();
            // Failed/partial searches can be retried by reopening the picker.
            _candidateTrackKey = candidates.Failures.Count == 0 ? expectedTrackKey : null;
            _cachedCandidates = candidates.Failures.Count == 0 ? rows : null;
            SetCandidates(rows);
            _candidateTrackKey = rows.Length > 0 ? expectedTrackKey : _candidateTrackKey;
            _cachedCandidateStatus = LyricsCandidateDisplay.GetSearchStatus(candidates,
                rows.FirstOrDefault()?.RecommendationSummary);
            LyricsPickerStatusText.Text = _cachedCandidateStatus;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            if (generation == _pickerGeneration
                && LyricsPickerToggle.IsChecked == true)
            {
                _candidateTrackKey = null;
                _cachedCandidates = null;
                LyricsPickerStatusText.Text = "歌词服务暂时不可用，请稍后收起再试。";
            }
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            {
                _searchCancellation = null;
                cancellation.Dispose();
                LyricsCandidatesList.IsEnabled = !_applyingCandidate;
            }
        }
    }

    private async void OnLyricsCandidateMouseUp(object sender, MouseButtonEventArgs args)
    {
        if (args.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(LyricsCandidatesList, source) is not ListBoxItem item
            || item.DataContext is not LyricsCandidateViewModel row)
        {
            return;
        }

        item.IsSelected = true;
        await ApplyCandidateAsync(row);
    }

    private async void OnLyricsCandidateKeyDown(object sender, System.Windows.Input.KeyEventArgs args)
    {
        if (args.Key is not (Key.Enter or Key.Space)
            || LyricsCandidatesList.SelectedItem is not LyricsCandidateViewModel row)
        {
            return;
        }

        args.Handled = true;
        await ApplyCandidateAsync(row);
    }

    private async Task ApplyCandidateAsync(LyricsCandidateViewModel row)
    {
        if (_applyingCandidate || _candidateTrackKey is null)
        {
            return;
        }

        CancelSelection();
        var expectedTrackKey = _candidateTrackKey;
        var cancellation = new CancellationTokenSource();
        _selectionCancellation = cancellation;
        var generation = ++_pickerGeneration;
        _applyingCandidate = true;
        LyricsCandidatesList.IsEnabled = false;
        LyricsPickerStatusText.Text = $"正在使用“{row.Title}”…";
        try
        {
            var applied = await _controller.SelectLyricsAsync(
                row.Candidate,
                expectedTrackKey,
                cancellation.Token);
            if (cancellation.IsCancellationRequested
                || generation != _pickerGeneration
                || LyricsPickerToggle.IsChecked != true)
            {
                return;
            }

            if (applied != LyricsSelectionResult.NotApplied)
            {
                LyricsPickerStatusText.Text = applied == LyricsSelectionResult.Saved
                    ? $"已选择 {row.Provider} 的歌词，下次播放仍会优先使用。"
                    : "本次已使用所选歌词，但未能保存。请检查歌词文件夹后重试。";
                return;
            }

            LyricsPickerStatusText.Text = "当前歌曲已经改变，正在重新搜索…";
            _candidateTrackKey = null;
            _cachedCandidates = null;
            await SearchCurrentLyricsAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            LyricsPickerStatusText.Text = "无法写入歌词文件夹，请检查文件夹权限。";
            ClearCandidateSelection();
        }
        catch (UnauthorizedAccessException)
        {
            LyricsPickerStatusText.Text = "没有权限写入歌词文件夹，请在设置中更换位置。";
            ClearCandidateSelection();
        }
        finally
        {
            if (ReferenceEquals(_selectionCancellation, cancellation))
            {
                _selectionCancellation = null;
                cancellation.Dispose();
                _applyingCandidate = false;
                LyricsCandidatesList.IsEnabled = _searchCancellation is null;
            }
        }
    }

    private async void OnCurrentTrackChanged(object? sender, TrackIdentity? track)
    {
        QueryTitle.Text = track?.Title ?? "";
        QueryArtist.Text = track?.Artist ?? "";
        UpdatePlayerOffsetControls(track);
        UpdateTrackCalibrationControls(track);
        CancelPickerOperations(clearCache: true);
        _candidateTrackKey = null;
        _cachedCandidates = null;
        SetCandidates(null);
        if (LyricsPickerToggle.IsChecked == true)
        {
            await SearchCurrentLyricsAsync();
        }
    }

    private void SetCandidates(IEnumerable<LyricsCandidateViewModel>? rows)
    {
        LyricsCandidatesList.SelectedItem = null;
        _visibleSearchResults = rows?.ToArray() ?? [];
        ApplySourceFilter();
    }

    private void ClearCandidateSelection() => LyricsCandidatesList.SelectedItem = null;

    private void CollapseLyricsPicker()
    {
        LyricsPickerToggle.IsChecked = false;
        PanelScroller.ScrollToTop();
        CancelPickerOperations(clearCache: false);
    }

    private void CancelPickerOperations(bool clearCache)
    {
        _pickerGeneration++;
        CancelSearch();
        CancelSelection();
        if (clearCache)
        {
            _candidateTrackKey = null;
            _cachedCandidates = null;
        }
    }

    private void CancelSearch()
    {
        var cancellation = _searchCancellation;
        _searchCancellation = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void CancelSelection()
    {
        var cancellation = _selectionCancellation;
        _selectionCancellation = null;
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        _applyingCandidate = false;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (LyricsPickerToggle.IsChecked != true && ActualHeight > 0)
        {
            _collapsedPanelHeight = ActualHeight;
        }

        ScheduleRepositionPanel();
    }

    private void ScheduleRepositionPanel()
    {
        if (!IsVisible || _repositionPending)
        {
            return;
        }

        _repositionPending = true;
        _ = Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() =>
            {
                _repositionPending = false;
                if (!IsVisible)
                {
                    return;
                }

                UpdateWorkingAreaConstraint();
                UpdateLayout();
                RepositionPanel();
            }));
    }

    private void UpdateWorkingAreaConstraint()
    {
        var workingArea = WindowPlacementService.GetCursorWorkingAreaInDips();
        MaxHeight = WindowPlacementService.QuickPanelHeight(workingArea.Height);

        if (_collapsedPanelHeight > 0)
        {
            var availablePickerHeight = MaxHeight
                - _collapsedPanelHeight
                - LyricsPickerPanel.Margin.Top;
            LyricsPickerPanel.MaxHeight = Math.Max(
                MinimumLyricsPickerHeight,
                availablePickerHeight);
        }
    }

    private void RepositionPanel()
    {
        if (!IsVisible)
        {
            return;
        }

        WindowPlacementService.PlaceBottomRightOnCursorMonitor(this, WorkAreaMargin);
    }

    private async void OnImportClick(object sender, RoutedEventArgs args)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择同步歌词文件",
            Filter = "同步歌词 (*.lrc;*.lrcx)|*.lrc;*.lrcx|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        _dismissal.ModalOpen = true;
        bool? chosen;
        try { chosen = dialog.ShowDialog(this); }
        finally { _dismissal.ModalOpen = false; }
        if (chosen == true) await _controller.ImportLyricsAsync(dialog.FileName);
    }

    private void OnPresetClick(object sender, RoutedEventArgs args)
    {
        var name = ((System.Windows.Controls.RadioButton)sender).Content as string;
        if (AppearancePresets.BuiltIn.FirstOrDefault(p => p.Name == name) is { } preset)
            Settings.ApplyAppearance(preset);
        RefreshPresets();
    }

    private void RefreshPresets()
    {
        foreach (System.Windows.Controls.RadioButton button in Presets.Children)
        {
            var name = (string)button.Content;
            button.IsChecked = AppearancePresets.BuiltIn.Any(p => p.Name == name && p == Settings.CaptureAppearance(name));
        }
    }

    private void OnScopeClick(object sender, RoutedEventArgs e) => UpdateScope();

    private void UpdateScope()
    {
        var player = PlayerScope.IsChecked == true;
        PlayerOffsetPanel.Visibility = player ? Visibility.Visible : Visibility.Collapsed;
        TrackCalibrationPanel.Visibility = player ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPlayerResetClick(object sender, RoutedEventArgs e)
    {
        if (_controller.CurrentTrack is { } track) Settings.SetPlayerLyricOffsetMilliseconds(track.SourceAppUserModelId, 0);
    }

    private async void OnQueryKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchCurrentLyricsAsync();
    }

    private void OnSourceClick(object sender, RoutedEventArgs e)
    {
        _sourceFilter = (string)((FrameworkElement)sender).Tag;
        ApplySourceFilter();
    }

    private void ApplySourceFilter() => LyricsCandidatesList.ItemsSource = _sourceFilter.Length == 0
        ? _visibleSearchResults : _visibleSearchResults.Where(row => row.Provider == _sourceFilter).ToArray();

    private void OnCompareClick(object sender, RoutedEventArgs e)
    {
        HidePanel();
        LyricsSearchWindow.Open(_controller, title: QueryTitle.Text, artist: QueryArtist.Text);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs args)
    {
        HidePanel();
        _openSettings();
    }

    private static long GetExtendedStyle(nint handle) => IntPtr.Size == 8
        ? GetWindowLongPtr(handle, GwlExStyle).ToInt64()
        : GetWindowLong(handle, GwlExStyle);

    private static void SetExtendedStyle(nint handle, long style)
    {
        if (IntPtr.Size == 8)
        {
            _ = SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
            return;
        }

        _ = SetWindowLong(handle, GwlExStyle, unchecked((int)style));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint newLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(nint windowHandle, int index, int newLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

}
