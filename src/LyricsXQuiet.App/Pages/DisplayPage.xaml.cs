using System.Windows;
using LyricsXQuiet.App.Windows;

namespace LyricsXQuiet.App.Pages;

public partial class DisplayPage : System.Windows.Controls.UserControl
{
    private LyricsXQuiet.Core.Settings.AppSettings Settings => ((LyricsXQuiet.App.State.AppViewModel)DataContext).Settings;
    public DisplayPage() => InitializeComponent();

    private void InitializeAppearance()
    {
        Loaded += (_, _) =>
        {
            if (DataContext is not LyricsXQuiet.App.State.AppViewModel) return;
            RefreshPresets();
            RefreshPreview();
        };
        System.ComponentModel.PropertyChangedEventManager.AddHandler(Settings, OnAppearanceChanged, string.Empty);
        PreviewMain.SizeChanged += (_, _) => RefreshPreview();
    }

    private void OnAppearanceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (e.PropertyName == nameof(Settings.CustomAppearancePresets)) RefreshPresets();
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (DataContext is not LyricsXQuiet.App.State.AppViewModel) return;
        PreviewSub.FontSize = Settings.FontSize * Settings.SecondaryLineScale;
        PreviewSubOutline.Visibility = Settings.DoubleLine ? Visibility.Visible : Visibility.Collapsed;
        PreviewMainOutline.Outline = PreviewSubOutline.Outline = Settings.TextBackingStyle == "CrispOutline";
        PreviewMainOutline.InvalidateVisual();
        PreviewSubOutline.InvalidateVisual();
        var soft = Settings.TextBackingStyle == "SoftShadow" ? OverlayWindow.Shadow(System.Windows.Media.Colors.Black, 8, 1.5, .72) : null;
        PreviewMain.Effect = PreviewSub.Effect = soft;
        PreviewProgress.Effect = Settings.TextBackingStyle == "ProgressGlow" ? OverlayWindow.Shadow(
            System.Windows.Media.Color.FromRgb(0,255,212), 12, 0, .55) : null;
        PreviewClip.Rect = new Rect(0, 0, PreviewMain.ActualWidth * (Settings.ProgressiveHighlight ? .45 : 1), PreviewMain.ActualHeight);
    }

    private void RefreshPresets()
    {
        var selected = (PresetList.SelectedItem as LyricsXQuiet.Core.Settings.NamedAppearancePreset)?.Name;
        var presets = LyricsXQuiet.Core.Settings.AppearancePresets.BuiltIn.Concat(Settings.CustomAppearancePresets).ToArray();
        PresetList.ItemsSource = presets;
        PresetList.SelectedItem = presets.FirstOrDefault(p => p.Name == selected) ??
            presets.FirstOrDefault(p => p == Settings.CaptureAppearance(p.Name)) ?? presets[0];
    }

    private void ApplyPreset(object sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not LyricsXQuiet.Core.Settings.NamedAppearancePreset preset) return;
        Settings.ApplyAppearance(preset);
        PresetFeedback.Text = $"已应用「{preset.Name}」。";
    }

    private void SavePreset(object sender, RoutedEventArgs e)
    {
        var name = PresetName.Text.Trim();
        if (name.Length == 0 || LyricsXQuiet.Core.Settings.AppearancePresets.BuiltIn.Any(p => p.Name == name))
        { PresetFeedback.Text = "请输入自定义名称，内置方案名称已保留。"; return; }
        var presets = Settings.CustomAppearancePresets;
        var value = Settings.CaptureAppearance(name);
        var index = presets.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) presets.Add(value); else presets[index] = value;
        Settings.CustomAppearancePresets = presets;
        PresetList.SelectedItem = value;
        PresetFeedback.Text = $"已保存「{name}」。";
    }
    internal void Connect(SettingsWindow host)
    {
        ResetLyricsPositionButton.Click += host.OnResetLyricsPositionClick;
        LyricColorButton.Click += host.OnLyricColorClick;
        ProgressColorButton.Click += host.OnProgressColorClick;
        BackgroundColorButton.Click += host.OnBackgroundColorClick;
        ResetLyricsDisplayButton.Click += host.OnResetLyricsDisplayClick;
        InitializeAppearance();
    }
}
