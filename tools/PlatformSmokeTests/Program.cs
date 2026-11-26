using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LyricsXQuiet.Core.Playback;
using LyricsXQuiet.Platform.Windows.Companion;
using LyricsXQuiet.Platform.Windows.Interop;
using LyricsXQuiet.Platform.Windows.Playback;

if (args is ["--monitor-fixture", var monitorRoot])
{
    PlayerMonitorTests.RunFixture(monitorRoot);
    return;
}
if (await NetEaseProcessRecoveryTests.RunFixtureAsync(args)) return;

var tempDirectory = Path.Combine(
    Path.GetTempPath(),
    $"LyrveilPlatformSmoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(tempDirectory);

try
{
    await PlayerMonitorTests.RunAsync(tempDirectory);
    await NetEaseBridgeDiscoveryTests.RunAsync();
    NetEaseShortcutMaintenanceTests.Run(tempDirectory);
    await NetEaseProcessRecoveryTests.RunAsync(tempDirectory);

    Assert(
        DesktopAppIdentity.AppUserModelId == "Lyrveil.DesktopLyrics",
        "Lyrveil should keep one stable shell identity across all player launchers.");
    Assert(
        !OperatingSystem.IsWindows() || DesktopAppIdentity.TryApply(),
        "Windows should accept Lyrveil's explicit process AppUserModelID.");

    var reportedPlayingWithoutAudio =
        GsmTcSessionArbitrationPolicy.GetScore(new(
            PlaybackState.Playing,
            IsAudiblyPlaying: false,
            HasUsableTimeline: false,
            IsWindowsCurrent: false));
    var pausedWithEveryWeakHint =
        GsmTcSessionArbitrationPolicy.GetScore(new(
            PlaybackState.Paused,
            IsAudiblyPlaying: false,
            HasUsableTimeline: true,
            IsWindowsCurrent: true));
    var stoppedWithEveryWeakHint =
        GsmTcSessionArbitrationPolicy.GetScore(new(
            PlaybackState.Stopped,
            IsAudiblyPlaying: false,
            HasUsableTimeline: true,
            IsWindowsCurrent: true));
    Assert(
        reportedPlayingWithoutAudio > pausedWithEveryWeakHint
            && reportedPlayingWithoutAudio > stoppedWithEveryWeakHint,
        "A reported Playing session must outrank paused/stopped sessions even when audio sampling is temporarily unavailable.");

    var audiblePausedSession = GsmTcSessionArbitrationPolicy.GetScore(new(
        PlaybackState.Paused,
        IsAudiblyPlaying: true,
        HasUsableTimeline: false,
        IsWindowsCurrent: false));
    Assert(
        audiblePausedSession > reportedPlayingWithoutAudio,
        "Recent Core Audio activity should remain the strongest fallback for players that misreport their GSMTC state.");

    var timelineHint = GsmTcSessionArbitrationPolicy.GetScore(new(
        PlaybackState.Paused,
        IsAudiblyPlaying: false,
        HasUsableTimeline: true,
        IsWindowsCurrent: false));
    var windowsCurrentHint = GsmTcSessionArbitrationPolicy.GetScore(new(
        PlaybackState.Paused,
        IsAudiblyPlaying: false,
        HasUsableTimeline: false,
        IsWindowsCurrent: true));
    Assert(
        timelineHint > windowsCurrentHint,
        "A usable timeline should refine equal playback signals ahead of Windows' stale-prone current-session hint.");

    Assert(
        GsmTcSessionArbitrationPolicy.IsWindowsCurrentSession(new(
            IsSameNativeSession: true,
            SourceAppUserModelIdMatches: true,
            MatchingSourceSessionCount: 2)),
        "The native GSMTC session object selected by Windows must remain current even when another session publishes the same AUMID.");
    Assert(
        !GsmTcSessionArbitrationPolicy.IsWindowsCurrentSession(new(
            IsSameNativeSession: false,
            SourceAppUserModelIdMatches: true,
            MatchingSourceSessionCount: 2)),
        "A duplicated AUMID must not make every native GSMTC session look like Windows' current session.");
    Assert(
        GsmTcSessionArbitrationPolicy.IsWindowsCurrentSession(new(
            IsSameNativeSession: false,
            SourceAppUserModelIdMatches: true,
            MatchingSourceSessionCount: 1)),
        "AUMID matching should remain a compatibility fallback when it identifies exactly one GSMTC session.");
    Assert(
        !GsmTcSessionArbitrationPolicy.IsWindowsCurrentSession(new(
            IsSameNativeSession: false,
            SourceAppUserModelIdMatches: false,
            MatchingSourceSessionCount: 1)),
        "A unique but different AUMID must not inherit Windows' current-session hint.");

    var activityTracker = new ProcessAudioActivityTracker(TimeSpan.FromSeconds(2));
    var audioSampleTime = DateTimeOffset.Parse("2026-08-31T00:00:00+08:00");
    var currentAndRecentActivity = activityTracker.Update(
        new HashSet<string>(["cloudmusic"], StringComparer.OrdinalIgnoreCase),
        audioSampleTime);
    Assert(
        currentAndRecentActivity.CurrentlyAudibleProcessNames.Contains("cloudmusic")
            && currentAndRecentActivity.RecentlyAudibleProcessNames.Contains("cloudmusic"),
        "A live audio sample should be exposed as both current and recent activity.");

    var heldRecentActivity = activityTracker.Update(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        audioSampleTime.AddMilliseconds(100));
    Assert(
        !heldRecentActivity.CurrentlyAudibleProcessNames.Contains("cloudmusic")
            && heldRecentActivity.RecentlyAudibleProcessNames.Contains("cloudmusic"),
        "The anti-flap hold may retain recent activity, but it must not masquerade as current audio for playback-state repair.");

    var expiredActivity = activityTracker.Update(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        audioSampleTime.AddMilliseconds(2001));
    Assert(
        !expiredActivity.CurrentlyAudibleProcessNames.Contains("cloudmusic")
            && !expiredActivity.RecentlyAudibleProcessNames.Contains("cloudmusic"),
        "Recent audio activity should expire after the selection hold window.");

    var netEasePath = Path.Combine(tempDirectory, "cloudmusic.exe");
    await File.WriteAllBytesAsync(netEasePath, [0]);
    var qqPath = Path.Combine(tempDirectory, "QQMusic.exe");
    await File.WriteAllBytesAsync(qqPath, [0]);
    Directory.CreateDirectory(Path.Combine(tempDirectory, "child"));

    var normalizedAppleTrack = PlaybackMediaMetadataNormalizer.CreateTrack(
        "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App",
        "程艾影",
        "赵雷 — 署前街少年",
        string.Empty,
        TimeSpan.FromMinutes(5));
    Assert(normalizedAppleTrack.Artist == "赵雷"
           && normalizedAppleTrack.Album == "署前街少年",
        "Apple Music 将歌手和专辑合并在 Artist 时应拆分为可匹配的字段。");
    var preservedAlbumTrack = PlaybackMediaMetadataNormalizer.CreateTrack(
        "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App",
        "Song",
        "Artist — Guest",
        "Published Album",
        TimeSpan.FromMinutes(3));
    Assert(preservedAlbumTrack.Artist == "Artist — Guest"
           && preservedAlbumTrack.Album == "Published Album",
        "Apple Music 已提供专辑时不应冒险拆分艺人文本。");

    Assert(
        CompanionLaunchRequest.TryCreate(netEasePath, true, out var request),
        "A normalized NetEase request should be accepted.");
    Assert(
        !CompanionLaunchRequest.TryCreate(qqPath, true, out _),
        "The NetEase-only bridge flag must be rejected for other players.");
    Assert(
        !CompanionLaunchRequest.TryCreate(
            Path.Combine(tempDirectory, "arbitrary.exe"),
            false,
            out _),
        "An unsupported executable must be rejected.");

    const int recoveredPort = 39217;
    Assert(
        NetEaseCdpPortDiscovery.TryParseExplicitLoopbackPort(
            $"\"{netEasePath}\" --orpheus-startup=autorun "
            + $"--remote-debugging-address=127.0.0.1 --remote-debugging-port={recoveredPort}",
            out var parsedPort)
        && parsedPort == recoveredPort,
        "An explicit numeric-loopback NetEase CDP endpoint should be recovered.");
    Assert(
        NetEaseCdpPortDiscovery.TryParseExplicitLoopbackPort(
            $"\"{netEasePath}\" --remote-debugging-address 127.0.0.1 "
            + "--remote-debugging-port 39218",
            out parsedPort)
        && parsedPort == 39218,
        "Separated Chromium option values should be parsed as distinct argv entries.");
    Assert(
        !NetEaseCdpPortDiscovery.TryParseExplicitLoopbackPort(
            $"\"{netEasePath}\" --remote-debugging-address=0.0.0.0 "
            + "--remote-debugging-port=39217",
            out _),
        "A non-loopback debugging address must be rejected.");
    Assert(
        !NetEaseCdpPortDiscovery.TryParseExplicitLoopbackPort(
            $"\"{netEasePath}\" "
            + "\"--remote-debugging-address=127.0.0.1 --remote-debugging-port=39217\"",
            out _),
        "Options embedded inside one quoted argv value must not be accepted.");
    Assert(
        !NetEaseCdpPortDiscovery.TryParseExplicitLoopbackPort(
            $"\"{netEasePath}\" --remote-debugging-address=127.0.0.1 "
            + "--remote-debugging-port=39217 --remote-debugging-port=39218",
            out _),
        "Conflicting port arguments must fail closed.");
    Assert(
        NetEaseCdpPortDiscovery.TryFindExplicitLoopbackPort(
            [
                $"\"{netEasePath}\" --remote-debugging-address=127.0.0.1 "
                    + "--remote-debugging-port=39217",
                $"\"{netEasePath}\" --remote-debugging-address=127.0.0.1 "
                    + "--remote-debugging-port=39217"
            ]) == 39217,
        "Several NetEase processes advertising the same endpoint should be unambiguous.");
    Assert(
        NetEaseCdpPortDiscovery.TryFindExplicitLoopbackPort(
            [
                $"\"{netEasePath}\" --remote-debugging-address=127.0.0.1 "
                    + "--remote-debugging-port=39217",
                $"\"{netEasePath}\" --remote-debugging-address=127.0.0.1 "
                    + "--remote-debugging-port=39218"
            ]) is null,
        "Different endpoints advertised by multiple processes must fail closed.");

    var probeScriptType = typeof(SwitchableNetEaseBridgeSnapshotSource).Assembly.GetType(
        "LyricsXQuiet.Platform.Windows.Playback.CdpNetEaseProbeScript",
        throwOnError: true)!;
    var probeExpression = (string)probeScriptType.GetField(
        "Expression",
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!
        .GetRawConstantValue()!;
    var resetExpression = (string)probeScriptType.GetField(
        "ResetExpression",
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!
        .GetRawConstantValue()!;
    Assert(
        probeExpression.Contains("subscribePlayStatus.call", StringComparison.Ordinal)
            && probeExpression.Contains("[\"playprogress\", \"seek\", \"playstate\"]", StringComparison.Ordinal)
            && probeExpression.Contains("{ type, callback }", StringComparison.Ordinal),
        "The production NetEase helper must subscribe read-only to progress and seek events.");
    Assert(
        probeExpression.Contains("let audioPlayerScore = -1", StringComparison.Ordinal)
            && probeExpression.Contains(
                "typeof possibleAudio.subscribePlayStatus === \"function\" ? 100 : 0",
                StringComparison.Ordinal)
            && probeExpression.Contains(
                "typeof possibleAudio.getPlaybackInfo === \"function\" ? 10 : 0",
                StringComparison.Ordinal)
            && probeExpression.Contains(
                "typeof possibleAudio.getPlayedTime === \"function\" ? 1 : 0",
                StringComparison.Ordinal)
            && probeExpression.Contains(
                "if (possibleAudioScore > audioPlayerScore)",
                StringComparison.Ordinal)
            && probeExpression.Contains(
                "if (audioPlayerScore >= 110 && storeFacade) break;",
                StringComparison.Ordinal)
            && probeExpression.Contains(
                "typeof audioPlayer.subscribePlayStatus !== \"function\"",
                StringComparison.Ordinal),
        "Module discovery must prefer the live subscriber, continue past weaker playback candidates, and fail closed without subscriptions.");
    Assert(
        probeExpression.Contains(
            "const helperKey = Symbol.for(\"lyrveil.netease.readonly-playback.v14\")",
            StringComparison.Ordinal)
            && probeExpression.Contains(
            "Symbol.for(\"lyrveil.netease.readonly-playback.v13\")",
            StringComparison.Ordinal)
            && probeExpression.Contains(
            "Symbol.for(\"lyrveil.netease.readonly-playback.v12\")",
            StringComparison.Ordinal)
            && probeExpression.Contains(
            "Symbol.for(\"lyrveil.netease.readonly-playback.v11\")",
            StringComparison.Ordinal)
            && probeExpression.Contains(
            "Symbol.for(\"lyrveil.netease.readonly-playback.v10\")",
            StringComparison.Ordinal)
            && probeExpression.Contains(
            "Symbol.for(\"lyrveil.netease.readonly-playback.v9\")",
            StringComparison.Ordinal)
            && probeExpression.Contains(
            "Symbol.for(\"lyrveil.netease.readonly-playback.v8\")",
            StringComparison.Ordinal)
            && probeExpression.Contains(
            "Symbol.for(\"lyrveil.netease.readonly-playback.v7\")",
            StringComparison.Ordinal)
            && probeExpression.Contains(
            "Symbol.for(\"lyrveil.netease.readonly-playback.v6\")",
            StringComparison.Ordinal)
            && probeExpression.Contains(
            "Symbol.for(\"lyrveil.netease.readonly-playback.v5\")",
            StringComparison.Ordinal)
            && probeExpression.Contains(
                "Symbol.for(\"lyrveil.netease.readonly-playback.v4\")",
                StringComparison.Ordinal)
            && probeExpression.Contains(
                "Symbol.for(\"lyrveil.netease.readonly-playback.v3\")",
                StringComparison.Ordinal)
            && probeExpression.Contains(
                "Symbol.for(\"lyrveil.netease.readonly-playback.v2\")",
                StringComparison.Ordinal)
            && probeExpression.Contains(
                "Symbol.for(\"lyrveil.netease.readonly-playback.v1\")",
                StringComparison.Ordinal)
            && probeExpression.Contains("delete globalThis[legacyHelperKey]", StringComparison.Ordinal)
            && resetExpression.Contains(
                "Symbol.for(\"lyrveil.netease.readonly-playback.v14\")",
                StringComparison.Ordinal),
        "The seek helper must install/reset v14 and retire v1-v13 helpers already in the page.");
    Assert(
        probeExpression.Contains("playingTimelineIsStale", StringComparison.Ordinal)
            && probeExpression.Contains("timelineEventAgeMs > 3000", StringComparison.Ordinal)
            && probeExpression.Contains("&& !seekGate.active", StringComparison.Ordinal)
            && probeExpression.Contains("clearLiveTimeline()", StringComparison.Ordinal)
            && probeExpression.Contains("audioPlayer.getPlayedTime.call", StringComparison.Ordinal)
            && probeExpression.Contains("playedAudioTime", StringComparison.Ordinal)
            && probeExpression.Contains("playedTime", StringComparison.Ordinal)
            && probeExpression.Contains(
                "const seekGate = {",
                StringComparison.Ordinal)
            && probeExpression.Contains(
                "seekGate.counterBaselineMs = accumulatedAudioMs",
                StringComparison.Ordinal)
            && probeExpression.Contains(
                "let positionMs = eventPositionMs ?? startupProgressMs;",
                StringComparison.Ordinal)
            && probeExpression.Contains(
                "readAccumulatedAudioMilliseconds",
                StringComparison.Ordinal)
            && !probeExpression.Contains("anchorPositionMs", StringComparison.Ordinal)
            && !probeExpression.Contains("anchorPlayedMs", StringComparison.Ordinal),
        "The bridge must use absolute progress as its media clock and accumulated audio only as an output-resume gate.");
    Assert(
        probeExpression.Contains("get\\s+playingProgress", StringComparison.Ordinal)
            && probeExpression.Contains("startupProgressReader", StringComparison.Ordinal)
            && probeExpression.Contains("readStartupProgressMilliseconds", StringComparison.Ordinal)
            && probeExpression.Contains("startupProgressMs", StringComparison.Ordinal)
            && probeExpression.Contains("? \"startup-progress\"", StringComparison.Ordinal),
        "NetEase 3.1.38 must provide a read-only startup anchor even when the helper attaches after playback was paused.");
    Assert(
        probeExpression.Contains(
                "seekGate.counterBaselineMs = null",
                StringComparison.Ordinal)
            && probeExpression.Contains(
                "accumulatedAudioMs - seekGate.counterBaselineMs >= 15",
                StringComparison.Ordinal)
            && probeExpression.Contains(
                "const acceptedPositionMs = seekGate.lastProgressMs",
                StringComparison.Ordinal)
            && probeExpression.Contains(
                "timelineFrozen: seekGate.active",
                StringComparison.Ordinal)
            && probeExpression.Contains(
                "? \"absolute-event\"",
                StringComparison.Ordinal)
            && !probeExpression.Contains("estimatedPlayedAtSeek", StringComparison.Ordinal)
            && !probeExpression.Contains("canReuseRecentAudioSample", StringComparison.Ordinal),
        "A seek must discard asynchronous baseline estimates, freeze the target, and resume from the newest absolute event.");
    Assert(
        probeExpression.Contains(
            "playId, resourceTrackId",
            StringComparison.Ordinal)
            && probeExpression.Contains(
                "seekGate.stableSamples >= 2",
                StringComparison.Ordinal)
            && probeExpression.Contains(
                "if (type !== \"playprogress\" && type !== \"seek\") return;",
                StringComparison.Ordinal)
            && probeExpression.Contains("beginSeekGate", StringComparison.Ordinal)
            && probeExpression.Contains("observeGateProgress", StringComparison.Ordinal)
            && probeExpression.Contains(
                "positionMs = seekGate.targetMs",
                StringComparison.Ordinal)
            && probeExpression.Contains(
                "Math.abs(numericPosition - expectedSeconds) > 0.75",
                StringComparison.Ordinal),
        "The gate must reject transitional progress, support missed seek callbacks, and keep the exact target frozen.");
    Assert(
        probeExpression.Contains("now - seekGate.playingObservedAt > 4500", StringComparison.Ordinal)
            && probeExpression.Contains("positionMs >= seekGate.targetMs - 1000", StringComparison.Ordinal)
            && probeExpression.Contains("positionMs <= seekGate.targetMs + 2500", StringComparison.Ordinal)
            && probeExpression.Contains("gateTimedOutWithoutProgress", StringComparison.Ordinal)
            && probeExpression.Contains("timelineRequiresFreshEvent", StringComparison.Ordinal)
            && probeExpression.Contains(
                "Fail closed until a fresh event",
                StringComparison.Ordinal)
            && !probeExpression.Contains("timedFallback", StringComparison.Ordinal),
        "A gate with a present but stationary output counter must fail closed rather than opening on a wall-clock timeout.");
    Assert(
        probeExpression.Contains(
            "const seekTransitionIsProtected = () => seekGate.active",
            StringComparison.Ordinal)
            && probeExpression.Contains(
                "Date.now() - seekGate.observedAt <= 750",
                StringComparison.Ordinal)
            && probeExpression.Contains(
                "status = lastStableStatus;",
                StringComparison.Ordinal)
            && probeExpression.Contains(
                "lastStableStatus === \"playing\" || lastStableStatus === \"paused\"",
                StringComparison.Ordinal)
            && probeExpression.Split(
                "&& !seekTransitionIsProtected()",
                StringSplitOptions.None).Length >= 4,
        "A single transitional status or identity sample must not cancel the exact seek anchor.");
    Assert(
        probeExpression.Contains("liveTimeline.position = acceptedPositionMs / 1000", StringComparison.Ordinal)
            && probeExpression.Contains("const acceptedObservedAt = seekGate.lastProgressAt", StringComparison.Ordinal)
            && probeExpression.Contains("liveTimeline.observedAt = acceptedObservedAt", StringComparison.Ordinal)
            && probeExpression.Contains("counterGeneration !== seekGate.generation", StringComparison.Ordinal)
            && probeExpression.Contains("seekLeadCorrectionMs: 0", StringComparison.Ordinal)
            && !probeExpression.Contains("outputSafetyMarginMs", StringComparison.Ordinal)
            && !probeExpression.Contains("applySeekLeadCorrection", StringComparison.Ordinal),
        "After output resumes, use this seek's latest absolute progress and timestamp with no lasting deduction.");
    Assert(
        probeExpression.Contains("if (type === \"playstate\")", StringComparison.Ordinal)
            && probeExpression.Contains("rememberStatus(status);", StringComparison.Ordinal)
            && probeExpression.Contains("hasRunningProgress = false", StringComparison.Ordinal)
            && probeExpression.Contains("lastStableStatus === \"playing\" && hasRunningProgress", StringComparison.Ordinal),
        "Missed-seek detection needs consecutive playing progress. A pause or stop invalidates the previous baseline.");
    Assert(
        probeExpression.Contains(
            "const milliseconds = Math.max(0, Math.round(number * 1000))",
            StringComparison.Ordinal)
            && probeExpression.Contains(
                "if (typeof subscription.result === \"function\") subscription.result()",
                StringComparison.Ordinal),
        "NetEase event positions must be converted from seconds directly and returned disposers must be called.");
    var returnedDisposerIndex = probeExpression.IndexOf(
        "if (typeof subscription.result === \"function\") subscription.result()",
        StringComparison.Ordinal);
    var namedUnsubscribeGuardIndex = returnedDisposerIndex < 0
        ? -1
        : probeExpression.IndexOf(
            "if (audioPlayer && typeof audioPlayer.unSubscribePlayStatus === \"function\")",
            returnedDisposerIndex,
            StringComparison.Ordinal);
    Assert(
        returnedDisposerIndex >= 0
            && namedUnsubscribeGuardIndex > returnedDisposerIndex,
        "A disposer returned by subscribePlayStatus must run even when the named unsubscribe API is unavailable.");
    Assert(
        probeExpression.Contains("trackKey: null", StringComparison.Ordinal)
            && probeExpression.Contains(
                "const currentTrackKey = `${String(songId)}|${String(resourceTrackId)}`",
                StringComparison.Ordinal)
            && probeExpression.Contains(
                "liveTimeline.trackKey !== currentTrackKey",
                StringComparison.Ordinal)
            && probeExpression.IndexOf(
                "liveTimeline.trackKey !== currentTrackKey",
                StringComparison.Ordinal)
                < probeExpression.IndexOf(
                    "const eventPlayIdMatches",
                    StringComparison.Ordinal),
        "Metadata track IDs must invalidate the old live anchor before it can supply a position for the new song.");
    Assert(
        resetExpression.Contains("helper.dispose()", StringComparison.Ordinal),
        "Resetting the page helper must deactivate its old callbacks before reinstalling it.");

    var parseBridgeSnapshot = typeof(LoopbackNetEaseBridgeSnapshotSource).GetMethod(
        "ParseAndValidate",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var observedAt = DateTimeOffset.UtcNow.ToString("O");
    var legacyWireJson = $$"""
        {"version":1,"seq":1,"observedAt":"{{observedAt}}","track":{"id":"123456","title":"Bridge Song","artists":["Artist"],"album":"Album","durationMs":180000},"state":"playing","positionMs":12000,"rate":1}
        """;
    var legacyWireSnapshot = (NetEaseBridgeSnapshot)parseBridgeSnapshot.Invoke(
        null,
        [Encoding.UTF8.GetBytes(legacyWireJson), -1L])!;
    var frozenWireJson = legacyWireJson
        .Replace("\"seq\":1", "\"seq\":2", StringComparison.Ordinal)
        .Replace("\"rate\":1}", "\"rate\":1,\"timelineFrozen\":true}", StringComparison.Ordinal);
    var frozenWireSnapshot = (NetEaseBridgeSnapshot)parseBridgeSnapshot.Invoke(
        null,
        [Encoding.UTF8.GetBytes(frozenWireJson), 1L])!;
    Assert(!legacyWireSnapshot.TimelineFrozen && frozenWireSnapshot.TimelineFrozen,
        "Loopback bridge wire must accept both legacy snapshots without timelineFrozen and v6 frozen snapshots.");

    var cdpSourceType = typeof(CdpNetEaseBridgeSnapshotSource);
    var shouldPublishUnavailable = cdpSourceType.GetMethod(
        "ShouldPublishUnavailable",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    Assert(
        !(bool)shouldPublishUnavailable.Invoke(null, [1])!
            && !(bool)shouldPublishUnavailable.Invoke(null, [2])!
            && (bool)shouldPublishUnavailable.Invoke(null, [3])!,
        "One or two incomplete CDP samples must retain the last exact anchor, while a sustained invalid state must fall back.");

    var platformPlaybackAssembly = typeof(SwitchableNetEaseBridgeSnapshotSource).Assembly;
    var cdpTargetType = platformPlaybackAssembly.GetType(
        "LyricsXQuiet.Platform.Windows.Playback.CdpNetEaseTarget",
        throwOnError: true)!;
    var cdpDiscoveryType = platformPlaybackAssembly.GetType(
        "LyricsXQuiet.Platform.Windows.Playback.CdpNetEaseDiscovery",
        throwOnError: true)!;
    var cdpTargetListType = typeof(List<>).MakeGenericType(cdpTargetType);
    var cdpTargets = (System.Collections.IList)Activator.CreateInstance(cdpTargetListType)!;
    object CreateCdpTarget(
        string id,
        string type,
        string title,
        string url,
        string webSocketDebuggerUrl) =>
        Activator.CreateInstance(
            cdpTargetType,
            [id, type, title, url, webSocketDebuggerUrl])!;

    cdpTargets.Add(CreateCdpTarget(
        "highest-score",
        "page",
        "网易云音乐 cloudmusic",
        "https://music.163.com/orpheus",
        "ws://127.0.0.1:39217/devtools/page/highest-score"));
    cdpTargets.Add(CreateCdpTarget(
        "last-success",
        "page",
        "网易云音乐",
        "https://example.invalid/",
        "ws://127.0.0.1:39217/devtools/page/last-success"));
    cdpTargets.Add(CreateCdpTarget(
        "irrelevant",
        "page",
        "Settings",
        "https://example.invalid/",
        "ws://127.0.0.1:39217/devtools/page/irrelevant"));
    cdpTargets.Add(CreateCdpTarget(
        "worker",
        "service_worker",
        "网易云音乐 cloudmusic",
        "https://music.163.com/orpheus",
        "ws://127.0.0.1:39217/devtools/page/worker"));

    var orderCdpTargets = cdpDiscoveryType.GetMethod(
        "OrderTargets",
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
    string[] ReadOrderedTargetIds(string? preferredTargetId) =>
        ((System.Collections.IEnumerable)orderCdpTargets.Invoke(
            null,
            [cdpTargets, preferredTargetId])!)
        .Cast<object>()
        .Select(target => (string)cdpTargetType.GetProperty("Id")!.GetValue(target)!)
        .ToArray();

    var scoreOrderedTargetIds = ReadOrderedTargetIds(preferredTargetId: null);
    var cachedTargetIds = ReadOrderedTargetIds("last-success");
    Assert(
        scoreOrderedTargetIds.SequenceEqual(["highest-score", "last-success"])
            && cachedTargetIds.SequenceEqual(["last-success", "highest-score"]),
        "CDP discovery must filter non-page and unrelated targets, rank candidates, and retry the last successful page first.");

    var targetProbePolicyType = platformPlaybackAssembly.GetType(
        "LyricsXQuiet.Platform.Windows.Playback.CdpNetEaseTargetProbePolicy",
        throwOnError: true)!;
    var shouldTryNextTarget = targetProbePolicyType.GetMethod(
        "ShouldTryNextTarget",
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
    using var missingModuleProbe = System.Text.Json.JsonDocument.Parse(
        """{"ok":false,"error":"The NetEase playback modules are unavailable."}""");
    using var incompletePlaybackProbe = System.Text.Json.JsonDocument.Parse(
        """{"ok":false,"error":"Playback information is incomplete."}""");
    bool TryNextTarget(
        System.Text.Json.JsonElement probe,
        bool producedUsableSnapshot,
        int consecutiveInvalidCaptures) =>
        (bool)shouldTryNextTarget.Invoke(
            null,
            [probe, producedUsableSnapshot, consecutiveInvalidCaptures])!;
    Assert(
        TryNextTarget(missingModuleProbe.RootElement, false, 1)
            && !TryNextTarget(incompletePlaybackProbe.RootElement, false, 7)
            && TryNextTarget(incompletePlaybackProbe.RootElement, false, 8)
            && !TryNextTarget(missingModuleProbe.RootElement, true, 7)
            && TryNextTarget(missingModuleProbe.RootElement, true, 8),
        "A never-usable CDP page must fall through promptly, while a previously working page gets a bounded reload recovery window.");

    var cdpAdapterType = typeof(SwitchableNetEaseBridgeSnapshotSource).Assembly.GetType(
        "LyricsXQuiet.Platform.Windows.Playback.CdpNetEaseSnapshotAdapter",
        throwOnError: true)!;
    var tryCreateCdpSnapshot = cdpAdapterType.GetMethod(
        "TryCreate",
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
    var liveProgressJson = """
        {"ok":true,"track":{"id":"123456","title":"Bridge Song","artist":"Artist","album":"Album"},"playback":{"status":"playing","positionMs":12000,"durationMs":180000,"timelineEventType":"playprogress","timelineEventAgeMs":87,"timelineFrozen":false}}
        """;
    using var liveProgressDocument = System.Text.Json.JsonDocument.Parse(liveProgressJson);
    var liveProgressArguments = new object?[] { liveProgressDocument.RootElement, 1L, null };
    Assert(
        (bool)tryCreateCdpSnapshot.Invoke(null, liveProgressArguments)!
            && liveProgressArguments[2] is NetEaseBridgeSnapshot liveProgressSnapshot
            && liveProgressSnapshot.PositionMs == 12087,
        "A live playing progress event should be advanced only by its bounded event age before anchoring at sample time.");
    var oldProgressJson = liveProgressJson.Replace(
        "\"timelineEventAgeMs\":87",
        "\"timelineEventAgeMs\":900",
        StringComparison.Ordinal);
    using var oldProgressDocument = System.Text.Json.JsonDocument.Parse(oldProgressJson);
    var oldProgressArguments = new object?[] { oldProgressDocument.RootElement, 2L, null };
    Assert(
        (bool)tryCreateCdpSnapshot.Invoke(null, oldProgressArguments)!
            && oldProgressArguments[2] is NetEaseBridgeSnapshot oldProgressSnapshot
            && oldProgressSnapshot.PositionMs == 12500,
        "Event-age advancement must be capped so a stale callback cannot cause a large jump.");
    var frozenSeekJson = liveProgressJson
        .Replace("\"status\":\"playing\"", "\"status\":\"paused\"", StringComparison.Ordinal)
        .Replace("\"timelineEventType\":\"playprogress\"", "\"timelineEventType\":\"seek\"", StringComparison.Ordinal)
        .Replace("\"timelineFrozen\":false", "\"timelineFrozen\":true", StringComparison.Ordinal);
    using var frozenSeekDocument = System.Text.Json.JsonDocument.Parse(frozenSeekJson);
    var frozenSeekArguments = new object?[] { frozenSeekDocument.RootElement, 3L, null };
    Assert(
        (bool)tryCreateCdpSnapshot.Invoke(null, frozenSeekArguments)!
            && frozenSeekArguments[2] is NetEaseBridgeSnapshot frozenSeekSnapshot
            && frozenSeekSnapshot.PositionMs == 12000,
        "Paused or frozen seek anchors must stay exact and must not be advanced by event age.");

    foreach (var noAdvanceJson in new[]
    {
        oldProgressJson.Replace("\"playing\"", "\"paused\"", StringComparison.Ordinal),
        oldProgressJson.Replace("\"timelineFrozen\":false", "\"timelineFrozen\":true", StringComparison.Ordinal),
        oldProgressJson.Replace("\"playprogress\"", "\"seek\"", StringComparison.Ordinal)
    })
    {
        using var document = System.Text.Json.JsonDocument.Parse(noAdvanceJson);
        Assert(CdpNetEaseSnapshotAdapter.TryCreate(document.RootElement, 4, out var sample) && sample?.PositionMs == 12000,
            "Pausing, freezing, and an explicit seek must each independently disable event-age advancement.");
    }

    var pipeName = $"Lyrveil.PlatformSmoke.{Guid.NewGuid():N}";
    var handledCount = 0;
    CompanionLaunchRequest? received = null;
    await using var channel = new SingleInstanceCompanionChannel(
        pipeName,
        (incoming, _) =>
        {
            received = incoming;
            var count = Interlocked.Increment(ref handledCount);
            return Task.FromResult(
                count == 1
                    ? CompanionLaunchResult.Started
                    : CompanionLaunchResult.AlreadyRunning);
        });
    channel.Start();

    var malformedResult = await SendRawAsync(
        pipeName,
        command: 99,
        flags: 0,
        path: string.Empty);
    Assert(
        malformedResult == CompanionLaunchResult.Rejected && handledCount == 0,
        "An unknown command must be rejected without reaching the handler.");

    var nonNormalizedPath = Path.Combine(
        tempDirectory,
        "child",
        "..",
        "cloudmusic.exe");
    var nonNormalizedResult = await SendRawAsync(
        pipeName,
        command: 1,
        flags: 1,
        path: nonNormalizedPath);
    Assert(
        nonNormalizedResult == CompanionLaunchResult.Rejected && handledCount == 0,
        "The server must reject non-normalized paths.");

    await using (var stalledClient = new NamedPipeClientStream(
        ".",
        pipeName,
        PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
    {
        using var stalledConnectTimeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await stalledClient.ConnectAsync(stalledConnectTimeout.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(3250));
    }

    var firstResult = await SingleInstanceCompanionChannel.SendAsync(
        pipeName,
        request!,
        TimeSpan.FromSeconds(2));
    Assert(
        firstResult == CompanionLaunchResult.Started
            && handledCount == 1
            && received == request,
        "A valid request should be delivered exactly once.");

    var secondResult = await SingleInstanceCompanionChannel.SendAsync(
        pipeName,
        request!,
        TimeSpan.FromSeconds(2));
    Assert(
        secondResult == CompanionLaunchResult.AlreadyRunning && handledCount == 2,
        "The server should accept a second connection after completing the first.");

    await channel.DisposeAsync();
    var refusedAfterDispose = false;
    try
    {
        _ = await SingleInstanceCompanionChannel.SendAsync(
            pipeName,
            request!,
            TimeSpan.FromMilliseconds(200));
    }
    catch (Exception exception) when (
        exception is OperationCanceledException or TimeoutException or IOException)
    {
        refusedAfterDispose = true;
    }

    Assert(refusedAfterDispose, "A disposed channel must stop accepting requests.");

    var timeoutRecoveryPipeName = $"Lyrveil.PlatformSmoke.TimeoutRecovery.{Guid.NewGuid():N}";
    var timeoutRecoveryHandledCount = 0;
    await using (var timeoutRecoveryChannel = new SingleInstanceCompanionChannel(
        timeoutRecoveryPipeName,
        async (_, _) =>
        {
            var count = Interlocked.Increment(ref timeoutRecoveryHandledCount);
            if (count == 1)
            {
                // Process.Start and similar platform calls cannot be cancelled once
                // entered. Model that tail by finishing just after the server's
                // fixed three-second request budget, so its response write observes
                // an already-cancelled token.
                await Task.Delay(TimeSpan.FromMilliseconds(3250));
                return CompanionLaunchResult.Started;
            }

            return CompanionLaunchResult.AlreadyRunning;
        }))
    {
        timeoutRecoveryChannel.Start();
        var firstClientDisconnected = false;
        try
        {
            _ = await SingleInstanceCompanionChannel.SendAsync(
                timeoutRecoveryPipeName,
                request!,
                TimeSpan.FromSeconds(5));
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or TimeoutException or IOException)
        {
            firstClientDisconnected = true;
        }

        Assert(
            firstClientDisconnected && timeoutRecoveryHandledCount == 1,
            "A handler that exceeds the server request budget should disconnect its client.");

        var resultAfterTimedOutResponse = await SingleInstanceCompanionChannel.SendAsync(
            timeoutRecoveryPipeName,
            request!,
            TimeSpan.FromSeconds(2));
        Assert(
            resultAfterTimedOutResponse == CompanionLaunchResult.AlreadyRunning
                && timeoutRecoveryHandledCount == 2,
            "The pipe server must accept a valid client after a timed-out response write.");
    }

    var (firstPort, secondPort) = AllocateTwoLoopbackPorts();
    var switchableSource = new SwitchableNetEaseBridgeSnapshotSource();
    await switchableSource.StartAsync();
    Assert(
        switchableSource.LocalEndpoint is null,
        "The switchable source should begin in native-only fallback mode.");
    await switchableSource.ConfigurePortAsync(firstPort);
    Assert(
        switchableSource.LocalEndpoint?.Port == firstPort,
        "The first exact-sync endpoint should be activated without replacing the playback service.");
    await switchableSource.ConfigurePortAsync(secondPort);
    Assert(
        switchableSource.LocalEndpoint?.Port == secondPort,
        "A later companion launch should replace the previous endpoint.");
    await switchableSource.DisableAsync();
    Assert(
        switchableSource.LocalEndpoint is null && switchableSource.Current is null,
        "Disabling the endpoint should return to native-only fallback mode.");
    await switchableSource.DisposeAsync();
    await switchableSource.DisposeAsync();

    var serviceNow = DateTimeOffset.Now;
    var nativeTrack = new TrackIdentity(
        "Bridge Clock Song",
        "Bridge Artist",
        "Bridge Album",
        TimeSpan.FromMinutes(3),
        "cloudmusic.exe");
    var nativePlaying = new PlaybackSnapshot(
        nativeTrack,
        PlaybackState.Playing,
        TimeSpan.Zero,
        1d,
        serviceNow,
        PlaybackTimelineKind.Unavailable);
    var fakeNative = new FakePlaybackService(nativePlaying);
    var bridgeTrack = new NetEaseBridgeTrack(
        "123456",
        nativeTrack.Title,
        [nativeTrack.Artist],
        nativeTrack.Album,
        180000);
    var freshBridge = new NetEaseBridgeSnapshot(
        NetEaseBridgeSnapshotMerger.CurrentProtocolVersion,
        1,
        serviceNow,
        bridgeTrack,
        PlaybackState.Playing,
        42000,
        1d);
    var fakeBridge = new FakeBridgeSource(freshBridge);
    await using (var enhancedService = new EnhancedNetEasePlaybackService(
        fakeNative,
        fakeBridge))
    {
        await enhancedService.StartAsync();
        Assert(
            enhancedService.Current?.TimelineKind == PlaybackTimelineKind.Reported
                && enhancedService.Current.Track.SourceTrackId == "netease:123456"
                && Math.Abs(enhancedService.Current.Position.TotalMilliseconds - 42000) < 1000,
            "A fresh bridge snapshot should anchor the enhanced clock and enrich the track ID.");

        fakeBridge.Publish(null);
        var fallbackPlaying = enhancedService.Current!;
        Assert(
            fallbackPlaying.TimelineKind == PlaybackTimelineKind.EnhancedEstimated
                && fallbackPlaying.State == PlaybackState.Playing
                && fallbackPlaying.Track.SourceTrackId == "netease:123456"
                && fallbackPlaying.Track.Duration == TimeSpan.FromMinutes(3)
                && fallbackPlaying.Position >= TimeSpan.FromSeconds(41),
            "Bridge loss on the same native song should retain metadata and continue from the last enhanced position.");

        await Task.Delay(40);
        fakeNative.Publish(nativePlaying with
        {
            State = PlaybackState.Paused,
            CapturedAt = DateTimeOffset.Now
        });
        var pausedFallback = enhancedService.Current!;
        var pausedPosition = pausedFallback.Position;
        await Task.Delay(40);
        Assert(
            pausedFallback.EstimatePosition(DateTimeOffset.Now) == pausedPosition,
            "A paused native state should freeze the enhanced fallback clock.");

        fakeNative.Publish(nativePlaying with
        {
            State = PlaybackState.Playing,
            CapturedAt = DateTimeOffset.Now
        });
        var resumedFallback = enhancedService.Current!;
        await Task.Delay(40);
        Assert(
            resumedFallback.EstimatePosition(DateTimeOffset.Now) > resumedFallback.Position,
            "Resuming the same song should continue the frozen fallback clock locally.");

        var recoveryTime = DateTimeOffset.Now;
        var recoveryPosition = resumedFallback.EstimatePosition(recoveryTime);
        var fallbackLyricPosition = NetEaseLyricsPositionCompensation.Apply(
            resumedFallback,
            recoveryPosition,
            120);
        var recoveredBridge = freshBridge with
        {
            Sequence = 2,
            ObservedAt = recoveryTime,
            PositionMs = (long)recoveryPosition.TotalMilliseconds
        };
        fakeBridge.Publish(recoveredBridge);
        var recoveredSnapshot = enhancedService.Current!;
        var recoveredLyricPosition = NetEaseLyricsPositionCompensation.Apply(
            recoveredSnapshot,
            recoveredSnapshot.Position,
            120);
        Assert(
            recoveredSnapshot.TimelineKind == PlaybackTimelineKind.Reported
                && Math.Abs(
                    (recoveredLyricPosition - fallbackLyricPosition).TotalMilliseconds) < 2,
            "Bridge loss and recovery must retain the same 120 ms lyric delay without a visible compensation jump.");

        var reanchoredBridge = recoveredBridge with
        {
            Sequence = 3,
            ObservedAt = DateTimeOffset.Now,
            PositionMs = 75000
        };
        fakeBridge.Publish(reanchoredBridge);
        Assert(
            enhancedService.Current?.TimelineKind == PlaybackTimelineKind.Reported
                && Math.Abs(enhancedService.Current.Position.TotalMilliseconds - 75000) < 50,
            "A returning fresh bridge must re-anchor immediately instead of slewing from the fallback clock.");

        var publicationOrder = new List<long>();
        var nestedPublicationTriggered = false;
        var firstOrderedBridge = reanchoredBridge with
        {
            Sequence = 4,
            ObservedAt = DateTimeOffset.Now,
            PositionMs = 84000
        };
        var secondOrderedBridge = firstOrderedBridge with
        {
            Sequence = 5,
            ObservedAt = firstOrderedBridge.ObservedAt + TimeSpan.FromMilliseconds(25),
            PositionMs = 91000
        };
        EventHandler<PlaybackSnapshot?> publicationObserver = (_, candidate) =>
        {
            if (candidate?.Track.SourceTrackId != "netease:123456")
            {
                return;
            }

            var positionMs = (long)Math.Round(candidate.Position.TotalMilliseconds);
            if (!nestedPublicationTriggered && positionMs == 84000)
            {
                nestedPublicationTriggered = true;
                fakeBridge.Publish(secondOrderedBridge);
            }

            publicationOrder.Add(positionMs);
        };
        enhancedService.SnapshotChanged += publicationObserver;
        fakeBridge.Publish(firstOrderedBridge);
        enhancedService.SnapshotChanged -= publicationObserver;
        Assert(
            publicationOrder.SequenceEqual([84000L, 91000L])
                && enhancedService.Current?.Position == TimeSpan.FromSeconds(91),
            "Nested native/bridge updates must be delivered in calculation order so an older frame can never overwrite a newer seek anchor.");

        var differentTrack = new TrackIdentity(
            "Different Song",
            "Bridge Artist",
            "Other Album",
            TimeSpan.FromMinutes(4),
            "cloudmusic.exe");
        fakeBridge.Publish(null);
        fakeNative.Publish(new PlaybackSnapshot(
            differentTrack,
            PlaybackState.Playing,
            TimeSpan.Zero,
            1d,
            DateTimeOffset.Now,
            PlaybackTimelineKind.Unavailable));
        Assert(
            enhancedService.Current?.Track.Title == differentTrack.Title
                && string.IsNullOrEmpty(enhancedService.Current.Track.SourceTrackId)
                && enhancedService.Current.Position == TimeSpan.Zero,
            "A new NetEase song must never inherit the previous song's enhanced clock or source ID.");

        fakeNative.Publish(new PlaybackSnapshot(
            nativeTrack with { SourceAppUserModelId = "Spotify.exe" },
            PlaybackState.Playing,
            TimeSpan.Zero,
            1d,
            DateTimeOffset.Now,
            PlaybackTimelineKind.Unavailable));
        Assert(
            enhancedService.Current?.Track.SourceAppUserModelId == "Spotify.exe"
                && string.IsNullOrEmpty(enhancedService.Current.Track.SourceTrackId),
            "Another player must never inherit a NetEase enhanced clock.");
    }
    Console.WriteLine("Platform IPC smoke tests passed.");
}
finally
{
    Directory.Delete(tempDirectory, recursive: true);
}

static async Task<CompanionLaunchResult> SendRawAsync(
    string pipeName,
    byte command,
    byte flags,
    string path)
{
    var pathBytes = Encoding.UTF8.GetBytes(path);
    var payload = new byte[12 + pathBytes.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x3152594C);
    payload[4] = 1;
    payload[5] = command;
    payload[6] = flags;
    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), pathBytes.Length);
    pathBytes.CopyTo(payload.AsSpan(12));

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    await using var client = new NamedPipeClientStream(
        ".",
        pipeName,
        PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await client.ConnectAsync(timeout.Token);
    await client.WriteAsync(payload, timeout.Token);
    await client.FlushAsync(timeout.Token);
    var response = new byte[8];
    await client.ReadExactlyAsync(response, timeout.Token);
    Assert(
        BinaryPrimitives.ReadUInt32LittleEndian(response) == 0x3152594C
            && response[4] == 1,
        "The response framing should be valid.");
    return (CompanionLaunchResult)response[5];
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static (int First, int Second) AllocateTwoLoopbackPorts()
{
    var first = new TcpListener(IPAddress.Loopback, 0);
    var second = new TcpListener(IPAddress.Loopback, 0);
    try
    {
        first.Start();
        second.Start();
        return (
            ((IPEndPoint)first.LocalEndpoint).Port,
            ((IPEndPoint)second.LocalEndpoint).Port);
    }
    finally
    {
        first.Stop();
        second.Stop();
    }
}

sealed class FakePlaybackService(PlaybackSnapshot? initial) : IPlaybackService
{
    public event EventHandler<PlaybackSnapshot?>? SnapshotChanged;

    public event EventHandler<PlaybackConnectionStatus>? ConnectionStatusChanged
    {
        add { }
        remove { }
    }

    public PlaybackSnapshot? Current { get; private set; } = initial;

    public PlaybackConnectionStatus ConnectionStatus { get; private set; } =
        new(PlaybackConnectionState.Connected);

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public void Publish(PlaybackSnapshot? snapshot)
    {
        Current = snapshot;
        SnapshotChanged?.Invoke(this, snapshot);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class FakeBridgeSource(NetEaseBridgeSnapshot? initial) : INetEaseBridgeSnapshotSource
{
    public event EventHandler<NetEaseBridgeSnapshot?>? SnapshotChanged;

    public NetEaseBridgeSnapshot? Current { get; private set; } = initial;

    public IPEndPoint? LocalEndpoint => null;

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public void Publish(NetEaseBridgeSnapshot? snapshot)
    {
        Current = snapshot;
        SnapshotChanged?.Invoke(this, snapshot);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
