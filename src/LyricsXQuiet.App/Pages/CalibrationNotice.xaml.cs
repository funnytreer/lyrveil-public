using System.Windows;
using LyricsXQuiet.App.Services;

namespace LyricsXQuiet.App.Pages;

public partial class CalibrationNotice : System.Windows.Controls.UserControl
{
    private AppController? _controller;
    public CalibrationNotice() => InitializeComponent();
    internal void Attach(AppController controller) { _controller = controller; DataContext = controller.State; }
    private void Keep(object sender, RoutedEventArgs args) => _controller?.ResolveCalibration(false);
    private void Clear(object sender, RoutedEventArgs args) => _controller?.ResolveCalibration(true);
}
