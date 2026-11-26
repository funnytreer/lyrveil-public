using System.Windows;
using LyricsXQuiet.App.Windows;

namespace LyricsXQuiet.App.Pages;

public partial class GeneralPage : System.Windows.Controls.UserControl
{
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public GeneralPage()
    {
        InitializeComponent();
        _statusTimer.Tick += (_, _) => RefreshStatus();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { RefreshStatus(); _statusTimer.Start(); }
            else _statusTimer.Stop();
        };
        Unloaded += (_, _) => _statusTimer.Stop();
    }

    private void RefreshStatus()
    {
        var running = LyricsXQuiet.Platform.Windows.Companion.PlayerMonitorRegistrationService.IsMonitorRunning();
        MonitorStatus.Text = running ? "后台轻量监听运行中" : MonitorModeCheckBox.IsChecked == true ? "等待启动" : "未启用";
    }

    internal void Connect(SettingsWindow host)
    {
        MonitorModeOptionBorder.MouseLeftButtonUp += host.OnMonitorModeOptionClick;
        MonitorModeCheckBox.Checked += host.OnPlayerFollowFeaturesChanged;
        MonitorModeCheckBox.Unchecked += host.OnPlayerFollowFeaturesChanged;
        ManageMonitoredPlayersButton.Click += host.OnManageMonitoredPlayersClick;
        ShortcutModeOptionBorder.MouseLeftButtonUp += host.OnShortcutModeOptionClick;
        ShortcutModeCheckBox.Checked += host.OnPlayerFollowFeaturesChanged;
        ShortcutModeCheckBox.Unchecked += host.OnPlayerFollowFeaturesChanged;
        CreateCompanionShortcutsButton.Click += host.OnCreateCompanionShortcutsClick;
        CheckPlayerCompatibilityButton.Click += host.OnCheckPlayerCompatibilityClick;
    }
}
