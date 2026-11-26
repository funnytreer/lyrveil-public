using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LyricsXQuiet.App.Services;
using LyricsXQuiet.App.State;
using LyricsXQuiet.App.Windows;
using LyricsXQuiet.Core.Lyrics;
using LyricsXQuiet.Core.Lyrics.Providers;
using LyricsXQuiet.Core.Playback;
using LyricsXQuiet.Core.Settings;

namespace LyricsXQuiet.Tools.OverlayPositionSmokeTests;

internal static class ReviewUiRegressionTests
{
    public static void CheckNetEaseCalibration(Action<TimeSpan> pump)
    {
        var tempBase = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(tempBase, "lyrveil-netease-calibration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = new AppSettings { ShowLyrics = false };
        var overlay = new OverlayWindow(settings);
        try
        {
            var store = new SettingsStore(root);
            using var persistence = new SettingsPersistenceService(store, settings,
                Dispatcher.CurrentDispatcher, () => { });
            var playback = new MutablePlayback();
            var cache = new LyricsCache(Path.Combine(root, "lyrics"));
            using var controller = new AppController(playback, new LyricsResolver(cache, [new SwitchingProvider()]),
                settings, new AppState(), overlay);
            controller.Start();
            var viewModel = new AppViewModel(settings, new AppState());
            var panel = new QuickPanelWindow(viewModel, controller, () => { });
            var window = new SettingsWindow(viewModel, controller, cache, () => { }, persistence.SaveAsync);
            try
            {
                var track = new TrackIdentity("Calibration fixture", "Artist", "Album",
                    TimeSpan.FromMinutes(3), "cloudmusic.exe", "netease:123456");
                playback.Publish(Snapshot(track, PlaybackState.Paused));
                pump(TimeSpan.FromMilliseconds(50));
                var baseline = (Slider)((FrameworkElement)window.FindName("PlaybackPanel")).FindName("NetEaseDefaultDelaySlider");
                var adjustment = (Slider)((FrameworkElement)window.FindName("PlaybackPanel")).FindName("NetEaseOffsetSlider");
                var value = (TextBlock)panel.FindName("PlayerOffsetValueText");
                var back = (Button)panel.FindName("PlayerOffsetBackButton");
                var forward = (Button)panel.FindName("PlayerOffsetForwardButton");
                var offsetPanel = (Grid)panel.FindName("PlayerOffsetPanel");
                Check(baseline.Value == 1000 && adjustment.Value == 0 && value.Text == "0 ms"
                      && back.IsEnabled && forward.IsEnabled,
                    "The quick panel must start at zero with a one-second editable default and adjustment in both directions.");
                Check(baseline.Minimum == 0 && baseline.Maximum == 3000
                      && adjustment.Minimum == -3000 && adjustment.Maximum == 3000,
                    "Default compensation and relative adjustment must expose their separate ranges.");
                baseline.Value = 1600;
                pump(TimeSpan.FromMilliseconds(30));
                Check(settings.NetEaseSyncDelayMilliseconds == 1600 && value.Text == "0 ms"
                      && settings.NetEaseOffsetMilliseconds == 0
                      && offsetPanel.ToolTip?.ToString()?.Contains("1600") == true,
                    "Changing the user's default must leave the quick offset at zero and refresh its explanation.");
                adjustment.Value = 200;
                pump(TimeSpan.FromMilliseconds(30));
                Check(value.Text == "+200 ms" && settings.NetEaseOffsetMilliseconds == 200,
                    "Settings and quick-panel adjustments must share the relative value.");
                back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                pump(TimeSpan.FromMilliseconds(30));
                Check(adjustment.Value == 100 && settings.NetEaseSyncDelayMilliseconds == 1600,
                    "Quick buttons must adjust the relative offset without rewriting the default.");
                baseline.Value = 1400;
                pump(TimeSpan.FromMilliseconds(30));
                Check(adjustment.Value == 100 && value.Text == "+100 ms",
                    "Editing the baseline must not reset an existing micro-adjustment.");
                adjustment.Value = -3000;
                pump(TimeSpan.FromMilliseconds(30));
                Check(!back.IsEnabled && forward.IsEnabled, "The NetEase negative bound must disable only its back button.");
                adjustment.Value = 3000;
                pump(TimeSpan.FromMilliseconds(30));
                Check(back.IsEnabled && !forward.IsEnabled, "The NetEase positive bound must disable only its forward button.");
                adjustment.Value = 0;
                pump(TimeSpan.FromMilliseconds(700));
                var reload = store.LoadAsync();
                PumpUntil(reload, pump);
                var restored = reload.GetAwaiter().GetResult();
                Check(restored.NetEaseSyncDelayMilliseconds == 1400 && restored.NetEaseOffsetMilliseconds == 0,
                    "Setting the adjustment back to zero must automatically save and retain the user's chosen default.");
                back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                pump(TimeSpan.FromMilliseconds(700));
                reload = store.LoadAsync();
                PumpUntil(reload, pump);
                restored = reload.GetAwaiter().GetResult();
                Check(restored.NetEaseSyncDelayMilliseconds == 1400 && restored.NetEaseOffsetMilliseconds == -100,
                    "A quick-panel edit must automatically persist independently of the baseline.");
                playback.Publish(Snapshot(track with { SourceAppUserModelId = "QQMusic.exe", SourceTrackId = "" }, PlaybackState.Paused));
                pump(TimeSpan.FromMilliseconds(30));
                Check(value.Text == "0 ms", "Switching players must not inherit NetEase's baseline or adjustment.");
                forward.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(settings.QqMusicOffsetMilliseconds == 100 && settings.NetEaseSyncDelayMilliseconds == 1400
                      && settings.NetEaseOffsetMilliseconds == -100 && settings.OffsetMilliseconds == 0,
                    "QQ adjustments must leave both NetEase values and the global offset untouched.");
                playback.Publish(Snapshot(track, PlaybackState.Paused));
                pump(TimeSpan.FromMilliseconds(30));
                Check(value.Text == "-100 ms", "Returning to NetEase must restore its relative adjustment.");
                Check(!overlay.IsVisible && !panel.IsVisible && !window.IsVisible,
                    "Calibration validation must not show or activate any window.");
            }
            finally
            {
                panel.ClosePermanently();
                window.ClosePermanently();
            }
        }
        finally
        {
            overlay.Close();
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.TrimEndingDirectorySeparator(tempBase) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("lyrveil-netease-calibration-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected calibration test cleanup path.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    public static void Run(Action<TimeSpan> pump)
    {
        var tempBase = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(tempBase, "lyrveil-ui-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = new AppSettings { HideWhenPaused = false, HoverHide = true, ShowLyrics = true, Locked = true };
        var window = new OverlayWindow(settings);
        try
        {
            var track = new TrackIdentity("Regression A", "Artist", "", TimeSpan.FromSeconds(180), "QQMusic.exe");
            var playback = new MutablePlayback();
            var source = new SwitchingProvider();
            var blocker = Path.Combine(root, "blocked-cache");
            File.WriteAllText(blocker, "fixture");
            var resolver = new LyricsResolver(new LyricsCache(blocker), [source]);
            var state = new AppState();
            using var controller = new AppController(playback, resolver, settings, state, window);
            controller.Start();
            bool IsRendering() => ReadField<bool>(controller, "_rendering");
            var cursorTimer = ReadField<DispatcherTimer>(window, "_cursorTimer");
            Check(!IsRendering() && !cursorTimer.IsEnabled, "Idle windows must not keep frame/mouse polling timers running.");
            playback.Publish(Snapshot(track, PlaybackState.Playing));
            pump(TimeSpan.FromMilliseconds(180));
            Check(window.IsVisible && CurrentText(window) == "Regression A lyric" && state.LyricsStatus.Contains("未能保存"),
                "A cache write failure must leave real lyrics visible and show the save warning.");
            Check(cursorTimer.IsEnabled, "Visible locked lyrics must enable hover detection.");
            var setHover = typeof(OverlayWindow).GetMethod("AnimateHover", BindingFlags.Instance | BindingFlags.NonPublic)!;
            setHover.Invoke(window, [true]);
            Check(!IsRendering(), "Hover-hidden lyrics must unsubscribe from composition immediately.");
            setHover.Invoke(window, [false]);
            Check(IsRendering(), "Leaving the overlay must immediately restore rendering.");
            settings.HoverHide = false;
            MeasureRendering(window);
            playback.Publish(Snapshot(track, PlaybackState.Paused));
            pump(TimeSpan.FromMilliseconds(100));
            Check(!IsRendering() && window.IsVisible, "Paused lyrics must remain visible without a frame timer.");
            MeasureIdle();
            settings.ShowLyrics = false;
            pump(TimeSpan.FromMilliseconds(50));
            Check(!cursorTimer.IsEnabled && !window.IsVisible, "Hiding the overlay must stop cursor polling.");
            settings.ShowLyrics = true;
            playback.Publish(Snapshot(track, PlaybackState.Playing));
            pump(TimeSpan.FromMilliseconds(100));
            Check(IsRendering() && window.IsVisible, "Playback/show changes must restart rendering without another lyric search.");

            settings.ShowTrackInfoWhenNoLyrics = false;
            var intro = new LyricsCandidate("intro", "Intro source", track.Title, track.Artist, "", track.Duration, 0,
                LrcParser.Parse("[00:02.40]First timed line", "Intro source"));
            playback.Publish(Snapshot(track, PlaybackState.Playing));
            pump(TimeSpan.FromMilliseconds(20));
            PumpUntil(controller.SelectLyricsAsync(intro, track.LyricsOperationKey), pump);
            Check(!window.IsVisible && !IsRendering(), "An empty intro must not drive composition.");
            pump(TimeSpan.FromMilliseconds(500));
            Check(window.IsVisible && IsRendering() && CurrentText(window) == "First timed line",
                "Hidden lyrics must wake for the first timestamp without a new player snapshot.");
            settings.ShowTrackInfoWhenNoLyrics = true;

            var selection = new LyricsCandidate("manual", "Manual source", track.Title, track.Artist, "", track.Duration, 0,
                LrcParser.Parse("[00:00.00]Manual lyric", "Manual source"));
            var selecting = controller.SelectLyricsAsync(selection, track.LyricsOperationKey);
            PumpUntil(selecting, pump);
            Check(selecting.Result == LyricsSelectionResult.SessionOnly && CurrentText(window) == "Manual lyric"
                  && state.LyricsStatus.Contains("未能保存"), "Manual session-only selection must render and report persistence failure.");

            source.DelayNext = true;
            var oldTrack = track with { Title = "Delayed B" };
            playback.Publish(Snapshot(oldTrack, PlaybackState.Playing));
            pump(TimeSpan.FromMilliseconds(60));
            var newTrack = track with { Title = "Current C" };
            playback.Publish(Snapshot(newTrack, PlaybackState.Playing));
            pump(TimeSpan.FromMilliseconds(180));
            source.CompleteDelayed(oldTrack);
            pump(TimeSpan.FromMilliseconds(100));
            Check(CurrentText(window) == "Current C lyric", "A cancelled old source must never overwrite the current song after late completion.");

            CheckIndependentPlayerOffsets(settings, controller, playback, window, pump);
            CheckSettingsPersistence(root, pump);
            CheckFilterDebounce(settings, controller, root, pump);
            VerifySettingsLifetime(settings, controller, root, pump);
            Console.WriteLine("Review UI regressions passed: visible lyrics on save failure, stale-result rejection, idle/resume timers, settings persistence, filter debounce.");
        }
        finally
        {
            window.Close();
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.TrimEndingDirectorySeparator(tempBase) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("lyrveil-ui-review-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected UI test cleanup path.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static void CheckSettingsPersistence(string root, Action<TimeSpan> pump)
    {
        var directory = Path.Combine(root, "settings");
        var settings = new AppSettings();
        var notifications = 0;
        using var persistence = new SettingsPersistenceService(new SettingsStore(directory), settings,
            Dispatcher.CurrentDispatcher, () => notifications++);
        settings.BackgroundOpacity = 0.3;
        settings.BackgroundOpacity = 0.4;
        settings.QqMusicOffsetMilliseconds = -2100;
        settings.KugouOffsetMilliseconds = 1600;
        settings.SpotifyOffsetMilliseconds = 3000;
        settings.AutoRepairNetEaseSync = true;
        pump(TimeSpan.FromMilliseconds(750));
        Check(notifications == 1, "A burst of setting edits must result in one debounced write notification.");
        settings.OverlayLeft = 123;
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "settings.json")));
        Check(json.RootElement.GetProperty("OverlayLeft").GetDouble() == 123, "Dragged position must be durable immediately.");
        Check(json.RootElement.GetProperty("AutoRepairNetEaseSync").GetBoolean(),
            "The user's recovery opt-in must persist across restarts.");
        Check(json.RootElement.GetProperty("QqMusicOffsetMilliseconds").GetInt32() == -2100
              && json.RootElement.GetProperty("KugouOffsetMilliseconds").GetInt32() == 1600
              && json.RootElement.GetProperty("SpotifyOffsetMilliseconds").GetInt32() == 3000,
            "Independent player edits must be saved automatically by the settings persistence service.");
    }

    private static void CheckIndependentPlayerOffsets(AppSettings settings, AppController controller,
        MutablePlayback playback, OverlayWindow overlay, Action<TimeSpan> pump)
    {
        settings.QqMusicOffsetMilliseconds = -3000;
        settings.KugouOffsetMilliseconds = 3000;
        settings.SpotifyOffsetMilliseconds = 0;
        var originalNetEase = settings.NetEaseSyncDelayMilliseconds;
        var originalApple = settings.AppleMusicSyncAdvanceMilliseconds;
        var panel = new QuickPanelWindow(new AppViewModel(settings, new AppState()), controller, () => { });
        try
        {
            var back = (Button)panel.FindName("PlayerOffsetBackButton");
            var forward = (Button)panel.FindName("PlayerOffsetForwardButton");
            var value = (TextBlock)panel.FindName("PlayerOffsetValueText");
            var playerName = (TextBlock)panel.FindName("PlayerOffsetSourceText");
            playback.Publish(null);
            pump(TimeSpan.FromMilliseconds(30));
            Check(!back.IsEnabled && !forward.IsEnabled && value.Text == "—",
                "No active player must disable quick offset adjustments.");

            var track = new TrackIdentity("Identical metadata", "Artist", "Album", TimeSpan.FromMinutes(3), "QQMusic.exe");
            void Publish(string source)
            {
                playback.Publish(Snapshot(track with { SourceAppUserModelId = source }, PlaybackState.Paused)
                    with { Position = TimeSpan.FromSeconds(10) });
                pump(TimeSpan.FromMilliseconds(30));
            }
            Publish("QQMusic.exe");
            var document = LrcParser.Parse("[00:00.00]Before\n[00:09.00]At\n[00:12.00]After", "offset fixture");
            PumpUntil(controller.SelectLyricsAsync(new LyricsCandidate("offset", "offset fixture", track.Title,
                track.Artist, track.Album, track.Duration, 0, document), track.LyricsOperationKey), pump);

            (string Source, string Name, string Value, string Line)[] cases =
            [
                ("QQMusic.exe", "QQ", "-3000 ms", "Before"),
                ("KGMUSIC.exe", "酷狗", "+3000 ms", "After"),
                ("SpotifyAB.SpotifyMusic_win!Spotify", "Spotify", "0 ms", "At"),
                ("QQMusic.exe", "QQ", "-3000 ms", "Before")
            ];
            foreach (var item in cases)
            {
                Publish(item.Source);
                Check(playerName.Text == item.Name && value.Text == item.Value && CurrentText(overlay) == item.Line,
                    $"Switching to {item.Name} with identical song metadata must refresh both the panel and rendered lyric.");
            }
            Check(!back.IsEnabled && forward.IsEnabled, "The quick controls must respect the negative bound.");
            forward.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(settings.QqMusicOffsetMilliseconds == -2900 && value.Text == "-2900 ms"
                  && settings.KugouOffsetMilliseconds == 3000 && settings.SpotifyOffsetMilliseconds == 0,
                "The QQ quick button must update QQ alone.");
            Publish("KGMUSIC.exe");
            Check(back.IsEnabled && !forward.IsEnabled, "The quick controls must respect the positive bound.");
            back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(settings.KugouOffsetMilliseconds == 2900 && value.Text == "+2900 ms"
                  && settings.QqMusicOffsetMilliseconds == -2900 && settings.SpotifyOffsetMilliseconds == 0,
                "The Kugou quick button must update Kugou alone.");
            Publish("Spotify.exe");
            forward.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(settings.SpotifyOffsetMilliseconds == 100 && value.Text == "+100 ms"
                  && settings.QqMusicOffsetMilliseconds == -2900 && settings.KugouOffsetMilliseconds == 2900,
                "The Spotify quick button must update Spotify alone.");
            settings.QqMusicOffsetMilliseconds = 2000;
            Check(value.Text == "+100 ms" && CurrentText(overlay) == "At",
                "Editing an inactive player must not change the active player's panel or lyric position.");
            Check(settings.OffsetMilliseconds == 0 && settings.NetEaseSyncDelayMilliseconds == originalNetEase
                  && settings.AppleMusicSyncAdvanceMilliseconds == originalApple,
                "Independent controls must preserve the global and existing player calibrations.");
        }
        finally { panel.ClosePermanently(); }
        Console.WriteLine("Independent player UI passed: same-song switching, rendered positions, scoped quick controls, limits.");
    }

    private static void CheckFilterDebounce(AppSettings settings, AppController controller,
        string root, Action<TimeSpan> pump)
    {
        settings.NetEaseSyncDelayMilliseconds = 3000;
        settings.AppleMusicSyncAdvanceMilliseconds = 3000;
        settings.QqMusicOffsetMilliseconds = -3000;
        settings.KugouOffsetMilliseconds = 3000;
        settings.SpotifyOffsetMilliseconds = -3000;
        var viewModel = new AppViewModel(settings, new AppState());
        var window = new SettingsWindow(viewModel, controller, new LyricsCache(Path.Combine(root, "settings-lyrics")),
            () => { }, () => Task.CompletedTask);
        try
        {
            window.ShowWindow();
            var navigation = (TabControl)window.FindName("Navigation");
            Check(navigation.Items.Count == 7, "设置中心必须包含七个页面。");
            var pages = navigation.Items.Cast<TabItem>().Select(t => t.Content).ToArray();
            for (var repeat = 0; repeat < 3; repeat++)
                for (var tab = 0; tab < 7; tab++)
                {
                    navigation.SelectedIndex = tab;
                    window.UpdateLayout();
                    Check(ReferenceEquals(pages[tab], navigation.SelectedContent), "切换应复用页面实例。");
                }
            navigation.SelectedIndex = 2;
            window.UpdateLayout();
            var display = (UserControl)navigation.SelectedContent;
            var scroller = (ScrollViewer)display.Content;
            var scrollbar = (System.Windows.Controls.Primitives.ScrollBar)scroller.Template.FindName("PART_VerticalScrollBar", scroller);
            Check(scrollbar.IsVisible && scrollbar.ActualWidth is > 0 and <= 6,
                $"The settings scrollbar should be six DIPs wide (actual {scrollbar.ActualWidth:F1}).");
            Check(System.Windows.Shell.WindowChrome.GetWindowChrome(window) is null && window.ResizeMode == ResizeMode.CanResize
                && double.IsPositiveInfinity(window.MaxWidth), "Settings should use the native resizable caption.");
            VisualAcceptance.Capture((FrameworkElement)window.Content, "display-settings");
            var feedback = (TextBlock)display.FindName("PresetFeedback");
            feedback.BringIntoView();
            window.UpdateLayout();
            VisualAcceptance.Capture((FrameworkElement)window.Content, "display-presets");
            MeasureSettingsMotion(window);
            MeasureNativeMotion(window, pump);
            navigation.SelectedIndex = 4;
            var oldMode = settings.UiThemeMode;
            using (var theme = new ThemeManager(Application.Current, settings))
            {
                settings.UiThemeMode = "Dark";
                pump(TimeSpan.FromMilliseconds(60));
                window.UpdateLayout();
                VisualAcceptance.Capture((FrameworkElement)window.Content, "shortcuts-settings");
                settings.UiThemeMode = oldMode;
            }
            navigation.SelectedIndex = 3;
            window.UpdateLayout();
            var lyricsPage = (UserControl)navigation.SelectedContent;
            var openFolder = (Button)lyricsPage.FindName("OpenLyricsDirectoryButton");
            var changeFolder = (Button)lyricsPage.FindName("ChooseLyricsDirectoryButton");
            Check(openFolder.Focus(), "The folder button should remain keyboard-accessible.");
            Check(openFolder.FocusVisualStyle is not null && !openFolder.IsDefault &&
                !openFolder.Template.Triggers.OfType<Trigger>().Any(trigger => trigger.Property == UIElement.IsKeyboardFocusedProperty),
                "Restoring focus on a settings page must leave the outline to keyboard navigation.");
            Check(openFolder.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)) && changeFolder.IsKeyboardFocused,
                "Tab navigation should move from opening the lyrics folder to changing its location.");
            var box = (TextBox)lyricsPage.FindName("FilterPatternsBox");
            var old = settings.LyricsFilterPatterns;
            box.Text = "first draft";
            box.Text = "second draft";
            Check(settings.LyricsFilterPatterns == old, "Typing must not rebuild lyrics on every keystroke.");
            pump(TimeSpan.FromMilliseconds(450));
            var recoveryToggle = (CheckBox)((FrameworkElement)window.FindName("ExperimentalPanel")).FindName("AutoRepairNetEaseSyncCheckBox");
            recoveryToggle.IsChecked = true;
            Check(settings.AutoRepairNetEaseSync, "The recovery setting must be wired to persistence.");
            Check(settings.NetEaseSyncDelayMilliseconds == 3000
                  && settings.AppleMusicSyncAdvanceMilliseconds == 3000
                  && settings.QqMusicOffsetMilliseconds == -3000
                  && settings.KugouOffsetMilliseconds == 3000
                  && settings.SpotifyOffsetMilliseconds == -3000,
                "Opening settings must preserve all players' full-range values without truncation.");
            (string Name, string Source)[] sliders =
            [
                ("QqMusicOffsetSlider", "QQMusic.exe"),
                ("KugouOffsetSlider", "KuGou.exe"),
                ("SpotifyOffsetSlider", "Spotify.exe")
            ];
            foreach (var item in sliders)
            {
                var slider = (Slider)((FrameworkElement)window.FindName("PlaybackPanel")).FindName(item.Name);
                var before = sliders.Select(entry => settings.GetPlayerLyricOffsetMilliseconds(entry.Source)).ToArray();
                Check(slider.Minimum == -3000 && slider.Maximum == 3000, "All three sliders must expose the full signed range.");
                var target = slider.Value < 0 ? 3000 : -3000;
                slider.Value = target;
                pump(TimeSpan.FromMilliseconds(20));
                Check(settings.GetPlayerLyricOffsetMilliseconds(item.Source) == target, "Slider changes must update the selected player.");
                for (var index = 0; index < sliders.Length; index++)
                    if (sliders[index].Source != item.Source)
                        Check(settings.GetPlayerLyricOffsetMilliseconds(sliders[index].Source) == before[index],
                            "Slider changes must leave every other player unchanged.");
            }
            Check(settings.LyricsFilterPatterns == "second draft", "Debounce must eventually apply the latest edit.");
            box.Text = "final edit";
            window.CommitPendingEdits();
            Check(settings.LyricsFilterPatterns == "final edit", "Closing/exiting before the delay must flush the pending edit.");
        }
        finally { window.ClosePermanently(); }
    }

    private static void VerifySettingsLifetime(AppSettings settings, AppController controller, string root, Action<TimeSpan> pump)
    {
        var references = OpenAndCloseSettings(settings, controller, root);
        for (int attempt = 0; attempt < 8 && references.Any(r => r.IsAlive); attempt++)
        {
            pump(TimeSpan.FromMilliseconds(250));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Check(references.All(r => !r.IsAlive), "Settings references still live after closing: " + string.Join(", ", references.Select(r => r.Target?.GetType().Name).Where(n => n is not null)));
        Console.WriteLine("Settings lifetime passed: all seven pages reused and released on close.");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference[] OpenAndCloseSettings(AppSettings settings, AppController controller, string root)
    {
        var window = new SettingsWindow(new AppViewModel(settings, new AppState()), controller,
            new LyricsCache(Path.Combine(root, "lifetime")), () => { }, () => Task.CompletedTask);
        // A desktop UI Automation client can retain a native TabItemAutomationPeer after Close.
        // Lay out the same navigation tree offscreen to measure references owned by the app.
        var tabs = (TabControl)window.FindName("Navigation");
        var pages = tabs.Items.Cast<TabItem>().Select(t => t.Content).ToArray();
        Check(pages.Length == 7, "Settings must have seven pages.");
        VisualAcceptance.CheckResolutionMatrix(window, tabs);
        for (int pass = 0; pass < 2; pass++)
        {
            for (int index = 0; index < pages.Length; index++)
            {
                tabs.SelectedIndex = index;
                tabs.Measure(new System.Windows.Size(960, 650));
                tabs.Arrange(new Rect(0, 0, 960, 650));
                tabs.UpdateLayout();
                Check(ReferenceEquals(tabs.SelectedContent, pages[index]), "Navigation recreated a settings page.");
            }
        }
        var references = pages.Select(page => new WeakReference(page)).Append(new WeakReference(window)).ToArray();
        window.ClosePermanently();
        return references;
    }

    private static void MeasureSettingsMotion(SettingsWindow window)
    {
        var origin = window.Left;
        var positions = new List<double>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan previousFrame = TimeSpan.MinValue;
        void MoveTestWindow(object? sender, EventArgs args)
        {
            if (args is not System.Windows.Media.RenderingEventArgs e || e.RenderingTime == previousFrame) return;
            previousFrame = e.RenderingTime;
            var ms = clock.Elapsed.TotalMilliseconds;
            positions.Add(ms);
            window.Left = origin + 12 * Math.Sin(ms / 180);
        }
        System.Windows.Media.CompositionTarget.Rendering += MoveTestWindow;
        try { WaitOnDispatcher(TimeSpan.FromSeconds(1.5)); }
        finally
        {
            System.Windows.Media.CompositionTarget.Rendering -= MoveTestWindow;
            window.Left = origin;
        }
        Check(positions.Count > 1, "The native settings window did not render while moving.");
        var gaps = positions.Zip(positions.Skip(1), (before, after) => after - before).Order().ToArray();
        Console.WriteLine($"Settings motion (test window only): {1000 * gaps.Length / gaps.Sum():F1} callbacks/s, p95 interval {gaps[(int)((gaps.Length - 1) * .95)]:F1} ms.");
    }

    private static void MeasureNativeMotion(SettingsWindow window, Action<TimeSpan> pump)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var motion = Task.Run(() =>
        {
            if (!GetWindowRect(handle, out var origin)) throw new InvalidOperationException("Missing test window bounds.");
            // Native title-bar dragging moves an existing compositor surface. Measure that path
            // separately from WPF's frame event and keep all input devices untouched.
            const uint flags = 0x0001 | 0x0004 | 0x0010; // NOSIZE | NOZORDER | NOACTIVATE
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var samples = new List<double>();
            try
            {
                while (clock.Elapsed < TimeSpan.FromSeconds(1.5))
                {
                    int step = samples.Count % 48;
                    int x = origin.Left + (step < 24 ? step : 48 - step);
                    if (!SetWindowPos(handle, 0, x, origin.Top, 0, 0, flags) || DwmFlush() < 0 ||
                        !GetWindowRect(handle, out var actual) || actual.Left != x)
                        throw new InvalidOperationException("The compositor did not confirm the test window move.");
                    samples.Add(clock.Elapsed.TotalMilliseconds);
                }
            }
            finally { SetWindowPos(handle, 0, origin.Left, origin.Top, 0, 0, flags); }
            Check(samples.Count >= 2, "No native window movement samples.");
            var gaps = samples.Zip(samples.Skip(1), (a, b) => b - a).Order().ToArray();
            Console.WriteLine($"Native window movement, DWM-synchronized: {1000 * gaps.Length / gaps.Sum():F1} moves/s, p95 {gaps[(int)((gaps.Length - 1) * .95)]:F1} ms.");
        });
        PumpUntil(motion, pump);
        motion.GetAwaiter().GetResult();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    private static void MeasureRendering(OverlayWindow window)
    {
        var samples = new HashSet<TimeSpan>();
        void Sample(object? sender, EventArgs args)
        {
            if (args is System.Windows.Media.RenderingEventArgs rendering) samples.Add(rendering.RenderingTime);
        }
        var mode = LyricsXQuiet.Platform.Windows.Interop.DisplayMetrics.ReadMode(new System.Windows.Interop.WindowInteropHelper(window).Handle);
        System.Windows.Media.CompositionTarget.Rendering += Sample;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try { WaitOnDispatcher(TimeSpan.FromSeconds(2)); }
        finally { System.Windows.Media.CompositionTarget.Rendering -= Sample; }
        Console.WriteLine($"Active display {mode.Width}x{mode.Height} @ {mode.Frequency} Hz; WPF composition callbacks {samples.Count / watch.Elapsed.TotalSeconds:F1}/s.");
    }

    private static void MeasureIdle()
    {
        WaitOnDispatcher(TimeSpan.FromMilliseconds(700));
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var initial = process.TotalProcessorTime;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        WaitOnDispatcher(TimeSpan.FromSeconds(4));
        process.Refresh();
        var oneCorePercent = (process.TotalProcessorTime - initial).TotalSeconds / watch.Elapsed.TotalSeconds * 100;
        Console.WriteLine($"Paused UI test process CPU: {oneCorePercent:F2}% of one logical core; {oneCorePercent / Environment.ProcessorCount:F2}% of total CPU.");
    }

    private static void WaitOnDispatcher(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void PumpUntil(Task task, Action<TimeSpan> pump)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) pump(TimeSpan.FromMilliseconds(20));
        Check(task.IsCompleted, "Asynchronous UI operation did not finish.");
        task.GetAwaiter().GetResult();
    }

    private static PlaybackSnapshot Snapshot(TrackIdentity track, PlaybackState state) =>
        new(track, state, TimeSpan.FromSeconds(2), 1, DateTimeOffset.Now, PlaybackTimelineKind.Reported);
    private static string CurrentText(OverlayWindow window) => ((TextBlock)window.FindName("CurrentBaseText")).Text;
    private static T ReadField<T>(object target, string field) => (T)target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class MutablePlayback : IPlaybackService
    {
        public event EventHandler<PlaybackSnapshot?>? SnapshotChanged;
        public event EventHandler<PlaybackConnectionStatus>? ConnectionStatusChanged { add { } remove { } }
        public PlaybackSnapshot? Current { get; private set; }
        public PlaybackConnectionStatus ConnectionStatus => PlaybackConnectionStatus.Waiting;
        public void Publish(PlaybackSnapshot? snapshot) { Current = snapshot; SnapshotChanged?.Invoke(this, snapshot); }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SwitchingProvider : ILyricsProvider
    {
        private readonly TaskCompletionSource<IReadOnlyList<LyricsCandidate>> _delayed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool DelayNext { get; set; }
        public string Name => "UI test source";
        public Task<LyricDocument?> FindAsync(TrackIdentity track, CancellationToken cancellationToken = default) => Task.FromResult<LyricDocument?>(null);
        public Task<IReadOnlyList<LyricsCandidate>> SearchAsync(TrackIdentity track, CancellationToken cancellationToken = default)
        {
            if (DelayNext) { DelayNext = false; return _delayed.Task; }
            return Task.FromResult<IReadOnlyList<LyricsCandidate>>([Candidate(track)]);
        }
        public void CompleteDelayed(TrackIdentity track) => _delayed.TrySetResult([Candidate(track)]);
        private LyricsCandidate Candidate(TrackIdentity track) => new(track.Title, Name, track.Title, track.Artist,
            track.Album, track.Duration, 0, LrcParser.Parse($"[00:00.00]{track.Title} lyric", Name));
    }
}
