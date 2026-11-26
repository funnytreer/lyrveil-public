using LyricsXQuiet.Core.Playback;
using LyricsXQuiet.Core.Settings;

internal static class PlayerOffsetRegressionTests
{
    public static async Task RunAsync()
    {
        var settings = new AppSettings
        {
            NetEaseSyncDelayMilliseconds = 1120,
            NetEaseOffsetMilliseconds = 200,
            AppleMusicSyncAdvanceMilliseconds = 300,
            QqMusicOffsetMilliseconds = -2400,
            KugouOffsetMilliseconds = 1800,
            SpotifyOffsetMilliseconds = 3000
        };
        (string Source, string TrackId, int Offset)[] cases =
        [
            ("cloudmusic.exe", "netease:123456", 200),
            ("AppleInc.AppleMusic_win!App", "", 300),
            ("QQMusic.exe", "", -2400),
            ("KGMUSIC.exe", "", 1800),
            ("SpotifyAB.SpotifyMusic_win!Spotify", "", 3000)
        ];
        foreach (var item in cases)
        {
            var snapshot = new PlaybackSnapshot(
                new TrackIdentity("Same song", "Same artist", "Same album",
                    TimeSpan.FromMinutes(3), item.Source, item.TrackId),
                PlaybackState.Paused, TimeSpan.FromSeconds(10), 1,
                DateTimeOffset.Now, PlaybackTimelineKind.Reported);
            Check(settings.GetPlayerLyricOffsetMilliseconds(item.Source) == item.Offset,
                $"{item.Source} must select its own saved offset.");
            Check(PlayerLyricsPositionCompensation.Apply(snapshot, snapshot.Position,
                    settings.GetPlayerLyricOffsetMilliseconds(item.Source), settings.NetEaseSyncDelayMilliseconds)
                  == TimeSpan.FromMilliseconds(10000 + item.Offset
                      - (item.Source == "cloudmusic.exe" ? 1120 : 0)),
                $"{item.Source} must apply its own baseline and adjustment exactly once.");
        }

        foreach (var item in cases.Where(item => item.Source != "AppleInc.AppleMusic_win!App"))
        {
            foreach (var input in new[] { int.MinValue, -3000, -100, 0, 100, 3000, int.MaxValue })
            {
                var before = cases.Select(value => settings.GetPlayerLyricOffsetMilliseconds(value.Source)).ToArray();
                var expected = Math.Clamp(input, -3000, 3000);
                settings.SetPlayerLyricOffsetMilliseconds(item.Source, input);
                Check(settings.GetPlayerLyricOffsetMilliseconds(item.Source) == expected,
                    $"{item.Source} must support and clamp the entire signed 3000 ms range.");
                for (var index = 0; index < cases.Length; index++)
                    if (cases[index].Source != item.Source)
                        Check(settings.GetPlayerLyricOffsetMilliseconds(cases[index].Source) == before[index],
                            "Changing one player must leave every other player unchanged.");
                Check(settings.OffsetMilliseconds == 0, "Player controls must never change the global offset.");
                Check(settings.NetEaseSyncDelayMilliseconds == 1120,
                    "Quick adjustments must not overwrite the user's NetEase baseline.");
            }
            settings.SetPlayerLyricOffsetMilliseconds(item.Source, item.Offset);
        }

        var nativeNetEase = new PlaybackSnapshot(
            new TrackIdentity("Song", "Artist", "", TimeSpan.FromMinutes(3), "cloudmusic.exe"),
            PlaybackState.Paused, TimeSpan.FromSeconds(10), 1, DateTimeOffset.Now,
            PlaybackTimelineKind.Reported);
        Check(PlayerLyricsPositionCompensation.Apply(nativeNetEase, nativeNetEase.Position, -1120)
              == nativeNetEase.Position, "NetEase calibration must still require its enhanced timeline.");
        var unknown = nativeNetEase with { Track = nativeNetEase.Track with { SourceAppUserModelId = "unknown.exe" } };
        settings.SetPlayerLyricOffsetMilliseconds(unknown.Track.SourceAppUserModelId, 2000);
        Check(settings.GetPlayerLyricOffsetMilliseconds(null) == 0
              && PlayerLyricsPositionCompensation.Apply(unknown, unknown.Position, 2000) == unknown.Position,
            "An unrecognized player must not inherit or modify another player's calibration.");

        var root = Path.Combine(Path.GetTempPath(), "lyrveil-player-offsets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SettingsStore(root);
            var defaults = await store.LoadAsync();
            var enhancedNetEase = nativeNetEase with
            {
                Track = nativeNetEase.Track with { SourceTrackId = "netease:123456" }
            };
            await store.SaveAsync(defaults);
            var reloadedDefaults = await store.LoadAsync();
            Check(reloadedDefaults.GetPlayerLyricOffsetMilliseconds("cloudmusic.exe") == 0
                  && PlayerLyricsPositionCompensation.Apply(enhancedNetEase, enhancedNetEase.Position,
                      reloadedDefaults.GetPlayerLyricOffsetMilliseconds("cloudmusic.exe"),
                      reloadedDefaults.NetEaseSyncDelayMilliseconds)
                  == TimeSpan.FromSeconds(9),
                "Zero quick offset must retain the one-second NetEase baseline after saving and reopening.");
            Check(reloadedDefaults.OffsetMilliseconds == 0
                  && reloadedDefaults.TrackLyricOffsetsMilliseconds.Count == 0
                  && reloadedDefaults.AppleMusicSyncAdvanceMilliseconds == 300
                  && reloadedDefaults.QqMusicOffsetMilliseconds == 0
                  && reloadedDefaults.KugouOffsetMilliseconds == 0
                  && reloadedDefaults.SpotifyOffsetMilliseconds == 0,
                "The NetEase default must not add a global, per-track, or other-player offset.");

            foreach (var timelineKind in new[] { PlaybackTimelineKind.Reported, PlaybackTimelineKind.EnhancedEstimated })
            {
                var snapshot = enhancedNetEase with { TimelineKind = timelineKind };
                foreach (var baseline in new[] { 0, 1000, 1600, 3000 })
                foreach (var adjustment in new[] { -3000, -200, 0, 200, 3000 })
                {
                    reloadedDefaults.NetEaseSyncDelayMilliseconds = baseline;
                    reloadedDefaults.SetPlayerLyricOffsetMilliseconds("cloudmusic.exe", adjustment);
                    await store.SaveAsync(reloadedDefaults);
                    var savedCalibration = await store.LoadAsync();
                    Check(savedCalibration.NetEaseSyncDelayMilliseconds == baseline
                          && savedCalibration.GetPlayerLyricOffsetMilliseconds("cloudmusic.exe") == adjustment
                          && PlayerLyricsPositionCompensation.Apply(snapshot, snapshot.Position,
                              savedCalibration.GetPlayerLyricOffsetMilliseconds("cloudmusic.exe"),
                              savedCalibration.NetEaseSyncDelayMilliseconds)
                          == TimeSpan.FromMilliseconds(10000 - baseline + adjustment),
                        "An editable default and signed micro-adjustment must persist independently and apply once in both precise and continued timelines.");
                }
            }
            Check(PlayerLyricsPositionCompensation.Apply(enhancedNetEase,
                      TimeSpan.FromMilliseconds(500), 0, 1000) == TimeSpan.FromMilliseconds(-500),
                "The baseline must retain a negative lyric position during the opening delay.");

            // Older calibration becomes the editable baseline with no extra
            // adjustment. Old zero/default values adopt the new baseline once.
            foreach (var savedDelay in new[] { 0, 120, 1000, 1120, 3000 })
            {
                await File.WriteAllTextAsync(Path.Combine(root, "settings.json"),
                    $"{{\"SettingsSchemaVersion\":4,\"NetEaseSyncDelayMilliseconds\":{savedDelay}}}");
                var saved = await store.LoadAsync();
                await store.SaveAsync(saved);
                var reopened = await store.LoadAsync();
                var expectedBaseline = savedDelay is 0 or 120 ? 1000 : savedDelay;
                Check(reopened.GetPlayerLyricOffsetMilliseconds("cloudmusic.exe") == 0
                      && reopened.NetEaseSyncDelayMilliseconds == expectedBaseline
                      && PlayerLyricsPositionCompensation.Apply(enhancedNetEase, enhancedNetEase.Position,
                          reopened.GetPlayerLyricOffsetMilliseconds("cloudmusic.exe"),
                          reopened.NetEaseSyncDelayMilliseconds)
                      == TimeSpan.FromMilliseconds(10000 - expectedBaseline),
                    "An old offset must become the default once, without adding another one-second correction on reload.");
            }

            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"),
                """{"SettingsSchemaVersion":4,"NetEaseSyncDelayMilliseconds":0,"neteaseoffsetmilliseconds":200}""");
            var explicitAdjustment = await store.LoadAsync();
            Check(explicitAdjustment.NetEaseSyncDelayMilliseconds == 0
                  && explicitAdjustment.NetEaseOffsetMilliseconds == 200,
                "Presence of the new adjustment field must preserve an explicit zero baseline even with an old schema marker.");

            await store.SaveAsync(settings);
            var reloaded = await store.LoadAsync();
            foreach (var item in cases)
                Check(reloaded.GetPlayerLyricOffsetMilliseconds(item.Source) == item.Offset,
                    $"{item.Source} calibration must survive saving and reopening.");
            Check(reloaded.OffsetMilliseconds == 0, "Persistence must keep the global offset unchanged.");

            // Existing non-default calibrations remain the user's baseline.
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"),
                """{"SettingsSchemaVersion":4,"NetEaseSyncDelayMilliseconds":1120,"AppleMusicSyncAdvanceMilliseconds":300,"OffsetMilliseconds":0}""");
            var old = await store.LoadAsync();
            Check(old.NetEaseSyncDelayMilliseconds == 1120 && old.AppleMusicSyncAdvanceMilliseconds == 300
                  && old.NetEaseOffsetMilliseconds == 0
                  && old.QqMusicOffsetMilliseconds == 0 && old.KugouOffsetMilliseconds == 0
                  && old.SpotifyOffsetMilliseconds == 0,
                "Opening older settings must preserve calibrated players and default new offsets to zero.");

            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"),
                """{"SettingsSchemaVersion":5,"NetEaseOffsetMilliseconds":3001,"QqMusicOffsetMilliseconds":-3001,"KugouOffsetMilliseconds":3001,"SpotifyOffsetMilliseconds":-3001}""");
            var clamped = await store.LoadAsync();
            Check(clamped.NetEaseOffsetMilliseconds == 3000
                  && clamped.QqMusicOffsetMilliseconds == -3000 && clamped.KugouOffsetMilliseconds == 3000
                  && clamped.SpotifyOffsetMilliseconds == -3000,
                "Invalid persisted offsets must be clamped before playback or UI binding.");
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("lyrveil-player-offsets-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected offset test cleanup path.");
            Directory.Delete(resolved, recursive: true);
        }
        Console.WriteLine("Independent player offsets passed: scope, signed bounds, switching, persistence, compatibility.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
