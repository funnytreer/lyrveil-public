using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Windows.Media;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using LyricsXQuiet.App.State;
using LyricsXQuiet.App.Windows;
using LyricsXQuiet.Core.Lyrics;
using LyricsXQuiet.Core.Playback;
using LyricsXQuiet.Core.Settings;

namespace LyricsXQuiet.App.Services;

public sealed class AppController : IDisposable
{
    private static readonly TimeSpan NetEaseSyncNoticeDelay = TimeSpan.FromSeconds(3);
    private readonly IPlaybackService _playbackService;
    private readonly LyricsResolver _lyricsResolver;
    private readonly AppSettings _settings;
    private readonly AppState _state;
    private readonly OverlayWindow _overlay;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _firstLineWake;
    private PlaybackSnapshot? _clockSnapshot;
    private DateTimeOffset _clockAnchor;
    private long _clockTimestamp;
    private TimeSpan _lyricCorrection;
    private bool _hasTimedFrame;
    private bool _rendering;
    private TimeSpan? _lastRenderingTime;
    private readonly DispatcherTimer _appleMusicSeekSettleTimer;
    private CancellationTokenSource? _loadCancellation;
    private long _lyricsOperationVersion;
    private PlaybackSnapshot? _snapshot;
    private PlaybackSnapshot? _pendingAppleMusicSeekSnapshot;
    private LyricDocument? _sourceDocument;
    private LyricTimeline? _timeline;
    private NetEaseSyncCapability? _netEaseSyncCapability;
    private NetEaseSyncCapability? _pendingNetEaseSyncNotice;
    private DateTimeOffset? _pendingNetEaseSyncNoticeSince;
    private string _documentStatus = string.Empty;
    private string? _loadedTrackKey;
    private string? _loadedLyricsCalibrationKey;
    private string? _activeLyricsTrackKey;
    private long? _suppressedTrackInfoOperationVersion;
    private bool _started;
    private bool _demoMode;

    public event EventHandler<TrackIdentity?>? CurrentTrackChanged;

    public AppController(
        IPlaybackService playbackService,
        LyricsResolver lyricsResolver,
        AppSettings settings,
        AppState state,
        OverlayWindow overlay)
    {
        _playbackService = playbackService;
        _lyricsResolver = lyricsResolver;
        _settings = settings;
        _state = state;
        _overlay = overlay;
        _dispatcher = overlay.Dispatcher;
        _firstLineWake = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher);
        _firstLineWake.Tick += OnFirstLineWake;
        _appleMusicSeekSettleTimer = new DispatcherTimer(
            AppleMusicSeekSettlingPolicy.SettleDelay,
            DispatcherPriority.DataBind,
            OnAppleMusicSeekSettleTimer,
            _dispatcher);
        _appleMusicSeekSettleTimer.Stop();
    }

    public AppState State => _state;
    private TrackIdentity? _calibrationTrack;

    public void ResolveCalibration(bool clear)
    {
        if (clear && _calibrationTrack is { } track && track.LyricCalibrationKey == CurrentTrack?.LyricCalibrationKey)
            _settings.ClearTrackLyricOffset(track);
        _calibrationTrack = null;
        _state.CalibrationPrompt = "";
    }

    private void OfferCalibration(TrackIdentity track)
    {
        var offset = _settings.GetTrackLyricOffsetMilliseconds(track);
        _calibrationTrack = offset == 0 ? null : track;
        _state.CalibrationPrompt = offset == 0 ? "" : $"已更换歌词。本曲原有校准为 {offset:+0;-0;0} ms。";
    }

    public TrackIdentity? CurrentTrack => _snapshot?.Track;

    public string GetPlaybackCompatibilitySummary()
    {
        var snapshot = _snapshot;
        var connection = _playbackService.ConnectionStatus;
        if (snapshot is null)
        {
            var connectionDescription = connection.State switch
            {
                PlaybackConnectionState.PlayerNeedsMediaSession => "已发现播放器，但尚未发布媒体会话",
                PlaybackConnectionState.Connected => "已连接，正在等待歌曲信息",
                _ => "正在等待播放器"
            };
            var guidance = connection.State == PlaybackConnectionState.PlayerNeedsMediaSession
                ? "已检测到播放器，但它还没有向 Windows 发布媒体会话。请在播放器设置中开启系统媒体控制（SMTC）。"
                : "尚未检测到受支持的播放器会话。请先播放一首歌曲，再重新检查。";
            return string.Join(
                Environment.NewLine,
                "播放器兼容性检查",
                "",
                $"连接：{connectionDescription}",
                $"结果：{guidance}");
        }

        var player = MusicPlayerCatalog.Identify(snapshot.Track.SourceAppUserModelId);
        var metadataReady = !string.IsNullOrWhiteSpace(snapshot.Track.Title)
            && !string.IsNullOrWhiteSpace(snapshot.Track.Artist);
        var durationReady = snapshot.Track.Duration > TimeSpan.Zero;
        var playbackStateDescription = snapshot.State switch
        {
            PlaybackState.Playing => "正在播放",
            PlaybackState.Paused => "已暂停",
            PlaybackState.Stopped => "已停止",
            PlaybackState.Closed => "播放器已关闭",
            _ => "状态未知"
        };
        var timelineDescription = snapshot.TimelineKind switch
        {
            PlaybackTimelineKind.Reported => "完整时间线，可跟随播放器拖动",
            PlaybackTimelineKind.EnhancedEstimated => "增强时间线暂时断开，正在本地续时",
            PlaybackTimelineKind.Estimated => "本地估算时间，拖动后可能需要校准",
            _ => "没有可用时间线，无法可靠跟随拖动"
        };
        var nextStep = snapshot.TimelineKind == PlaybackTimelineKind.Reported
            ? "当前适配正常，无需额外设置。"
            : player?.Id == "netease"
                ? "网易云可尝试从词间创建的组合快捷方式启动，以启用实验性精确进度。"
                : "请确认播放器已开启系统媒体控制；若仍无时间线，该播放器版本可能只发布歌曲信息。";

        return string.Join(
            Environment.NewLine,
            "播放器兼容性检查",
            "",
            $"播放器：{player?.DisplayName ?? "未识别的媒体会话"}",
            $"播放状态：{playbackStateDescription}",
            $"歌曲信息：{(metadataReady ? "完整" : "不完整")}",
            $"专辑：{(string.IsNullOrWhiteSpace(snapshot.Track.Album) ? "未提供" : "已提供")}",
            $"时长：{(durationReady ? "已提供" : "未提供")}",
            $"时间线：{timelineDescription}",
            "",
            $"建议：{nextStep}");
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _overlay.RenderVisibilityChanged += OnRenderVisibilityChanged;
        _playbackService.SnapshotChanged += OnSnapshotChanged;
        _playbackService.ConnectionStatusChanged += OnConnectionStatusChanged;
        _settings.PropertyChanged += OnSettingsChanged;
        if (_playbackService.Current is not null)
        {
            AcceptSnapshot(_playbackService.Current);
        }
        else
        {
            ApplyConnectionStatus(_playbackService.ConnectionStatus);
        }
    }

    public void StartDemo()
    {
        Start();
        _demoMode = true;
        var demoTrack = new TrackIdentity(
            "一行之间",
            "词间 · Lyrveil",
            "桌面歌词演示",
            TimeSpan.FromSeconds(36),
            "demo");
        _snapshot = new PlaybackSnapshot(
            demoTrack,
            PlaybackState.Playing,
            TimeSpan.FromSeconds(7.8),
            1,
            DateTimeOffset.Now,
            PlaybackTimelineKind.Reported);
        CurrentTrackChanged?.Invoke(this, demoTrack);
        UseDocument(
            LrcParser.Parse(DemoLyrics, "内置演示"),
            "内置增强 LRC · 逐字同步");
        _loadedTrackKey = demoTrack.LyricsOperationKey;
        _loadedLyricsCalibrationKey = demoTrack.LyricCalibrationKey;
        _state.TrackDisplay = demoTrack.DisplayName;
        _state.PlayerDisplay = "演示模式";
        UpdateFrame();
    }

    public async Task RetryLyricsAsync()
    {
        if (_demoMode || _snapshot?.Track is not { IsUsable: true } track)
        {
            return;
        }

        await LoadLyricsAsync(track, true);
    }

    public async Task<bool> ImportLyricsAsync(string path)
    {
        if (_snapshot?.Track is not { IsUsable: true } track)
        {
            return false;
        }

        var operationVersion = TakeManualLyricsOwnership(track.LyricsOperationKey);
        try
        {
            var result = await _lyricsResolver.ImportWithStatusAsync(track, path,
                documentAvailable: document => ApplyAvailableDocument(track, operationVersion,
                    document, "正在使用导入歌词 · 正在保存…"));
            var document = result.Document;
            if (!CanApplyLyricsOperation(track.LyricsOperationKey, operationVersion))
            {
                return false;
            }

            if (document is null)
            {
                _state.LyricsStatus = "这个文件没有可用的同步时间标签";
                return false;
            }

            ReleaseTrackInfoSuppression(operationVersion);
            UseDocument(document, result.SaveFailed
                ? "正在使用导入歌词 · 未能保存，请检查歌词文件夹"
                : "已固定使用手动导入的歌词");
            _loadedTrackKey = track.LyricsOperationKey;
            _loadedLyricsCalibrationKey = track.LyricCalibrationKey;
            OfferCalibration(track);
            UpdateFrame();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (CanApplyLyricsOperation(track.LyricsOperationKey, operationVersion))
            {
                _state.LyricsStatus = "无法读取这个歌词文件";
            }

            return false;
        }
        finally
        {
            CompleteLyricsOperation(track.LyricsOperationKey, operationVersion);
        }
    }

    public async Task<LyricsSearchResult> SearchLyricsAsync(
        string title,
        string artist,
        CancellationToken cancellationToken = default)
    {
        if (_snapshot?.Track is not { IsUsable: true } currentTrack
            || string.IsNullOrWhiteSpace(title))
        {
            return LyricsSearchResult.Empty;
        }

        var searchTrack = currentTrack with
        {
            Title = title.Trim(),
            Artist = artist.Trim()
        };
        var candidates = await _lyricsResolver.SearchWithStatusAsync(searchTrack, cancellationToken);
        if (_snapshot?.Track.LyricsOperationKey != currentTrack.LyricsOperationKey)
        {
            return LyricsSearchResult.Empty;
        }

        var sources = new[] { ("网易云音乐", _settings.EnableNetEaseLyrics), ("QQ 音乐", _settings.EnableQqMusicLyrics),
            ("酷狗音乐", _settings.EnableKugouLyrics), ("LRCLIB", _settings.EnableLrclibLyrics) };
        _state.SourceHealth = string.Join("　", sources.Select(source =>
        {
            var failure = candidates.Failures.FirstOrDefault(f => f.Provider == source.Item1);
            var status = !source.Item2 ? "已关闭" : failure?.Kind switch
            {
                LyricsProviderFailureKind.Network => "网络错误", LyricsProviderFailureKind.Timeout => "超时",
                LyricsProviderFailureKind.InvalidResponse => "响应异常", _ => "已响应"
            };
            return $"{source.Item1} · {status}";
        }));
        return candidates;
    }

    public async Task<LyricsSelectionResult> SelectLyricsAsync(
        LyricsCandidate candidate,
        string expectedTrackKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_snapshot?.Track is not { IsUsable: true } track
            || !string.Equals(track.LyricsOperationKey, expectedTrackKey, StringComparison.Ordinal))
        {
            return LyricsSelectionResult.NotApplied;
        }

        var operationVersion = TakeManualLyricsOwnership(expectedTrackKey);
        var selectionPresented = false;
        try
        {
            var result = await _lyricsResolver.SaveSelectionAsync(track, candidate, cancellationToken,
                document =>
                {
                    ApplyAvailableDocument(track, operationVersion, document,
                        $"正在使用 {candidate.Provider} · 正在保存…");
                    selectionPresented = true;
                });
            if (!CanApplyLyricsOperation(expectedTrackKey, operationVersion))
            {
                return LyricsSelectionResult.NotApplied;
            }

            ReleaseTrackInfoSuppression(operationVersion);
            UseDocument(
                candidate.Document,
                result.SaveFailed
                    ? $"正在使用 {candidate.Provider} · 未能保存，请检查歌词文件夹"
                    : $"已固定使用 {candidate.Provider} · {candidate.Title}");
            _loadedTrackKey = track.LyricsOperationKey;
            _loadedLyricsCalibrationKey = track.LyricCalibrationKey;
            OfferCalibration(track);
            UpdateFrame();
            return result.SaveFailed ? LyricsSelectionResult.SessionOnly : LyricsSelectionResult.Saved;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (selectionPresented && CanApplyLyricsOperation(expectedTrackKey, operationVersion))
            {
                _documentStatus = $"正在使用 {candidate.Provider} · 保存已取消，本次播放仍可使用";
                _state.LyricsStatus = ComposeDocumentStatus();
                UpdateFrame();
            }
            throw;
        }
        finally
        {
            CompleteLyricsOperation(expectedTrackKey, operationVersion);
        }
    }

    private void OnSnapshotChanged(object? sender, PlaybackSnapshot? snapshot)
    {
        _dispatcher.BeginInvoke(() => ReceiveSnapshot(snapshot));
    }

    private void ApplyAvailableDocument(TrackIdentity track, long operationVersion,
        LyricDocument document, string status)
    {
        if (!CanApplyLyricsOperation(track.LyricsOperationKey, operationVersion)) return;
        ReleaseTrackInfoSuppression(operationVersion);
        UseDocument(document, status);
        _loadedTrackKey = track.LyricsOperationKey;
        _loadedLyricsCalibrationKey = track.LyricCalibrationKey;
        UpdateFrame();
    }

    private void ReceiveSnapshot(PlaybackSnapshot? snapshot)
    {
        var newestKnown = _pendingAppleMusicSeekSnapshot ?? _snapshot;
        if (AppleMusicSeekSettlingPolicy.IsOlderThan(newestKnown, snapshot))
        {
            return;
        }

        if (_pendingAppleMusicSeekSnapshot is not null)
        {
            switch (AppleMusicSeekSettlingPolicy.ClassifyPendingCandidate(
                        _snapshot,
                        _pendingAppleMusicSeekSnapshot,
                        snapshot))
            {
                case AppleMusicSeekSettlingPolicy.PendingCandidateKind.Older:
                    return;

                case AppleMusicSeekSettlingPolicy.PendingCandidateKind.AcceptedPosition:
                    // Apple Music can publish the pre-seek position after the target position.
                    // Keep the target pending; rendering the old cluster here causes the lyrics to
                    // flash back or remain ahead until another periodic update arrives.
                    return;

                case AppleMusicSeekSettlingPolicy.PendingCandidateKind.PendingPosition:
                    _pendingAppleMusicSeekSnapshot = snapshot;
                    return;

                case AppleMusicSeekSettlingPolicy.PendingCandidateKind.ReplacementPosition:
                    _pendingAppleMusicSeekSnapshot = snapshot;
                    _appleMusicSeekSettleTimer.Stop();
                    _appleMusicSeekSettleTimer.Start();
                    return;

                case AppleMusicSeekSettlingPolicy.PendingCandidateKind.NotApplicable:
                    CancelAppleMusicSeekSettling();
                    AcceptSnapshot(snapshot);
                    return;
            }
        }

        if (!AppleMusicSeekSettlingPolicy.ShouldSettle(_snapshot, snapshot))
        {
            CancelAppleMusicSeekSettling();
            AcceptSnapshot(snapshot);
            return;
        }

        if (_pendingAppleMusicSeekSnapshot is null && _snapshot is not null)
        {
            var frozenAt = DateTimeOffset.Now;
            _snapshot = _snapshot with
            {
                Position = _snapshot.EstimatePosition(frozenAt),
                PlaybackRate = 0,
                CapturedAt = frozenAt
            };
            UpdateFrame();
        }

        _pendingAppleMusicSeekSnapshot = snapshot;
        _appleMusicSeekSettleTimer.Stop();
        _appleMusicSeekSettleTimer.Start();
    }

    private void OnAppleMusicSeekSettleTimer(object? sender, EventArgs args)
    {
        _appleMusicSeekSettleTimer.Stop();
        var snapshot = _pendingAppleMusicSeekSnapshot;
        _pendingAppleMusicSeekSnapshot = null;
        if (snapshot is not null)
        {
            AcceptSnapshot(snapshot);
        }
    }

    private void CancelAppleMusicSeekSettling()
    {
        _appleMusicSeekSettleTimer.Stop();
        _pendingAppleMusicSeekSnapshot = null;
    }

    private void OnConnectionStatusChanged(object? sender, PlaybackConnectionStatus status)
    {
        _dispatcher.BeginInvoke(() => ApplyConnectionStatus(status));
    }

    private void AcceptSnapshot(PlaybackSnapshot? snapshot)
    {
        if (_demoMode)
        {
            return;
        }

        var previousTrack = _snapshot?.Track is { IsUsable: true } usablePreviousTrack
            ? usablePreviousTrack
            : null;
        var previousTrackKey = previousTrack?.StableKey;
        var previousLyricsOperationKey = previousTrack?.LyricsOperationKey;
        var previousCalibrationKey = previousTrack?.LyricCalibrationKey;
        _snapshot = snapshot;
        var currentTrack = snapshot?.Track is { IsUsable: true } usableTrack
            ? usableTrack
            : null;
        var trackChanged = !string.Equals(
            previousTrackKey,
            currentTrack?.StableKey,
            StringComparison.Ordinal);
        var lyricsTrackChanged = !string.Equals(
            previousLyricsOperationKey,
            currentTrack?.LyricsOperationKey,
            StringComparison.Ordinal);
        if (lyricsTrackChanged)
        {
            CancelActiveLyricsLoad();
            Interlocked.Increment(ref _lyricsOperationVersion);
            _activeLyricsTrackKey = null;
        }

        if (trackChanged
            || lyricsTrackChanged
            || !string.Equals(previousTrack?.SourceAppUserModelId,
                currentTrack?.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                previousCalibrationKey,
                currentTrack?.LyricCalibrationKey,
                StringComparison.Ordinal))
        {
            ResolveCalibration(false);
            CurrentTrackChanged?.Invoke(this, currentTrack);
        }

        if (snapshot is null || !snapshot.Track.IsUsable)
        {
            ApplyConnectionStatus(_playbackService.ConnectionStatus);
            return;
        }

        _state.ActivePlayerId = MusicPlayerCatalog.Identify(snapshot.Track.SourceAppUserModelId)?.Id ?? "";
        _state.TrackDisplay = snapshot.Track.DisplayName;
        _state.PlayerDisplay = FriendlyPlayerName(snapshot.Track.SourceAppUserModelId);
        _state.SyncBadge = (MusicPlayerCatalog.Identify(snapshot.Track.SourceAppUserModelId)?.DisplayName ?? "播放器") +
            (snapshot.TimelineKind == PlaybackTimelineKind.Reported ? " · 精确同步" : " · 估算进度");
        UpdatePlaybackSyncState(snapshot);
        var needsInitialLyricsLoad = !string.Equals(
            _loadedTrackKey,
            snapshot.Track.LyricsOperationKey,
            StringComparison.Ordinal);
        var hasExactNetEaseSourceId = SourceTrackIdParser.TryGetNetEaseSongId(
            snapshot.Track.SourceTrackId,
            out _);
        var needsExactSourceUpgrade = !needsInitialLyricsLoad
            && hasExactNetEaseSourceId
            && !string.Equals(
                _loadedLyricsCalibrationKey,
                snapshot.Track.LyricCalibrationKey,
                StringComparison.Ordinal);
        if ((needsInitialLyricsLoad || needsExactSourceUpgrade)
            && !string.Equals(_activeLyricsTrackKey, snapshot.Track.LyricsOperationKey, StringComparison.Ordinal))
        {
            if (needsInitialLyricsLoad)
            {
                ClearDocument();
                _loadedTrackKey = null;
                _loadedLyricsCalibrationKey = null;
            }

            _ = LoadLyricsAsync(snapshot.Track, false);
        }

        UpdateFrame();
    }

    private void ApplyConnectionStatus(PlaybackConnectionStatus status)
    {
        if (_demoMode || _snapshot?.Track is { IsUsable: true })
        {
            return;
        }

        ClearDocument();
        _state.SyncBadge = "等待播放器";
        _state.ActivePlayerId = "";
        _loadedTrackKey = null;
        _loadedLyricsCalibrationKey = null;
        _suppressedTrackInfoOperationVersion = null;
        _overlay.SetContentVisible(false);
        ResetNetEaseSyncState();

        if (status.State == PlaybackConnectionState.PlayerNeedsMediaSession
            && status.Player is { } player)
        {
            _state.TrackDisplay = $"检测到{player.DisplayName}";
            if (string.Equals(player.Id, "netease", StringComparison.OrdinalIgnoreCase))
            {
                _state.PlayerDisplay = "播放器已运行，但未向 Windows 提供播放状态";
                _state.LyricsStatus = "请在网易云音乐 设置 → 系统 中开启 SMTC";
            }
            else
            {
                _state.PlayerDisplay = "播放器尚未向 Windows 提供播放状态";
                _state.LyricsStatus = "请在播放器设置中开启系统媒体控制";
            }

            return;
        }

        if (status.State == PlaybackConnectionState.Connected)
        {
            _state.TrackDisplay = status.Player is null
                ? "已连接音乐播放器"
                : $"已连接{status.Player.DisplayName}";
            _state.PlayerDisplay = "正在等待当前歌曲信息";
            _state.LyricsStatus = "开始播放后会自动匹配歌词";
            return;
        }

        _state.TrackDisplay = "等待播放器";
        _state.PlayerDisplay = "尚未检测到受支持的音乐播放器";
        _state.LyricsStatus = "播放音乐后会自动匹配歌词";
    }

    private async Task LoadLyricsAsync(TrackIdentity track, bool forceNetwork)
    {
        CancelActiveLyricsLoad();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        var cancellationToken = cancellation.Token;
        var operationVersion = Interlocked.Increment(ref _lyricsOperationVersion);
        _activeLyricsTrackKey = track.LyricsOperationKey;
        SuppressTrackInfoWhileLyricsArePending(operationVersion);
        _state.LyricsStatus = forceNetwork ? "正在重新搜索同步歌词…" : "正在匹配同步歌词…";

        try
        {
            var result = await _lyricsResolver.ResolveWithStatusAsync(track, forceNetwork, cancellationToken,
                document => ApplyAvailableDocument(track, operationVersion, document, $"{document.Source}歌词"));
            var document = result.Document;
            if (cancellationToken.IsCancellationRequested
                || !CanApplyLyricsOperation(track.LyricsOperationKey, operationVersion))
            {
                return;
            }

            if (document is null)
            {
                ReleaseTrackInfoSuppression(operationVersion);
                ClearDocument();
                _loadedTrackKey = track.LyricsOperationKey;
                _loadedLyricsCalibrationKey = track.LyricCalibrationKey;
                _state.LyricsStatus = "未找到同步歌词，可手动导入 LRC";
                UpdateFrame();
                return;
            }

            ReleaseTrackInfoSuppression(operationVersion);
            UseDocument(document, result.SaveFailed
                ? $"{document.Source}歌词 · 未能保存，请检查歌词文件夹"
                : $"{document.Source}歌词");
            _loadedTrackKey = track.LyricsOperationKey;
            _loadedLyricsCalibrationKey = track.LyricCalibrationKey;
            UpdateFrame();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or UnauthorizedAccessException)
        {
            if (cancellationToken.IsCancellationRequested
                || !CanApplyLyricsOperation(track.LyricsOperationKey, operationVersion))
            {
                return;
            }

            ReleaseTrackInfoSuppression(operationVersion);
            _loadedTrackKey = track.LyricsOperationKey;
            _loadedLyricsCalibrationKey = track.LyricCalibrationKey;
            if (_sourceDocument is not null)
                _documentStatus = "继续使用当前歌词 · 歌词来源暂时不可用，可稍后重试";
            _state.LyricsStatus = _sourceDocument is null
                ? "歌词来源请求失败，请检查网络或稍后重试"
                : ComposeDocumentStatus();
            UpdateFrame();
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
            {
                _loadCancellation = null;
            }

            CompleteLyricsOperation(track.LyricsOperationKey, operationVersion);
            cancellation.Dispose();
        }
    }

    private long TakeManualLyricsOwnership(string trackKey)
    {
        CancelActiveLyricsLoad();
        var operationVersion = Interlocked.Increment(ref _lyricsOperationVersion);
        _activeLyricsTrackKey = trackKey;
        SuppressTrackInfoWhileLyricsArePending(operationVersion);
        return operationVersion;
    }

    private void SuppressTrackInfoWhileLyricsArePending(long operationVersion)
    {
        if (_sourceDocument is null)
        {
            _suppressedTrackInfoOperationVersion = operationVersion;
            _overlay.SetContentVisible(false);
        }
    }

    private void ReleaseTrackInfoSuppression(long operationVersion)
    {
        if (_suppressedTrackInfoOperationVersion == operationVersion)
        {
            _suppressedTrackInfoOperationVersion = null;
        }
    }

    private void CancelActiveLyricsLoad()
    {
        var cancellation = _loadCancellation;
        _loadCancellation = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private bool CanApplyLyricsOperation(string trackKey, long operationVersion) =>
        Volatile.Read(ref _lyricsOperationVersion) == operationVersion
        && string.Equals(
            _snapshot?.Track.LyricsOperationKey,
            trackKey,
            StringComparison.Ordinal);

    private void CompleteLyricsOperation(string trackKey, long operationVersion)
    {
        if (Volatile.Read(ref _lyricsOperationVersion) == operationVersion
            && string.Equals(_activeLyricsTrackKey, trackKey, StringComparison.Ordinal))
        {
            _activeLyricsTrackKey = null;
        }

        ReleaseTrackInfoSuppression(operationVersion);
    }

    private void OnFirstLineWake(object? sender, EventArgs args) => UpdateFrame();

    private void OnRenderVisibilityChanged(object? sender, EventArgs args) => UpdateRenderingSubscription();

    private void UpdateRenderingSubscription()
    {
        var active = _started && _hasTimedFrame && _overlay.CanRenderLyrics && _settings.ShowLyrics
            && _snapshot is { State: PlaybackState.Playing, PlaybackRate: > 0 }
            && _suppressedTrackInfoOperationVersion is null;
        if (_rendering == active) return;
        _rendering = active;
        _lastRenderingTime = null;
        if (active) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs args)
    {
        if (!_rendering || args is not RenderingEventArgs frame || _lastRenderingTime == frame.RenderingTime) return;
        _lastRenderingTime = frame.RenderingTime;
        PresentFrame();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AppSettings.LyricsFilterEnabled)
            or nameof(AppSettings.LyricsFilterPatterns)
            or nameof(AppSettings.ShowLyricsCredits)
            or nameof(AppSettings.ShowTrackInfoWhenNoLyrics))
        {
            RebuildTimeline();
        }

        UpdateFrame();
    }

    private void UseDocument(LyricDocument document, string status)
    {
        _sourceDocument = document;
        _documentStatus = status;
        RebuildTimeline();
    }

    private void ClearDocument()
    {
        _firstLineWake.Stop();
        _hasTimedFrame = false;
        UpdateRenderingSubscription();
        _sourceDocument = null;
        _timeline = null;
        _documentStatus = string.Empty;
        _state.TimingBadge = "等待歌词";
    }

    private void RebuildTimeline()
    {
        if (_sourceDocument is null)
        {
            _timeline = null;
            return;
        }

        if (_sourceDocument.IsInstrumental)
        {
            _timeline = null;
            _state.TimingBadge = "纯音乐";
            _state.LyricsStatus = _settings.ShowTrackInfoWhenNoLyrics
                ? "纯音乐 · 显示歌曲信息"
                : "纯音乐 · 不显示歌词";
            _overlay.SetContentVisible(false);
            return;
        }

        var document = _settings.ShowLyricsCredits
            ? _sourceDocument
            : LyricFilter.RemoveCredits(_sourceDocument);
        if (_settings.LyricsFilterEnabled)
        {
            document = LyricFilter.Apply(document, _settings.LyricsFilterPatterns);
        }
        if (!document.HasSynchronizedLines)
        {
            _timeline = null;
            _state.TimingBadge = "无可显示歌词";
            _state.LyricsStatus = "全部歌词已被过滤设置移除";
            _overlay.SetContentVisible(false);
            return;
        }

        _state.TimingBadge = document.Lines.Any(line => line.Words.Count > 0) ? "逐字" : "逐行";
        _timeline = new LyricTimeline(document);
        _state.LyricsStatus = ComposeDocumentStatus();
    }

    private void UpdatePlaybackSyncState(PlaybackSnapshot snapshot)
    {
        var player = MusicPlayerCatalog.Identify(snapshot.Track.SourceAppUserModelId);
        if (!string.Equals(player?.Id, "netease", StringComparison.OrdinalIgnoreCase))
        {
            ResetNetEaseSyncState();
        }
        else
        {
            var capability = NetEaseSyncCapabilityClassifier.Classify(
                snapshot.Track,
                snapshot.TimelineKind);
            _netEaseSyncCapability = capability;
            UpdateNetEaseSyncNotice(capability, snapshot.CapturedAt);
        }

        if (_timeline is not null)
        {
            _state.LyricsStatus = ComposeDocumentStatus();
        }
    }

    private void UpdateNetEaseSyncNotice(
        NetEaseSyncCapability capability,
        DateTimeOffset observedAt)
    {
        if (capability is NetEaseSyncCapability.PreciseTimeline
            or NetEaseSyncCapability.EnhancedPreciseTimeline)
        {
            _pendingNetEaseSyncNotice = null;
            _pendingNetEaseSyncNoticeSince = null;
            _state.ShowNetEasePreciseSyncHelp = false;
            return;
        }

        if (_pendingNetEaseSyncNotice != capability)
        {
            _pendingNetEaseSyncNotice = capability;
            _pendingNetEaseSyncNoticeSince = observedAt;
            _state.ShowNetEasePreciseSyncHelp = false;
        }

        if (capability == NetEaseSyncCapability.EnhancedEstimatedTimeline)
        {
            _state.NetEasePreciseSyncTitle = "精确进度暂时中断";
            _state.NetEasePreciseSyncDescription =
                "正在从最近一次增强进度继续本地计时，歌词不会跳回开头；连接恢复后会立即重新对准。此期间拖动进度可能需要等待重新连接。";
            _state.NetEasePreciseSyncActionText = "查看排查方法…";
        }
        else if (capability == NetEaseSyncCapability.EnhancedMetadataOnly)
        {
            _state.NetEasePreciseSyncTitle = "检测到增强信息，但时间线不可用";
            _state.NetEasePreciseSyncDescription =
                "已连接词间内置进度通道，但尚未收到有效时间线。可能仍在初始化，或当前网易云版本暂不兼容。";
            _state.NetEasePreciseSyncActionText = "查看排查方法…";
        }
        else
        {
            _state.NetEasePreciseSyncTitle = string.IsNullOrWhiteSpace(_state.NetEaseRecoveryStatus)
                ? "网易云播放进度未连接" : _state.NetEaseRecoveryStatus;
            _state.NetEasePreciseSyncDescription =
                _settings.AutoRepairNetEaseSync && PlayerFollowModes.UsesShortcut(_settings.PlayerFollowMode)
                    ? "词间会自动维护网易云启动入口。若更新后丢失精确进度，会在暂停后尝试重新打开网易云；播放中不会打断。"
                    : "当前歌词靠本地计时，中途打开或拖动进度后可能对不上。可在设置中开启自动维护网易云同步，或完全退出网易云后从组合快捷方式启动。";
            _state.NetEasePreciseSyncActionText = "查看启用方式…";
        }

        _state.ShowNetEasePreciseSyncHelp = _pendingNetEaseSyncNoticeSince is { } pendingSince
            && observedAt - pendingSince >= NetEaseSyncNoticeDelay;
    }

    private void ResetNetEaseSyncState()
    {
        _netEaseSyncCapability = null;
        _pendingNetEaseSyncNotice = null;
        _pendingNetEaseSyncNoticeSince = null;
        _state.ShowNetEasePreciseSyncHelp = false;
    }

    private string ComposeDocumentStatus()
    {
        if (string.IsNullOrWhiteSpace(_documentStatus))
        {
            return string.Empty;
        }

        var clockStatus = _netEaseSyncCapability switch
        {
            NetEaseSyncCapability.EnhancedPreciseTimeline => "实验性精确进度",
            NetEaseSyncCapability.EnhancedEstimatedTimeline =>
                "增强本地续时 · 等待精确进度恢复",
            _ => _snapshot?.TimelineKind switch
            {
                PlaybackTimelineKind.Reported => "精确同步 · 可跟随拖动",
                PlaybackTimelineKind.Estimated => "本地计时 · 拖动后可能需要校准",
                PlaybackTimelineKind.EnhancedEstimated =>
                    "增强本地续时 · 等待精确进度恢复",
                _ => "未获得播放进度"
            }
        };
        var filterWarning = _settings.LyricsFilterEnabled
            && LyricFilter.FindTimedOutPatternLines(_settings.LyricsFilterPatterns).Count > 0
                ? " · 部分过滤规则耗时过长，已暂时跳过" : string.Empty;
        return $"{_documentStatus} · {clockStatus}{filterWarning}";
    }

    private void UpdateFrame()
    {
        _firstLineWake.Stop();
        if (_snapshot is null || _suppressedTrackInfoOperationVersion is not null || !_settings.ShowLyrics
            || (_settings.HideWhenPaused && _snapshot.State != PlaybackState.Playing))
        {
            _hasTimedFrame = false;
            UpdateRenderingSubscription();
            _overlay.SetContentVisible(false);
            return;
        }

        if (!ReferenceEquals(_clockSnapshot, _snapshot))
        {
            _clockAnchor = DateTimeOffset.UtcNow;
            _clockTimestamp = Stopwatch.GetTimestamp();
            _clockSnapshot = _snapshot;
        }
        // Resolve the recording identity and saved corrections on input changes, outside the render loop.
        var correction = PlayerLyricsPositionCompensation.Apply(
            _snapshot,
            TimeSpan.FromMilliseconds(_settings.OffsetMilliseconds),
            _settings.GetPlayerLyricOffsetMilliseconds(_snapshot.Track.SourceAppUserModelId),
            _settings.NetEaseSyncDelayMilliseconds);
        _lyricCorrection = TrackLyricOffsetCalibration.Apply(
            correction, _settings.GetTrackLyricOffsetMilliseconds(_snapshot.Track));
        PresentFrame();
    }

    private void PresentFrame()
    {
        if (_snapshot is null) return;
        var now = _clockAnchor + Stopwatch.GetElapsedTime(_clockTimestamp);
        var position = _snapshot.EstimatePosition(now) + _lyricCorrection;
        var frame = _timeline?.GetFrame(position, _settings.DoubleLine);
        _hasTimedFrame = frame is not null;
        if (frame is null && _started && _timeline?.FirstLineStart is { } first && first > position
            && _snapshot is { State: PlaybackState.Playing, PlaybackRate: > 0 }
            && (_snapshot.Track.Duration <= TimeSpan.Zero || first <= _snapshot.Track.Duration + _lyricCorrection))
        {
            // An intro needs just one wake-up, even when the overlay is completely hidden.
            var wait = (first - position).TotalMilliseconds / _snapshot.PlaybackRate;
            _firstLineWake.Interval = TimeSpan.FromMilliseconds(Math.Clamp(Math.Ceiling(wait), 1, int.MaxValue));
            _firstLineWake.Start();
        }
        UpdateRenderingSubscription();
        if (frame is null && _settings.ShowTrackInfoWhenNoLyrics)
        {
            frame = new LyricFrame(
                _snapshot.Track.Title,
                _snapshot.Track.Artist,
                0,
                false,
                int.MinValue,
                LyricFrameKind.TrackInfo);
        }

        if (frame is null)
        {
            _overlay.SetContentVisible(false);
            return;
        }

        _overlay.SetFrame(frame);
        _overlay.SetContentVisible(true);
    }

    private static string FriendlyPlayerName(string appUserModelId)
    {
        var player = MusicPlayerCatalog.Identify(appUserModelId);
        return player is null
            ? "受支持的音乐播放器"
            : $"{player.DisplayName} · 系统媒体会话";
    }

    public void Dispose()
    {
        _started = false;
        _playbackService.SnapshotChanged -= OnSnapshotChanged;
        _playbackService.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _settings.PropertyChanged -= OnSettingsChanged;
        _overlay.RenderVisibilityChanged -= OnRenderVisibilityChanged;
        _firstLineWake.Stop();
        UpdateRenderingSubscription();
        CancelAppleMusicSeekSettling();
        CancelActiveLyricsLoad();
        Interlocked.Increment(ref _lyricsOperationVersion);
        _activeLyricsTrackKey = null;
        _suppressedTrackInfoOperationVersion = null;
    }

    private const string DemoLyrics = """
        [00:00.00]<00:00.00>音乐继续<00:01.80>，<00:02.10>手上的事<00:03.70>也继续
        [00:06.00]<00:06.00>一句歌词<00:07.70>，<00:08.00>轻轻落在<00:09.50>桌面
        [00:12.00]<00:12.00>想看时<00:13.30>出现<00:14.40>，<00:14.70>不需要时<00:16.20>隐去
        [00:19.00]<00:19.00>不抢焦点<00:20.80>，<00:21.10>也不挡住<00:22.60>鼠标
        [00:26.00]<00:26.00>词间<00:27.30> · <00:28.00>Lyrveil
        [00:31.50]<00:31.50>正在播放<00:33.00>桌面歌词演示
        """;
}
