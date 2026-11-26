using System.Windows;
using LyricsXQuiet.App.Windows;

namespace LyricsXQuiet.App.Pages;

public partial class ExperimentalPage : System.Windows.Controls.UserControl
{
    public ExperimentalPage() => InitializeComponent();
    internal void Connect(SettingsWindow host)
    {
        OpenNetEasePreciseSyncHelpButton.Click += host.OnOpenNetEasePreciseSyncHelpClick;
    }
}
