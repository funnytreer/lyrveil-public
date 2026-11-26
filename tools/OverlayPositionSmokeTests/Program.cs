using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using LyricsXQuiet.App.Services;
using LyricsXQuiet.App.State;
using LyricsXQuiet.App.Windows;
using LyricsXQuiet.Core.Lyrics;
using LyricsXQuiet.Core.Lyrics.Providers;
using LyricsXQuiet.Core.Playback;
using LyricsXQuiet.Core.Settings;

namespace LyricsXQuiet.Tools.OverlayPositionSmokeTests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var application = new Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/LyricsXQuiet.App;component/Themes/ThemeLight.xaml", UriKind.Relative) });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/LyricsXQuiet.App;component/Themes/SharedStyles.xaml", UriKind.Relative)
        });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/LyricsXQuiet.App;component/Themes/SettingsStyles.xaml", UriKind.Relative) });
        if (args.Contains("--netease-calibration-only", StringComparer.Ordinal))
        {
            // Validate bindings and persistence without showing any window or
            // connecting to the user's running players.
            try
            {
                ReviewUiRegressionTests.CheckNetEaseCalibration(PumpFor);
                Console.WriteLine("NetEase calibration controls passed without showing windows.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }
        var settings = new AppSettings
        {
            ShowLyrics = true,
            Locked = true,
            DoubleLine = true,
            CenterAlignedDragging = false,
            FontSize = 24
        };
        var window = new OverlayWindow(settings);
        application.MainWindow = window;
        Exception? failure = null;

        application.Startup += (_, _) =>
        {
            _ = window.Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() =>
                {
                    try
                    {
                        VerifyTheme(application);
                        VerifyTrayMenu();
                        VerifyGlobalHotKeys();
                        RunScenarios(window, settings);
                        RunTrackChangeLoadingPresentationScenario(window, settings);
                        VerifyOverlayStyles(window, settings);
                        RunQuickPanelPickerScenario(window, settings);
                        ReviewUiRegressionTests.Run(PumpFor);
                        ReviewUiRegressionTests.CheckNetEaseCalibration(PumpFor);
                        Console.WriteLine("Overlay and quick-panel layout smoke tests passed.");
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                    finally
                    {
                        window.Close();
                    }
                }));
        };

        _ = application.Run();
        if (failure is null)
        {
            return 0;
        }

        Console.Error.WriteLine(failure);
        return 1;
    }

    private static void VerifyGlobalHotKeys()
    {
        var firstSettings = new AppSettings();
        string? received = null;
        var first = new AppHotKeys(firstSettings, action => received = action);
        using var second = new AppHotKeys(new AppSettings(), _ => { });
        var candidates = Enumerable.Range(13, 12).Select(n => $"Ctrl+Shift+Alt+F{n}").ToArray();
        string gesture;
        try
        {
            gesture = candidates.First(key => first.Set("ToggleLyrics", key, out _));
            var spare = candidates.First(key => key != gesture && second.Set("OpenSearch", key, out _));
            Assert(!second.Set("OpenSearch", gesture, out _) && !first.Set("ToggleLock", spare, out _),
                "系统冲突必须保留两个窗口原先的绑定");
            Assert(!first.Set("ToggleLock", gesture, out _) && !first.Set("ToggleLock", "Q", out _),
                "重复组合和普通单键应被拒绝");
            var source = (System.Windows.Interop.HwndSource)typeof(AppHotKeys).GetField("_source",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(first)!;
            Assert(PostMessage(source.Handle, 0x0312, 1, 0), "无法向测试窗口发送快捷键消息");
            PumpFor(TimeSpan.FromMilliseconds(60));
            Assert(received == "ToggleLyrics", "Windows 快捷键消息应触发所绑定的操作");
        }
        finally { first.Dispose(); }
        Assert(second.Set("ToggleLyrics", gesture, out _), "退出后的组合应立即可重新注册");
        Console.WriteLine("Global shortcuts passed: registration, local/system conflicts, preserved binding, dispatch, disposal.");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(nint window, uint message, nint key, nint data);

    private static void VerifyOverlayStyles(OverlayWindow window, AppSettings settings)
    {
        var saved = settings.CaptureAppearance("原设置");
        var wasLocked = settings.Locked;
        var main = (TextBlock)window.FindName("CurrentBaseText");
        var secondary = (TextBlock)window.FindName("SecondaryText");
        var progress = (TextBlock)window.FindName("CurrentProgressText");
        var tools = (Border)window.FindName("EditingTools");
        try
        {
            settings.ApplyAppearance(AppearancePresets.BuiltIn[0]);
            Assert(main.FontWeight == FontWeights.Normal && secondary.FontSize == 24 && secondary.Opacity == 1
                && progress.Effect is System.Windows.Media.Effects.DropShadowEffect { BlurRadius: 12, Opacity: .55 }
                && main.Effect is null, "经典原版应保留等大双行及独立进度微光");
            var clear = AppearancePresets.BuiltIn.Single(p => p.Name == "无界清墨");
            settings.ApplyAppearance(clear);
            Assert(((OutlinedLyricText)window.FindName("CurrentBaseTextOutline")).Outline
                && ((OutlinedLyricText)window.FindName("SecondaryTextOutline")).Outline
                && settings.BackgroundOpacity == 0 && secondary.FontSize == settings.FontSize * .8,
                "透明预设应给两行文字描边");
            window.SetFrame(new LyricFrame("中文歌词", "第二句歌词", .55, true, 104));
            window.SetContentVisible(true);
            PumpFor(TimeSpan.FromMilliseconds(240));
            var ink = VisualAcceptance.DarkPixelsOnWhite((FrameworkElement)window.Content);
            settings.TextBackingStyle = "None";
            PumpFor(TimeSpan.FromMilliseconds(50));
            Assert(ink > VisualAcceptance.DarkPixelsOnWhite((FrameworkElement)window.Content) + 10,
                "白底上的透明歌词必须渲染真实的深色描边");
            settings.Locked = false;
            Assert(tools.Visibility == Visibility.Visible, "解锁时应出现位置操作");
            ((Button)((StackPanel)tools.Child).Children[2]).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert(settings.Locked && tools.Visibility == Visibility.Collapsed, "完成并锁定应隐藏编辑控件");
        }
        finally { settings.ApplyAppearance(saved); settings.Locked = wasLocked; }
    }

    private static void VerifyTrayMenu()
    {
        using var menu = new TrayContextMenu(new AppSettings(), () => { }, () => { }, () => { });
        var root = (FrameworkElement)menu.Content;
        root.Measure(new Size(menu.Width, double.PositiveInfinity));
        root.Arrange(new Rect(0, 0, menu.Width, root.DesiredSize.Height));
        root.UpdateLayout();
        var card = (Border)((Grid)root).Children[1];
        Assert(Math.Abs(card.ActualWidth - 216) <= 1, "托盘菜单主体应为 216 DIP 宽");
        var rows = FindVisualChild<StackPanel>(root)!.Children.OfType<ButtonBase>().ToArray();
        Assert(rows.Length == 5 && rows.All(row => row.Height == 35 && row.FontWeight == FontWeights.Normal),
            "托盘菜单应包含五个等高的常规字重菜单项");
        foreach (var row in rows)
        {
            var icon = ((Grid)row.Content).Children.OfType<System.Windows.Shapes.Path>().First();
            Assert(icon.Data is not null && icon.Width == 15 && icon.Height == 15, "托盘图标应为 15 DIP 矢量图形");
        }
        VisualAcceptance.Capture(root, "tray-menu");
    }

    private static void VerifyTheme(Application app)
    {
        var preferences = new AppSettings { UiThemeMode = "Light" };
        using var theme = new ThemeManager(app, preferences);
        var button = new Button { Style = (Style)app.FindResource("SettingsButton") };
        var preview = new Window { Content = button, Width = 160, Height = 80, ShowInTaskbar = false, ShowActivated = false };
        preview.Show();
        try
        {
            var original = app.Resources.MergedDictionaries.Count;
            foreach (var (mode, color) in new[] { ("Light", "#FF141917"), ("Dark", "#FFEBF0EE"), ("Light", "#FF141917") })
            {
                preferences.UiThemeMode = mode;
                PumpFor(TimeSpan.FromMilliseconds(20));
                var brush = (System.Windows.Media.SolidColorBrush)button.Foreground;
                Assert(brush.Color.ToString() == color && brush.IsFrozen,
                    $"主题 {mode} 应使用 {color}，实际为 {brush.Color}，画刷冻结状态 {brush.IsFrozen}");
                Assert(app.Resources.MergedDictionaries.Count == original, "切换主题不应不断添加资源字典");
            }
        }
        finally { preview.Close(); }
    }

    private static void RunScenarios(OverlayWindow window, AppSettings settings)
    {
        window.SetFrame(new LyricFrame(
            "May nararamdaman din ako 'di kasi manhid na tulad mo",
            "我对你的感觉是真实的 而你在感情上却很麻木",
            0.45,
            true,
            1));
        window.SetContentVisible(true);
        PumpFor(TimeSpan.FromMilliseconds(300));
        var longCenter = GetCenterX(window);

        window.SetFrame(new LyricFrame(
            "词间",
            string.Empty,
            0.5,
            true,
            2));
        PumpFor(TimeSpan.FromMilliseconds(300));
        var shortCenter = GetCenterX(window);

        window.SetFrame(new LyricFrame(
            "Pa'no naman ako nahulog na sa 'yo",
            "那我呢 我已经爱上你了",
            0.65,
            true,
            3));
        PumpFor(TimeSpan.FromMilliseconds(300));
        var doubleLineCenter = GetCenterX(window);

        AssertNear(longCenter, shortCenter, "long-to-short center");
        AssertNear(longCenter, doubleLineCenter, "single-to-double-line center");

        settings.DoubleLine = false;
        window.SetFrame(new LyricFrame(
            "Track title",
            "Track artist",
            0,
            false,
            int.MinValue,
            LyricFrameKind.TrackInfo));
        PumpFor(TimeSpan.FromMilliseconds(120));
        var trackInfoSecondary = (TextBlock)window.FindName("SecondaryText");
        var progressClip = (Border)window.FindName("CurrentProgressClip");
        Assert(trackInfoSecondary.Visibility == Visibility.Visible,
            "歌曲信息模式应始终显示歌手，不受双行歌词开关影响");
        Assert(progressClip.Visibility == Visibility.Collapsed,
            "歌曲信息模式不应显示歌词进度遮罩");

        window.SetFrame(new LyricFrame(
            "First lyric line",
            string.Empty,
            0,
            false,
            0));
        window.UpdateLayout();
        var lyricStack = (StackPanel)window.FindName("LyricStack");
        Assert(Math.Abs(lyricStack.Opacity - 1) < 0.001,
            "歌曲信息切换到第一句歌词时不应通过淡入动画造成视觉延迟");
        Assert(progressClip.Visibility == Visibility.Visible,
            "进入第一句歌词后应恢复歌词进度遮罩");
        settings.DoubleLine = true;

        settings.CenterAlignedDragging = true;
        PumpFor(TimeSpan.FromMilliseconds(200));
        var centeredModeAnchor = GetCenterX(window);

        window.SetFrame(new LyricFrame(
            "This deliberately much longer lyric must keep the exact same screen center",
            string.Empty,
            0.2,
            true,
            4));
        PumpFor(TimeSpan.FromMilliseconds(300));
        AssertNear(centeredModeAnchor, GetCenterX(window), "center-aligned mode center");

        VerifyVerticalEdgePlacement(window);
        VerifyTaskbarSnapGeometry();
        VerifyAdaptiveBackgroundWidth(window, settings);
        VerifyDefaultTaskbarPlacement(window, settings);
    }

    private static void VerifyAdaptiveBackgroundWidth(
        OverlayWindow window,
        AppSettings settings)
    {
        settings.DoubleLine = false;
        window.SetFrame(new LyricFrame(
            "This deliberately long lyric line should expand its background naturally",
            string.Empty,
            0.5,
            true,
            1));
        PumpFor(TimeSpan.FromMilliseconds(180));
        var longWidth = window.ActualWidth;

        window.SetFrame(new LyricFrame(
            "短句",
            string.Empty,
            0.5,
            true,
            1));
        PumpFor(TimeSpan.FromMilliseconds(180));
        var shortWidth = window.ActualWidth;

        Assert(shortWidth < longWidth * 0.65,
            "短歌词背景应随当前文字收紧，不能保留固定的大块最小宽度");
        Assert(shortWidth < 180,
            "短歌词背景只应包含文字和两侧留白");
    }

    private static void VerifyDefaultTaskbarPlacement(
        OverlayWindow window,
        AppSettings settings)
    {
        var workArea = GetCursorWorkingArea();
        window.ResetToDefaultPosition();
        PumpFor(TimeSpan.FromMilliseconds(120));
        AssertNear(
            workArea.Bottom,
            window.Top + window.ActualHeight,
            "default taskbar-edge placement");
        Assert(settings.OverlayDockedToTaskbar == true,
            "默认位置应记住贴靠任务栏，而不是只保存一次性坐标");

        var previousDoubleLine = settings.DoubleLine;
        settings.DoubleLine = false;
        window.SetFrame(new LyricFrame(
            "Single line after restart",
            string.Empty,
            0.5,
            true,
            1));
        PumpFor(TimeSpan.FromMilliseconds(180));
        AssertNear(
            workArea.Bottom,
            window.Top + window.ActualHeight,
            "taskbar attachment after shrinking to one line");

        settings.DoubleLine = true;
        window.SetFrame(new LyricFrame(
            "First line after restart",
            "Second line changes the window height",
            0.5,
            true,
            1));
        PumpFor(TimeSpan.FromMilliseconds(180));
        AssertNear(
            workArea.Bottom,
            window.Top + window.ActualHeight,
            "taskbar attachment after expanding to two lines");
        settings.DoubleLine = previousDoubleLine;
    }

    private static void VerifyVerticalEdgePlacement(Window window)
    {
        var workArea = GetCursorWorkingArea();
        window.UpdateLayout();

        window.Top = workArea.Top - 80;
        ClampCenteredOverlayToWorkingArea(window);
        PumpFor(TimeSpan.FromMilliseconds(80));
        AssertNear(workArea.Top, window.Top, "top working-area edge");

        window.Top = workArea.Bottom + 80;
        ClampCenteredOverlayToWorkingArea(window);
        PumpFor(TimeSpan.FromMilliseconds(80));
        AssertNear(
            workArea.Bottom,
            window.Top + window.ActualHeight,
            "bottom working-area edge");
    }

    private static void ClampCenteredOverlayToWorkingArea(Window window)
    {
        var type = typeof(QuickPanelWindow).Assembly.GetType(
            "LyricsXQuiet.App.Services.WindowPlacementService",
            throwOnError: true)!;
        var method = type.GetMethod(
            "CenterHorizontallyOnCurrentMonitor",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        _ = method.Invoke(null, [window, 24d, 0d, false]);
    }

    private static void VerifyTaskbarSnapGeometry()
    {
        var type = typeof(QuickPanelWindow).Assembly.GetType(
            "LyricsXQuiet.App.Services.WindowPlacementService",
            throwOnError: true)!;
        var method = type.GetMethod(
            "CalculateVerticalDragY",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var nearTaskbar = InvokeVerticalDrag(method, 695, false);
        Assert(nearTaskbar == (700, true),
            "歌词下边缘接近任务栏上边缘时应吸附");

        var heldByHysteresis = InvokeVerticalDrag(method, 718, true);
        Assert(heldByHysteresis == (700, true),
            "已吸附状态应使用更宽的释放距离，避免边缘抖动");

        var releasedBelowTaskbar = InvokeVerticalDrag(method, 730, true);
        Assert(releasedBelowTaskbar == (730, false),
            "继续向下拖超过释放距离后应进入任务栏区域");

        var physicalBottom = InvokeVerticalDrag(method, 980, false);
        Assert(physicalBottom == (900, false),
            "歌词应能移动到物理屏幕最下方");

        var noBottomTaskbar = InvokeVerticalDrag(method, 795, false, workAreaBottom: 1000);
        Assert(noBottomTaskbar == (795, false),
            "没有底部任务栏时不应产生虚假的吸附点");
    }

    private static (int Y, bool Snapped) InvokeVerticalDrag(
        System.Reflection.MethodInfo method,
        int requestedY,
        bool wasSnapped,
        int workAreaBottom = 800)
    {
        var result = method.Invoke(
            null,
            [requestedY, 100, 0, 1000, workAreaBottom, 0, 12, 24, wasSnapped])!;
        var resultType = result.GetType();
        return (
            (int)resultType.GetProperty("Y")!.GetValue(result)!,
            (bool)resultType.GetProperty("SnappedToTaskbar")!.GetValue(result)!);
    }

    private static double GetCenterX(Window window)
    {
        window.UpdateLayout();
        return window.Left + (window.ActualWidth / 2);
    }

    private static void AssertNear(double expected, double actual, string scenario)
    {
        if (Math.Abs(expected - actual) > 1)
        {
            throw new InvalidOperationException(
                $"{scenario} drifted by {Math.Abs(expected - actual):F2} DIPs "
                + $"(expected {expected:F2}, actual {actual:F2}).");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RunQuickPanelPickerScenario(OverlayWindow overlay, AppSettings settings)
    {
        var state = new AppState
        {
            TrackDisplay = "测试歌曲 · 测试歌手",
            PlayerDisplay = "Spotify · 系统媒体会话",
            LyricsStatus = "正在检查候选歌词"
        };
        var playback = new StubPlaybackService();
        var resolver = new LyricsResolver(
            new LyricsCache(Path.Combine(Path.GetTempPath(), "LyrveilQuickPanelSmoke")),
            Array.Empty<ILyricsProvider>());
        using var controller = new AppController(playback, resolver, settings, state, overlay);
        var originalAppearance = settings.CaptureAppearance("test");
        settings.ApplyAppearance(AppearancePresets.Veil);
        var panel = new QuickPanelWindow(new AppViewModel(settings, state), controller, () => { });

        try
        {
            var presets = ((UniformGrid)panel.FindName("Presets")).Children.OfType<RadioButton>().ToArray();
            Assert(presets.Length == 5 && presets.Single(item => item.IsChecked == true).Content.Equals(AppearancePresets.Veil.Name),
                "首次打开面板时，应只选中与当前外观匹配的预设");
            settings.FontSize++;
            Assert(presets.All(item => item.IsChecked != true), "自定义外观不应误标为内置预设");
            settings.ApplyAppearance(AppearancePresets.Veil);
            panel.ShowPanel();
            PumpFor(TimeSpan.FromMilliseconds(250));
            var collapsedHeight = panel.ActualHeight;
            VisualAcceptance.Capture((FrameworkElement)panel.Content, "quick-panel-collapsed");
            var playerScope = (RadioButton)panel.FindName("PlayerScope");
            var trackScope = (RadioButton)panel.FindName("TrackScope");
            var playerControls = (Grid)panel.FindName("PlayerOffsetPanel");
            var trackControls = (Grid)panel.FindName("TrackCalibrationPanel");
            Assert(trackControls.IsVisible && !playerControls.IsVisible, "快速调节默认显示单曲校准");
            playerScope.IsChecked = true;
            playerScope.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert(playerControls.IsVisible && !trackControls.IsVisible, "切换播放器作用域时应只显示播放器微调");
            trackScope.IsChecked = true;
            trackScope.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            var toggle = (ToggleButton)panel.FindName("LyricsPickerToggle");
            var list = (ListBox)panel.FindName("LyricsCandidatesList");
            var scroller = (ScrollViewer)panel.FindName("PanelScroller");
            toggle.IsChecked = true;
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            list.ItemsSource = Enumerable.Range(1, 16)
                .Select(index => new PickerTestRow(
                    $"候选歌词 {index}",
                    "测试歌手 · 03:45",
                    "测试专辑 · 测试来源",
                    index % 2 == 0 ? "逐字" : "逐行"))
                .ToArray();
            PumpFor(TimeSpan.FromMilliseconds(450));
            panel.UpdateLayout();

            VisualAcceptance.Capture((FrameworkElement)panel.Content, "quick-panel");
            var workArea = GetCursorWorkingArea();
            if (panel.ActualHeight > panel.MaxHeight + 1)
            {
                throw new InvalidOperationException(
                    $"Expanded quick panel exceeded MaxHeight: {panel.ActualHeight:F1} > {panel.MaxHeight:F1}.");
            }

            if (panel.Top < workArea.Top + 15
                || panel.Top + panel.ActualHeight > workArea.Bottom - 15)
            {
                throw new InvalidOperationException(
                    $"Expanded quick panel left the working area: top={panel.Top:F1}, "
                    + $"bottom={panel.Top + panel.ActualHeight:F1}, work={workArea}.");
            }

            if (scroller.ScrollableHeight > 1)
            {
                throw new InvalidOperationException(
                    $"The whole quick panel scrolls by {scroller.ScrollableHeight:F1} DIPs; "
                    + "the candidate list should absorb normal overflow.");
            }

            var candidateScroller = FindVisualChild<ScrollViewer>(list);
            if (candidateScroller is null || candidateScroller.ScrollableHeight <= 0)
            {
                throw new InvalidOperationException(
                    "The expanded candidate list did not expose its own vertical scrolling.");
            }

            var candidateScrollBar = FindVisualChild<ScrollBar>(
                list,
                scrollBar => scrollBar.Orientation == Orientation.Vertical
                    && scrollBar.Visibility == Visibility.Visible);
            if (candidateScrollBar is null)
            {
                throw new InvalidOperationException(
                    "The candidate list did not expose a vertical scrollbar.");
            }

            var candidateTrack = candidateScrollBar.Template.FindName(
                "PART_Track",
                candidateScrollBar) as Track;
            if (candidateTrack?.Thumb is null || candidateTrack.Thumb.ActualWidth > 6 || candidateScrollBar.ActualWidth > 6)
            {
                throw new InvalidOperationException(
                    $"Candidate scrollbar width {candidateScrollBar.ActualWidth:F2} (requested {candidateScrollBar.Width}, min {candidateScrollBar.MinWidth}, max {candidateScrollBar.MaxWidth}), thumb {candidateTrack?.Thumb?.ActualWidth:F2}; expected at most 6 DIPs.");
            }

            toggle.IsChecked = false;
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpFor(TimeSpan.FromMilliseconds(300));
            if (Math.Abs(panel.ActualHeight - collapsedHeight) > 1)
            {
                throw new InvalidOperationException(
                    $"Collapsed quick panel did not restore its height: "
                    + $"{panel.ActualHeight:F1} vs {collapsedHeight:F1}.");
            }

            var dismissal = typeof(QuickPanelWindow).GetField("_dismissal",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;
            var modal = dismissal.GetType().GetProperty("ModalOpen")!;
            var deactivate = typeof(Window).GetMethod("OnDeactivated", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!;
            modal.SetValue(dismissal, true);
            deactivate.Invoke(panel, [EventArgs.Empty]);
            Assert(panel.IsVisible, "导入对话框打开时应保留快速面板");
            modal.SetValue(dismissal, false);
            deactivate.Invoke(panel, [EventArgs.Empty]);
            Assert(!panel.IsVisible, "点击外部后应收起快速面板");
            panel.TogglePanel();
            Assert(!panel.IsVisible, "刚收起时应忽略重复托盘点击");
        }
        finally
        {
            panel.ClosePermanently();
            settings.ApplyAppearance(originalAppearance);
            playback.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void RunTrackChangeLoadingPresentationScenario(
        OverlayWindow overlay,
        AppSettings settings)
    {
        settings.ShowTrackInfoWhenNoLyrics = true;
        settings.HideWhenPaused = true;
        var track = new TrackIdentity(
            "切歌测试",
            "测试歌手",
            "测试专辑",
            TimeSpan.FromMinutes(3),
            "spotify");
        var snapshot = new PlaybackSnapshot(
            track,
            PlaybackState.Playing,
            TimeSpan.Zero,
            1,
            DateTimeOffset.Now,
            PlaybackTimelineKind.Reported);
        var provider = new DelayedLyricsProvider(track);
        var cachePath = Path.Combine(
            Path.GetTempPath(),
            $"LyrveilTrackChangeSmoke-{Guid.NewGuid():N}");
        var resolver = new LyricsResolver(
            new LyricsCache(cachePath),
            [provider]);
        var state = new AppState();
        var playback = new StubPlaybackService(snapshot);
        using var controller = new AppController(playback, resolver, settings, state, overlay);

        controller.Start();
        PumpFor(TimeSpan.FromMilliseconds(150));
        Assert(!overlay.IsVisible,
            "切歌后歌词仍在查找时，不应先闪现歌名和歌手");

        provider.Complete();
        PumpFor(TimeSpan.FromMilliseconds(500));
        Assert(overlay.IsVisible,
            "新歌歌词准备好后应显示悬浮歌词");
        var currentText = (TextBlock)overlay.FindName("CurrentBaseText");
        Assert(string.Equals(currentText.Text, "第一句正式歌词", StringComparison.Ordinal),
            "新歌应一次性从隐藏状态切换到正式歌词");
    }


    private static Rect GetCursorWorkingArea()
    {
        var type = typeof(QuickPanelWindow).Assembly.GetType(
            "LyricsXQuiet.App.Services.WindowPlacementService",
            throwOnError: true)!;
        var method = type.GetMethod(
            "GetCursorWorkingAreaInDips",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        return (Rect)method.Invoke(null, null)!;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static T? FindVisualChild<T>(
        DependencyObject parent,
        Func<T, bool> predicate) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match && predicate(match))
            {
                return match;
            }

            var descendant = FindVisualChild(child, predicate);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static void PumpFor(TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(20),
                DispatcherPriority.ApplicationIdle,
                (_, _) => frame.Continue = false,
                Dispatcher.CurrentDispatcher);
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }
    }

    private sealed record PickerTestRow(
        string Title,
        string ArtistAndDuration,
        string AlbumAndProvider,
        string QualitySummary);

    private sealed class StubPlaybackService(PlaybackSnapshot? current = null) : IPlaybackService
    {
        public event EventHandler<PlaybackSnapshot?>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<PlaybackConnectionStatus>? ConnectionStatusChanged
        {
            add { }
            remove { }
        }

        public PlaybackSnapshot? Current => current;

        public PlaybackConnectionStatus ConnectionStatus => PlaybackConnectionStatus.Waiting;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DelayedLyricsProvider(TrackIdentity track) : ILyricsProvider
    {
        private readonly TaskCompletionSource<IReadOnlyList<LyricsCandidate>> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "延迟测试词源";

        public Task<LyricDocument?> FindAsync(
            TrackIdentity ignoredTrack,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<LyricDocument?>(null);

        public Task<IReadOnlyList<LyricsCandidate>> SearchAsync(
            TrackIdentity ignoredTrack,
            CancellationToken cancellationToken = default) =>
            _completion.Task.WaitAsync(cancellationToken);

        public void Complete()
        {
            var document = LrcParser.Parse("[00:00.00]第一句正式歌词", Name);
            _completion.TrySetResult(
            [
                new LyricsCandidate(
                    "delayed-exact",
                    Name,
                    track.Title,
                    track.Artist,
                    track.Album,
                    track.Duration,
                    0,
                    document)
            ]);
        }
    }
}
