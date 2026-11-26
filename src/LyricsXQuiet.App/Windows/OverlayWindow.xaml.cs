using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LyricsXQuiet.App.Services;
using LyricsXQuiet.App.State;
using LyricsXQuiet.Core.Lyrics;
using LyricsXQuiet.Core.Settings;
using LyricsXQuiet.Platform.Windows.Interop;
using MediaColor = System.Windows.Media.Color;

namespace LyricsXQuiet.App.Windows;

public partial class OverlayWindow : Window
{
    // A ratio of 1 is clamped so the window bottom sits exactly on the work
    // area's bottom edge: immediately above a bottom taskbar, with no gap.
    private const double DefaultVerticalPositionRatio = 1.0;
    private const double DefaultEdgeMargin = 24;
    private const double VerticalEdgeMargin = 0;
    private const double TaskbarSnapDistance = 12;
    private const double TaskbarSnapReleaseDistance = 24;
    private const double LegacyDockDetectionDistance = 48;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _cursorTimer;
    private nint _handle;
    private bool _contentVisible;
    private bool _hoverHidden;
    private bool _hasInitialPosition;
    private bool _centerDragActive;
    private bool _taskbarSnapActive;
    private bool _dockedToTaskbar;
    private bool _positionRestorePending;
    internal bool CanRenderLyrics => IsVisible && !_hoverHidden;
    internal event EventHandler? RenderVisibilityChanged;
    private System.Windows.Point _dragPointerOffset;
    private double _currentProgress = 1;
    private int _lineIndex = -1;
    private LyricFrameKind _frameKind = LyricFrameKind.Lyrics;
    private WindowPlacementService.WindowCenter? _positionAnchor;

    public OverlayWindow(AppSettings settings)
    {
        _settings = settings;
        _dockedToTaskbar = settings.OverlayDockedToTaskbar == true;
        InitializeComponent();
        LyricsXQuiet.App.Services.ThemeManager.AttachChrome(this);
        _settings.PropertyChanged += OnSettingsChanged;
        SourceInitialized += OnSourceInitialized;
        ContentRendered += OnContentRendered;
        SizeChanged += OnSizeChanged;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        LostMouseCapture += OnLostMouseCapture;
        Closed += OnClosed;
        _cursorTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(80),
            DispatcherPriority.Background,
            OnCursorTimer,
            Dispatcher);
        _cursorTimer.Stop();
        IsVisibleChanged += OnVisibilityChanged;
        CurrentBaseText.SizeChanged += (_, _) => ApplyProgressWidth();
        ApplyVisualSettings();
    }

    public void SetFrame(LyricFrame frame)
    {
        var layoutChanged = false;
        var enteringLyricsFromTrackInfo = _frameKind == LyricFrameKind.TrackInfo
            && frame.Kind == LyricFrameKind.Lyrics;
        if (_frameKind != frame.Kind)
        {
            _frameKind = frame.Kind;
            ApplyFramePresentation();
            layoutChanged = true;
        }

        if (!string.Equals(CurrentBaseText.Text, frame.CurrentText, StringComparison.Ordinal))
        {
            CurrentBaseText.Text = frame.CurrentText;
            CurrentProgressText.Text = frame.CurrentText;
            layoutChanged = true;
        }

        if (!string.Equals(SecondaryText.Text, frame.SecondaryText, StringComparison.Ordinal))
        {
            SecondaryText.Text = frame.SecondaryText;
            layoutChanged = true;
        }

        var secondaryVisibility = GetSecondaryVisibility(frame.SecondaryText);
        if (SecondaryText.Visibility != secondaryVisibility)
        {
            SecondaryText.Visibility = secondaryVisibility;
            layoutChanged = true;
        }

        var progress = _settings.ProgressiveHighlight ? frame.Progress : 1;
        if (layoutChanged || Math.Abs(_currentProgress - progress) > 0.00001)
        {
            _currentProgress = progress;
            ApplyProgressWidth();
        }
        if (layoutChanged)
        {
            CurrentBaseTextOutline.InvalidateVisual();
            SecondaryTextOutline.InvalidateVisual();
            SchedulePositionRestore();
        }

        if (_lineIndex != frame.LineIndex)
        {
            _lineIndex = frame.LineIndex;
            LineTranslation.BeginAnimation(TranslateTransform.YProperty, null);
            if (enteringLyricsFromTrackInfo)
            {
                LyricStack.BeginAnimation(OpacityProperty, null);
                LyricStack.Opacity = 1;
            }
            else
            {
                var fade = new DoubleAnimation(0.45, 1, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Timeline.SetDesiredFrameRate(fade, DisplayMetrics.GetRefreshRate(_handle));
                LyricStack.BeginAnimation(OpacityProperty, fade);
                if (_settings.LineTransitionStyle == "SlideFade")
                {
                    var slide = new DoubleAnimation(4, 0, fade.Duration) { EasingFunction = fade.EasingFunction };
                    Timeline.SetDesiredFrameRate(slide, Timeline.GetDesiredFrameRate(fade));
                    LineTranslation.BeginAnimation(TranslateTransform.YProperty, slide);
                }
            }
        }
    }

    public void SetContentVisible(bool visible)
    {
        _contentVisible = visible;
        var shouldShow = visible && _settings.ShowLyrics;
        if (shouldShow && !IsVisible)
        {
            Show();
            Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                RestorePositionAfterShow);
        }
        else if (!shouldShow && IsVisible)
        {
            Hide();
        }
    }

    public void ResetToDefaultPosition()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ResetToDefaultPosition);
            return;
        }

        _settings.OverlayLeft = null;
        _settings.OverlayTop = null;
        _positionAnchor = null;
        _hasInitialPosition = true;
        PositionAtDefaultLocation();
    }

    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        _handle = new WindowInteropHelper(this).Handle;
        UpdateWidthConstraint();
        LocationChanged += (_, _) => UpdateWidthConstraint();
        OverlayWindowInterop.Configure(_handle, _settings.Locked);
        _ = OverlayWindowInterop.SetCaptureExclusion(_handle, _settings.ExcludeFromCapture);
        UpdateCursorMonitoring();
    }

    private void UpdateWidthConstraint()
    {
        var width = WindowPlacementService.GetWindowWorkingAreaInDips(this).Width;
        MaxWidth = WindowPlacementService.OverlayWidth(width);
    }

    private void OnContentRendered(object? sender, EventArgs args)
    {
        if (_hasInitialPosition)
        {
            return;
        }

        _hasInitialPosition = true;
        if (_settings.OverlayLeft is double left && _settings.OverlayTop is double top)
        {
            Left = left;
            Top = top;
            Dispatcher.BeginInvoke(
                DispatcherPriority.Render,
                new Action(() =>
                {
                    if (WindowPlacementService.IsMeaningfullyVisible(this, includeTaskbar: true))
                    {
                        if (_settings.OverlayDockedToTaskbar is null)
                        {
                            _dockedToTaskbar = WindowPlacementService.IsNearBottomWorkingAreaEdge(
                                this,
                                LegacyDockDetectionDistance);
                            _settings.OverlayDockedToTaskbar = _dockedToTaskbar;
                        }

                        if (_dockedToTaskbar)
                        {
                            PositionAtDockedLocationOnCurrentMonitor();
                            return;
                        }

                        CenterHorizontallyIfRequested();
                        CapturePositionAnchor();
                        return;
                    }

                    _settings.OverlayLeft = null;
                    _settings.OverlayTop = null;
                    PositionAtDefaultLocation();
                }));
            return;
        }

        PositionAtDefaultLocation();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (_hasInitialPosition)
        {
            SchedulePositionRestore();
        }

        ApplyProgressWidth();
    }

    private void PositionAtDefaultLocation()
    {
        _dockedToTaskbar = true;
        _settings.OverlayDockedToTaskbar = true;
        WindowPlacementService.PlaceBottomCenterOnCursorMonitor(
            this,
            DefaultVerticalPositionRatio,
            DefaultEdgeMargin,
            VerticalEdgeMargin);
        CapturePositionAnchor();
    }

    private void PositionAtDockedLocationOnCurrentMonitor()
    {
        WindowPlacementService.PlaceBottomCenterOnCurrentMonitor(
            this,
            DefaultEdgeMargin,
            VerticalEdgeMargin);
        CapturePositionAnchor();
    }

    private void ApplyProgressWidth()
    {
        ProgressGeometry.Rect = new Rect(0, 0, CurrentBaseText.ActualWidth * _currentProgress, CurrentBaseText.ActualHeight);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnSettingsChanged(sender, args));
            return;
        }

        if (args.PropertyName is nameof(AppSettings.FontSize) or nameof(AppSettings.FontFamilyName)
            or nameof(AppSettings.BackgroundOpacity) or nameof(AppSettings.BackgroundColor)
            or nameof(AppSettings.LyricColor) or nameof(AppSettings.ProgressColor)
            or nameof(AppSettings.SecondaryLineScale) or nameof(AppSettings.SecondaryLineOpacity)
            or nameof(AppSettings.TextBackingStyle) or nameof(AppSettings.LyricFontWeight) or nameof(AppSettings.ProgressiveHighlight))
        {
            ApplyVisualSettings();
            SchedulePositionRestore();
            return;
        }

        if (args.PropertyName is nameof(AppSettings.Locked) or nameof(AppSettings.HoverHide)
            or nameof(AppSettings.ShowLyrics) or nameof(AppSettings.Topmost)
            or nameof(AppSettings.ExcludeFromCapture) or nameof(AppSettings.CenterAlignedDragging))
        {
            if (_centerDragActive && _settings.Locked) EndCenterDrag();
            Topmost = _settings.Topmost;
            if (_handle != nint.Zero)
            {
                OverlayWindowInterop.SetClickThrough(_handle, _settings.Locked);
                _ = OverlayWindowInterop.SetCaptureExclusion(_handle, _settings.ExcludeFromCapture);
            }
            UpdateEditingControls();
            SetContentVisible(_contentVisible);
            UpdateCursorMonitoring();
        }
        if (args.PropertyName == nameof(AppSettings.CenterAlignedDragging)
            && _settings.CenterAlignedDragging)
        {
            CenterHorizontallyIfRequested();
        }

    }

    private void ApplyVisualSettings()
    {
        if (_centerDragActive
            && _settings.Locked)
        {
            EndCenterDrag();
        }

        UpdateEditingControls();
        var alpha = (byte)Math.Round(_settings.BackgroundOpacity * byte.MaxValue);
        var backgroundColor = ParseRgb(_settings.BackgroundColor, Colors.Black);
        var lyricColor = ParseRgb(_settings.LyricColor, Colors.White);
        var progressColor = ParseRgb(_settings.ProgressColor, MediaColor.FromRgb(0x33, 0xFF, 0xDD));
        PanelBorder.Background = FrozenBrush(MediaColor.FromArgb(
            alpha,
            backgroundColor.R,
            backgroundColor.G,
            backgroundColor.B));
        PanelBorder.CornerRadius = new CornerRadius(_settings.FontSize / 2);
        PanelBorder.Padding = new Thickness(
            _settings.FontSize,
            _settings.FontSize / 3,
            _settings.FontSize,
            _settings.FontSize / 3);
        CurrentBaseText.FontSize = _settings.FontSize;
        CurrentProgressText.FontSize = _settings.FontSize;
        SecondaryText.FontSize = _settings.FontSize * _settings.SecondaryLineScale;
        var fontFamily = LyricFontResolver.Create(_settings.FontFamilyName);
        CurrentBaseText.FontFamily = fontFamily;
        CurrentProgressText.FontFamily = fontFamily;
        SecondaryText.FontFamily = fontFamily;
        CurrentBaseText.Foreground = FrozenBrush(lyricColor);
        CurrentProgressText.Foreground = FrozenBrush(progressColor);
        SecondaryText.Foreground = CurrentBaseText.Foreground;
        CurrentBaseTextOutline.Outline = SecondaryTextOutline.Outline = _settings.TextBackingStyle == "CrispOutline";
        var shadow = _settings.TextBackingStyle == "SoftShadow" ? Shadow(Colors.Black, 8, 1.5, .72) : null;
        SecondaryText.Effect = shadow;
        CurrentBaseText.Effect = shadow;
        CurrentProgressText.Effect = _settings.TextBackingStyle == "ProgressGlow" && _settings.ProgressiveHighlight
            ? Shadow(MediaColor.FromRgb(0, 255, 212), 12, 0, .55) : null;
        CurrentBaseTextOutline.InvalidateVisual();
        SecondaryTextOutline.InvalidateVisual();
        SecondaryText.Margin = new Thickness(0, _settings.FontSize / 3, 0, 0);
        ApplyFramePresentation();
        Topmost = _settings.Topmost;

        if (_handle != nint.Zero)
        {
            OverlayWindowInterop.SetClickThrough(_handle, _settings.Locked);
            _ = OverlayWindowInterop.SetCaptureExclusion(_handle, _settings.ExcludeFromCapture);
        }

        if (!_settings.HoverHide || !_settings.Locked)
        {
            AnimateHover(false);
        }

        SetContentVisible(_contentVisible);
    }

    private void ApplyFramePresentation()
    {
        var isTrackInfo = _frameKind == LyricFrameKind.TrackInfo;
        CurrentProgressClip.Visibility = isTrackInfo ? Visibility.Collapsed : Visibility.Visible;
        CurrentBaseText.Opacity = isTrackInfo ? 0.88 : 1;
        SecondaryText.Opacity = isTrackInfo ? 0.62 : _settings.SecondaryLineOpacity;
        CurrentBaseText.FontWeight = isTrackInfo || _settings.LyricFontWeight == "SemiBold" ? FontWeights.SemiBold : FontWeights.Normal;
        CurrentProgressText.FontWeight = CurrentBaseText.FontWeight;
        SecondaryText.FontWeight = FontWeights.Normal;
        SecondaryText.Visibility = GetSecondaryVisibility(SecondaryText.Text);
    }

    private Visibility GetSecondaryVisibility(string text) =>
        !string.IsNullOrWhiteSpace(text)
        && (_frameKind == LyricFrameKind.TrackInfo || _settings.DoubleLine)
            ? Visibility.Visible
            : Visibility.Collapsed;

    private static SolidColorBrush FrozenBrush(MediaColor color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static MediaColor ParseRgb(string value, MediaColor fallback)
    {
        try
        {
            if (System.Windows.Media.ColorConverter.ConvertFromString(value) is MediaColor color)
            {
                return MediaColor.FromRgb(color.R, color.G, color.B);
            }
        }
        catch (FormatException)
        {
        }

        return fallback;
    }

    internal static DropShadowEffect Shadow(MediaColor color, double blur, double depth, double opacity)
    {
        var effect = new DropShadowEffect { Color = color, BlurRadius = blur, ShadowDepth = depth,
            Opacity = opacity, RenderingBias = RenderingBias.Performance };
        effect.Freeze();
        return effect;
    }

    private void UpdateEditingControls()
    {
        EditingTools.Visibility = EditingOutline.Visibility = _settings.Locked ? Visibility.Collapsed : Visibility.Visible;
        if (_settings.Locked) CenterGuide.Visibility = Visibility.Collapsed;
    }

    private void OnCenterClick(object sender, RoutedEventArgs e)
    {
        WindowPlacementService.CenterHorizontallyOnCurrentMonitor(this);
        StoreCurrentPosition();
    }

    private void OnDockClick(object sender, RoutedEventArgs e)
    {
        _dockedToTaskbar = true;
        PositionAtDockedLocationOnCurrentMonitor();
        StoreCurrentPosition();
    }

    private void OnLockClick(object sender, RoutedEventArgs e) => _settings.Locked = true;

    private void OnCursorTimer(object? sender, EventArgs args)
    {
        if (!IsVisible || _handle == nint.Zero || !_settings.HoverHide || !_settings.Locked)
        {
            return;
        }

        AnimateHover(OverlayWindowInterop.IsCursorInside(_handle));
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        UpdateCursorMonitoring();
        RenderVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateWidthConstraint();
        // The old monitor's pixel anchor is no longer valid after WM_DPICHANGED.
        _positionAnchor = null;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, RestorePositionAfterShow);
    }

    private void UpdateCursorMonitoring()
    {
        var enabled = IsVisible && _handle != nint.Zero && _settings.ShowLyrics
            && _settings.HoverHide && _settings.Locked;
        if (enabled)
        {
            if (!_cursorTimer.IsEnabled) _cursorTimer.Start();
        }
        else
        {
            _cursorTimer.Stop();
            AnimateHover(false);
        }
    }

    private void AnimateHover(bool hidden)
    {
        if (_hoverHidden == hidden)
        {
            return;
        }

        _hoverHidden = hidden;
        RenderVisibilityChanged?.Invoke(this, EventArgs.Empty);
        var animation = new DoubleAnimation(
            hidden ? 0 : 1,
            hidden ? TimeSpan.FromMilliseconds(110) : TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        Timeline.SetDesiredFrameRate(animation, DisplayMetrics.GetRefreshRate(_handle));
        BeginAnimation(OpacityProperty, animation);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (_settings.Locked || args.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _centerDragActive = CaptureMouse();
        if (!_centerDragActive) return;
        _taskbarSnapActive = _dockedToTaskbar;
        _dragPointerOffset = args.GetPosition(this);
        MoveAtCursor();
        args.Handled = true;
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs args)
    {
        if (!_centerDragActive)
        {
            return;
        }

        if (args.LeftButton != MouseButtonState.Pressed)
        {
            EndCenterDrag();
            return;
        }

        MoveAtCursor();
        args.Handled = true;
    }

    private void MoveAtCursor()
    {
        _taskbarSnapActive = WindowPlacementService.MoveAtCursor(this, _dragPointerOffset, _settings.CenterAlignedDragging,
            out var centered, DefaultEdgeMargin, VerticalEdgeMargin, _taskbarSnapActive, TaskbarSnapDistance, TaskbarSnapReleaseDistance);
        CenterGuide.Visibility = centered ? Visibility.Visible : Visibility.Collapsed;
        CapturePositionAnchor();
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs args)
    {
        if (_centerDragActive && args.ChangedButton == MouseButton.Left)
        {
            EndCenterDrag();
            args.Handled = true;
        }
    }

    private void OnLostMouseCapture(object sender, System.Windows.Input.MouseEventArgs args)
    {
        if (_centerDragActive)
        {
            _dockedToTaskbar = _taskbarSnapActive;
            _centerDragActive = false;
        CenterGuide.Visibility = Visibility.Collapsed;
            _taskbarSnapActive = false;
            StoreCurrentPosition();
        }
    }

    private void EndCenterDrag()
    {
        if (!_centerDragActive)
        {
            return;
        }

        _dockedToTaskbar = _taskbarSnapActive;
        _centerDragActive = false;
        CenterGuide.Visibility = Visibility.Collapsed;
        _taskbarSnapActive = false;
        ReleaseMouseCapture();
        StoreCurrentPosition();
    }

    private void CenterHorizontallyIfRequested()
    {
        if (!_hasInitialPosition || !_settings.CenterAlignedDragging || !IsVisible)
        {
            return;
        }

        var hadManualPosition = _settings.OverlayLeft is not null;
        WindowPlacementService.CenterHorizontallyOnCurrentMonitor(
            this,
            DefaultEdgeMargin,
            VerticalEdgeMargin,
            allowTaskbarOverlap: true);
        CapturePositionAnchor();
        if (hadManualPosition)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, StoreCurrentPosition);
        }
    }

    private void StoreCurrentPosition()
    {
        CapturePositionAnchor();
        _settings.OverlayLeft = Left;
        _settings.OverlayTop = Top;
        _settings.OverlayDockedToTaskbar = _dockedToTaskbar;
    }

    private void RestorePositionAfterShow()
    {
        if (!_hasInitialPosition || !IsVisible)
        {
            return;
        }

        if (_dockedToTaskbar)
        {
            PositionAtDockedLocationOnCurrentMonitor();
            return;
        }

        if (_settings.CenterAlignedDragging)
        {
            WindowPlacementService.CenterHorizontallyOnCurrentMonitor(
                this,
                DefaultEdgeMargin,
                VerticalEdgeMargin,
                allowTaskbarOverlap: true);
            CapturePositionAnchor();
            return;
        }

        RestorePositionAnchor();
    }

    private void SchedulePositionRestore()
    {
        if (!_hasInitialPosition || _positionRestorePending)
        {
            return;
        }

        _positionRestorePending = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() =>
            {
                _positionRestorePending = false;
                RestorePositionAnchor();
            }));
    }

    private void RestorePositionAnchor()
    {
        if (!_hasInitialPosition || !IsVisible || _centerDragActive)
        {
            return;
        }

        if (_dockedToTaskbar)
        {
            PositionAtDockedLocationOnCurrentMonitor();
            return;
        }

        if (_positionAnchor is WindowPlacementService.WindowCenter anchor)
        {
            WindowPlacementService.PlaceAtCenter(
                this,
                anchor,
                DefaultEdgeMargin,
                VerticalEdgeMargin,
                allowTaskbarOverlap: true);
            return;
        }

        CapturePositionAnchor();
    }

    private void CapturePositionAnchor()
    {
        if (WindowPlacementService.TryGetWindowCenter(this, out var center))
        {
            _positionAnchor = center;
        }
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        IsVisibleChanged -= OnVisibilityChanged;
        _cursorTimer.Stop();
        _centerDragActive = false;
        CenterGuide.Visibility = Visibility.Collapsed;
        _taskbarSnapActive = false;
        _settings.PropertyChanged -= OnSettingsChanged;
    }
}
