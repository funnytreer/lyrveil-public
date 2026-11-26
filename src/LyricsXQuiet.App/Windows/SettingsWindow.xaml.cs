using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LyricsXQuiet.App.Services;
using LyricsXQuiet.App.State;
using LyricsXQuiet.Core.Lyrics;
using LyricsXQuiet.Core.Playback;
using LyricsXQuiet.Core.Settings;
using LyricsXQuiet.Platform.Windows.Companion;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace LyricsXQuiet.App.Windows;

public partial class SettingsWindow : Window
{
    private readonly Action _resetLyricsPosition;
    private readonly Func<Task> _saveSettingsAsync;
    private readonly AppController _controller;
    internal AppController Controller => _controller;
    private readonly LyricsCache _lyricsCache;
    private bool _allowClose;
    private bool _updatingPlayerFollowMode;

    public SettingsWindow(
        AppViewModel viewModel,
        AppController controller,
        LyricsCache lyricsCache,
        Action resetLyricsPosition,
        Func<Task> saveSettingsAsync)
    {
        _resetLyricsPosition = resetLyricsPosition;
        _saveSettingsAsync = saveSettingsAsync;
        _controller = controller;
        _lyricsCache = lyricsCache;
        DataContext = viewModel;
        InitializeComponent();
        GeneralPanel.DataContext = viewModel;
        GeneralPanel.Connect(this);
        PlaybackPanel.DataContext = viewModel;
        PlaybackPanel.Connect(this);
        DisplayPanel.DataContext = viewModel;
        DisplayPanel.Connect(this);
        LyricsPanel.DataContext = viewModel;
        LyricsPanel.Connect(this);
        ExperimentalPanel.DataContext = viewModel;
        ExperimentalPanel.Connect(this);
        ShortcutsPanel.DataContext = viewModel;
        ShortcutsPanel.Connect(this);
        AboutPanel.DataContext = viewModel;
        AboutPanel.Connect(this);
        ThemeManager.AttachChrome(this);
        Loaded += OnLoaded;
        Closing += OnClosing;
        viewModel.Settings.PropertyChanged += OnFilterSettingsChanged;
        Closed += (_, _) =>
        {
            viewModel.Settings.PropertyChanged -= OnFilterSettingsChanged;
            foreach (TabItem item in Navigation.Items)
            {
                if (item.Content is FrameworkElement page) page.DataContext = null;
                item.Content = null;
            }
            Navigation.Items.Clear();
            DataContext = null;
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        FitToWorkingArea();
        UpdatePlayerFollowModeControls();
    }

    private void FitToWorkingArea()
    {
        const double outerMargin = 24;
        ConstrainSize(WindowPlacementService.GetCursorWorkingAreaInDips());
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render,
            new Action(() => { if (IsVisible) WindowPlacementService.CenterOnCursorMonitor(this, outerMargin); }));
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.InvokeAsync(() => ConstrainSize(WindowPlacementService.GetWindowWorkingAreaInDips(this)));
    }

    internal void ConstrainSize(Rect workArea)
    {
        var availableWidth = Math.Max(1, workArea.Width - 48);
        var availableHeight = Math.Max(1, workArea.Height - 48);

        MinWidth = Math.Min(760, availableWidth);
        MinHeight = Math.Min(540, availableHeight);
        if (WindowState != WindowState.Normal) return;
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);
    }

    public void ShowWindow()
    {
        UpdateFilterValidation();
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        UpdateLayout();
        if (!WindowPlacementService.IsMeaningfullyVisible(this))
        {
            FitToWorkingArea();
        }

        Activate();
    }

    public void ClosePermanently()
    {
        _allowClose = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        CommitPendingEdits();
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        Hide();
    }

    internal void OnLyricColorClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is AppViewModel viewModel)
        {
            PickColor(viewModel.Settings.LyricColor, value => viewModel.Settings.LyricColor = value);
        }
    }

    internal void OnProgressColorClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is AppViewModel viewModel)
        {
            PickColor(viewModel.Settings.ProgressColor, value => viewModel.Settings.ProgressColor = value);
        }
    }

    internal void OnBackgroundColorClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is AppViewModel viewModel)
        {
            PickColor(viewModel.Settings.BackgroundColor, value => viewModel.Settings.BackgroundColor = value);
        }
    }

    internal void OnCopyDiagnosticsClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is not AppViewModel viewModel)
        {
            return;
        }

        var enabledSources = new[]
        {
            viewModel.Settings.EnableLrclibLyrics ? "LRCLIB" : null,
            viewModel.Settings.EnableNetEaseLyrics ? "网易云音乐" : null,
            viewModel.Settings.EnableQqMusicLyrics ? "QQ 音乐" : null,
            viewModel.Settings.EnableKugouLyrics ? "酷狗音乐" : null
        }.Where(source => source is not null);
        var diagnostics = string.Join(
            Environment.NewLine,
            "词间 · Lyrveil 诊断摘要",
            $"应用版本：{viewModel.VersionDisplay}",
            $"系统：{Environment.OSVersion.VersionString}",
            $"运行环境：{RuntimeInformation.FrameworkDescription} / {RuntimeInformation.ProcessArchitecture}",
            $"播放器：{viewModel.State.PlayerDisplay}",
            $"歌词与同步：{viewModel.State.LyricsStatus}",
            $"启用歌词源：{string.Join("、", enabledSources)}",
            $"启动方式：{viewModel.Settings.PlayerFollowMode}",
            $"本地播放器：{GetLocalPlayerDiagnosticValue(viewModel.Settings.LocalPlayerExecutablePath)}",
            $"全局偏移：{viewModel.Settings.OffsetMilliseconds} ms",
            $"网易云默认延后：{viewModel.Settings.NetEaseSyncDelayMilliseconds} ms",
            $"网易云播放器微调：{viewModel.Settings.NetEaseOffsetMilliseconds} ms",
            $"Apple Music 提前：{viewModel.Settings.AppleMusicSyncAdvanceMilliseconds} ms",
            $"单曲校准记录：{viewModel.Settings.TrackLyricOffsetsMilliseconds.Count} 条",
            $"生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

        try
        {
            System.Windows.Clipboard.SetText(diagnostics);
            AboutPanel.DiagnosticsStatusText.Text = "已复制（不含歌曲与歌词内容）";
        }
        catch (ExternalException exception)
        {
            AboutPanel.DiagnosticsStatusText.Text = string.Empty;
            System.Windows.MessageBox.Show(
                $"暂时无法写入剪贴板。\n\n{exception.Message}",
                "词间 · Lyrveil",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    internal void OnResetLyricsPositionClick(object sender, RoutedEventArgs args) =>
        _resetLyricsPosition();

    internal void OnCheckPlayerCompatibilityClick(object sender, RoutedEventArgs args)
    {
        System.Windows.MessageBox.Show(
            this,
            _controller.GetPlaybackCompatibilitySummary(),
            "播放器兼容性检查",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    internal async void OnChooseLocalPlayerClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is not AppViewModel viewModel)
        {
            return;
        }

        var currentPath = viewModel.Settings.LocalPlayerExecutablePath;
        using var dialog = new WinForms.OpenFileDialog
        {
            Title = "选择本地播放器程序",
            Filter = "应用程序 (*.exe)|*.exe",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            InitialDirectory = File.Exists(currentPath)
                ? Path.GetDirectoryName(currentPath)
                : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };
        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        var selectedPath = Path.GetFullPath(dialog.FileName);
        var processName = Path.GetFileNameWithoutExtension(selectedPath);
        var builtInPlayer = MusicPlayerCatalog.SupportedPlayers.FirstOrDefault(player =>
            string.Equals(
                player.ProcessName,
                processName,
                StringComparison.OrdinalIgnoreCase));
        if (builtInPlayer is not null)
        {
            System.Windows.MessageBox.Show(
                $"{builtInPlayer.DisplayName}已经由词间内置支持，不需要再配置为本地播放器。",
                "词间 · Lyrveil",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!MusicPlayerCatalog.IsLocalPlayerExecutableAllowed(selectedPath))
        {
            System.Windows.MessageBox.Show(
                "请选择实际的桌面音乐播放器程序。浏览器和词间自身不能配置为本地播放器。",
                "词间 · Lyrveil",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        viewModel.Settings.LocalPlayerExecutablePath = selectedPath;
        await SaveLocalPlayerSettingAsync();
    }

    internal async void OnClearLocalPlayerClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is not AppViewModel viewModel
            || string.IsNullOrWhiteSpace(viewModel.Settings.LocalPlayerExecutablePath))
        {
            return;
        }

        viewModel.Settings.LocalPlayerExecutablePath = string.Empty;
        await SaveLocalPlayerSettingAsync();
    }

    private async Task SaveLocalPlayerSettingAsync()
    {
        try
        {
            await _saveSettingsAsync();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            System.Windows.MessageBox.Show(
                $"本地播放器已经在本次运行中生效，但暂时无法保存设置。\n\n{exception.Message}",
                "词间 · Lyrveil",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    internal void OnResetLyricsDisplayClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is not AppViewModel viewModel)
        {
            return;
        }

        viewModel.Settings.ResetAppearanceSettings();
        _resetLyricsPosition();
    }

    internal void OnSearchLyricsClick(object sender, RoutedEventArgs args)
    {
        LyricsSearchWindow.Open(_controller, this);
    }

    internal async void OnRetryLyricsClick(object sender, RoutedEventArgs args)
    {
        if (sender is System.Windows.Controls.Button button)
        {
            button.IsEnabled = false;
            try
            {
                await _controller.RetryLyricsAsync();
            }
            finally
            {
                button.IsEnabled = true;
            }
        }
    }

    internal async void OnChooseLyricsDirectoryClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is not AppViewModel viewModel)
        {
            return;
        }

        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "选择用于保存歌词文件的文件夹",
            UseDescriptionForTitle = true,
            InitialDirectory = viewModel.Settings.LyricsDirectory,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            SetLyricsMaintenanceButtonsEnabled(false);
            try
            {
                var result = await _lyricsCache.RelocateAsync(
                    dialog.SelectedPath,
                    directory => Dispatcher.InvokeAsync(
                        () => viewModel.Settings.LyricsDirectory = directory).Task);

                string? settingsSaveError = null;
                if (result.Changed)
                {
                    try
                    {
                        await _saveSettingsAsync();
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException)
                    {
                        settingsSaveError = exception.Message;
                    }
                }

                if (!result.Changed)
                {
                    return;
                }

                if (result.SourceRemoved)
                {
                    var detail = result.FileCount == 0
                        ? "歌词文件夹位置已更改，旧文件夹已清理。"
                        : $"已迁移 {result.FileCount} 个文件，旧文件夹已完整清理。";
                    if (settingsSaveError is not null)
                    {
                        detail += $"\n\n新位置正在使用，但设置暂时无法写入磁盘：\n{settingsSaveError}";
                    }

                    System.Windows.MessageBox.Show(
                        detail,
                        "词间 · Lyrveil",
                        MessageBoxButton.OK,
                        settingsSaveError is null
                            ? MessageBoxImage.Information
                            : MessageBoxImage.Warning);
                }
                else
                {
                    var detail = result.SourceRetainedForSafety
                        ? $"歌词已复制到新位置。\n\n{result.CleanupError}\n\n原位置：\n{result.SourceDirectory}"
                        : $"歌词已完整复制到新位置，但旧文件夹正被其他程序使用，暂时无法完全清理。\n\n" +
                          $"请关闭正在使用它的程序后删除：\n{result.SourceDirectory}\n\n{result.CleanupError}";
                    System.Windows.MessageBox.Show(
                        detail +
                        (settingsSaveError is null
                            ? string.Empty
                            : $"\n\n设置暂时无法写入磁盘：\n{settingsSaveError}"),
                        "词间 · Lyrveil",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                System.Windows.MessageBox.Show(
                    $"无法迁移歌词文件夹，原位置没有改变。\n\n{exception.Message}",
                    "词间 · Lyrveil",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                SetLyricsMaintenanceButtonsEnabled(true);
            }
        }
    }

    internal async void OnOpenLyricsDirectoryClick(object sender, RoutedEventArgs args)
    {
        SetLyricsMaintenanceButtonsEnabled(false);
        try
        {
            var directory = await _lyricsCache.EnsureDirectoryAsync();
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(directory);
            _ = Process.Start(startInfo);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
        {
            System.Windows.MessageBox.Show(
                $"无法打开歌词文件夹。\n\n{exception.Message}",
                "词间 · Lyrveil",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            SetLyricsMaintenanceButtonsEnabled(true);
        }
    }

    internal async void OnOrganizeLyricsCacheClick(object sender, RoutedEventArgs args)
    {
        SetLyricsMaintenanceButtonsEnabled(false);
        try
        {
            var plan = await _lyricsCache.ScanCleanupAsync();
            if (plan.Items.Count == 0)
            {
                System.Windows.MessageBox.Show(
                    this,
                    $"已扫描 {plan.ScannedFileCount} 个 LRC 文件，没有发现可安全整理的多余缓存。\n\n"
                    + "手选、导入、不同专辑与 Live/演唱会版本均保持不变。",
                    "整理歌词缓存",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var reasonSummary = string.Join(
                Environment.NewLine,
                plan.Items
                    .GroupBy(item => item.ReasonText)
                    .Select(group => $"• {group.Key}：{group.Count()} 个"));
            var filePreview = string.Join(
                Environment.NewLine,
                plan.Items.Take(8).Select(item => $"• {item.FileName}"));
            if (plan.Items.Count > 8)
            {
                filePreview += $"\n…还有 {plan.Items.Count - 8} 个";
            }

            var confirmation = System.Windows.MessageBox.Show(
                this,
                $"已扫描 {plan.ScannedFileCount} 个 LRC 文件，其中 {plan.ManagedFileCount} 个可确认为词间缓存。\n\n"
                + $"本次可整理 {plan.Items.Count} 个，共 {FormatFileSize(plan.TotalBytes)}：\n"
                + $"{reasonSummary}\n\n{filePreview}\n\n"
                + "确认后文件会移入可恢复备份，不会永久删除。\n"
                + "手选/导入歌词、不同专辑与 Live/演唱会版本不会被合并。",
                "整理歌词缓存",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            var result = await _lyricsCache.ApplyCleanupAsync(plan);
            if (result.MovedFileCount == 0)
            {
                System.Windows.MessageBox.Show(
                    this,
                    "扫描后文件发生了变化，本次没有移动任何歌词。请稍后重新整理。",
                    "整理歌词缓存",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var detail = $"已整理 {result.MovedFileCount} 个文件，共 {FormatFileSize(result.MovedBytes)}。\n\n"
                + "它们仍保留在可恢复备份中，如果发现误判，可使用“恢复最近整理的文件…”。";
            if (result.SkippedFileCount > 0)
            {
                detail += $"\n\n有 {result.SkippedFileCount} 个文件在扫描后发生变化或正被使用，已安全跳过。";
            }

            System.Windows.MessageBox.Show(
                this,
                detail,
                "整理歌词缓存",
                MessageBoxButton.OK,
                result.SkippedFileCount == 0
                    ? MessageBoxImage.Information
                    : MessageBoxImage.Warning);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            System.Windows.MessageBox.Show(
                this,
                $"无法整理歌词缓存。\n\n{exception.Message}",
                "整理歌词缓存",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            SetLyricsMaintenanceButtonsEnabled(true);
        }
    }

    internal async void OnRestoreLyricsCacheClick(object sender, RoutedEventArgs args)
    {
        SetLyricsMaintenanceButtonsEnabled(false);
        try
        {
            var plan = await _lyricsCache.GetLatestCleanupBackupAsync();
            if (plan is null)
            {
                System.Windows.MessageBox.Show(
                    this,
                    "没有找到可恢复的整理备份。",
                    "恢复歌词缓存",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var confirmation = System.Windows.MessageBox.Show(
                this,
                $"最近一次整理时间：{plan.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n"
                + $"可恢复 {plan.Items.Count} 个文件，共 {FormatFileSize(plan.TotalBytes)}。\n\n"
                + "恢复不会覆盖当前歌词文件；如果出现同名文件，将保留备份并跳过。\n\n是否继续？",
                "恢复歌词缓存",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            var result = await _lyricsCache.RestoreCleanupBackupAsync(plan);
            var detail = $"已恢复 {result.RestoredFileCount} 个歌词文件。";
            if (result.ConflictFileCount > 0)
            {
                detail += $"\n{result.ConflictFileCount} 个同名文件未被覆盖，备份仍然保留。";
            }

            if (result.MissingFileCount > 0)
            {
                detail += $"\n{result.MissingFileCount} 个备份文件已不存在。";
            }

            System.Windows.MessageBox.Show(
                this,
                detail,
                "恢复歌词缓存",
                MessageBoxButton.OK,
                result.ConflictFileCount == 0 && result.MissingFileCount == 0
                    ? MessageBoxImage.Information
                    : MessageBoxImage.Warning);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            System.Windows.MessageBox.Show(
                this,
                $"无法恢复歌词缓存。\n\n{exception.Message}",
                "恢复歌词缓存",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            SetLyricsMaintenanceButtonsEnabled(true);
        }
    }

    private void SetLyricsMaintenanceButtonsEnabled(bool enabled)
    {
        LyricsPanel.OpenLyricsDirectoryButton.IsEnabled = enabled;
        LyricsPanel.ChooseLyricsDirectoryButton.IsEnabled = enabled;
        LyricsPanel.OrganizeLyricsCacheButton.IsEnabled = enabled;
        LyricsPanel.RestoreLyricsCacheButton.IsEnabled = enabled;
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024d:0.0} KB";
        }

        return $"{bytes / (1024d * 1024d):0.0} MB";
    }

    internal void OnOpenNetEasePreciseSyncHelpClick(object sender, RoutedEventArgs args)
    {
        System.Windows.MessageBox.Show(
            "尝试启用网易云实验性精确同步：\n\n"
            + "1. 在网易云音乐“设置 → 系统”中开启 SMTC。\n"
            + "2. 在词间“常规 → 跟随播放器启动”中选择“使用组合快捷方式”。\n"
            + "3. 选择网易云音乐并创建快捷方式。\n"
            + "4. 完全退出正在运行的网易云，再从新快捷方式启动。\n\n"
            + "注意：网易云自身的开机启动和更新后的自动重启可能绕过组合快捷方式。"
            + "出现“网易云播放进度未连接”时，需要完全退出后再从组合快捷方式打开。"
            + "词间会持续尝试连接已启用的进度通道，无需一起重启词间。\n\n"
            + "词间会建立一个仅限本机、仅在这次网易云运行期间存在的临时进度通道。"
            + "它不安装插件、不修改网易云安装目录，也不保存播放记录。"
            + "若当前网易云版本不兼容或通道连接失败，词间会自动退回系统媒体会话或本地计时。",
            "网易云实验性精确同步",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    internal void OnResetLyricsFiltersClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is AppViewModel viewModel)
        {
            viewModel.Settings.LyricsFilterPatterns = LyricFilter.DefaultPatterns;
        }
    }

    internal async void OnClearTrackLyricOffsetsClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is not AppViewModel viewModel)
        {
            return;
        }

        var existing = viewModel.Settings.TrackLyricOffsetsMilliseconds;
        if (existing.Count == 0)
        {
            System.Windows.MessageBox.Show(
                "目前没有保存任何单曲校准。",
                "词间 · Lyrveil",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var choice = System.Windows.MessageBox.Show(
            $"确定清除已保存的 {existing.Count} 条单曲校准吗？\n\n"
            + "全局偏移和播放器延迟设置不会改变。此操作无法撤销。",
            "清除全部单曲校准",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (choice != MessageBoxResult.Yes)
        {
            return;
        }

        viewModel.Settings.ClearAllTrackLyricOffsets();
        try
        {
            await _saveSettingsAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            viewModel.Settings.TrackLyricOffsetsMilliseconds = existing;
            System.Windows.MessageBox.Show(
                $"无法保存更改，单曲校准没有被清除。\n\n{exception.Message}",
                "词间 · Lyrveil",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    public void CommitPendingEdits() => LyricsPanel.FilterPatternsBox
        .GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();

    private void OnFilterSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AppSettings.LyricsFilterPatterns) or nameof(AppSettings.LyricsFilterEnabled))
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, UpdateFilterValidation);
    }

    private void UpdateFilterValidation()
    {
        if (LyricsPanel.FilterValidationText is null || DataContext is not AppViewModel viewModel)
        {
            return;
        }

        var patterns = viewModel.Settings.LyricsFilterPatterns;
        var invalid = LyricFilter.FindPatternErrors(patterns);
        var timedOut = LyricFilter.FindTimedOutPatternLines(patterns);
        LyricsPanel.FilterValidationText.Text = timedOut.Count > 0
            ? $"第 {string.Join("、", timedOut)} 行处理耗时过长，已暂时跳过。请简化规则后重试。"
            : invalid.Count == 0
            ? "规则格式正确；匹配到的内容会从显示中移除，原始歌词文件保持不变。"
            : BuildFilterValidationMessage(invalid);
        LyricsPanel.FilterValidationText.SetResourceReference(TextBlock.ForegroundProperty,
            invalid.Count == 0 && timedOut.Count == 0 ? "Text.Secondary" : "Danger.Text");
    }

    private static string BuildFilterValidationMessage(
        IReadOnlyList<LyricPatternError> invalidPatterns)
    {
        const int maximumDisplayedLines = 4;
        var displayedLines = string.Join(
            "、",
            invalidPatterns
                .Take(maximumDisplayedLines)
                .Select(error => error.LineNumber));
        var suffix = invalidPatterns.Count > maximumDisplayedLines ? " 等" : string.Empty;
        return $"第 {displayedLines} 行{suffix}格式无效（共 {invalidPatterns.Count} 条），修正前会自动忽略。";
    }

    private static string GetLocalPlayerDiagnosticValue(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return "未配置";
        }

        var fileName = Path.GetFileName(executablePath);
        return string.IsNullOrWhiteSpace(fileName)
            ? "已配置（路径已隐藏）"
            : $"已配置：{fileName}（路径已隐藏）";
    }

    private static void PickColor(string currentValue, Action<string> apply)
    {
        using var dialog = new WinForms.ColorDialog
        {
            Color = Drawing.ColorTranslator.FromHtml(currentValue),
            FullOpen = true,
            AnyColor = true
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        var color = dialog.Color;
        apply($"#{color.R:X2}{color.G:X2}{color.B:X2}");
    }

    internal void OnPlayerFollowFeaturesChanged(object sender, RoutedEventArgs args)
    {
        if (_updatingPlayerFollowMode || DataContext is not AppViewModel viewModel)
        {
            return;
        }

        var monitorEnabled = GeneralPanel.MonitorModeCheckBox.IsChecked == true;
        var shortcutEnabled = GeneralPanel.ShortcutModeCheckBox.IsChecked == true;
        if (!monitorEnabled && !shortcutEnabled)
        {
            _updatingPlayerFollowMode = true;
            try
            {
                if (ReferenceEquals(sender, GeneralPanel.MonitorModeCheckBox))
                {
                    GeneralPanel.MonitorModeCheckBox.IsChecked = true;
                }
                else
                {
                    GeneralPanel.ShortcutModeCheckBox.IsChecked = true;
                }
            }
            finally
            {
                _updatingPlayerFollowMode = false;
            }

            return;
        }

        var previousMode = viewModel.Settings.PlayerFollowMode;
        var nextMode = PlayerFollowModes.FromFeatures(monitorEnabled, shortcutEnabled);
        viewModel.Settings.PlayerFollowMode = nextMode;
        if (PlayerFollowModes.UsesShortcut(previousMode)
            && !PlayerFollowModes.UsesShortcut(nextMode))
        {
            RestoreOriginalPlayerShortcuts();
        }
    }

    private static void RestoreOriginalPlayerShortcuts()
    {
        try
        {
            var result = CompanionShortcutService.RestoreOriginalShortcuts();
            if (result.SkippedCount > 0)
            {
                System.Windows.MessageBox.Show(
                    $"已恢复 {result.RestoredCount} 个播放器快捷方式；另有 {result.SkippedCount} 个已被用户修改，为避免覆盖而保留现状。",
                    "词间 · Lyrveil",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or COMException)
        {
            System.Windows.MessageBox.Show(
                $"后台监听已经启用，但恢复原播放器快捷方式时遇到问题。\n\n{exception.Message}",
                "词间 · Lyrveil",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    internal void OnMonitorModeOptionClick(object sender, MouseButtonEventArgs args)
    {
        GeneralPanel.MonitorModeCheckBox.IsChecked = true;
        GeneralPanel.MonitorModeCheckBox.Focus();
    }

    internal void OnShortcutModeOptionClick(object sender, MouseButtonEventArgs args)
    {
        GeneralPanel.ShortcutModeCheckBox.IsChecked = true;
        GeneralPanel.ShortcutModeCheckBox.Focus();
    }

    private void UpdatePlayerFollowModeControls()
    {
        if (DataContext is not AppViewModel viewModel)
        {
            return;
        }

        _updatingPlayerFollowMode = true;
        try
        {
            GeneralPanel.MonitorModeCheckBox.IsChecked =
                PlayerFollowModes.UsesMonitor(viewModel.Settings.PlayerFollowMode);
            GeneralPanel.ShortcutModeCheckBox.IsChecked =
                PlayerFollowModes.UsesShortcut(viewModel.Settings.PlayerFollowMode);
        }
        finally
        {
            _updatingPlayerFollowMode = false;
        }
    }

    internal void OnManageMonitoredPlayersClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is not AppViewModel viewModel)
        {
            return;
        }

        var dialog = new CompanionPlayersWindow(
            viewModel.Settings,
            CompanionPlayerSelectionMode.Monitor)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            GeneralPanel.MonitorModeCheckBox.IsChecked = true;
            UpdatePlayerFollowModeControls();
        }
    }

    internal void OnCreateCompanionShortcutsClick(object sender, RoutedEventArgs args)
    {
        if (DataContext is not AppViewModel viewModel)
        {
            return;
        }

        var dialog = new CompanionPlayersWindow(
            viewModel.Settings,
            CompanionPlayerSelectionMode.Shortcut)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            GeneralPanel.ShortcutModeCheckBox.IsChecked = true;
            UpdatePlayerFollowModeControls();
        }
    }
}
