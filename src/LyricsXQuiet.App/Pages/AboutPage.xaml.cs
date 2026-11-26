using System.Windows;
using LyricsXQuiet.App.Windows;

namespace LyricsXQuiet.App.Pages;

public partial class AboutPage : System.Windows.Controls.UserControl
{
    public AboutPage() => InitializeComponent();
    internal void Connect(SettingsWindow host)
    {
        CheckPlayerCompatibilityButton.Click += host.OnCheckPlayerCompatibilityClick;
        CopyDiagnosticsButton.Click += host.OnCopyDiagnosticsClick;
    }
}
