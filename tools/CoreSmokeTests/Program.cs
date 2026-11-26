using LyricsXQuiet.Core.Lyrics;
using LyricsXQuiet.Core.Lyrics.Providers;
using LyricsXQuiet.Core.Playback;
using LyricsXQuiet.Core.Settings;

await PlayerOffsetRegressionTests.RunAsync();
NetEaseRecoveryRegressionTests.Run();

const string sample = """
    [offset:100]
    [00:01.00]<00:01.00>你<00:01.50>好
    [00:01.00]Hello
    [00:03.00]下一句
    """;

var document = LrcParser.Parse(sample, "smoke-test");
Assert(document.Lines.Count == 2, "重复时间戳应合并为双语行");
Assert(document.Lines[0].Start == TimeSpan.FromSeconds(1.1), "应应用 LRC offset");
Assert(document.Lines[0].Text == "你好", "应移除增强 LRC 的逐字时间标签");
Assert(document.Lines[0].Translation == "Hello", "第二个相同时间戳应作为翻译");
Assert(document.Lines[0].Words.Count == 2, "应解析逐字时间标签");

var frame = new LyricTimeline(document).GetFrame(TimeSpan.FromSeconds(1.35));
Assert(frame is not null, "有效时间点应产生歌词帧");
var checkedFrame = frame ?? throw new InvalidOperationException("歌词帧为空");
Assert(checkedFrame.SecondaryText == "Hello", "歌词帧应优先显示翻译");
Assert(checkedFrame.HasWordTiming, "增强 LRC 应启用字形进度");
Assert(checkedFrame.Progress is > 0.20 and < 0.30, "逐字进度应在当前字内部插值");

var lineTimedDocument = LrcParser.Parse(
    "[00:00.00]只有逐行时间\n[00:10.00]下一句",
    "line-timing-test");
var lineTimedFrame = new LyricTimeline(lineTimedDocument).GetFrame(TimeSpan.FromSeconds(5));
Assert(lineTimedFrame is { HasWordTiming: false }, "普通 LRC 应保持逐行时间类型");
var checkedLineTimedFrame = lineTimedFrame
    ?? throw new InvalidOperationException("逐行歌词帧为空");
Assert(checkedLineTimedFrame.Progress is > 0.49 and < 0.51,
    "普通逐行歌词应按当前行到下一行的时间平滑估算遮罩进度");

var now = DateTimeOffset.Now;
var track = new TrackIdentity("Song", "Artist", "Album", TimeSpan.FromMinutes(3));
const string appleMusicAumid = "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App";
var appleMusicTrackWithoutDuration = new TrackIdentity(
    "程艾影",
    "赵雷",
    "署前街少年",
    TimeSpan.Zero,
    appleMusicAumid);
var appleMusicTrackWithDuration = appleMusicTrackWithoutDuration with
{
    Duration = TimeSpan.FromMinutes(5)
};
Assert(appleMusicTrackWithoutDuration.StableKey == appleMusicTrackWithDuration.StableKey,
    "Apple Music 分阶段补全时长时不应把同一首歌当成新歌");
Assert(appleMusicTrackWithDuration.StableKey
       != (appleMusicTrackWithDuration with { Album = "不同专辑" }).StableKey,
    "Apple Music 应使用专辑区分标准、Live 和 Remix 等不同版本");
Assert(track.StableKey
       != (track with { Duration = TimeSpan.FromMinutes(4) }).StableKey,
    "其他播放器仍应保留时长身份校验");
var appleAcceptedSnapshot = new PlaybackSnapshot(
    appleMusicTrackWithDuration,
    PlaybackState.Playing,
    TimeSpan.FromSeconds(10),
    1,
    now,
    PlaybackTimelineKind.Reported);
var appleSeekCandidate = appleAcceptedSnapshot with
{
    Position = TimeSpan.FromSeconds(48),
    CapturedAt = now + TimeSpan.FromMilliseconds(50)
};
Assert(AppleMusicSeekSettlingPolicy.ShouldSettle(
        appleAcceptedSnapshot,
        appleSeekCandidate),
    "Apple Music 大幅跳转应进入短暂收敛窗口，避免中间歌词行闪烁");
Assert(!AppleMusicSeekSettlingPolicy.ShouldSettle(
        appleAcceptedSnapshot,
        appleAcceptedSnapshot with
        {
            Position = TimeSpan.FromSeconds(10.2),
            CapturedAt = now + TimeSpan.FromMilliseconds(50)
        }),
    "Apple Music 正常进度校准不应被误当成跳转");
Assert(!AppleMusicSeekSettlingPolicy.ShouldSettle(
        appleAcceptedSnapshot,
        appleSeekCandidate with
        {
            Track = appleSeekCandidate.Track with
            {
                SourceAppUserModelId = "QQMusic.exe"
            }
        }),
    "Apple Music 防闪烁策略不应改变 QQ 音乐已验证的跳转链路");
Assert(AppleMusicSeekSettlingPolicy.IsOlderThan(
        appleSeekCandidate,
        appleSeekCandidate with { CapturedAt = now }),
    "Apple Music 目标位置已到达后应丢弃晚到的旧时间线快照");
Assert(!AppleMusicSeekSettlingPolicy.IsOlderThan(
        appleSeekCandidate,
        appleSeekCandidate with
        {
            State = PlaybackState.Paused,
            CapturedAt = now
        }),
    "Apple Music 的新暂停状态即使沿用旧时间线锚点也不能被丢弃");
Assert(!AppleMusicSeekSettlingPolicy.IsOlderThan(
        appleSeekCandidate,
        appleSeekCandidate with
        {
            Track = appleSeekCandidate.Track with
            {
                SourceAppUserModelId = "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"
            },
            CapturedAt = now
        }),
    "跨播放器交接不能用彼此独立的时间线锚点判断事件新旧");
var frozenAppleOrigin = appleAcceptedSnapshot with
{
    PlaybackRate = 0,
    CapturedAt = now + TimeSpan.FromMilliseconds(50)
};
Assert(AppleMusicSeekSettlingPolicy.ClassifyPendingCandidate(
        frozenAppleOrigin,
        appleSeekCandidate,
        appleAcceptedSnapshot with
        {
            Position = TimeSpan.FromSeconds(10.1),
            CapturedAt = now + TimeSpan.FromMilliseconds(100)
        }) == AppleMusicSeekSettlingPolicy.PendingCandidateKind.AcceptedPosition,
    "Apple Music 跳转收敛期间补发的旧位置应归入原位置组，不能进入歌词渲染");
Assert(AppleMusicSeekSettlingPolicy.ClassifyPendingCandidate(
        frozenAppleOrigin,
        appleSeekCandidate,
        appleSeekCandidate with
        {
            Position = TimeSpan.FromSeconds(48.2),
            CapturedAt = now + TimeSpan.FromMilliseconds(120)
        }) == AppleMusicSeekSettlingPolicy.PendingCandidateKind.PendingPosition,
    "Apple Music 同一跳转目标的连续快照应合并为一个稳定位置组");
Assert(AppleMusicSeekSettlingPolicy.ClassifyPendingCandidate(
        frozenAppleOrigin,
        appleSeekCandidate,
        appleSeekCandidate with
        {
            Position = TimeSpan.FromSeconds(92),
            CapturedAt = now + TimeSpan.FromMilliseconds(130)
        }) == AppleMusicSeekSettlingPolicy.PendingCandidateKind.ReplacementPosition,
    "Apple Music 连续拖动到另一个目标时应替换等待中的目标，而不是显示中间歌词行");
Assert(AppleMusicSeekSettlingPolicy.ShouldSettle(
        appleAcceptedSnapshot,
        appleSeekCandidate with { State = PlaybackState.Paused }),
    "Apple Music 拖动时短暂切换播放状态也应保持在同一收敛窗口");
var quantizedAppleClock = new PlaybackSnapshot(
    appleMusicTrackWithDuration,
    PlaybackState.Playing,
    TimeSpan.FromSeconds(56),
    1,
    now,
    PlaybackTimelineKind.Reported);
var quantizedAppleUpdateAt = now + TimeSpan.FromMilliseconds(280);
var smoothedApplePosition = AppleMusicProgressStabilityPolicy.Stabilize(
    quantizedAppleClock,
    appleMusicTrackWithDuration,
    PlaybackState.Playing,
    TimeSpan.FromSeconds(56),
    quantizedAppleUpdateAt);
Assert(smoothedApplePosition == TimeSpan.FromSeconds(56.28),
    "Apple Music 同一整秒内的重复进度不应把本地时钟拉回，避免固定行边界闪烁");
Assert(AppleMusicProgressStabilityPolicy.Stabilize(
        quantizedAppleClock,
        appleMusicTrackWithDuration,
        PlaybackState.Playing,
        TimeSpan.FromSeconds(57),
        quantizedAppleUpdateAt) == TimeSpan.FromSeconds(57),
    "Apple Music 正常进入下一整秒时应接受向前校准");
Assert(AppleMusicProgressStabilityPolicy.Stabilize(
        quantizedAppleClock,
        appleMusicTrackWithDuration,
        PlaybackState.Playing,
        TimeSpan.FromSeconds(40),
        quantizedAppleUpdateAt) == TimeSpan.FromSeconds(40),
    "Apple Music 明显向后拖动不能被正常播放防回跳误吞掉");
Assert(AppleMusicProgressStabilityPolicy.Stabilize(
        quantizedAppleClock,
        appleMusicTrackWithDuration with { SourceAppUserModelId = "Spotify.exe" },
        PlaybackState.Playing,
        TimeSpan.FromSeconds(56),
        quantizedAppleUpdateAt) == TimeSpan.FromSeconds(56),
    "Apple Music 整秒量化修正不能影响 Spotify 等其他播放器");
var reconciledQuantizedAppleClock = PlaybackClockCalibrator.Reconcile(
    quantizedAppleClock,
    appleMusicTrackWithDuration,
    PlaybackState.Playing,
    TimeSpan.FromSeconds(56),
    1,
    quantizedAppleUpdateAt,
    quantizedAppleUpdateAt);
Assert(reconciledQuantizedAppleClock.Position == TimeSpan.FromSeconds(56.28)
       && reconciledQuantizedAppleClock.CapturedAt == quantizedAppleUpdateAt,
    "Apple Music 的系统整秒时间线应转换为连续、单调的本地锚点");
var appleQuantizationCompensator = new AppleMusicTimelineQuantizationCompensator();
var appleBoundaryStart = now + TimeSpan.FromSeconds(1);
Assert(appleQuantizationCompensator.Compensate(
        appleMusicTrackWithDuration,
        PlaybackState.Playing,
        TimeSpan.FromSeconds(56),
        appleBoundaryStart,
        appleBoundaryStart) == TimeSpan.FromSeconds(56),
    "Apple Music 首个整秒样本没有前序观察时不应凭空添加补偿");
Assert(appleQuantizationCompensator.Compensate(
        appleMusicTrackWithDuration,
        PlaybackState.Playing,
        TimeSpan.FromSeconds(56),
        appleBoundaryStart + TimeSpan.FromMilliseconds(280),
        appleBoundaryStart + TimeSpan.FromMilliseconds(285)) == TimeSpan.FromSeconds(56),
    "Apple Music 同一整秒的最后一次观察应记录量化边界区间");
Assert(appleQuantizationCompensator.Compensate(
        appleMusicTrackWithDuration,
        PlaybackState.Playing,
        TimeSpan.FromSeconds(57),
        appleBoundaryStart + TimeSpan.FromMilliseconds(560),
        appleBoundaryStart + TimeSpan.FromMilliseconds(565))
       == TimeSpan.FromMilliseconds(57140),
    "Apple Music 新整秒应以前后观察区间中点补偿约半个刷新周期");
Assert(appleQuantizationCompensator.Compensate(
        appleMusicTrackWithDuration,
        PlaybackState.Playing,
        TimeSpan.FromSeconds(57),
        appleBoundaryStart + TimeSpan.FromMilliseconds(560),
        appleBoundaryStart + TimeSpan.FromMilliseconds(570))
       == TimeSpan.FromMilliseconds(57140),
    "重复读取同一系统时间线不应重复累计 Apple Music 量化补偿");
Assert(appleQuantizationCompensator.Compensate(
        appleMusicTrackWithDuration,
        PlaybackState.Playing,
        TimeSpan.FromSeconds(90),
        appleBoundaryStart + TimeSpan.FromMilliseconds(840),
        appleBoundaryStart + TimeSpan.FromMilliseconds(845)) == TimeSpan.FromSeconds(90),
    "Apple Music 大幅拖动必须按目标位置处理，不能套用正常整秒补偿");
Assert(appleQuantizationCompensator.Compensate(
        appleMusicTrackWithDuration with { SourceAppUserModelId = "Spotify.exe" },
        PlaybackState.Playing,
        TimeSpan.FromSeconds(91),
        appleBoundaryStart + TimeSpan.FromMilliseconds(1120),
        appleBoundaryStart + TimeSpan.FromMilliseconds(1125)) == TimeSpan.FromSeconds(91),
    "Apple Music 动态边界补偿不得影响 Spotify 等其他播放器");
Assert(appleQuantizationCompensator.Compensate(
        appleMusicTrackWithDuration,
        PlaybackState.Paused,
        TimeSpan.FromSeconds(91),
        appleBoundaryStart + TimeSpan.FromMilliseconds(1400),
        appleBoundaryStart + TimeSpan.FromMilliseconds(1405)) == TimeSpan.FromSeconds(91),
    "暂停状态应保留播放器给出的固定位置，不得继续提前歌词");
var snapshot = new PlaybackSnapshot(
    track,
    PlaybackState.Playing,
    TimeSpan.FromSeconds(1),
    1,
    now - TimeSpan.FromSeconds(2));
var estimated = snapshot.EstimatePosition(now);
Assert(estimated >= TimeSpan.FromSeconds(2.9) && estimated <= TimeSpan.FromSeconds(3.1),
    "播放中的时间线应使用本地时钟推进");

var missingTimelineTimestamp = new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);
Assert(SourceTrackIdParser.FromGenres(["Pop", "NCM-123456"]) == "netease:123456",
    "应从增强网易云会话的 Genres 中读取规范歌曲 ID");
Assert(SourceTrackIdParser.FromGenres(["NCM-0", "NCM--1", "NCM-12x"]) == string.Empty,
    "网易云歌曲 ID 解析必须拒绝零、负数和非数字值");
Assert(SourceTrackIdParser.FromGenres([" NCM-123456"]) == string.Empty,
    "网易云歌曲 ID 解析不应接受带前导空白的宽松格式");
var clockTrack = new TrackIdentity("Clock Song", "Artist", "", TimeSpan.FromMinutes(3), "cloudmusic.exe");
Assert(NetEaseSyncCapabilityClassifier.Classify(
        clockTrack,
        PlaybackTimelineKind.Unavailable) == NetEaseSyncCapability.NativeLimited,
    "没有增强歌曲 ID 和精确时间线时应判定为网易云原生受限状态");
Assert(NetEaseSyncCapabilityClassifier.Classify(
        clockTrack with { SourceTrackId = "netease:123456" },
        PlaybackTimelineKind.Estimated) == NetEaseSyncCapability.EnhancedMetadataOnly,
    "只有增强歌曲 ID 时不能误报增强同步已生效");
Assert(NetEaseSyncCapabilityClassifier.Classify(
        clockTrack,
        PlaybackTimelineKind.Reported) == NetEaseSyncCapability.PreciseTimeline,
    "没有增强标记但存在精确时间线时只应报告通用精确同步");
Assert(NetEaseSyncCapabilityClassifier.Classify(
        clockTrack with { SourceTrackId = "netease:123456" },
        PlaybackTimelineKind.Reported) == NetEaseSyncCapability.EnhancedPreciseTimeline,
    "只有增强歌曲 ID 与精确时间线同时存在时才能报告增强同步已生效");
Assert(NetEaseSyncCapabilityClassifier.Classify(
        clockTrack with { SourceTrackId = "netease:123456" },
        PlaybackTimelineKind.EnhancedEstimated)
        == NetEaseSyncCapability.EnhancedEstimatedTimeline,
    "桥接中断后从增强锚点续走的时间线应保留独立来源标记");
Assert(NetEaseSyncCapabilityClassifier.Classify(
        clockTrack,
        PlaybackTimelineKind.EnhancedEstimated) == NetEaseSyncCapability.NativeLimited,
    "缺少有效网易云歌曲 ID 时不能仅凭时间线枚举冒充增强续时");
Assert(PlaybackClockCalibrator.HasUsableTimeline(
        clockTrack.Duration,
        TimeSpan.Zero,
        now,
        now),
    "歌曲起点的有效时间线不应被拒绝");
Assert(!PlaybackClockCalibrator.HasUsableTimeline(
        TimeSpan.Zero,
        TimeSpan.Zero,
        now,
        now),
    "缺少歌曲时长时不能宣称获得精确时间线");
Assert(!PlaybackClockCalibrator.HasUsableTimeline(
        clockTrack.Duration,
        TimeSpan.FromSeconds(-1),
        now,
        now),
    "负数播放位置不能视为有效时间线");
Assert(!PlaybackClockCalibrator.HasUsableTimeline(
        clockTrack.Duration,
        clockTrack.Duration + TimeSpan.FromSeconds(3),
        now,
        now),
    "明显超出歌曲时长的位置不能视为有效时间线");
var clockStart = PlaybackClockCalibrator.Reconcile(
    null,
    clockTrack,
    PlaybackState.Playing,
    TimeSpan.Zero,
    1,
    missingTimelineTimestamp,
    now);
var clockAfterTwoSeconds = PlaybackClockCalibrator.Reconcile(
    clockStart,
    clockTrack,
    PlaybackState.Playing,
    TimeSpan.Zero,
    1,
    missingTimelineTimestamp,
    now + TimeSpan.FromSeconds(2));
var clockAfterFourSeconds = PlaybackClockCalibrator.Reconcile(
    clockAfterTwoSeconds,
    clockTrack,
    PlaybackState.Playing,
    TimeSpan.Zero,
    1,
    missingTimelineTimestamp,
    now + TimeSpan.FromSeconds(4));
Assert(clockAfterTwoSeconds.Position == TimeSpan.FromSeconds(2)
       && clockAfterFourSeconds.Position == TimeSpan.FromSeconds(4),
    "缺失时间线的同一首歌不应被每两秒刷新重置为零");
Assert(clockAfterFourSeconds.TimelineKind == PlaybackTimelineKind.Estimated,
    "缺失时间线时必须明确标记为本地推算，不能冒充精确同步");

var pausedClock = PlaybackClockCalibrator.Reconcile(
    clockAfterFourSeconds,
    clockTrack,
    PlaybackState.Paused,
    TimeSpan.Zero,
    0,
    missingTimelineTimestamp,
    now + TimeSpan.FromSeconds(5));
Assert(pausedClock.EstimatePosition(now + TimeSpan.FromSeconds(20)) == TimeSpan.FromSeconds(5),
    "缺失时间线时暂停应冻结本地推算位置");
var resumedClock = PlaybackClockCalibrator.Reconcile(
    pausedClock,
    clockTrack,
    PlaybackState.Playing,
    TimeSpan.Zero,
    0,
    missingTimelineTimestamp,
    now + TimeSpan.FromSeconds(10));
Assert(resumedClock.PlaybackRate == 1
       && resumedClock.EstimatePosition(now + TimeSpan.FromSeconds(12)) == TimeSpan.FromSeconds(7),
    "恢复播放应从暂停位置续走，且 Playing 状态的无效速率应回退到 1x");

var handedOffClock = PlaybackClockCalibrator.Reconcile(
    resumedClock,
    clockTrack with { SourceAppUserModelId = "kugou" },
    PlaybackState.Playing,
    TimeSpan.Zero,
    1,
    missingTimelineTimestamp,
    now + TimeSpan.FromSeconds(12));
Assert(handedOffClock.EstimatePosition(now + TimeSpan.FromSeconds(12)) == TimeSpan.FromSeconds(7),
    "同一首歌切换播放器且新播放器没有时间线时，应从交接位置继续本地计时");

var identifiedClock = new PlaybackSnapshot(
    clockTrack with { SourceTrackId = "netease:100" },
    PlaybackState.Playing,
    TimeSpan.FromSeconds(42),
    1,
    now,
    PlaybackTimelineKind.Reported);
var changedSourceIdClock = PlaybackClockCalibrator.Reconcile(
    identifiedClock,
    clockTrack with { SourceTrackId = "netease:200" },
    PlaybackState.Playing,
    TimeSpan.Zero,
    1,
    missingTimelineTimestamp,
    now + TimeSpan.FromSeconds(1));
Assert(changedSourceIdClock.Position == TimeSpan.Zero,
    "元数据相同但网易云歌曲 ID 不同时必须视为换曲，不能继承旧进度");
var partiallyIdentifiedClock = PlaybackClockCalibrator.Reconcile(
    identifiedClock,
    clockTrack,
    PlaybackState.Playing,
    TimeSpan.Zero,
    1,
    missingTimelineTimestamp,
    now + TimeSpan.FromSeconds(1));
Assert(partiallyIdentifiedClock.Position == TimeSpan.FromSeconds(43),
    "只有一侧提供歌曲 ID 时应回退元数据比较，保留跨播放器接力");

var differentVersionClock = PlaybackClockCalibrator.Reconcile(
    resumedClock,
    clockTrack with
    {
        Album = "Different Version",
        Duration = clockTrack.Duration + TimeSpan.FromSeconds(10),
        SourceAppUserModelId = "kugou"
    },
    PlaybackState.Playing,
    TimeSpan.Zero,
    1,
    missingTimelineTimestamp,
    now + TimeSpan.FromSeconds(12));
Assert(differentVersionClock.Position == TimeSpan.Zero,
    "跨播放器但专辑和时长明显不同的版本不应继承上一首的进度");

var changedTrackClock = PlaybackClockCalibrator.Reconcile(
    resumedClock,
    clockTrack with { Title = "Next Song" },
    PlaybackState.Playing,
    TimeSpan.Zero,
    1,
    missingTimelineTimestamp,
    now + TimeSpan.FromSeconds(12));
Assert(changedTrackClock.Position == TimeSpan.Zero, "换曲且时间线无效时应从零开始本地计时");

var timelineCapturedAt = now + TimeSpan.FromSeconds(20);
var calibratedClock = PlaybackClockCalibrator.Reconcile(
    resumedClock,
    clockTrack,
    PlaybackState.Playing,
    TimeSpan.FromSeconds(30),
    1,
    timelineCapturedAt - TimeSpan.FromSeconds(2),
    timelineCapturedAt);
Assert(calibratedClock.CapturedAt == timelineCapturedAt - TimeSpan.FromSeconds(2)
       && calibratedClock.EstimatePosition(timelineCapturedAt) == TimeSpan.FromSeconds(32),
    "有效时间线应用 LastUpdatedTime 锚定并立即校准");
Assert(calibratedClock.TimelineKind == PlaybackTimelineKind.Reported,
    "有效播放器时间线应标记为可精确校准");
var seekedClock = PlaybackClockCalibrator.Reconcile(
    calibratedClock,
    clockTrack,
    PlaybackState.Playing,
    TimeSpan.FromSeconds(8),
    1,
    timelineCapturedAt + TimeSpan.FromSeconds(1),
    timelineCapturedAt + TimeSpan.FromSeconds(1));
Assert(seekedClock.EstimatePosition(timelineCapturedAt + TimeSpan.FromSeconds(1)) == TimeSpan.FromSeconds(8),
    "有效时间线的跳转应立即覆盖本地推算位置");
Assert(seekedClock.TimelineKind == PlaybackTimelineKind.Reported,
    "手动跳转后的新锚点仍应保持精确时间线状态");

var netEaseNativeSnapshot = new PlaybackSnapshot(
    new TrackIdentity(
        "Bridge Song",
        "Bridge Artist / Guest",
        "Bridge Album",
        TimeSpan.Zero,
        "cloudmusic.exe"),
    PlaybackState.Playing,
    TimeSpan.FromSeconds(7),
    1,
    now,
    PlaybackTimelineKind.Estimated);
var freshBridgeSnapshot = new NetEaseBridgeSnapshot(
    NetEaseBridgeSnapshotMerger.CurrentProtocolVersion,
    10,
    now,
    new NetEaseBridgeTrack(
        "123456",
        "Bridge Song",
        ["Bridge Artist", "Guest"],
        "Bridge Album",
        180_000),
    PlaybackState.Playing,
    91_000,
    1);
var mergedNetEaseSnapshot = NetEaseBridgeSnapshotMerger.Merge(
    netEaseNativeSnapshot,
    freshBridgeSnapshot,
    now);
Assert(mergedNetEaseSnapshot.TimelineKind == PlaybackTimelineKind.Reported
       && mergedNetEaseSnapshot.Position == TimeSpan.FromSeconds(91)
       && mergedNetEaseSnapshot.Track.Duration == TimeSpan.FromMinutes(3)
       && mergedNetEaseSnapshot.Track.SourceTrackId == "netease:123456",
    "匹配且新鲜的网易云桥快照应补齐 ID、时长并提供精确进度");

var matchingIdentityNativeSnapshot = netEaseNativeSnapshot with
{
    Track = netEaseNativeSnapshot.Track with
    {
        Album = "Bridge - Album",
        Duration = TimeSpan.FromSeconds(182),
        SourceTrackId = "NETEASE:123456"
    }
};
var matchingIdentityBridgeResult = NetEaseBridgeSnapshotMerger.Merge(
    matchingIdentityNativeSnapshot,
    freshBridgeSnapshot,
    now);
Assert(!ReferenceEquals(matchingIdentityBridgeResult, matchingIdentityNativeSnapshot)
       && matchingIdentityBridgeResult.Track.SourceTrackId == "netease:123456",
    "歌曲 ID 一致、专辑仅标点差异且时长在容差内时应接受桥快照");

var mismatchedSourceTrackIdResult = NetEaseBridgeSnapshotMerger.Merge(
    netEaseNativeSnapshot with
    {
        Track = netEaseNativeSnapshot.Track with { SourceTrackId = "netease:654321" }
    },
    freshBridgeSnapshot,
    now);
Assert(mismatchedSourceTrackIdResult.Track.SourceTrackId == "netease:654321"
       && mismatchedSourceTrackIdResult.TimelineKind == PlaybackTimelineKind.Estimated,
    "双方都有歌曲 ID 时必须一致，旧桥快照不得覆盖新歌");

var mismatchedAlbumNativeSnapshot = netEaseNativeSnapshot with
{
    Track = netEaseNativeSnapshot.Track with { Album = "A New Release" }
};
var mismatchedAlbumResult = NetEaseBridgeSnapshotMerger.Merge(
    mismatchedAlbumNativeSnapshot,
    freshBridgeSnapshot,
    now);
Assert(ReferenceEquals(mismatchedAlbumResult, mismatchedAlbumNativeSnapshot),
    "双方都有专辑时必须在规范化后一致");

var mismatchedDurationNativeSnapshot = netEaseNativeSnapshot with
{
    Track = netEaseNativeSnapshot.Track with { Duration = TimeSpan.FromSeconds(190) }
};
var mismatchedDurationResult = NetEaseBridgeSnapshotMerger.Merge(
    mismatchedDurationNativeSnapshot,
    freshBridgeSnapshot,
    now);
Assert(ReferenceEquals(mismatchedDurationResult, mismatchedDurationNativeSnapshot),
    "双方都有有效时长时，超出合理容差的旧桥快照不得合并");

var sameTitleArtistNewTrack = netEaseNativeSnapshot with
{
    Track = netEaseNativeSnapshot.Track with
    {
        Album = "New Album",
        Duration = TimeSpan.FromSeconds(205),
        SourceTrackId = "netease:999999"
    }
};
var staleSameTitleArtistBridgeResult = NetEaseBridgeSnapshotMerger.Merge(
    sameTitleArtistNewTrack,
    freshBridgeSnapshot,
    now);
Assert(ReferenceEquals(staleSameTitleArtistBridgeResult, sameTitleArtistNewTrack),
    "切到同名同歌手的新歌时，旧桥快照不得因标题和歌手相同而覆盖它");

var frozenBridgeSnapshot = freshBridgeSnapshot with
{
    Sequence = 11,
    ObservedAt = now + TimeSpan.FromMilliseconds(100),
    PositionMs = 12_000,
    TimelineFrozen = true
};
var frozenNetEaseSnapshot = NetEaseBridgeSnapshotMerger.Merge(
    mergedNetEaseSnapshot,
    frozenBridgeSnapshot,
    frozenBridgeSnapshot.ObservedAt);
Assert(frozenNetEaseSnapshot.State == PlaybackState.Playing
       && frozenNetEaseSnapshot.PlaybackRate == 0
       && frozenNetEaseSnapshot.EstimatePosition(
           frozenBridgeSnapshot.ObservedAt + TimeSpan.FromSeconds(2))
           == TimeSpan.FromSeconds(12),
    "seek settling 应保持播放态但冻结时间线，避免歌词继续跑动或触发暂停隐藏");
var resumedBridgeSnapshot = frozenBridgeSnapshot with
{
    Sequence = 12,
    ObservedAt = now + TimeSpan.FromMilliseconds(400),
    PositionMs = 12_300,
    TimelineFrozen = false
};
var resumedNetEaseSnapshot = NetEaseBridgeSnapshotMerger.Merge(
    frozenNetEaseSnapshot,
    resumedBridgeSnapshot,
    resumedBridgeSnapshot.ObservedAt);
Assert(resumedNetEaseSnapshot.State == PlaybackState.Playing
       && resumedNetEaseSnapshot.PlaybackRate == 1
       && resumedNetEaseSnapshot.EstimatePosition(
           resumedBridgeSnapshot.ObservedAt + TimeSpan.FromMilliseconds(100))
           == TimeSpan.FromMilliseconds(12_400),
    "稳定 progress 到达后应立即恢复正常播放速率");

var seekBridgeSnapshot = freshBridgeSnapshot with
{
    Sequence = 11,
    ObservedAt = now + TimeSpan.FromMilliseconds(500),
    PositionMs = 12_000
};
var mergedSeekSnapshot = NetEaseBridgeSnapshotMerger.Merge(
    mergedNetEaseSnapshot,
    seekBridgeSnapshot,
    now + TimeSpan.FromMilliseconds(500));
Assert(mergedSeekSnapshot.Position == TimeSpan.FromSeconds(12)
       && mergedSeekSnapshot.TimelineKind == PlaybackTimelineKind.Reported,
    "桥接端报告手动跳转后应立即回到对应歌词时间点");

var netEaseSeekTimeline = new LyricTimeline(LrcParser.Parse("""
    [00:14.530]我来到 你的城市
    [00:22.150]走过你来时的路
    [00:48.560]熟悉的那一条街
    [00:54.540]只是没了你的画面
    """, "网易云跳转回归"));
var firstLineBridge = freshBridgeSnapshot with
{
    Sequence = 20,
    ObservedAt = now,
    State = PlaybackState.Paused,
    PositionMs = 14_530
};
var seekTargetBridge = firstLineBridge with
{
    Sequence = 21,
    ObservedAt = now + TimeSpan.FromMilliseconds(100),
    PositionMs = 48_560
};
var seekTargetAfterPause = seekTargetBridge with
{
    Sequence = 22,
    ObservedAt = now + TimeSpan.FromSeconds(4.1)
};
var firstLineSnapshot = NetEaseBridgeSnapshotMerger.Merge(
    netEaseNativeSnapshot,
    firstLineBridge,
    firstLineBridge.ObservedAt);
var seekTargetSnapshot = NetEaseBridgeSnapshotMerger.Merge(
    firstLineSnapshot,
    seekTargetBridge,
    seekTargetBridge.ObservedAt);
var seekTargetAfterPauseSnapshot = NetEaseBridgeSnapshotMerger.Merge(
    seekTargetSnapshot,
    seekTargetAfterPause,
    seekTargetAfterPause.ObservedAt);
Assert(netEaseSeekTimeline.GetFrame(firstLineSnapshot.Position)?.CurrentText
           == "我来到 你的城市"
       && netEaseSeekTimeline.GetFrame(seekTargetSnapshot.Position)?.CurrentText
           == "熟悉的那一条街",
    "网易云从 14.53 秒拖到 48.56 秒时，最新桥锚点应立即选择对应歌词");
Assert(seekTargetAfterPauseSnapshot.Position == TimeSpan.FromMilliseconds(48_560)
       && netEaseSeekTimeline.GetFrame(seekTargetAfterPauseSnapshot.Position)?.CurrentText
           == "熟悉的那一条街",
    "网易云暂停跳转超过三秒后仍应保持 seek 锚点，不得回退到旧累计播放时间");
var lastOfConsecutiveSeeks = NetEaseBridgeSnapshotMerger.Merge(
    seekTargetAfterPauseSnapshot,
    seekTargetAfterPause with
    {
        Sequence = 23,
        ObservedAt = now + TimeSpan.FromSeconds(4.2),
        PositionMs = 22_150
    },
    now + TimeSpan.FromSeconds(4.2));
Assert(lastOfConsecutiveSeeks.Position == TimeSpan.FromMilliseconds(22_150)
       && netEaseSeekTimeline.GetFrame(lastOfConsecutiveSeeks.Position)?.CurrentText
           == "走过你来时的路",
    "连续前后跳转时必须采用最后一个 seek 位置");

var staleBridgeResult = NetEaseBridgeSnapshotMerger.Merge(
    netEaseNativeSnapshot,
    freshBridgeSnapshot with { ObservedAt = now - TimeSpan.FromSeconds(4) },
    now);
Assert(ReferenceEquals(staleBridgeResult, netEaseNativeSnapshot),
    "过期的桥接快照不得继续校准网易云进度");

var wrongTrackBridgeResult = NetEaseBridgeSnapshotMerger.Merge(
    netEaseNativeSnapshot,
    freshBridgeSnapshot with
    {
        Track = freshBridgeSnapshot.Track with { Title = "A Different Song" }
    },
    now);
Assert(ReferenceEquals(wrongTrackBridgeResult, netEaseNativeSnapshot),
    "歌曲或歌手不匹配时桥接快照不得污染当前歌曲");

var qqNativeSnapshot = netEaseNativeSnapshot with
{
    Track = netEaseNativeSnapshot.Track with { SourceAppUserModelId = "QQMusic.exe" }
};
var qqBridgeResult = NetEaseBridgeSnapshotMerger.Merge(
    qqNativeSnapshot,
    freshBridgeSnapshot,
    now);
Assert(ReferenceEquals(qqBridgeResult, qqNativeSnapshot),
    "网易云桥不得抢占或修改 QQ 音乐等其他播放器的快照");

var degradedClock = PlaybackClockCalibrator.Reconcile(
    seekedClock,
    clockTrack,
    PlaybackState.Playing,
    TimeSpan.Zero,
    1,
    missingTimelineTimestamp,
    timelineCapturedAt + TimeSpan.FromSeconds(2));
Assert(degradedClock.TimelineKind == PlaybackTimelineKind.Estimated,
    "已取得精确锚点后若时间线暂时缺失，应明确降级为本地计时而不是继续冒充精确同步");

var exactIdHandler = new NetEaseExactIdHandler();
using (var exactIdClient = new HttpClient(exactIdHandler))
using (var exactIdProvider = new NetEaseProvider(exactIdClient))
{
    var exactIdTrack = new TrackIdentity(
        "Exact Song",
        "Exact Artist",
        "Exact Album",
        TimeSpan.FromMinutes(3),
        "cloudmusic.exe",
        "netease:123456");
    var exactIdCandidates = await exactIdProvider.SearchAsync(exactIdTrack);
    Assert(exactIdCandidates.Count == 1
           && exactIdCandidates[0].Id == "netease:123456"
           && exactIdCandidates[0].Document.HasSynchronizedLines,
        "增强网易云会话提供歌曲 ID 时应直接获取该歌曲歌词");
    Assert(exactIdHandler.LyricsRequestCount == 1 && exactIdHandler.SearchRequestCount == 0,
        "精确歌曲 ID 获取成功时不应再发起模糊搜索");
}

var settings = new AppSettings { BackgroundOpacity = 2, OffsetMilliseconds = 30_000 };
Assert(!settings.ShowLyricsCredits,
    "新安装默认应隐藏词曲、编曲、封面与发行等制作名单");
Assert(settings.BackgroundOpacity == 1, "背景透明度应允许调到完全不透明并限制在有效范围");
Assert(settings.OffsetMilliseconds == 10_000, "时间偏移应限制在安全范围");
Assert(settings.ShowTrackInfoWhenNoLyrics, "默认应在前奏或无歌词时显示歌曲信息");
Assert(settings.NetEaseSyncDelayMilliseconds == 1000
       && settings.GetPlayerLyricOffsetMilliseconds("cloudmusic.exe") == 0,
    "网易云默认应延后 1000 ms，快捷面板偏移 0 ms 表示使用默认补偿");
settings.NetEaseSyncDelayMilliseconds = -20;
Assert(settings.NetEaseSyncDelayMilliseconds == 0, "网易云歌词延迟不应小于 0 ms");
settings.NetEaseSyncDelayMilliseconds = 1120;
Assert(settings.NetEaseSyncDelayMilliseconds == 1120,
    "超过 500 ms 的网易云实测延迟不应被静默截断");
settings.NetEaseSyncDelayMilliseconds = 3020;
Assert(settings.NetEaseSyncDelayMilliseconds == 3000, "网易云歌词延迟不应超过 3000 ms");
settings.NetEaseSyncDelayMilliseconds = 120;
Assert(settings.AppleMusicSyncAdvanceMilliseconds == 300,
    "Apple Music 的默认歌词提前补偿应为 300 ms");
settings.AppleMusicSyncAdvanceMilliseconds = -20;
Assert(settings.AppleMusicSyncAdvanceMilliseconds == 0,
    "Apple Music 歌词提前补偿不应小于 0 ms");
settings.AppleMusicSyncAdvanceMilliseconds = 2800;
Assert(settings.AppleMusicSyncAdvanceMilliseconds == 2800,
    "Apple Music 较大的用户校准值不应被截断到旧的 300 ms 上限");
settings.AppleMusicSyncAdvanceMilliseconds = 3020;
Assert(settings.AppleMusicSyncAdvanceMilliseconds == 3000,
    "Apple Music 歌词提前补偿不应超过 3000 ms");
settings.AppleMusicSyncAdvanceMilliseconds = 300;

settings.FontSize = 31;
settings.FontFamilyName = "SimSun";
settings.LyricColor = "#123456";
settings.ProgressColor = "#65CDBA";
settings.BackgroundColor = "#101820";
settings.BackgroundOpacity = 0.37;
settings.ProgressiveHighlight = false;
settings.DoubleLine = false;
settings.SaveCurrentAppearancePreset();
Assert(settings.HasSavedAppearancePreset,
    "保存当前外观后应启用“我的方案”入口");
settings.FontSize = 18;
settings.FontFamilyName = "Microsoft YaHei UI";
settings.LyricColor = "#FFFFFF";
settings.ProgressColor = "#33FFDD";
settings.BackgroundColor = "#000000";
settings.BackgroundOpacity = 0.9;
settings.ProgressiveHighlight = true;
settings.DoubleLine = true;
Assert(settings.ApplySavedAppearancePreset()
       && settings.FontSize == 31
       && settings.FontFamilyName == "SimSun"
       && settings.LyricColor == "#123456"
       && settings.ProgressColor == "#65CDBA"
       && settings.BackgroundColor == "#101820"
       && Math.Abs(settings.BackgroundOpacity - 0.37) < 0.0001
       && !settings.ProgressiveHighlight
       && !settings.DoubleLine,
    "我的方案应完整恢复字号、字体、颜色、背景和歌词显示方式");

var compensationCapturedAt = DateTimeOffset.UtcNow;
var enhancedNetEaseSnapshot = new PlaybackSnapshot(
    new TrackIdentity(
        "Compensation Song",
        "Artist",
        "Album",
        TimeSpan.FromMinutes(3),
        "cloudmusic.exe",
        "netease:123456"),
    PlaybackState.Playing,
    TimeSpan.FromSeconds(10),
    1,
    compensationCapturedAt,
    PlaybackTimelineKind.Reported);
Assert(
    NetEaseLyricsPositionCompensation.Apply(
        enhancedNetEaseSnapshot,
        TimeSpan.FromSeconds(10),
        settings.NetEaseSyncDelayMilliseconds) == TimeSpan.FromMilliseconds(9_880),
    "120 ms 手动延迟应只在网易云增强精确时间线上表示不可观测输出延迟");
Assert(
    NetEaseLyricsPositionCompensation.Apply(
        enhancedNetEaseSnapshot,
        TimeSpan.FromMilliseconds(80),
        settings.NetEaseSyncDelayMilliseconds) == TimeSpan.FromMilliseconds(-40),
    "网易云延迟补偿应保留负的歌词位置，使首句之前继续隐藏");

var nativeNetEaseSnapshot = enhancedNetEaseSnapshot with
{
    Track = enhancedNetEaseSnapshot.Track with { SourceTrackId = string.Empty }
};
var qqSnapshot = enhancedNetEaseSnapshot with
{
    Track = enhancedNetEaseSnapshot.Track with
    {
        SourceAppUserModelId = "QQMusic.exe",
        SourceTrackId = "qqmusic:123456"
    }
};
var estimatedEnhancedNetEaseSnapshot = enhancedNetEaseSnapshot with
{
    TimelineKind = PlaybackTimelineKind.Estimated
};
var derivedEnhancedNetEaseSnapshot = enhancedNetEaseSnapshot with
{
    TimelineKind = PlaybackTimelineKind.EnhancedEstimated
};
Assert(
    NetEaseLyricsPositionCompensation.Apply(
        nativeNetEaseSnapshot,
        TimeSpan.FromSeconds(10),
        120) == TimeSpan.FromSeconds(10)
    && NetEaseLyricsPositionCompensation.Apply(
        qqSnapshot,
        TimeSpan.FromSeconds(10),
        120) == TimeSpan.FromSeconds(10)
    && NetEaseLyricsPositionCompensation.Apply(
        estimatedEnhancedNetEaseSnapshot,
        TimeSpan.FromSeconds(10),
        120) == TimeSpan.FromSeconds(10),
    "网易云原生时间线、其他播放器与非精确时间线都不应应用网易云专用延迟");
Assert(
    NetEaseLyricsPositionCompensation.Apply(
        derivedEnhancedNetEaseSnapshot,
        TimeSpan.FromSeconds(10),
        120) == TimeSpan.FromMilliseconds(9_880)
    && NetEaseLyricsPositionCompensation.Apply(
        enhancedNetEaseSnapshot,
        TimeSpan.FromSeconds(10),
        120) == NetEaseLyricsPositionCompensation.Apply(
            derivedEnhancedNetEaseSnapshot,
            TimeSpan.FromSeconds(10),
            120),
    "增强桥接转为本地续时时必须保持相同输出延迟，避免断开瞬间前跳 120 ms");

var appleMusicSnapshot = enhancedNetEaseSnapshot with
{
    Track = enhancedNetEaseSnapshot.Track with
    {
        SourceAppUserModelId = "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App",
        SourceTrackId = string.Empty
    }
};
var migratedNetEasePosition = NetEaseLyricsPositionCompensation.Apply(
    enhancedNetEaseSnapshot, TimeSpan.FromSeconds(10), 1120);
Assert(
    migratedNetEasePosition == TimeSpan.FromMilliseconds(8880)
    && migratedNetEasePosition == NetEaseLyricsPositionCompensation.Apply(
        enhancedNetEaseSnapshot, TimeSpan.FromSeconds(9), 120)
    && NetEaseLyricsPositionCompensation.Apply(
        derivedEnhancedNetEaseSnapshot, TimeSpan.FromSeconds(10), 1120) == migratedNetEasePosition,
    "将全局 -1000 ms 转到原有 120 ms 网易云专属延迟后，精确进度和断线续时的显示位置必须保持一致");
Assert(
    NetEaseLyricsPositionCompensation.Apply(
        qqSnapshot, TimeSpan.FromSeconds(10), 1120) == TimeSpan.FromSeconds(10)
    && NetEaseLyricsPositionCompensation.Apply(
        appleMusicSnapshot, TimeSpan.FromSeconds(10), 1120) == TimeSpan.FromSeconds(10),
    "网易云 1120 ms 补偿不能让 QQ 音乐或 Apple Music 额外延迟");
Assert(
    NetEaseLyricsPositionCompensation.Apply(
        enhancedNetEaseSnapshot, TimeSpan.FromMilliseconds(500), 1120) == TimeSpan.FromMilliseconds(-620)
    && NetEaseLyricsPositionCompensation.Apply(
        enhancedNetEaseSnapshot, TimeSpan.FromSeconds(10), int.MaxValue) == TimeSpan.FromSeconds(7),
    "较长补偿仍应保留首句前的负位置，并限制非法超大输入");
Assert(
    AppleMusicLyricsPositionCompensation.Apply(
        appleMusicSnapshot,
        TimeSpan.FromSeconds(10),
        settings.AppleMusicSyncAdvanceMilliseconds) == TimeSpan.FromMilliseconds(10_300),
    "Apple Music 应独立应用 300 ms 歌词提前补偿");
Assert(
    AppleMusicLyricsPositionCompensation.Apply(
        appleMusicSnapshot, TimeSpan.FromSeconds(10), 2800) == TimeSpan.FromMilliseconds(12_800)
    && AppleMusicLyricsPositionCompensation.Apply(
        appleMusicSnapshot, TimeSpan.FromSeconds(10), int.MaxValue) == TimeSpan.FromSeconds(13),
    "Apple Music 较大补偿必须在实际歌词位置中生效，并遵守 3000 ms 上限");
Assert(
    AppleMusicLyricsPositionCompensation.Apply(
        qqSnapshot,
        TimeSpan.FromSeconds(10),
        settings.AppleMusicSyncAdvanceMilliseconds) == TimeSpan.FromSeconds(10),
    "Apple Music 专用提前补偿不应影响其他播放器");

var standardCalibrationTrack = new TrackIdentity(
    "Same Song",
    "Artist",
    "Standard Album",
    TimeSpan.FromSeconds(180),
    "QQMusic.exe");
var liveCalibrationTrack = standardCalibrationTrack with
{
    Album = "2025 Live in Shanghai"
};
Assert(
    standardCalibrationTrack.StableKey == liveCalibrationTrack.StableKey
    && standardCalibrationTrack.LyricsOperationKey != liveCalibrationTrack.LyricsOperationKey
    && standardCalibrationTrack.LyricCalibrationKey != liveCalibrationTrack.LyricCalibrationKey,
    "歌词操作与单曲校准键应在保留旧 StableKey 的前提下区分不同专辑版本");

var sourceCalibrationTrack = standardCalibrationTrack with
{
    SourceAppUserModelId = "cloudmusic.exe",
    SourceTrackId = "netease:1001"
};
var alternateSourceCalibrationTrack = sourceCalibrationTrack with
{
    SourceTrackId = "netease:1002"
};
Assert(
    sourceCalibrationTrack.LyricCalibrationKey
    != alternateSourceCalibrationTrack.LyricCalibrationKey,
    "来源歌曲 ID 不同时应使用彼此独立的单曲校准");

var calibrationFallbacks = new Dictionary<string, int>
{
    [TrackLyricOffsetCalibration.GetMetadataKey(sourceCalibrationTrack)] = -160,
    [alternateSourceCalibrationTrack.LyricCalibrationKey] = 240
};
Assert(
    TrackLyricOffsetCalibration.Resolve(sourceCalibrationTrack, calibrationFallbacks) == -160,
    "具有来源 ID 的歌曲应能回退到先前按元数据保存的校准");
Assert(
    TrackLyricOffsetCalibration.Resolve(alternateSourceCalibrationTrack, calibrationFallbacks) == 240,
    "来源 ID 的精确校准应优先于元数据回退值");
Assert(
    TrackLyricOffsetCalibration.Apply(TimeSpan.FromSeconds(1), -160)
    == TimeSpan.FromMilliseconds(840),
    "负的单曲校准应延后歌词且不钳制时间位置");

var calibrationSettings = new AppSettings();
var calibrationSettingsChanged = 0;
calibrationSettings.PropertyChanged += (_, args) =>
{
    if (args.PropertyName == nameof(AppSettings.TrackLyricOffsetsMilliseconds))
    {
        calibrationSettingsChanged++;
    }
};
calibrationSettings.SetTrackLyricOffsetMilliseconds(sourceCalibrationTrack, -160);
calibrationSettings.SetTrackLyricOffsetMilliseconds(alternateSourceCalibrationTrack, 20_000);
Assert(
    calibrationSettings.GetTrackLyricOffsetMilliseconds(sourceCalibrationTrack) == -160
    && calibrationSettings.GetTrackLyricOffsetMilliseconds(alternateSourceCalibrationTrack) == 10_000,
    "单曲校准应按歌曲保存并限制在安全范围");
Assert(calibrationSettingsChanged == 2,
    "单曲校准变化应触发设置自动保存通知");
var detachedCalibrationMap = calibrationSettings.TrackLyricOffsetsMilliseconds;
detachedCalibrationMap[sourceCalibrationTrack.LyricCalibrationKey] = 999;
Assert(calibrationSettings.GetTrackLyricOffsetMilliseconds(sourceCalibrationTrack) == -160,
    "外部修改设置字典副本不应绕过自动保存通知");

var settingsCompatibilityRoot = Path.Combine(
    Path.GetTempPath(),
    $"LyricsXQuiet-CoreSmoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(settingsCompatibilityRoot);
try
{
    await File.WriteAllTextAsync(
        Path.Combine(settingsCompatibilityRoot, "settings.json"),
        "{\"OffsetMilliseconds\":321}");
    var compatibilityStore = new SettingsStore(settingsCompatibilityRoot);
    var legacySettings = await compatibilityStore.LoadAsync();
    Assert(
        legacySettings.OffsetMilliseconds == 321
        && legacySettings.TrackLyricOffsetsMilliseconds.Count == 0
        && legacySettings.NetEaseSyncDelayMilliseconds == 1000
        && legacySettings.AppleMusicSyncAdvanceMilliseconds == 300,
        "不含新增字段的旧设置文件应兼容，并采用当前播放器补偿默认值");

    await File.WriteAllTextAsync(
        Path.Combine(settingsCompatibilityRoot, "settings.json"),
        "{\"SettingsSchemaVersion\":2,\"AppleMusicSyncAdvanceMilliseconds\":80}");
    var migratedAppleMusicAdvance = await compatibilityStore.LoadAsync();
    Assert(migratedAppleMusicAdvance.AppleMusicSyncAdvanceMilliseconds == 300,
        "旧版 80 ms Apple Music 默认值应一次性迁移到实测的 300 ms");

    await File.WriteAllTextAsync(
        Path.Combine(settingsCompatibilityRoot, "settings.json"),
        "{\"SettingsSchemaVersion\":2,\"AppleMusicSyncAdvanceMilliseconds\":140}");
    var preservedAppleMusicAdvance = await compatibilityStore.LoadAsync();
    Assert(preservedAppleMusicAdvance.AppleMusicSyncAdvanceMilliseconds == 140,
        "旧设置中的非默认 Apple Music 补偿应视为用户校准并保留");

    await File.WriteAllTextAsync(
        Path.Combine(settingsCompatibilityRoot, "settings.json"),
        "{\"SettingsSchemaVersion\":3,\"PlayerFollowMode\":\"Combined\",\"FollowedPlayerIds\":\"netease\"}");
    var migratedMonitorSelection = await compatibilityStore.LoadAsync();
    Assert(
        migratedMonitorSelection.FollowedPlayerIds == AppSettings.DefaultFollowedPlayerIds,
        "旧版组合模式遗留的单网易云监听应升级为多播放器监听");

    await File.WriteAllTextAsync(
        Path.Combine(settingsCompatibilityRoot, "settings.json"),
        "{\"SettingsSchemaVersion\":3,\"PlayerFollowMode\":\"Combined\",\"FollowedPlayerIds\":\"netease;spotify\"}");
    var preservedCustomMonitorSelection = await compatibilityStore.LoadAsync();
    Assert(
        preservedCustomMonitorSelection.FollowedPlayerIds == "netease;spotify",
        "旧版已自定义的多播放器监听选择应原样保留");

    await File.WriteAllTextAsync(
        Path.Combine(settingsCompatibilityRoot, "settings.json"),
        "{\"SettingsSchemaVersion\":3,\"PlayerFollowMode\":\"Shortcut\",\"FollowedPlayerIds\":\"netease\"}");
    var preservedShortcutSelection = await compatibilityStore.LoadAsync();
    Assert(
        preservedShortcutSelection.FollowedPlayerIds == "netease",
        "未启用后台监听时不应改写用户的快捷方式选择");

    await File.WriteAllTextAsync(
        Path.Combine(settingsCompatibilityRoot, "settings.json"),
        "{\"NetEaseSyncDelayMilliseconds\":30}");
    var migratedDefaultDelay = await compatibilityStore.LoadAsync();
    Assert(migratedDefaultDelay.NetEaseSyncDelayMilliseconds == 1000,
        "无 schema 的旧版 30 ms 默认值应一次性迁移到当前 1000 ms 基线");

    await File.WriteAllTextAsync(
        Path.Combine(settingsCompatibilityRoot, "settings.json"),
        "{\"NetEaseSyncDelayMilliseconds\":45}");
    var preservedLegacyDelay = await compatibilityStore.LoadAsync();
    Assert(preservedLegacyDelay.NetEaseSyncDelayMilliseconds == 45,
        "旧设置中的非默认网易云延迟应视为用户校准并保留");

    await File.WriteAllTextAsync(
        Path.Combine(settingsCompatibilityRoot, "settings.json"),
        "{\"SettingsSchemaVersion\":1,\"NetEaseSyncDelayMilliseconds\":30}");
    var preservedCurrentDelay = await compatibilityStore.LoadAsync();
    Assert(preservedCurrentDelay.NetEaseSyncDelayMilliseconds == 30,
        "带旧 schema 的明确 30 ms 用户设置应继续保留");

    var calibratedNetEaseSettings = new AppSettings
    {
        NetEaseSyncDelayMilliseconds = 1120,
        AppleMusicSyncAdvanceMilliseconds = 2800
    };
    await compatibilityStore.SaveAsync(calibratedNetEaseSettings);
    var restoredNetEaseSettings = await compatibilityStore.LoadAsync();
    Assert(restoredNetEaseSettings.NetEaseSyncDelayMilliseconds == 1120
           && restoredNetEaseSettings.AppleMusicSyncAdvanceMilliseconds == 2800
           && restoredNetEaseSettings.OffsetMilliseconds == 0,
        "网易云 1120 ms 和 Apple Music 2800 ms 专属补偿重启后应完整保留，全局偏移保持独立");

    await File.WriteAllTextAsync(
        Path.Combine(settingsCompatibilityRoot, "settings.json"),
        System.Text.Json.JsonSerializer.Serialize(new
        {
            SettingsSchemaVersion = 1,
            LyricsFilterPatterns = LyricFilter.LegacyDefaultPatterns
        }));
    var migratedCreditFilter = await compatibilityStore.LoadAsync();
    Assert(!migratedCreditFilter.ShowLyricsCredits
           && migratedCreditFilter.LyricsFilterPatterns == LyricFilter.DefaultPatterns,
        "旧版默认制作名单正则应迁移到独立开关，避免与显示制作信息选项冲突");

    const string persistedLocalPlayerPath = @"C:\Program Files\LocalAudio\foobar2000.exe";
    migratedDefaultDelay.SetTrackLyricOffsetMilliseconds(sourceCalibrationTrack, -220);
    migratedDefaultDelay.SaveCurrentAppearancePreset();
    migratedDefaultDelay.LocalPlayerExecutablePath = persistedLocalPlayerPath;
    migratedDefaultDelay.PlayerFollowMode = PlayerFollowModes.Combined;
    await compatibilityStore.SaveAsync(migratedDefaultDelay);
    var restoredSettings = await compatibilityStore.LoadAsync();
    Assert(
        restoredSettings.GetTrackLyricOffsetMilliseconds(sourceCalibrationTrack) == -220,
        "单曲校准应随 settings.json 持久化并能重新加载");
    Assert(restoredSettings.LocalPlayerExecutablePath == persistedLocalPlayerPath,
        "用户选择的本地播放器程序路径应随 settings.json 持久化");
    Assert(restoredSettings.PlayerFollowMode == PlayerFollowModes.Combined,
        "同时启用监听器与组合快捷方式的模式应随 settings.json 持久化");
    Assert(restoredSettings.HasSavedAppearancePreset
           && restoredSettings.ApplySavedAppearancePreset(),
        "用户保存的自定义外观方案应随 settings.json 持久化并可再次应用");
}
finally
{
    Directory.Delete(settingsCompatibilityRoot, recursive: true);
}

settings.LyricColor = "#80aabbcc";
settings.ProgressColor = "not-a-color";
Assert(settings.LyricColor == "#AABBCC", "歌词颜色应丢弃透明度并规范化 RGB");
Assert(settings.ProgressColor == "#5EEAD4", "无效颜色应回退到默认值");
Assert(settings.EnableLrclibLyrics
       && settings.EnableNetEaseLyrics
       && settings.EnableQqMusicLyrics
       && settings.EnableKugouLyrics,
    "初次使用应启用当前提供的四个歌词来源");
settings.PlayerFollowMode = "monitor";
Assert(settings.PlayerFollowMode == PlayerFollowModes.Monitor, "播放器监听模式应规范化保存");
settings.PlayerFollowMode = "combined";
Assert(settings.PlayerFollowMode == PlayerFollowModes.Combined,
    "组合快捷方式与后台监听应能同时启用");
Assert(PlayerFollowModes.UsesMonitor(settings.PlayerFollowMode)
       && PlayerFollowModes.UsesShortcut(settings.PlayerFollowMode),
    "同时启用模式应同时注册监听器并保留组合快捷方式");
Assert(PlayerFollowModes.FromFeatures(true, true) == PlayerFollowModes.Combined
       && PlayerFollowModes.FromFeatures(true, false) == PlayerFollowModes.Monitor
       && PlayerFollowModes.FromFeatures(false, true) == PlayerFollowModes.Shortcut,
    "独立启动功能开关应稳定映射到持久化模式");
settings.PlayerFollowMode = "invalid";
Assert(settings.PlayerFollowMode == PlayerFollowModes.Shortcut, "无效伴随模式应回退到组合快捷方式");
settings.Locked = false;
settings.HoverHide = false;
settings.DoubleLine = false;
settings.ProgressiveHighlight = false;
settings.ShowTrackInfoWhenNoLyrics = false;
settings.ExcludeFromCapture = true;
settings.CenterAlignedDragging = true;
settings.BackgroundOpacity = 0.2;
settings.FontSize = 37;
settings.FontFamilyName = "SimSun";
settings.LyricColor = "#112233";
settings.ProgressColor = "#445566";
settings.BackgroundColor = "#778899";
settings.NetEaseSyncDelayMilliseconds = 360;
settings.NetEaseOffsetMilliseconds = -200;
settings.AppleMusicSyncAdvanceMilliseconds = 220;
settings.ResetLyricsDisplay();
Assert(settings.Locked && settings.HoverHide && settings.DoubleLine && settings.ProgressiveHighlight,
    "恢复歌词显示默认设置应恢复锁定、经过隐藏、双行显示与歌词渐变");
Assert(settings.ShowTrackInfoWhenNoLyrics,
    "恢复歌词显示默认设置应恢复前奏或无歌词时显示歌曲信息");
Assert(!settings.ExcludeFromCapture, "恢复歌词显示默认设置应关闭截图排除");
Assert(!settings.CenterAlignedDragging, "恢复歌词显示默认设置应关闭居中移动模式");
Assert(settings.BackgroundOpacity == 0.52 && settings.FontSize == 24,
    "恢复歌词显示默认设置应恢复背景深浅与字号");
Assert(settings.FontFamilyName == "Microsoft YaHei UI",
    "恢复歌词显示默认设置应恢复默认字体");
Assert(settings.LyricColor == "#F8FAFC"
       && settings.ProgressColor == "#5EEAD4"
       && settings.BackgroundColor == "#0E1318",
    "恢复歌词显示默认设置应恢复三种默认颜色");
Assert(settings.NetEaseSyncDelayMilliseconds == 1000
       && settings.GetPlayerLyricOffsetMilliseconds("cloudmusic.exe") == 0,
    "恢复默认应将网易云默认延后恢复为 1000 ms，播放器微调恢复为 0 ms");
Assert(settings.AppleMusicSyncAdvanceMilliseconds == 300,
    "恢复歌词显示默认设置应将 Apple Music 提前补偿恢复为 300 ms");

const string noisyLyrics = """
    [00:01.00]作词：某某
    [00:03.00]真正的第一句
    [00:05.00]Producer: Someone
    [00:06.00]封面绘画Cover Painting：熊茗玥/郭振易
    [00:06.50]发行单位：街声派歌
    [00:07.00]真正的第二句
    """;
var unfilteredCreditsDocument = LrcParser.Parse(noisyLyrics, "filter-test");
var filteredDocument = LyricFilter.RemoveCredits(unfilteredCreditsDocument);
Assert(filteredDocument.Lines.Count == 2,
    "关闭制作信息后应移除中英文制作名单、封面绘画和发行单位");
Assert(filteredDocument.Lines[0].Text == "真正的第一句"
       && filteredDocument.Lines[1].Text == "真正的第二句",
    "制作信息过滤不应改动正常歌词");
Assert(LyricFilter.Apply(unfilteredCreditsDocument, LyricFilter.DefaultPatterns).Lines.Count == 6,
    "新的默认高级正则不应重复隐藏制作名单，保证显示制作信息开关真正有效");
Assert(LyricFilter.FindInvalidPatterns("[invalid").Count == 1, "应识别无效正则而不抛出异常");
var invalidPatternLines = LyricFilter.FindPatternErrors("^valid$\n\n[invalid\r\n(also-invalid");
Assert(invalidPatternLines.Count == 2
       && invalidPatternLines[0].LineNumber == 3
       && invalidPatternLines[1].LineNumber == 4,
    "正则验证应保留空行后的真实行号，帮助用户直接定位错误规则");
var instrumentalDocument = LrcParser.Parse("[00:00.00]纯音乐，请欣赏", "instrumental-test");
Assert(instrumentalDocument.IsInstrumental, "纯音乐提示应被识别并用于隐藏悬浮歌词");
var ordinaryNoLyricsMarker = LrcParser.Parse("[00:00.00]暂无歌词", "missing-test");
Assert(!ordinaryNoLyricsMarker.IsInstrumental, "缺少歌词不能被误判为纯音乐");

var exactCandidate = new LyricsCandidate(
    "exact",
    "test",
    "Song",
    "Artist",
    "Album",
    TimeSpan.FromMinutes(3),
    0,
    document);
var liveCandidate = exactCandidate with
{
    Id = "live",
    Title = "Song (Live)",
    Duration = TimeSpan.FromMinutes(4)
};
Assert(
    LyricsMatchScorer.Score(exactCandidate, track) < LyricsMatchScorer.Score(liveCandidate, track),
    "候选评分应优先选择名称和时长一致的歌曲版本");
var exactAssessment = LyricsMatchScorer.Assess(exactCandidate, track);
Assert(exactAssessment.ArtistMatches
       && exactAssessment.HasComparableAlbum
       && exactAssessment.AlbumMatches
       && exactAssessment.HasComparableDuration
       && exactAssessment.DurationDifference == TimeSpan.Zero,
    "候选解释应准确暴露歌手、专辑和时长的匹配依据");
var mismatchedAssessment = LyricsMatchScorer.Assess(
    exactCandidate with
    {
        Artist = "Other Artist",
        Album = "Different Release",
        Duration = TimeSpan.FromSeconds(220)
    },
    track);
Assert(!mismatchedAssessment.ArtistMatches
       && !mismatchedAssessment.AlbumMatches
       && mismatchedAssessment.DurationDifference == TimeSpan.FromSeconds(40),
    "候选解释不得把歌手、专辑或明显时长差异误报为匹配");
var featuredArtistAssessment = LyricsMatchScorer.Assess(
    exactCandidate with { Artist = "Artist feat. Guest" },
    track);
Assert(featuredArtistAssessment.ArtistMatches,
    "带 feat. 合作歌手的普通署名应继续匹配主歌手");

var standardTrack = new TrackIdentity(
    "Song",
    "Artist",
    "Original Album",
    TimeSpan.FromMinutes(3));
var standardRelease = exactCandidate with
{
    Album = "Original Album",
    Duration = TimeSpan.FromMinutes(3)
};
var concertRelease = exactCandidate with
{
    Id = "concert",
    Title = "Song (Live)",
    Album = "2020 World Tour Live",
    Duration = TimeSpan.FromMinutes(3)
};
Assert(
    LyricsMatchScorer.Score(standardRelease, standardTrack)
        < LyricsMatchScorer.Score(concertRelease, standardTrack),
    "没有版本标志的歌曲应优先匹配标准发行，而不是现场或演唱会版本");

var liveTrack = standardTrack with
{
    Title = "Song (Live)",
    Album = "2020 World Tour Live"
};
var traditionalConcertTrack = new TrackIdentity(
    "倒帶 - 演唱會版",
    "周杰倫",
    "The One 演唱會",
    TimeSpan.FromMinutes(4.1));
var simplifiedLiveCandidate = exactCandidate with
{
    Id = "concert-live-zh",
    Title = "倒带 (Live)",
    Artist = "周杰伦",
    Album = "The One Live",
    Duration = TimeSpan.FromMinutes(4.1)
};
var simplifiedStandardCandidate = simplifiedLiveCandidate with
{
    Id = "concert-standard-zh",
    Title = "倒带",
    Album = "叶惠美"
};
Assert(
    LyricsMatchScorer.IsRelevant(simplifiedLiveCandidate, traditionalConcertTrack),
    "繁体“演唱會版”标题应把同曲 Live 候选保留到版本评分阶段");
Assert(
    !LyricsMatchScorer.IsRelevantTitle("被动", "被動"),
    "移除简繁偏好后，普通歌曲不应再靠简繁转换进入候选；仅现场/Live 版本保留例外");
Assert(
    LyricsMatchScorer.Score(simplifiedLiveCandidate, traditionalConcertTrack)
        < LyricsMatchScorer.Score(simplifiedStandardCandidate, traditionalConcertTrack),
    "演唱会版曲目应优先匹配 Live 候选，而不是标准录音室版本");
var otherConcertRelease = concertRelease with
{
    Id = "other-concert",
    Album = "2022 World Tour Live"
};
Assert(
    LyricsMatchScorer.Score(concertRelease, liveTrack)
        < LyricsMatchScorer.Score(otherConcertRelease, liveTrack)
    && LyricsMatchScorer.Score(concertRelease, liveTrack)
        < LyricsMatchScorer.Score(standardRelease, liveTrack),
    "现场版应优先匹配相同演唱会专辑，并降低其他场次和标准版的优先级");

var requestedVersionCases = new[]
{
    new
    {
        Name = "Remix",
        Track = standardTrack with
        {
            Title = "Song (Remix)",
            Artist = "Artist / DJ Example",
            Album = "Song Remixes"
        },
        Candidate = standardRelease with
        {
            Id = "matching-remix",
            Title = "Song (Remix)",
            Artist = "Artist / DJ Example",
            Album = "Song Remixes"
        }
    },
    new
    {
        Name = "伴奏",
        Track = standardTrack with { Album = "Song 伴奏版" },
        Candidate = standardRelease with { Id = "matching-instrumental", Album = "Song 伴奏版" }
    },
    new
    {
        Name = "翻唱",
        Track = standardTrack with { Artist = "Cover Singer（翻唱）", Album = "热门翻唱集" },
        Candidate = standardRelease with
        {
            Id = "matching-cover",
            Artist = "Cover Singer（翻唱）",
            Album = "热门翻唱集"
        }
    },
    new
    {
        Name = "Demo",
        Track = standardTrack with { Title = "Song (Demo)", Album = "Demo Sessions" },
        Candidate = standardRelease with
        {
            Id = "matching-demo",
            Title = "Song (Demo)",
            Album = "Demo Sessions"
        }
    },
    new
    {
        Name = "DJ",
        Track = standardTrack with { Artist = "Artist / DJ Example", Album = "DJ版" },
        Candidate = standardRelease with
        {
            Id = "matching-dj",
            Artist = "Artist / DJ Example",
            Album = "DJ版"
        }
    }
};

foreach (var versionCase in requestedVersionCases)
{
    Assert(
        LyricsMatchScorer.Score(versionCase.Candidate, versionCase.Track)
            < LyricsMatchScorer.Score(standardRelease, versionCase.Track),
        $"带有 {versionCase.Name} 标志的曲目应优先匹配对应版本");

    Assert(
        LyricsMatchScorer.Score(standardRelease, standardTrack)
            < LyricsMatchScorer.Score(versionCase.Candidate, standardTrack),
        $"没有版本标志的曲目不应优先匹配 {versionCase.Name} 版本");
}

var pickerTrack = new TrackIdentity(
    "美人蕉",
    "李琳",
    string.Empty,
    TimeSpan.FromMinutes(4.5));
var pickerExact = new LyricsCandidate(
    "same-id",
    "来源 A",
    "美人蕉",
    "李琳",
    string.Empty,
    TimeSpan.FromMinutes(4.5),
    0,
    document);
var pickerUnrelated = pickerExact with
{
    Id = "unrelated",
    Title = "一个人挺好",
    Duration = TimeSpan.FromMinutes(4.38)
};
var pickerLive = pickerExact with { Id = "live", Title = "美人蕉 (Live)" };
var pickerRemix = pickerExact with { Id = "remix", Title = "美人蕉 - Remix" };
var pickerInstrumentalVersion = pickerExact with { Id = "instrumental", Title = "美人蕉伴奏" };
var pickerOtherSinger = pickerExact with { Id = "cover", Artist = "另一位歌手" };

Assert(!LyricsMatchScorer.IsRelevant(pickerUnrelated, pickerTrack),
    "搜索结果不应仅因歌手相同就混入明显无关的歌名");
Assert(LyricsMatchScorer.IsRelevant(pickerLive, pickerTrack)
       && LyricsMatchScorer.IsRelevant(pickerRemix, pickerTrack)
       && LyricsMatchScorer.IsRelevant(pickerInstrumentalVersion, pickerTrack),
    "相关性过滤应保留 Live、Remix 和伴奏等同曲版本");
Assert(LyricsMatchScorer.IsRelevant(pickerOtherSinger, pickerTrack),
    "相关性过滤应保留同名歌曲的其他歌手版本");

var artistAliasTrack = new TrackIdentity(
    "去时风",
    "声无哀乐SWAL",
    "声有·水无贰 - Single",
    TimeSpan.FromMinutes(6.47));
var artistAliasCandidate = pickerExact with
{
    Id = "correct-artist-alias",
    Title = "去时风",
    Artist = "聲無哀樂乐队THEWEAPONS",
    Album = string.Empty,
    Duration = TimeSpan.FromMinutes(4.87)
};
var wrongSingerCandidate = artistAliasCandidate with
{
    Id = "wrong-singer",
    Artist = "雷震宇",
    Duration = TimeSpan.FromMinutes(2.82)
};
var exactDurationArtistCandidate = artistAliasCandidate with
{
    Id = "correct-artist-and-duration",
    Duration = artistAliasTrack.Duration
};
var mediumDurationMismatchCandidate = artistAliasCandidate with
{
    Id = "medium-duration-mismatch",
    Duration = TimeSpan.FromMinutes(4.87)
};
var severeDurationMismatchCandidate = artistAliasCandidate with
{
    Id = "severe-duration-mismatch",
    Duration = TimeSpan.FromMinutes(2.82)
};
Assert(
    LyricsMatchScorer.Score(artistAliasCandidate, artistAliasTrack)
        < LyricsMatchScorer.Score(wrongSingerCandidate, artistAliasTrack),
    "同歌名候选中，简繁/乐队后缀/英文艺名不同的同一歌手应排在完全不同歌手之前");
Assert(
    LyricsMatchScorer.Score(exactDurationArtistCandidate, artistAliasTrack)
        < LyricsMatchScorer.Score(mediumDurationMismatchCandidate, artistAliasTrack)
    && LyricsMatchScorer.Score(mediumDurationMismatchCandidate, artistAliasTrack)
        < LyricsMatchScorer.Score(severeDurationMismatchCandidate, artistAliasTrack),
    "同歌名同歌手候选应按时长差持续降权，不能把所有超过 90 秒的差异压成同一分数");

var pickerCacheDirectory = Path.Combine(
    Path.GetTempPath(),
    $"lyrveil-picker-smoke-{Guid.NewGuid():N}");
try
{
    var sourceA = new StaticLyricsProvider(
        "来源 A",
        [
            pickerExact,
            pickerExact with { },
            pickerUnrelated,
            pickerLive,
            pickerRemix,
            pickerInstrumentalVersion,
            pickerOtherSinger
        ]);
    var sourceB = new StaticLyricsProvider(
        "来源 B",
        [pickerExact with { Provider = "来源 B" }]);
    var pickerResolver = new LyricsResolver(
        new LyricsCache(pickerCacheDirectory),
        [sourceA, sourceB]);
    var filteredCandidates = await pickerResolver.SearchAsync(pickerTrack);
    Assert(filteredCandidates.All(candidate => candidate.Title != "一个人挺好"),
        "手动歌词列表应移除明显无关的候选");
    Assert(filteredCandidates.Count(candidate =>
               candidate.Provider == "来源 A"
               && candidate.Title == "美人蕉"
               && candidate.Artist == "李琳") == 1,
        "同一来源内歌曲 ID 相同的重复候选应合并");
    Assert(filteredCandidates.Count(candidate =>
               candidate.Title == "美人蕉"
               && candidate.Artist == "李琳") == 2,
        "不同来源的同一首歌词应分别保留，便于用户选择来源");
    Assert(filteredCandidates.Any(candidate => candidate.Title == "美人蕉 (Live)")
           && filteredCandidates.Any(candidate => candidate.Title == "美人蕉 - Remix")
           && filteredCandidates.Any(candidate => candidate.Title == "美人蕉伴奏")
           && filteredCandidates.Any(candidate => candidate.Artist == "另一位歌手"),
        "去重后仍应保留合理版本和同名不同歌手候选");
}
finally
{
    if (Directory.Exists(pickerCacheDirectory))
    {
        Directory.Delete(pickerCacheDirectory, true);
    }
}

var automaticCacheDirectory = Path.Combine(
    Path.GetTempPath(),
    $"lyrveil-auto-resolve-smoke-{Guid.NewGuid():N}");
try
{
    var unrelatedDocument = LrcParser.Parse(
        "[00:01.00]这不是当前歌曲",
        "auto-unrelated-source");
    var relevantDocument = LrcParser.Parse(
        "[00:01.00]这是正确歌曲",
        "auto-relevant-source");
    var unrelatedSource = new StaticLyricsProvider(
        "自动来源 A",
        [pickerUnrelated with
        {
            Provider = "自动来源 A",
            Document = unrelatedDocument
        }]);
    var relevantSource = new StaticLyricsProvider(
        "自动来源 B",
        [pickerExact with
        {
            Provider = "自动来源 B",
            Document = relevantDocument
        }]);
    var automaticCache = new LyricsCache(automaticCacheDirectory);
    var automaticResolver = new LyricsResolver(
        automaticCache,
        [unrelatedSource, relevantSource]);

    var automaticallyResolved = await automaticResolver.ResolveAsync(
        pickerTrack,
        forceNetwork: true);
    Assert(automaticallyResolved?.Source == "auto-relevant-source",
        "自动匹配应跳过首个来源的无关歌名，并使用下一来源的相关结果");
    var automaticFiles = Directory.GetFiles(automaticCacheDirectory, "*.lrc");
    Assert(automaticFiles.Length == 1,
        "自动匹配只应缓存最终选中的相关歌词");
    var automaticFileText = await File.ReadAllTextAsync(automaticFiles[0]);
    Assert(automaticFileText.Contains("[source:auto-relevant-source]", StringComparison.Ordinal)
           && !automaticFileText.Contains("auto-unrelated-source", StringComparison.Ordinal),
        "明显无关的自动搜索结果不得写入歌词缓存");

    var standardVersionDocument = LrcParser.Parse(
        "[00:01.00]标准录音版本",
        "auto-standard-version");
    var matchingConcertDocument = LrcParser.Parse(
        "[00:01.00]对应演唱会版本",
        "auto-matching-concert");
    var versionAwareResolver = new LyricsResolver(
        automaticCache,
        [
            new StaticLyricsProvider(
                "标准版来源",
                [standardRelease with
                {
                    Provider = "标准版来源",
                    Document = standardVersionDocument
                }]),
            new StaticLyricsProvider(
                "现场版来源",
                [concertRelease with
                {
                    Provider = "现场版来源",
                    Document = matchingConcertDocument
                }])
        ]);
    var versionAwareResolved = await versionAwareResolver.ResolveAsync(
        liveTrack,
        forceNetwork: true);
    Assert(versionAwareResolved?.Source == "auto-matching-concert",
        "自动匹配应跨来源比较版本与专辑，并为 Live 曲目选择对应演唱会版本");

    var wordTimedQualityDocument = LrcParser.Parse(
        "[00:00.00]<00:00.00>只<00:01.00>有<00:02.00>逐<00:03.00>字\n"
        + "[00:10.00]<00:10.00>下<00:11.00>一<00:12.00>句",
        "word-timing-quality-test");
    var lineTimedCandidate = standardRelease with
    {
        Id = "line-timed",
        Provider = "A 逐行来源",
        Document = lineTimedDocument
    };
    var wordTimedCandidate = standardRelease with
    {
        Id = "word-timed",
        Provider = "B 逐字来源",
        Document = wordTimedQualityDocument
    };
    var progressiveHighlightEnabled = true;
    var qualityAwareResolver = new LyricsResolver(
        automaticCache,
        [
            new StaticLyricsProvider("A 逐行来源", [lineTimedCandidate]),
            new StaticLyricsProvider("B 逐字来源", [wordTimedCandidate])
        ],
        () => progressiveHighlightEnabled);

    var qualityPreferred = await qualityAwareResolver.SearchAsync(standardTrack);
    Assert(qualityPreferred[0].Provider == "B 逐字来源",
        "开启渐变时，同一歌曲和版本应优先真实逐字歌词");

    progressiveHighlightEnabled = false;
    var qualityNeutral = await qualityAwareResolver.SearchAsync(standardTrack);
    Assert(qualityNeutral[0].Provider == "A 逐行来源",
        "关闭渐变时，不应再额外提高逐字歌词的排序");

    var slightlyLessExactWordTimedCandidate = wordTimedCandidate with
    {
        Id = "word-timed-near-tie",
        Duration = standardTrack.Duration + TimeSpan.FromSeconds(2)
    };
    var slightPreferenceResolver = new LyricsResolver(
        automaticCache,
        [
            new StaticLyricsProvider("A 精确逐行来源", [lineTimedCandidate]),
            new StaticLyricsProvider("B 近似逐字来源", [slightlyLessExactWordTimedCandidate])
        ],
        () => true);
    var slightPreference = await slightPreferenceResolver.SearchAsync(standardTrack);
    Assert(slightPreference[0].Id == "word-timed-near-tie",
        "开启渐变时，真实逐字歌词应能在元数据近似平局时获得小幅优先级");

    var noticeablyLessExactWordTimedCandidate = wordTimedCandidate with
    {
        Id = "word-timed-not-near-tie",
        Duration = standardTrack.Duration + TimeSpan.FromSeconds(5)
    };
    var boundedPreferenceResolver = new LyricsResolver(
        automaticCache,
        [
            new StaticLyricsProvider("A 精确逐行来源", [lineTimedCandidate]),
            new StaticLyricsProvider("B 偏差逐字来源", [noticeablyLessExactWordTimedCandidate])
        ],
        () => true);
    var boundedPreference = await boundedPreferenceResolver.SearchAsync(standardTrack);
    Assert(boundedPreference[0].Id == "line-timed",
        "逐字歌词的小幅优先级不得压过已经明显更准确的歌曲时长");

    var wrongVersionWithWordTiming = wordTimedCandidate with
    {
        Id = "word-timed-live",
        Title = "Song (Live)",
        Album = "2022 World Tour Live"
    };
    Assert(
        LyricsMatchScorer.Score(lineTimedCandidate, standardTrack)
            < LyricsMatchScorer.Score(wrongVersionWithWordTiming, standardTrack),
        "逐字质量不得压过歌曲版本匹配，标准版仍应排在错误的 Live 逐字版本前");

    var lessAccurateWordTimedCandidate = wordTimedCandidate with
    {
        Id = "word-timed-less-accurate",
        Album = "另一张专辑",
        Duration = standardTrack.Duration + TimeSpan.FromSeconds(8)
    };
    var metadataFirstResolver = new LyricsResolver(
        automaticCache,
        [
            new StaticLyricsProvider("A 精确逐行来源", [lineTimedCandidate]),
            new StaticLyricsProvider("B 较弱逐字来源", [lessAccurateWordTimedCandidate])
        ],
        () => true);
    var metadataFirst = await metadataFirstResolver.SearchAsync(standardTrack);
    Assert(metadataFirst[0].Id == "line-timed",
        "歌曲名、歌手、专辑和时长必须先于逐字质量决定排序");

    var nearTieKugouCandidate = lineTimedCandidate with
    {
        Id = "kugou-near-tie",
        Provider = "酷狗音乐",
        Duration = standardTrack.Duration + TimeSpan.FromSeconds(2),
        Document = LrcParser.Parse("[00:01.00]酷狗近似并列歌词", "酷狗音乐")
    };
    var exactNetEaseWordCandidate = wordTimedCandidate with
    {
        Id = "netease-exact-word",
        Provider = "网易云音乐",
        Document = LrcParser.Parse(
            "[00:00.00]<00:00.00>网<00:01.00>易<00:02.00>云\n"
            + "[00:05.00]<00:05.00>逐<00:06.00>字",
            "网易云音乐")
    };
    var kugouPreferredResolver = new LyricsResolver(
        automaticCache,
        [
            new StaticLyricsProvider("网易云音乐", [exactNetEaseWordCandidate]),
            new StaticLyricsProvider("酷狗音乐", [nearTieKugouCandidate])
        ],
        () => true);
    var kugouPreferred = await kugouPreferredResolver.SearchAsync(standardTrack);
    Assert(kugouPreferred[0].Provider == "酷狗音乐",
        "歌曲身份近似并列时，应按用户偏好优先酷狗歌词");

    var wrongSingerKugouCandidate = nearTieKugouCandidate with
    {
        Id = "kugou-wrong-singer",
        Artist = "完全不同的歌手",
        Duration = standardTrack.Duration
    };
    var identityFirstResolver = new LyricsResolver(
        automaticCache,
        [
            new StaticLyricsProvider("网易云音乐", [exactNetEaseWordCandidate]),
            new StaticLyricsProvider("酷狗音乐", [wrongSingerKugouCandidate])
        ],
        () => true);
    var identityFirst = await identityFirstResolver.SearchAsync(standardTrack);
    Assert(identityFirst[0].Provider == "网易云音乐",
        "酷狗来源偏好不得抬高明显错误歌手、版本或时长的候选");

}
finally
{
    if (Directory.Exists(automaticCacheDirectory))
    {
        Directory.Delete(automaticCacheDirectory, true);
    }
}

var kugouPolicyCacheDirectory = Path.Combine(
    Path.GetTempPath(),
    $"lyrveil-kugou-policy-smoke-{Guid.NewGuid():N}");
try
{
    var migrationTrack = new TrackIdentity(
        "缓存迁移歌曲",
        "同一歌手",
        "同一专辑",
        TimeSpan.FromMinutes(3.5));
    var oldAutomaticDocument = LrcParser.Parse(
        "[00:01.00]旧的网易云自动歌词",
        "网易云音乐");
    var preferredDocument = LrcParser.Parse(
        "[00:01.00]新的酷狗优先歌词",
        "酷狗音乐");
    var preferredCandidate = new LyricsCandidate(
        "kugou-policy-exact",
        "酷狗音乐",
        migrationTrack.Title,
        migrationTrack.Artist,
        migrationTrack.Album,
        migrationTrack.Duration,
        0,
        preferredDocument);
    var preferredProvider = new StaticLyricsProvider(
        "酷狗音乐",
        [preferredCandidate]);
    var policyCache = new LyricsCache(kugouPolicyCacheDirectory);
    await policyCache.SaveAsync(migrationTrack, oldAutomaticDocument);
    var policyResolver = new LyricsResolver(policyCache, [preferredProvider]);

    var migratedDocument = await policyResolver.ResolveAsync(migrationTrack);
    Assert(migratedDocument?.Source == "酷狗音乐",
        "旧自动缓存应按新策略一次性升级为可信的酷狗歌词");
    var migratedEntry = await policyCache.TryLoadEntryAsync(migrationTrack);
    Assert(migratedEntry?.RankingPolicy == "kugou-first-v1"
           && migratedEntry.DeclaredSource == "酷狗音乐",
        "升级后的自动缓存应记录酷狗优先策略，避免每次播放重复联网");
    Assert(preferredProvider.SearchRequestCount == 1,
        "自动缓存迁移首次只应搜索一次");

    _ = await policyResolver.ResolveAsync(migrationTrack);
    Assert(preferredProvider.SearchRequestCount == 1,
        "已迁移缓存再次播放时不应重复搜索歌词");

    var manualDocument = LrcParser.Parse(
        "[00:01.00]用户手动固定的歌词",
        "网易云音乐");
    await policyResolver.SaveSelectionAsync(
        migrationTrack,
        preferredCandidate with
        {
            Id = "manual-selection",
            Provider = "网易云音乐",
            Document = manualDocument
        });
    var manualResult = await policyResolver.ResolveAsync(migrationTrack);
    Assert(manualResult?.RawText.Contains("用户手动固定", StringComparison.Ordinal) == true
           && preferredProvider.SearchRequestCount == 1,
        "酷狗优先策略不得覆盖用户手动选择或导入的歌词");
}
finally
{
    if (Directory.Exists(kugouPolicyCacheDirectory))
    {
        Directory.Delete(kugouPolicyCacheDirectory, true);
    }
}

Assert(
    PlaybackStateTrustPolicy.ApplyAudioFallback(
        PlaybackState.Paused,
        "applemusic",
        hasUsableTimeline: false,
        isCurrentlyAudible: true) == PlaybackState.Paused,
    "Apple Music 明确暂停时不应被短暂音频尾音改回播放中");
Assert(
    PlaybackStateTrustPolicy.ApplyAudioFallback(
        PlaybackState.Paused,
        "spotify",
        hasUsableTimeline: true,
        isCurrentlyAudible: true) == PlaybackState.Paused,
    "Spotify 明确暂停时应立即隐藏歌词");
Assert(
    PlaybackStateTrustPolicy.ApplyAudioFallback(
        PlaybackState.Paused,
        "netease",
        hasUsableTimeline: false,
        isCurrentlyAudible: true) == PlaybackState.Playing,
    "没有时间线的网易云原生会话仍需允许音频活动修正错误状态");
Assert(
    PlaybackStateTrustPolicy.ApplyAudioFallback(
        PlaybackState.Paused,
        "netease",
        hasUsableTimeline: false,
        isCurrentlyAudible: false) == PlaybackState.Paused,
    "网易云暂停后的最近有声尾窗不得把明确暂停状态改回播放中");
Assert(
    PlaybackStateTrustPolicy.ApplyAudioFallback(
        PlaybackState.Paused,
        "netease",
        hasUsableTimeline: true,
        isCurrentlyAudible: true) == PlaybackState.Paused,
    "网易云提供可靠时间线后应相信明确暂停状态");
Assert(
    PlaybackStateTrustPolicy.ApplyAudioFallback(
        PlaybackState.Unknown,
        "local",
        hasUsableTimeline: false,
        isCurrentlyAudible: true) == PlaybackState.Playing,
    "播放器状态未知但确有声音时仍可使用音频兜底");

var cacheDirectory = Path.Combine(
    Path.GetTempPath(),
    $"lyrveil-core-smoke-{Guid.NewGuid():N}");
try
{
    var cache = new LyricsCache(cacheDirectory);
    await cache.SaveAsync(track, document);
    var savedFiles = Directory.GetFiles(cacheDirectory, "*.lrc");
    Assert(savedFiles.Length == 1, "歌词应保存为独立 LRC 文件");
    Assert(Path.GetFileName(savedFiles[0]).Contains("Song", StringComparison.Ordinal),
        "歌词文件名应包含可读的歌曲名");
    Assert((await File.ReadAllTextAsync(savedFiles[0])).Contains("[source:smoke-test]", StringComparison.Ordinal),
        "缓存文件应保留歌词来源，便于下次识别手选版本");
    var cachedDocument = await cache.TryLoadAsync(track);
    Assert(cachedDocument is { HasSynchronizedLines: true }, "下次播放应能读取已选择的歌词文件");
    Assert(cachedDocument?.Source.Contains("smoke-test", StringComparison.Ordinal) == true,
        "重新读取缓存后应恢复原歌词来源");
    Assert(cachedDocument?.Lines[0].Words.Count == document.Lines[0].Words.Count,
        "缓存往返必须保留逐字时间标签");

    var providerSpecificDocument = document with
    {
        Source = "酷狗音乐",
        RawText = "[1000,2000]<0,500,0>你<500,500,0>好"
    };
    await cache.SaveAsync(track, providerSpecificDocument);
    var providerSpecificRoundTrip = await cache.TryLoadAsync(track);
    Assert(providerSpecificRoundTrip is { HasSynchronizedLines: true }
           && providerSpecificRoundTrip.Lines[0].Words.Count == 2,
        "酷狗 KRC 等来源专用格式应统一保存为可回读的增强 LRC");

    var appleCacheDirectory = Path.Combine(cacheDirectory, "apple-compatibility");
    var appleCache = new LyricsCache(appleCacheDirectory);
    var legacyAppleTrack = new TrackIdentity(
        "程艾影",
        "赵雷 — 署前街少年",
        string.Empty,
        TimeSpan.Zero);
    var normalizedAppleTrack = new TrackIdentity(
        "程艾影",
        "赵雷",
        "署前街少年",
        TimeSpan.FromMinutes(5),
        appleMusicAumid);
    await appleCache.SaveAsync(legacyAppleTrack, document with { Source = "网易云音乐" });
    Assert(await appleCache.TryLoadAsync(normalizedAppleTrack)
           is { HasSynchronizedLines: true },
        "Apple Music 元数据规范化后仍应直接复用旧的歌手—专辑缓存");

    var mismatchedNetworkDocument = LrcParser.Parse(
        "[00:01.00]错误歌词",
        "QQ 音乐");
    await cache.SaveAsync(track, mismatchedNetworkDocument);
    var mismatchedPath = Directory.GetFiles(cacheDirectory, "*.lrc").Single();
    var mismatchedContent = await File.ReadAllTextAsync(mismatchedPath);
    mismatchedContent = mismatchedContent.Replace(
        "[ti:Song]",
        "[ti:完全不同的歌曲]",
        StringComparison.Ordinal);
    await File.WriteAllTextAsync(mismatchedPath, mismatchedContent);
    Assert(await cache.TryLoadAsync(track) is null,
        "网络来源缓存的标题与当前歌曲不符时不应继续使用");
}
finally
{
    if (Directory.Exists(cacheDirectory))
    {
        Directory.Delete(cacheDirectory, true);
    }
}

var versionedCacheRoot = Path.Combine(
    Path.GetTempPath(),
    $"lyrveil-versioned-cache-smoke-{Guid.NewGuid():N}");
try
{
    var versionedCache = new LyricsCache(versionedCacheRoot);
    var storageStandardTrack = new TrackIdentity(
        "同名歌曲",
        "同一歌手",
        "标准录音室专辑",
        TimeSpan.FromMilliseconds(180_125),
        "Spotify.exe");
    var storageLiveTrack = storageStandardTrack with
    {
        Album = "2026巡回演唱会 Live",
        Duration = TimeSpan.FromMilliseconds(241_620)
    };
    var storageAlternateEditTrack = storageStandardTrack with
    {
        Duration = TimeSpan.FromMilliseconds(186_900)
    };

    Assert(LyricsCacheIdentity.GetPrimaryKey(storageStandardTrack)
           != LyricsCacheIdentity.GetPrimaryKey(storageLiveTrack),
        "标准版和演唱会版必须使用不同歌词缓存身份");
    Assert(LyricsCacheIdentity.GetPrimaryKey(storageStandardTrack)
           != LyricsCacheIdentity.GetPrimaryKey(storageAlternateEditTrack),
        "同歌名、歌手和专辑但时长不同的录音版本也必须分开缓存");
    Assert(LyricsCacheIdentity.GetPrimaryKey(storageStandardTrack)
           == LyricsCacheIdentity.GetPrimaryKey(
               storageStandardTrack with { Duration = TimeSpan.FromMilliseconds(180_310) }),
        "同一录音在不同播放器产生的毫秒级时长误差不应制造重复歌词文件");

    await versionedCache.SaveAsync(
        storageStandardTrack,
        LrcParser.Parse("[00:01.00]标准版歌词", "手动导入"),
        LyricsCacheSelectionKind.Manual);
    await versionedCache.SaveAsync(
        storageLiveTrack,
        LrcParser.Parse("[00:01.00]现场版歌词", "手动导入"),
        LyricsCacheSelectionKind.Manual);
    await versionedCache.SaveAsync(
        storageAlternateEditTrack,
        LrcParser.Parse("[00:01.00]加长版歌词", "手动导入"),
        LyricsCacheSelectionKind.Manual);

    Assert(Directory.GetFiles(versionedCacheRoot, "*.lrc").Length == 3,
        "三个不同录音版本应保存为三个独立 LRC 文件");
    Assert((await versionedCache.TryLoadAsync(storageStandardTrack))?.Lines[0].Text == "标准版歌词",
        "标准版应只加载自己的歌词文件");
    Assert((await versionedCache.TryLoadAsync(storageLiveTrack))?.Lines[0].Text == "现场版歌词",
        "演唱会版应只加载自己的歌词文件");
    Assert((await versionedCache.TryLoadAsync(storageAlternateEditTrack))?.Lines[0].Text == "加长版歌词",
        "不同时长版本应只加载自己的歌词文件");

    var unstableDurationCache = new LyricsCache(Path.Combine(versionedCacheRoot, "unstable-duration"));
    var manuallyConfirmedTrack = new TrackIdentity(
        "播放器时长暂时错误的歌曲",
        "同一歌手",
        "唯一对应专辑",
        TimeSpan.FromSeconds(388),
        "Spotify.exe");
    await unstableDurationCache.SaveAsync(
        manuallyConfirmedTrack,
        LrcParser.Parse("[00:01.00]用户手动确认的歌词", "酷狗音乐"),
        LyricsCacheSelectionKind.Manual);
    await unstableDurationCache.SaveAsync(
        manuallyConfirmedTrack with { Duration = TimeSpan.FromSeconds(174) },
        LrcParser.Parse("[00:01.00]错误时长触发的自动歌词", "QQ 音乐"),
        LyricsCacheSelectionKind.Automatic);
    Assert((await unstableDurationCache.TryLoadAsync(
            manuallyConfirmedTrack with { Duration = TimeSpan.FromSeconds(193) }))?.Lines[0].Text
           == "用户手动确认的歌词",
        "同一歌曲只有一个手选版本时，播放器的临时时长错误不应清除手选记忆");

    var unrelatedAlbumTrack = manuallyConfirmedTrack with
    {
        Album = "另一场演唱会 Live",
        Duration = TimeSpan.FromSeconds(241)
    };
    await unstableDurationCache.SaveAsync(
        unrelatedAlbumTrack,
        LrcParser.Parse("[00:01.00]另一现场版本", "手动导入"),
        LyricsCacheSelectionKind.Manual);
    Assert((await unstableDurationCache.TryLoadAsync(unrelatedAlbumTrack))?.Lines[0].Text
           == "另一现场版本",
        "专辑或演唱会不同的手选歌词仍必须相互隔离");

    var incompleteMetadataTrack = new TrackIdentity(
        "稍后补全信息",
        "歌手",
        string.Empty,
        TimeSpan.Zero,
        "QQMusic.exe");
    await versionedCache.SaveAsync(
        incompleteMetadataTrack,
        LrcParser.Parse("[00:01.00]分阶段元数据歌词", "QQ 音乐"));
    var completedMetadataTrack = incompleteMetadataTrack with
    {
        Album = "后来取得的专辑",
        Duration = TimeSpan.FromMinutes(3)
    };
    Assert((await versionedCache.TryLoadAsync(completedMetadataTrack))?.Lines[0].Text
           == "分阶段元数据歌词",
        "播放器稍后补全专辑和时长时应兼容读取先前缓存");
}
finally
{
    if (Directory.Exists(versionedCacheRoot))
    {
        Directory.Delete(versionedCacheRoot, true);
    }
}

var cleanupCacheRoot = Path.Combine(
    Path.GetTempPath(),
    $"lyrveil-cleanup-cache-smoke-{Guid.NewGuid():N}");
var cleanupMovedRoot = Path.Combine(
    Path.GetTempPath(),
    $"lyrveil-cleanup-cache-moved-smoke-{Guid.NewGuid():N}");
try
{
    var nonOwnedDirectory = Path.Combine(cleanupCacheRoot, "non-owned-user-folder");
    Directory.CreateDirectory(nonOwnedDirectory);
    await File.WriteAllTextAsync(
        Path.Combine(nonOwnedDirectory, "user.lrc"),
        "[00:01.00]用户文件");
    var nonOwnedRejected = false;
    try
    {
        _ = await new LyricsCache(nonOwnedDirectory).ScanCleanupAsync();
    }
    catch (IOException)
    {
        nonOwnedRejected = true;
    }

    Assert(nonOwnedRejected
           && !File.Exists(Path.Combine(
               nonOwnedDirectory,
               LyricsStorage.OwnershipMarkerFileName)),
        "整理不得给普通用户文件夹自动添加所有权标记");

    var cleanupCache = new LyricsCache(cleanupCacheRoot);
    var selectedTrack = new TrackIdentity(
        "同名歌曲",
        "同一歌手",
        "标准录音室专辑",
        TimeSpan.FromSeconds(388),
        "Spotify.exe");
    await cleanupCache.SaveAsync(
        selectedTrack,
        LrcParser.Parse("[00:01.00]用户确认的歌词", "酷狗音乐"),
        LyricsCacheSelectionKind.Manual);
    await cleanupCache.SaveAsync(
        selectedTrack with { Duration = TimeSpan.FromSeconds(174) },
        LrcParser.Parse("[00:01.00]错误自动匹配", "QQ 音乐"),
        LyricsCacheSelectionKind.Automatic);
    var cleanupLiveTrack = selectedTrack with
    {
        Title = "同名歌曲 - Live",
        Album = "2026 巡回演唱会",
        Duration = TimeSpan.FromSeconds(241)
    };
    await cleanupCache.SaveAsync(
        cleanupLiveTrack,
        LrcParser.Parse("[00:01.00]现场版歌词", "网易云音乐"),
        LyricsCacheSelectionKind.Automatic);

    var duplicateTrack = new TrackIdentity(
        "完全重复的歌词",
        "歌手",
        "专辑",
        TimeSpan.FromSeconds(200),
        "QQMusic.exe");
    await cleanupCache.SaveAsync(
        duplicateTrack,
        LrcParser.Parse("[00:01.00]相同的歌词", "QQ 音乐"));
    var duplicateOriginal = Directory.GetFiles(cleanupCacheRoot, "完全重复的歌词*.lrc").Single();
    var duplicateCopy = Path.Combine(
        cleanupCacheRoot,
        "完全重复的歌词 - 歌手 [1111111111].lrc");
    File.Copy(duplicateOriginal, duplicateCopy);

    var unreadableAutomatic = Path.Combine(
        cleanupCacheRoot,
        "无法读取 - 歌手 [2222222222].lrc");
    await File.WriteAllTextAsync(
        unreadableAutomatic,
        "[selection:auto]\n[source:酷狗音乐]\n[0,100]<0,100,0>无法由 LRC 解析");
    var ambiguousLegacyCache = Path.Combine(
        cleanupCacheRoot,
        "历史手选状态不明 - 歌手 [3333333333].lrc");
    await File.WriteAllTextAsync(
        ambiguousLegacyCache,
        "[source:酷狗音乐]\n[0,100]<0,100,0>可能是旧版手选歌词");
    var unknownUserFile = Path.Combine(cleanupCacheRoot, "用户自己的文件.lrc");
    await File.WriteAllTextAsync(unknownUserFile, "[00:01.00]不得移动");

    var cleanupPlan = await cleanupCache.ScanCleanupAsync();
    Assert(cleanupPlan.Items.Any(item =>
            item.Reason == LyricsCacheCleanupReason.SupersededByExplicitSelection),
        "同一歌曲的自动缓存应被手选歌词替代");
    Assert(cleanupPlan.Items.Count(item =>
            item.Reason == LyricsCacheCleanupReason.ExactDuplicate) == 1,
        "两份完全相同的自动缓存只应整理其中一份");
    Assert(cleanupPlan.Items.Any(item =>
            item.Reason == LyricsCacheCleanupReason.UnreadableAutomaticCache),
        "无法读取的旧自动缓存应进入可恢复整理清单");
    Assert(!cleanupPlan.Items.Any(item =>
            item.FileName.Contains("Live", StringComparison.OrdinalIgnoreCase)),
        "Live/演唱会版本不得与标准版合并");
    Assert(!cleanupPlan.Items.Any(item =>
            item.FileName == Path.GetFileName(unknownUserFile)),
        "非词间管理的用户 LRC 绝不得进入整理清单");
    Assert(!cleanupPlan.Items.Any(item =>
            item.FileName == Path.GetFileName(ambiguousLegacyCache)),
        "没有 selection 标记的旧网络歌词可能是历史手选，整理必须保守保留");

    var cleanupResult = await cleanupCache.ApplyCleanupAsync(cleanupPlan);
    Assert(cleanupResult.MovedFileCount == cleanupPlan.Items.Count,
        "未变更的整理清单应全部移入可恢复备份");
    Assert(File.Exists(unknownUserFile),
        "整理后用户自己的 LRC 必须保留");
    Assert(Directory.GetFiles(cleanupCacheRoot, "*Live*.lrc").Length == 1,
        "整理后 Live/演唱会版本必须保留");

    var migration = LyricsDirectoryMigrator.Migrate(
        cleanupCacheRoot,
        cleanupMovedRoot);
    Assert(migration.Changed,
        "含整理备份的歌词文件夹应可正常迁移");
    var movedCache = new LyricsCache(cleanupMovedRoot);
    var restorePlan = await movedCache.GetLatestCleanupBackupAsync();
    Assert(restorePlan is not null
           && restorePlan.Items.Count == cleanupPlan.Items.Count,
        "更改歌词文件夹位置后可恢复整理清单必须一起迁移");
    var restoreResult = await movedCache.RestoreCleanupBackupAsync(restorePlan!);
    Assert(restoreResult.RestoredFileCount == cleanupPlan.Items.Count
           && restoreResult.ConflictFileCount == 0,
        "最近一次整理应可完整恢复");

    var secondPlan = await movedCache.ScanCleanupAsync();
    var secondResult = await movedCache.ApplyCleanupAsync(secondPlan);
    Assert(secondResult.MovedFileCount > 0,
        "恢复后应可重新生成整理备份");
    var conflictPlan = await movedCache.GetLatestCleanupBackupAsync();
    Assert(conflictPlan is not null, "重新整理后应存在恢复清单");
    var conflictPath = Path.Combine(cleanupMovedRoot, conflictPlan!.Items[0].FileName);
    await File.WriteAllTextAsync(conflictPath, "不可覆盖的当前文件");
    var tamperedBackupPath = Path.Combine(
        conflictPlan.BatchDirectory,
        conflictPlan.Items[1].FileName);
    await File.WriteAllTextAsync(tamperedBackupPath, "已被更改的备份");
    var conflictResult = await movedCache.RestoreCleanupBackupAsync(conflictPlan);
    Assert(conflictResult.ConflictFileCount == 1
           && conflictResult.MissingFileCount == 1
           && await File.ReadAllTextAsync(conflictPath) == "不可覆盖的当前文件",
        "恢复绝不得覆盖当前同名歌词，也不得恢复已被篡改的备份");
}
finally
{
    if (Directory.Exists(cleanupCacheRoot))
    {
        Directory.Delete(cleanupCacheRoot, true);
    }

    if (Directory.Exists(cleanupMovedRoot))
    {
        Directory.Delete(cleanupMovedRoot, true);
    }
}

var migrationRoot = Path.Combine(
    Path.GetTempPath(),
    $"lyrveil-migration-smoke-{Guid.NewGuid():N}");
try
{
    var sourceDirectory = Path.Combine(migrationRoot, "old", "Lyrveil Lyrics");
    var nestedSourceDirectory = Path.Combine(sourceDirectory, "manual", "nested");
    Directory.CreateDirectory(nestedSourceDirectory);
    Directory.CreateDirectory(Path.Combine(sourceDirectory, "empty", "child"));
    LyricsStorage.EnsureOwnedDirectory(sourceDirectory);
    await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "one.lrc"), "[00:01.00]one");
    await File.WriteAllTextAsync(Path.Combine(nestedSourceDirectory, "two.lrc"), "[00:02.00]two");

    var destinationDirectory = Path.Combine(migrationRoot, "new", "Lyrics");
    var migration = LyricsDirectoryMigrator.Migrate(sourceDirectory, destinationDirectory);
    Assert(migration.SourceRemoved && !Directory.Exists(sourceDirectory),
        "更改歌词位置后应删除整个旧文件夹");
    Assert(File.Exists(Path.Combine(destinationDirectory, "one.lrc"))
        && File.Exists(Path.Combine(destinationDirectory, "manual", "nested", "two.lrc")),
        "更改歌词位置应保留全部文件和子目录");
    Assert(Directory.Exists(Path.Combine(destinationDirectory, "empty", "child")),
        "更改歌词位置应保留空子目录");

    var mergeSource = Path.Combine(migrationRoot, "merge-source");
    var mergeDestination = Path.Combine(migrationRoot, "merge-destination");
    Directory.CreateDirectory(mergeSource);
    Directory.CreateDirectory(mergeDestination);
    LyricsStorage.EnsureOwnedDirectory(mergeSource);
    await File.WriteAllTextAsync(Path.Combine(mergeSource, "moved.lrc"), "moved");
    await File.WriteAllTextAsync(Path.Combine(mergeDestination, "keep.txt"), "keep");
    var merged = LyricsDirectoryMigrator.Migrate(mergeSource, mergeDestination);
    Assert(merged.SourceRemoved && !Directory.Exists(mergeSource),
        "合并到已有文件夹后也不应残留旧空文件夹");
    Assert(File.Exists(Path.Combine(mergeDestination, "moved.lrc"))
        && File.Exists(Path.Combine(mergeDestination, "keep.txt")),
        "迁移到已有文件夹时应保留双方文件");

    var conflictSource = Path.Combine(migrationRoot, "conflict-source");
    var conflictDestination = Path.Combine(migrationRoot, "conflict-destination");
    Directory.CreateDirectory(conflictSource);
    Directory.CreateDirectory(conflictDestination);
    await File.WriteAllTextAsync(Path.Combine(conflictSource, "same.lrc"), "source");
    await File.WriteAllTextAsync(Path.Combine(conflictDestination, "same.lrc"), "destination");
    var conflictRejected = false;
    try
    {
        _ = LyricsDirectoryMigrator.Migrate(conflictSource, conflictDestination);
    }
    catch (IOException)
    {
        conflictRejected = true;
    }

    Assert(conflictRejected
        && Directory.Exists(conflictSource)
        && await File.ReadAllTextAsync(Path.Combine(conflictDestination, "same.lrc")) == "destination",
        "目标存在不同内容的同名文件时应中止且不改动两边");

    var nestedTargetRejected = false;
    try
    {
        _ = LyricsDirectoryMigrator.Migrate(
            conflictSource,
            Path.Combine(conflictSource, "inside"));
    }
    catch (ArgumentException)
    {
        nestedTargetRejected = true;
    }

    Assert(nestedTargetRejected, "新位置不能位于旧歌词文件夹内部");

    var sharedSource = Path.Combine(migrationRoot, "shared-music-folder");
    var sharedDestination = Path.Combine(migrationRoot, "safe-destination");
    Directory.CreateDirectory(sharedSource);
    await File.WriteAllTextAsync(Path.Combine(sharedSource, "song.lrc"), "[00:01.00]lyric");
    await File.WriteAllTextAsync(Path.Combine(sharedSource, "recording.mp3"), "personal audio");
    var sharedMigration = LyricsDirectoryMigrator.Migrate(sharedSource, sharedDestination);
    Assert(sharedMigration.SourceRetainedForSafety
           && !sharedMigration.SourceRemoved
           && File.Exists(Path.Combine(sharedDestination, "song.lrc"))
           && File.Exists(Path.Combine(sharedSource, "song.lrc"))
           && File.Exists(Path.Combine(sharedSource, "recording.mp3"))
           && !File.Exists(Path.Combine(sharedDestination, "recording.mp3")),
        "普通用户目录只能复制 LRC，绝不能移动或删除歌词原件及其他个人文件");
}
finally
{
    if (Directory.Exists(migrationRoot))
    {
        Directory.Delete(migrationRoot, true);
    }
}

Assert(MusicPlayerCatalog.Identify("cloudmusic.exe")?.DisplayName == "网易云音乐",
    "应识别网易云音乐桌面会话");
Assert(MusicPlayerCatalog.Identify("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App")?.DisplayName == "Apple Music",
    "应识别 Apple Music 商店应用会话");
Assert(MusicPlayerCatalog.IsSupported("Spotify.exe"), "应识别 Spotify 桌面会话");
Assert(MusicPlayerCatalog.Identify("KuGou.exe")?.DisplayName == "酷狗音乐",
    "应识别酷狗音乐桌面会话");
Assert(MusicPlayerCatalog.IsSupported("KGMusic"), "应识别酷狗音乐的备用会话标识");
Assert(!MusicPlayerCatalog.IsSupported("chrome.exe"), "不应把 Chrome 网页媒体识别为音乐播放器");
Assert(!MusicPlayerCatalog.IsSupported("msedge.exe"), "不应把 Edge 网页媒体识别为音乐播放器");
Assert(!MusicPlayerCatalog.IsSupported("firefox.exe"), "不应把 Firefox 网页媒体识别为音乐播放器");
const string localPlayerPath = @"C:\Program Files\LocalAudio\foobar2000.exe";
MusicPlayerCatalog.ConfigureLocalPlayer(localPlayerPath);
Assert(MusicPlayerCatalog.Identify("foobar2000.exe") is { Id: "local", DisplayName: "本地播放器" },
    "用户选择的桌面程序应作为本地播放器媒体会话被识别");
Assert(MusicPlayerCatalog.Identify("org.foobar2000.Player")?.Id == "local",
    "本地播放器应兼容包含进程名的应用媒体会话标识");
Assert(MusicPlayerCatalog.DetectablePlayers.Count == MusicPlayerCatalog.SupportedPlayers.Count + 1,
    "配置本地播放器后应加入运行检测范围，但不改变内置播放器清单");
Assert(MusicPlayerCatalog.SelectForMonitoring("spotify").Any(player => player.Id == "local"),
    "配置的本地播放器应自动加入后台监听范围");
Assert(!MusicPlayerCatalog.IsLocalPlayerExecutableAllowed(@"C:\Program Files\Browser\chrome.exe"),
    "浏览器不能被配置为本地播放器");
Assert(MusicPlayerCatalog.Identify("Spotify.exe")?.Id == "spotify",
    "本地播放器配置不能覆盖内置播放器识别");
MusicPlayerCatalog.ConfigureLocalPlayer(string.Empty);
Assert(!MusicPlayerCatalog.IsSupported("foobar2000.exe")
       && MusicPlayerCatalog.DetectablePlayers.Count == MusicPlayerCatalog.SupportedPlayers.Count,
    "清除本地播放器后应立即停止识别其媒体会话");
Assert(!MusicPlayerCatalog.SelectForMonitoring("spotify").Any(player => player.Id == "local"),
    "清除本地播放器后应从后台监听范围移除");
var selectedPlayers = MusicPlayerCatalog.SelectByIds("spotify;kugou;netease;unknown");
Assert(selectedPlayers.Count == 3, "监听列表应忽略未知播放器并保留多个受支持播放器");
Assert(MusicPlayerCatalog.SerializeIds(selectedPlayers.Select(player => player.Id)) == "netease;kugou;spotify",
    "播放器选择应按稳定顺序保存");
Assert(MusicPlayerCatalog.DetectablePlayers.Any(player => player.Id == "applemusic"),
    "生命周期检测应包含未被后台监听选中的 Apple Music");
Assert(MusicPlayerCatalog.DetectablePlayers.Any(player => player.Id == "spotify"),
    "生命周期检测应包含未被后台监听选中的 Spotify");

var monitorPolicyStart = DateTimeOffset.Now;
var monitorLaunchPolicy = new PlayerMonitorLaunchPolicy(
    TimeSpan.FromSeconds(4),
    TimeSpan.FromSeconds(2),
    TimeSpan.FromSeconds(30),
    TimeSpan.FromSeconds(30));
Assert(monitorLaunchPolicy.Observe(false, false, monitorPolicyStart)
       == PlayerMonitorLaunchDecision.None,
    "播放器未运行时后台监听不应拉起词间");
Assert(monitorLaunchPolicy.Observe(true, false, monitorPolicyStart)
       == PlayerMonitorLaunchDecision.StartMainInstance,
    "播放器首次出现时后台监听应尝试拉起词间");
Assert(monitorLaunchPolicy.State == PlayerMonitorLaunchState.AwaitingConfirmation
       && monitorLaunchPolicy.Observe(
           true,
           false,
           monitorPolicyStart + TimeSpan.FromSeconds(3)) == PlayerMonitorLaunchDecision.None,
    "启动后必须等待主实例 mutex，不能立即重复拉起进程");
Assert(monitorLaunchPolicy.Observe(
        true,
        false,
        monitorPolicyStart + TimeSpan.FromSeconds(4)) == PlayerMonitorLaunchDecision.None
       && monitorLaunchPolicy.State == PlayerMonitorLaunchState.RetryDelay
       && monitorLaunchPolicy.ConsecutiveFailures == 1,
    "确认超时后应进入有界退避，而不是永久认定启动成功");
Assert(monitorLaunchPolicy.Observe(
        true,
        false,
        monitorPolicyStart + TimeSpan.FromSeconds(5.9)) == PlayerMonitorLaunchDecision.None
       && monitorLaunchPolicy.Observe(
           true,
           false,
           monitorPolicyStart + TimeSpan.FromSeconds(6))
       == PlayerMonitorLaunchDecision.StartMainInstance,
    "退避期结束且播放器仍在时应重试启动");
Assert(monitorLaunchPolicy.Observe(
        true,
        true,
        monitorPolicyStart + TimeSpan.FromSeconds(6.5)) == PlayerMonitorLaunchDecision.None
       && monitorLaunchPolicy.State == PlayerMonitorLaunchState.Running
       && monitorLaunchPolicy.ConsecutiveFailures == 1,
    "只有观察到主实例 mutex 后才应把本次启动视为成功，但稳定运行前应保留失败记录");
Assert(monitorLaunchPolicy.Observe(
        true,
        false,
        monitorPolicyStart + TimeSpan.FromSeconds(7)) == PlayerMonitorLaunchDecision.None
       && monitorLaunchPolicy.State == PlayerMonitorLaunchState.RetryDelay,
    "主实例崩溃但播放器仍在时应重新进入退避重试");
Assert(monitorLaunchPolicy.Observe(
        true,
        false,
        monitorPolicyStart + TimeSpan.FromSeconds(10.9))
       == PlayerMonitorLaunchDecision.None
       && monitorLaunchPolicy.Observe(
           true,
           false,
           monitorPolicyStart + TimeSpan.FromSeconds(11))
       == PlayerMonitorLaunchDecision.StartMainInstance,
    "主实例崩溃后的退避期结束时应重新启动");
monitorLaunchPolicy.ReportLaunchFailure(monitorPolicyStart + TimeSpan.FromSeconds(11));
Assert(monitorLaunchPolicy.State == PlayerMonitorLaunchState.RetryDelay
       && monitorLaunchPolicy.ConsecutiveFailures == 3
       && monitorLaunchPolicy.Observe(
           true,
           false,
           monitorPolicyStart + TimeSpan.FromSeconds(18.9)) == PlayerMonitorLaunchDecision.None
       && monitorLaunchPolicy.Observe(
           true,
           false,
           monitorPolicyStart + TimeSpan.FromSeconds(19))
       == PlayerMonitorLaunchDecision.StartMainInstance,
    "Process.Start 失败后应指数退避，避免形成进程风暴");
Assert(monitorLaunchPolicy.Observe(
        false,
        false,
        monitorPolicyStart + TimeSpan.FromSeconds(19.1)) == PlayerMonitorLaunchDecision.None
       && monitorLaunchPolicy.State == PlayerMonitorLaunchState.Idle
       && monitorLaunchPolicy.Observe(
           true,
           false,
           monitorPolicyStart + TimeSpan.FromSeconds(19.2))
       == PlayerMonitorLaunchDecision.StartMainInstance,
    "播放器全部退出后应重置状态，让下一次播放会话可以立即启动");
var cappedMonitorLaunchPolicy = new PlayerMonitorLaunchPolicy(
    TimeSpan.FromSeconds(4),
    TimeSpan.FromSeconds(2),
    TimeSpan.FromSeconds(30),
    TimeSpan.FromSeconds(30));
var cappedRetryNow = monitorPolicyStart;
for (var attempt = 0; attempt < 8; attempt++)
{
    Assert(cappedMonitorLaunchPolicy.Observe(true, false, cappedRetryNow)
           == PlayerMonitorLaunchDecision.StartMainInstance,
        "退避结束后应允许下一次启动尝试");
    cappedMonitorLaunchPolicy.ReportLaunchFailure(cappedRetryNow);
    var retryAt = cappedMonitorLaunchPolicy.NextTransitionAt
        ?? throw new InvalidOperationException("后台监听没有安排重试时间。");
    Assert(retryAt - cappedRetryNow <= TimeSpan.FromSeconds(30),
        "后台监听的重试等待必须有上限");
    cappedRetryNow = retryAt;
}
var crashLoopPolicy = new PlayerMonitorLaunchPolicy(
    TimeSpan.FromSeconds(4),
    TimeSpan.FromSeconds(2),
    TimeSpan.FromSeconds(30),
    TimeSpan.FromSeconds(30));
Assert(crashLoopPolicy.Observe(true, false, monitorPolicyStart)
       == PlayerMonitorLaunchDecision.StartMainInstance,
    "崩溃循环测试应先发起启动");
_ = crashLoopPolicy.Observe(
    true,
    true,
    monitorPolicyStart + TimeSpan.FromMilliseconds(100));
_ = crashLoopPolicy.Observe(
    true,
    false,
    monitorPolicyStart + TimeSpan.FromMilliseconds(200));
Assert(crashLoopPolicy.Observe(
        true,
        false,
        monitorPolicyStart + TimeSpan.FromMilliseconds(2200))
       == PlayerMonitorLaunchDecision.StartMainInstance,
    "主实例首次崩溃后应在退避结束时重试");
_ = crashLoopPolicy.Observe(
    true,
    true,
    monitorPolicyStart + TimeSpan.FromMilliseconds(2300));
_ = crashLoopPolicy.Observe(
    true,
    false,
    monitorPolicyStart + TimeSpan.FromMilliseconds(2400));
Assert(crashLoopPolicy.ConsecutiveFailures == 2
       && crashLoopPolicy.NextTransitionAt
       == monitorPolicyStart + TimeSpan.FromMilliseconds(6400),
    "短时间内反复启动后崩溃应继续加长退避，避免形成进程风暴");
_ = crashLoopPolicy.Observe(
    true,
    true,
    monitorPolicyStart + TimeSpan.FromSeconds(7));
_ = crashLoopPolicy.Observe(
    true,
    true,
    monitorPolicyStart + TimeSpan.FromSeconds(37));
Assert(crashLoopPolicy.ConsecutiveFailures == 0,
    "主实例稳定运行一段时间后应清除历史失败次数");

var exitPolicy = new CompanionExitPolicy(
    TimeSpan.FromSeconds(15),
    TimeSpan.FromSeconds(2),
    TimeSpan.FromMinutes(5));
var policyStart = DateTimeOffset.Now;
Assert(!exitPolicy.ShouldExit(false, false, false, policyStart), "启动时应等待播放器进程出现");
Assert(!exitPolicy.ShouldExit(false, false, false, policyStart + TimeSpan.FromSeconds(14)), "播放器启动等待期内不应退出");
Assert(!exitPolicy.ShouldExit(true, false, false, policyStart + TimeSpan.FromSeconds(14)), "检测到播放器后应继续运行");
Assert(!exitPolicy.ShouldExit(false, false, false, policyStart + TimeSpan.FromSeconds(15)), "播放器消失后应短暂探测是否重启");
Assert(exitPolicy.ShouldExit(false, false, false, policyStart + TimeSpan.FromSeconds(17)), "普通退出应在短探测后关闭词间");

var updatePolicy = new CompanionExitPolicy(
    TimeSpan.FromSeconds(15),
    TimeSpan.FromSeconds(2),
    TimeSpan.FromMinutes(5));
Assert(!updatePolicy.ShouldExit(true, false, false, policyStart), "检测到播放器时不应退出");
Assert(!updatePolicy.ShouldExit(false, false, true, policyStart + TimeSpan.FromSeconds(1)), "检测到更新时应进入重启等待");
Assert(!updatePolicy.ShouldExit(false, false, false, policyStart + TimeSpan.FromMinutes(4)), "更新等待期内不应退出");
Assert(updatePolicy.ShouldExit(false, false, false, policyStart + TimeSpan.FromMinutes(6)), "更新等待超时后应退出");

var settingsOpenPolicy = new CompanionExitPolicy(
    TimeSpan.FromSeconds(15),
    TimeSpan.FromSeconds(2),
    TimeSpan.FromMinutes(5));
Assert(!settingsOpenPolicy.ShouldExit(true, false, false, policyStart), "检测到播放器时不应退出");
Assert(!settingsOpenPolicy.ShouldExit(false, true, false, policyStart + TimeSpan.FromSeconds(1)), "设置窗口打开时不应退出");
Assert(!settingsOpenPolicy.ShouldExit(false, false, false, policyStart + TimeSpan.FromSeconds(2)), "关闭设置窗口后仍应完成短重启探测");
Assert(settingsOpenPolicy.ShouldExit(false, false, false, policyStart + TimeSpan.FromSeconds(4)), "短探测结束后应退出");

if (args.Contains("--online", StringComparer.OrdinalIgnoreCase))
{
    using var provider = new LrclibProvider();
    var onlineTrack = new TrackIdentity(
        "I Want to Live",
        "Borislav Slavov",
        "Baldur's Gate 3 (Original Game Soundtrack)",
        TimeSpan.FromSeconds(233));
    var onlineLyrics = await provider.FindAsync(onlineTrack);
    Assert(onlineLyrics is { HasSynchronizedLines: true }, "LRCLIB 在线精确匹配应返回同步歌词");
    var onlineCandidates = await provider.SearchAsync(onlineTrack);
    Assert(onlineCandidates.Count > 0, "LRCLIB 手动搜索应返回候选版本");
    Assert(onlineCandidates.All(candidate => candidate.Document.HasSynchronizedLines),
        "手动选择列表只应包含同步歌词");
    Console.WriteLine($"Online provider smoke test passed: LRCLIB, {onlineCandidates.Count} candidates.");
}

if (args.Contains("--online-sources", StringComparer.OrdinalIgnoreCase))
{
    var sourceTrack = new TrackIdentity(
        "晴天",
        "周杰伦",
        "叶惠美",
        TimeSpan.FromSeconds(269));
    (ILyricsProvider Provider, string ExpectedName)[] sourceProviders =
    [
        (new QqMusicProvider(), "QQ 音乐"),
        (new KugouProvider(), "酷狗音乐"),
        (new NetEaseProvider(), "网易云音乐")
    ];

    try
    {
        foreach (var (provider, expectedName) in sourceProviders)
        {
            Assert(provider.Name == expectedName, $"{expectedName} Provider 应暴露稳定的来源名称");
            var candidates = await provider.SearchAsync(sourceTrack);
            var synchronizedCandidate = candidates.FirstOrDefault(candidate =>
                candidate.Document.HasSynchronizedLines);

            Assert(synchronizedCandidate is not null, $"{expectedName} 在线搜索应返回至少一份同步歌词");
            Assert(candidates.All(candidate => candidate.Provider == expectedName),
                $"{expectedName} 候选应保留正确的来源名称");

            var checkedCandidate = synchronizedCandidate
                ?? throw new InvalidOperationException($"{expectedName} 未返回同步歌词");
            Console.WriteLine(
                $"Online source passed: {expectedName}, "
                + $"candidates={candidates.Count}, lines={checkedCandidate.Document.Lines.Count}.");
        }

        var relevanceTrack = new TrackIdentity(
            "美人蕉",
            "李琳",
            string.Empty,
            TimeSpan.FromSeconds(234));
        var relevanceMatchCount = 0;
        foreach (var (provider, expectedName) in sourceProviders)
        {
            var candidates = await provider.SearchAsync(relevanceTrack);
            Assert(candidates.All(candidate =>
                    LyricsMatchScorer.IsRelevantTitle(candidate.Title, relevanceTrack.Title)),
                $"{expectedName} 不应返回与《美人蕉》歌名无关的候选");
            relevanceMatchCount += candidates.Count;
            Console.WriteLine(
                $"Online relevance passed: {expectedName}, candidates={candidates.Count}.");
        }

        Assert(relevanceMatchCount > 0, "多来源搜索应能为《美人蕉》找到相关歌词");

        var concertTrack = new TrackIdentity(
            "倒帶 - 演唱會版",
            "周杰倫",
            "The One 演唱會",
            TimeSpan.Zero);
        var concertMatchCount = 0;
        foreach (var (provider, expectedName) in sourceProviders)
        {
            var candidates = await provider.SearchAsync(concertTrack);
            var bestCandidate = candidates
                .Where(candidate => LyricsMatchScorer.IsRelevant(candidate, concertTrack))
                .OrderBy(candidate => LyricsMatchScorer.Score(candidate, concertTrack))
                .FirstOrDefault();
            if (bestCandidate is null)
            {
                Console.WriteLine($"Online concert source returned no synchronized match: {expectedName}.");
                continue;
            }

            var releaseDescription = $"{bestCandidate.Title} {bestCandidate.Album}";
            Assert(
                releaseDescription.Contains("live", StringComparison.OrdinalIgnoreCase)
                    || releaseDescription.Contains("演唱会", StringComparison.OrdinalIgnoreCase)
                    || releaseDescription.Contains("演唱會", StringComparison.OrdinalIgnoreCase)
                    || releaseDescription.Contains("现场", StringComparison.OrdinalIgnoreCase),
                $"{expectedName} 为《倒帶 - 演唱會版》选择的首选候选应是 Live/演唱会版本");
            concertMatchCount++;
            Console.WriteLine(
                $"Online concert matching passed: {expectedName}, "
                + $"title={bestCandidate.Title}, album={bestCandidate.Album}.");
        }

        Assert(concertMatchCount > 0, "至少一个在线来源应能为《倒帶 - 演唱會版》找到 Live 候选");
    }
    finally
    {
        foreach (var (provider, _) in sourceProviders)
        {
            if (provider is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}

using (var disabledProvider = new ConfigurableLyricsProvider(
           new LrclibProvider(),
           () => false))
{
    Assert((await disabledProvider.SearchAsync(track)).Count == 0,
        "关闭某个来源后不应发起搜索或返回候选");
}

await RedesignSettingsTests.RunAsync();
await ReviewRegressionTests.RunAsync();
Console.WriteLine("Core smoke tests passed: LRC, filters, cache, folder migration, timeline, playback estimation, settings.");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

file sealed class StaticLyricsProvider(
    string name,
    IReadOnlyList<LyricsCandidate> candidates) : ILyricsProvider
{
    public string Name { get; } = name;

    public int SearchRequestCount { get; private set; }

    public Task<LyricDocument?> FindAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(candidates.FirstOrDefault()?.Document);
    }

    public Task<IReadOnlyList<LyricsCandidate>> SearchAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SearchRequestCount++;
        return Task.FromResult(candidates);
    }
}

file sealed class NetEaseExactIdHandler : HttpMessageHandler
{
    public int LyricsRequestCount { get; private set; }

    public int SearchRequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        if (path.Contains("/search/", StringComparison.OrdinalIgnoreCase))
        {
            SearchRequestCount++;
            return JsonResponse("{\"code\":200,\"result\":{\"songs\":[]}}");
        }

        if (path.Contains("/lyric", StringComparison.OrdinalIgnoreCase))
        {
            LyricsRequestCount++;
            return JsonResponse(
                "{\"code\":200,\"lrc\":{\"lyric\":\"[00:00.00]Exact line\\n[00:05.00]Next line\"}}");
        }

        throw new InvalidOperationException($"Unexpected NetEase request: {request.RequestUri}");
    }

    private static Task<HttpResponseMessage> JsonResponse(string json) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(
                json,
                System.Text.Encoding.UTF8,
                "application/json")
        });
}
