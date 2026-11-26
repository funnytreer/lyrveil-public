using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using LyricsXQuiet.Core.Playback;
using LyricsXQuiet.Core.Settings;
using LyricsXQuiet.Platform.Windows.Companion;

namespace LyricsXQuiet.App.Windows;

public partial class CompanionPlayersWindow : Window
{
    private readonly ObservableCollection<CompanionPlayerOption> _options = [];
    private readonly AppSettings _settings;
    private readonly CompanionPlayerSelectionMode _mode;

    public CompanionPlayersWindow(AppSettings settings, CompanionPlayerSelectionMode mode)
    {
        _settings = settings;
        _mode = mode;
        InitializeComponent();
        LyricsXQuiet.App.Services.ThemeManager.AttachChrome(this);
        PlayerOptionsList.ItemsSource = _options;
        ConfigureForMode();
        Loaded += OnLoaded;
    }

    private void ConfigureForMode()
    {
        if (_mode == CompanionPlayerSelectionMode.Monitor)
        {
            Title = "播放器监听 · 词间";
            HeaderTitleText.Text = "选择要监听的播放器";
            HeaderDescriptionText.Text = "从播放器原来的入口启动即可；词间会自动跟随运行。";
            FooterHintText.Text = "监听器没有窗口和托盘图标，只在后台等待播放器启动。";
            ConfirmButton.Content = "保存监听设置";
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        Loaded -= OnLoaded;
        var installations = await Task.Run(CompanionPlayerDiscoveryService.Discover);
        foreach (var installation in installations)
        {
            var option = new CompanionPlayerOption(
                installation.Player,
                installation.ExecutablePath,
                installation.IsDetected,
                _mode);
            option.PropertyChanged += OnOptionPropertyChanged;
            _options.Add(option);
        }

        if (_mode == CompanionPlayerSelectionMode.Monitor)
        {
            var selectedIds = MusicPlayerCatalog.SelectByIds(_settings.FollowedPlayerIds)
                .Select(player => player.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var option in _options)
            {
                option.IsSelected = selectedIds.Contains(option.Player.Id);
            }
        }
        else
        {
            ApplyAutomaticSelection();
        }

        UpdateSelectionStatus();
    }

    private void OnAutoSelectChanged(object sender, RoutedEventArgs args) =>
        ApplyAutomaticSelection();

    private void ApplyAutomaticSelection()
    {
        if (AutoSelectCheckBox.IsChecked != true)
        {
            return;
        }

        foreach (var option in _options.Where(option => option.IsResolved))
        {
            option.IsSelected = true;
        }
    }

    private void OnOptionPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(CompanionPlayerOption.IsSelected)
            or nameof(CompanionPlayerOption.IsResolved))
        {
            UpdateSelectionStatus();
        }
    }

    private void UpdateSelectionStatus()
    {
        var detectedCount = _options.Count(option => option.IsResolved);
        var selectedCount = _options.Count(option => option.IsSelected);
        DetectionStatusText.Text = _mode == CompanionPlayerSelectionMode.Monitor
            ? detectedCount == 0
                ? $"暂未检测到已安装的播放器；已选择 {selectedCount} 个以后要监听的播放器。"
                : $"已检测到 {detectedCount} 个播放器，已选择 {selectedCount} 个。"
            : detectedCount == 0
                ? $"没有自动找到播放器；已选择 {selectedCount} 个，请使用右侧按钮手动定位。"
                : $"已找到 {detectedCount} 个播放器，已选择 {selectedCount} 个。";
        ConfirmButton.IsEnabled = selectedCount > 0;
    }

    private void OnBrowsePlayerClick(object sender, RoutedEventArgs args)
    {
        if (sender is not System.Windows.Controls.Button { Tag: CompanionPlayerOption option })
        {
            return;
        }

        var dialog = CreatePlayerDialog();
        if (dialog.ShowDialog(this) == true)
        {
            ApplyManualPath(option, dialog.FileName);
        }
    }

    private static Microsoft.Win32.OpenFileDialog CreatePlayerDialog() => new()
    {
        Title = "选择音乐播放器程序",
        Filter = "播放器程序 (*.exe)|*.exe",
        CheckFileExists = true,
        Multiselect = false
    };

    private void ApplyManualPath(CompanionPlayerOption option, string path)
    {
        if (!CompanionPlayerDiscoveryService.IsMatchingPlayer(path, option.Player))
        {
            System.Windows.MessageBox.Show(
                $"选择的程序不是 {option.Player.DisplayName}。",
                "词间 · Lyrveil",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        option.SetPath(path, false);
        option.IsSelected = true;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs args)
    {
        var selected = _options.Where(option => option.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            System.Windows.MessageBox.Show(
                "请至少勾选一个播放器。",
                "词间 · Lyrveil",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (_mode == CompanionPlayerSelectionMode.Monitor)
        {
            _settings.FollowedPlayerIds = MusicPlayerCatalog.SerializeIds(
                selected.Select(option => option.Player.Id));
            DialogResult = true;
            return;
        }

        var unresolved = selected.Where(option => !option.IsResolved).ToArray();
        if (unresolved.Length > 0)
        {
            System.Windows.MessageBox.Show(
                $"请先手动定位这些播放器：\n\n{string.Join("\n", unresolved.Select(option => option.Player.DisplayName))}",
                "词间 · Lyrveil",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (selected.Any(option => string.Equals(
                option.Player.Id,
                "netease",
                StringComparison.OrdinalIgnoreCase)))
        {
            var confirmation = System.Windows.MessageBox.Show(
                "网易云组合快捷方式会尝试启用词间的实验性精确进度通道。\n\n"
                + "它会让本次网易云进程开放一个随机的本机调试端口；不安装插件、不修改播放器目录，也不保存播放记录。"
                + "该端口仅绑定 127.0.0.1，但在网易云退出前，同一台电脑上的其他程序理论上也可能访问它。\n\n"
                + "是否继续创建？",
                "网易云实验性精确同步",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }
        }

        var created = new List<string>();
        var failures = new List<string>();
        foreach (var option in selected)
        {
            try
            {
                created.Add(CompanionShortcutService.CreateDesktopShortcut(option.ExecutablePath!));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or COMException or InvalidOperationException)
            {
                failures.Add($"{option.Player.DisplayName}：{exception.Message}");
            }
        }

        if (failures.Count > 0)
        {
            System.Windows.MessageBox.Show(
                $"已创建 {created.Count} 个快捷方式，但有部分失败：\n\n{string.Join("\n", failures)}",
                "词间 · Lyrveil",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        System.Windows.MessageBox.Show(
            $"已创建 {created.Count} 个快捷方式，并保留播放器原有的名称和图标。",
            "词间 · Lyrveil",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        _settings.FollowedPlayerIds = MusicPlayerCatalog.SerializeIds(
            selected.Select(option => option.Player.Id));
        DialogResult = true;
    }

    private sealed class CompanionPlayerOption : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string? _executablePath;
        private bool _wasAutoDetected;

        public CompanionPlayerOption(
            MusicPlayerDefinition player,
            string? executablePath,
            bool wasAutoDetected,
            CompanionPlayerSelectionMode mode)
        {
            Player = player;
            _executablePath = executablePath;
            _wasAutoDetected = wasAutoDetected;
            Mode = mode;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public MusicPlayerDefinition Player { get; }

        public CompanionPlayerSelectionMode Mode { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        public string? ExecutablePath => _executablePath;

        public bool IsResolved => !string.IsNullOrWhiteSpace(_executablePath);

        public string StatusText => Mode == CompanionPlayerSelectionMode.Monitor
            ? IsResolved ? "已检测到" : "未检测到，仍可选择监听"
            : IsResolved
                ? _wasAutoDetected ? "已自动找到" : "已手动选择"
                : "未找到，可手动定位";

        public string PathDisplay => _executablePath ?? "尚未设置程序路径";

        public Visibility PathControlsVisibility => Mode == CompanionPlayerSelectionMode.Shortcut
            ? Visibility.Visible
            : Visibility.Collapsed;

        public void SetPath(string path, bool wasAutoDetected)
        {
            _executablePath = Path.GetFullPath(path);
            _wasAutoDetected = wasAutoDetected;
            OnPropertyChanged(nameof(ExecutablePath));
            OnPropertyChanged(nameof(IsResolved));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(PathDisplay));
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            OnPropertyChanged(propertyName);
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public enum CompanionPlayerSelectionMode
{
    Monitor,
    Shortcut
}
