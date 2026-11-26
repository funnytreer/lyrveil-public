using LyricsXQuiet.Core.Settings;

internal static class RedesignSettingsTests
{
    private static void CheckPlayerLifecycle()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = new LyricsXQuiet.Core.Playback.PlayerMonitorLaunchPolicy(TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        policy.SuppressUntilAllPlayersExit();
        Check(policy.Observe(true, false, now.AddMinutes(2)) == LyricsXQuiet.Core.Playback.PlayerMonitorLaunchDecision.None
            && policy.Observe(false, false, now.AddMinutes(3), anyOtherSupportedPlayerRunning: true) == LyricsXQuiet.Core.Playback.PlayerMonitorLaunchDecision.None
            && policy.State == LyricsXQuiet.Core.Playback.PlayerMonitorLaunchState.SuppressedUntilAllPlayersExit,
            "An explicit exit must survive retries and a different player remaining open.");
        policy.Observe(false, false, now.AddMinutes(4));
        Check(policy.Observe(true, false, now.AddMinutes(5)) == LyricsXQuiet.Core.Playback.PlayerMonitorLaunchDecision.StartMainInstance,
            "A new listening session should wake lyrics after all players exited.");
        var lifetime = new LyricsXQuiet.Core.Playback.CompanionExitPolicy(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(4), TimeSpan.FromMinutes(5));
        bool Exit(bool player, int seconds, bool panel = false) => lifetime.ShouldExit(player, panel, false, now.AddSeconds(seconds), requireObservedPlayer: true);
        Check(!Exit(false, 0) && !Exit(false, 120) && !lifetime.HasObservedPlayer && !Exit(true, 121) && lifetime.HasObservedPlayer,
            "Opening settings manually must remain safe without a player.");
        Check(!Exit(false, 122) && !Exit(false, 124) && !Exit(false, 128, true) && Exit(false, 129),
            "Closing the last player should respect the debounce and any open configuration panel.");
    }

    public static async Task RunAsync()
    {
        CheckPlayerLifecycle();
        var root = Directory.CreateTempSubdirectory("lyrveil-schema6-");
        try
        {
            var store = new SettingsStore(root.FullName);
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "settings.json"), """
                {
                  "SettingsSchemaVersion": 5, "NetEaseSyncDelayMilliseconds": 0, "OffsetMilliseconds": -130,
                  "NetEaseOffsetMilliseconds": -995, "AppleMusicSyncAdvanceMilliseconds": 331,
                  "QqMusicOffsetMilliseconds": -210, "KugouOffsetMilliseconds": 600, "SpotifyOffsetMilliseconds": 810,
                  "TrackLyricOffsetsMilliseconds": { "track-one": 455, "track-two": -780 },
                  "OverlayLeft": -1290.5, "OverlayTop": 857.25, "OverlayDockedToTaskbar": false,
                  "LyricsFilterPatterns": "^录音.*$\n^abc$", "LyricsFilterEnabled": false,
                  "SavedAppearancePreset": {
                    "FontSize": 27, "FontFamilyName": "Segoe UI", "LyricColor": "#ABCDEF", "ProgressColor": "#12ABCD",
                    "BackgroundColor": "#122332", "BackgroundOpacity": 0.31, "ProgressiveHighlight": false, "DoubleLine": true
                  }
                }
                """);
            var settings = await store.LoadAsync();
            var expected = new AppearancePreset(27, "Segoe UI", "#ABCDEF", "#12ABCD", "#122332", 0.31, false, true);
            for (var pass = 0; pass < 2; pass++)
            {
                Check(settings.SettingsSchemaVersion == 6 && settings.NetEaseSyncDelayMilliseconds == 0
                    && settings.OffsetMilliseconds == -130 && settings.NetEaseOffsetMilliseconds == -995
                    && settings.AppleMusicSyncAdvanceMilliseconds == 331 && settings.QqMusicOffsetMilliseconds == -210
                    && settings.KugouOffsetMilliseconds == 600 && settings.SpotifyOffsetMilliseconds == 810,
                    "A v5 upgrade changed a timing calibration.");
                Check(settings.TrackLyricOffsetsMilliseconds.Count == 2 && settings.TrackLyricOffsetsMilliseconds["track-one"] == 455
                    && settings.TrackLyricOffsetsMilliseconds["track-two"] == -780 && settings.OverlayLeft == -1290.5
                    && settings.OverlayTop == 857.25 && settings.OverlayDockedToTaskbar == false
                    && !settings.LyricsFilterEnabled && settings.LyricsFilterPatterns == "^录音.*$\n^abc$",
                    "Lost a per-track offset, regex or monitor placement.");
                Check(settings.CustomAppearancePresets.Single() == new NamedAppearancePreset(
                    "我的方案", expected, 1, 1, "ProgressGlow", "Normal", "FadeOnly"), "Legacy preset was lost or duplicated.");
                Check(settings.UiThemeMode == "System" && settings.SecondaryLineScale == 0.8 && settings.SecondaryLineOpacity == 0.74
                    && settings.HotKeys.Count == 6 && settings.HotKeys.Values.All(string.IsNullOrEmpty), "Incorrect v6 defaults.");
                store.SaveNow(settings);
                settings = await store.LoadAsync();
            }

            settings.SecondaryLineOpacity = 2;
            settings.SecondaryLineScale = -1;
            Check(settings.SecondaryLineOpacity == 1 && settings.SecondaryLineScale == 0.7, "Incorrect upper/lower limits.");
            settings.SecondaryLineOpacity = -1;
            settings.SecondaryLineScale = 2;
            Check(settings.SecondaryLineOpacity == 0.5 && settings.SecondaryLineScale == 1, "Incorrect complementary limits.");
            settings.SecondaryLineScale = double.NaN;
            settings.SecondaryLineOpacity = double.PositiveInfinity;
            settings.UiThemeMode = settings.TextBackingStyle = settings.LineTransitionStyle = settings.LyricFontWeight = "invalid";
            Check(settings.SecondaryLineScale == 0.80 && settings.SecondaryLineOpacity == 0.74 && settings.UiThemeMode == "System"
                && settings.TextBackingStyle == "SoftShadow" && settings.LineTransitionStyle == "SlideFade"
                && settings.LyricFontWeight == "SemiBold", "Invalid configuration must get safe defaults.");

            foreach (var invalid in new[] { "A", "7", "OemPlus", "Enter", "Ctrl", "Ctrl+Shift", "Ctrl++A",
                                             "F25", "Ctrl+Ctrl+K", "Hyper+Q", "Ctrl+F99" })
                Check(!HotKeyBindings.TryNormalize(invalid, out _), $"Unsafe shortcut accepted: {invalid}");
            foreach (var valid in new[] { "F1", "F24", "MediaPlayPause", "Win+Space", "Shift+OemPlus", "Ctrl+9", "" })
                Check(HotKeyBindings.TryNormalize(valid, out _), $"Valid shortcut refused: {valid}");
            settings.HotKeys = new() { ["ToggleLyrics"] = " shift + ctrl + k ", ["ToggleLock"] = "CTRL+SHIFT+K",
                ["OffsetForward"] = "F12", ["unknown"] = "F24" };
            settings.UiThemeMode = "Dark";
            settings.CustomAppearancePresets = [.. settings.CustomAppearancePresets, new("另一个方案", expected)];
            store.SaveNow(settings);
            settings = await store.LoadAsync();
            Check(settings.HotKeys.Count == 6 && settings.HotKeys["ToggleLyrics"] == "Ctrl+Shift+K"
                && settings.HotKeys["ToggleLock"] == "" && settings.HotKeys["OffsetForward"] == "F12"
                && settings.UiThemeMode == "Dark" && settings.CustomAppearancePresets.Count == 2, "v6 round-trip failed.");
            var writers = Enumerable.Range(0, 3).Select(id => Task.Run(async () =>
            {
                var writer = new SettingsStore(root.FullName);
                for (int i = 0; i < 5; i++)
                {
                    var complete = new AppSettings { NetEaseOffsetMilliseconds = 100 * (id + 1) };
                    writer.SaveNow(complete);
                    var loaded = await writer.LoadAsync();
                    Check(loaded.NetEaseOffsetMilliseconds is 100 or 200 or 300, "A concurrent reader saw a partial save.");
                }
            }));
            await Task.WhenAll(writers);
        }
        finally { root.Delete(recursive: true); }
        Console.WriteLine("Schema v6 migration, round-trip, appearance bounds and hotkey validation passed.");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
