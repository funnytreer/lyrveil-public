using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using LyricsXQuiet.App.Windows;
using LyricsXQuiet.App.Services;
using System.Windows.Media.Imaging;

namespace LyricsXQuiet.Tools.OverlayPositionSmokeTests;

internal static class VisualAcceptance
{
    public static void CheckResolutionMatrix(SettingsWindow window, TabControl navigation)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var root = new Decorator { Child = content, UseLayoutRounding = true, SnapsToDevicePixels = true, DataContext = window.DataContext };
        try
        {
            var screens = new[] { (1920, 1080, 1d), (1920, 1080, 1.25), (2560, 1440, 1d),
                (2560, 1440, 1.25), (2560, 1440, 1.5), (3840, 2160, 1.5), (3840, 2160, 1.75), (3840, 2160, 2d) };
            foreach (var (pixelsX, pixelsY, scale) in screens)
            {
                var work = new Rect(-pixelsX / scale, 0, pixelsX / scale, pixelsY / scale - 48);
                VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
                window.ConstrainSize(work);
                var size = new Size(window.Width, window.Height);
                for (var page = 0; page < navigation.Items.Count; page++)
                {
                    navigation.SelectedIndex = page;
                    root.Measure(size);
                    root.Arrange(new Rect(size));
                    root.UpdateLayout();
                    if (root.DesiredSize.Width > size.Width + 1 || navigation.ActualWidth <= 0 ||
                        size.Width > work.Width - 48 || size.Height > work.Height - 48)
                        throw new InvalidOperationException($"Settings overflow at {pixelsX}×{pixelsY} scale {scale}, page {page}.");
                }
                if (WindowPlacementService.OverlayWidth(work.Width) > Math.Min(1280, work.Width)
                    || WindowPlacementService.QuickPanelHeight(work.Height) > Math.Min(640, work.Height - 36))
                    throw new InvalidOperationException($"Popup overflow at {pixelsX}×{pixelsY} scale {scale}.");
            }
            Console.WriteLine("DPI layout matrix passed: seven pages, eight resolutions/scale combinations, negative monitor origin.");
        }
        finally
        {
            root.Child = null;
            window.Content = content;
        }
    }

    public static BitmapSource Render(FrameworkElement element)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(element.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    public static void Capture(FrameworkElement element, string name)
    {
        var directory = Environment.GetEnvironmentVariable("LYRVEIL_TEST_PREVIEWS");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Render(element)));
        using var output = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(output);
    }

    public static int DarkPixelsOnWhite(FrameworkElement element)
    {
        var bitmap = Render(element);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        int count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var whiteBehind = 255 - pixels[i + 3];
            if (whiteBehind + Math.Max(pixels[i], Math.Max(pixels[i+1], pixels[i+2])) < 96) count++;
        }
        return count;
    }
}
