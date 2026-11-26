using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LyricsXQuiet.App.Windows;

public sealed class OutlinedLyricText : Decorator
{
    private static readonly System.Windows.Media.Pen OutlinePen = MakePen();
    public static readonly DependencyProperty OutlineProperty = DependencyProperty.Register(nameof(Outline),
        typeof(bool), typeof(OutlinedLyricText), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool Outline
    {
        get => (bool)GetValue(OutlineProperty);
        set => SetValue(OutlineProperty, value);
    }

    protected override void OnRender(DrawingContext context)
    {
        if (Outline && Child is TextBlock label && label.IsVisible && label.Text.Length > 0)
        {
            var text = new FormattedText(label.Text, CultureInfo.CurrentUICulture, label.FlowDirection,
                new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch), label.FontSize,
                System.Windows.Media.Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var origin = label.TranslatePoint(new System.Windows.Point((label.ActualWidth - text.WidthIncludingTrailingWhitespace) / 2, 0), this);
            context.DrawGeometry(null, OutlinePen, text.BuildGeometry(origin));
        }
        base.OnRender(context);
    }

    private static System.Windows.Media.Pen MakePen()
    {
        var pen = new System.Windows.Media.Pen(System.Windows.Media.Brushes.Black, 2) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }
}
