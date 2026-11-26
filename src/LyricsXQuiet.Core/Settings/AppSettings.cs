using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using LyricsXQuiet.Core.Lyrics;
using LyricsXQuiet.Core.Playback;

namespace LyricsXQuiet.Core.Settings;

public sealed class AppSettings : INotifyPropertyChanged
{
    public const int CurrentSettingsSchemaVersion = 6;
    public const string DefaultFollowedPlayerIds =
        "netease;qqmusic;kugou;applemusic;spotify";

    private const bool DefaultLocked = true;
    private const bool DefaultHoverHide = true;
    private const bool DefaultDoubleLine = true;
    private const bool DefaultProgressiveHighlight = true;
    private const bool DefaultShowTrackInfoWhenNoLyrics = true;
    private const bool DefaultExcludeFromCapture = false;
    private const double DefaultBackgroundOpacity = 0.52;
    private const double DefaultFontSize = 24;
    private const string DefaultFontFamilyName = "Microsoft YaHei UI";
    private const string DefaultLyricColor = "#F8FAFC";
    private const string DefaultProgressColor = "#5EEAD4";
    private const string DefaultBackgroundColor = "#0E1318";
    // The editable NetEase baseline is independent of the quick-panel offset.
    // A zero quick-panel offset uses this baseline without further adjustment.
    internal const int DefaultNetEaseSyncDelayMilliseconds =
        NetEaseLyricsPositionCompensation.DefaultDelayMilliseconds;
    // Apple Music for Windows publishes a delayed, whole-second timeline. The
    // dynamic boundary estimator removes quantization jitter; the remaining
    // measured delivery lag is handled here without affecting other players.
    private const int DefaultAppleMusicSyncAdvanceMilliseconds = 300;

    private bool _showLyrics = true;
    private int _settingsSchemaVersion = CurrentSettingsSchemaVersion;
    private bool _locked = DefaultLocked;
    private bool _hoverHide = DefaultHoverHide;
    private bool _hideWhenPaused = true;
    private bool _doubleLine = DefaultDoubleLine;
    private bool _progressiveHighlight = DefaultProgressiveHighlight;
    private bool _showTrackInfoWhenNoLyrics = DefaultShowTrackInfoWhenNoLyrics;
    private bool _showLyricsCredits;
    private bool _excludeFromCapture = DefaultExcludeFromCapture;
    private bool _centerAlignedDragging;
    private bool _topmost = true;
    private double _backgroundOpacity = DefaultBackgroundOpacity;
    private double _fontSize = DefaultFontSize;
    private string _fontFamilyName = DefaultFontFamilyName;
    private string _lyricColor = DefaultLyricColor;
    private string _progressColor = DefaultProgressColor;
    private string _backgroundColor = DefaultBackgroundColor;
    private string _lyricsDirectory = LyricsStorage.DefaultDirectory;
    private bool _enableLrclibLyrics = true;
    private bool _enableNetEaseLyrics = true;
    private bool _enableQqMusicLyrics = true;
    private bool _enableKugouLyrics = true;
    private bool _lyricsFilterEnabled = true;
    private string _lyricsFilterPatterns = LyricFilter.DefaultPatterns;
    private string _playerFollowMode = PlayerFollowModes.Shortcut;
    private string _followedPlayerIds = DefaultFollowedPlayerIds;
    private string _localPlayerExecutablePath = string.Empty;
    private bool _exitWhenPlayersClose = true;
    private bool _autoRepairNetEaseSync;
    private int _offsetMilliseconds;
    private int _netEaseSyncDelayMilliseconds = DefaultNetEaseSyncDelayMilliseconds;
    private int _netEaseOffsetMilliseconds;
    private int _appleMusicSyncAdvanceMilliseconds =
        DefaultAppleMusicSyncAdvanceMilliseconds;
    private int _qqMusicOffsetMilliseconds;
    private int _kugouOffsetMilliseconds;
    private int _spotifyOffsetMilliseconds;
    private Dictionary<string, int> _trackLyricOffsetsMilliseconds =
        new(StringComparer.Ordinal);
    private AppearancePreset? _savedAppearancePreset;
    private double? _overlayLeft;
    private double? _overlayTop;
    private bool? _overlayDockedToTaskbar;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int SettingsSchemaVersion
    {
        get => _settingsSchemaVersion;
        set => Set(ref _settingsSchemaVersion, Math.Clamp(value, 0, CurrentSettingsSchemaVersion));
    }

    private string _uiThemeMode = "System";
    private double _secondaryLineScale = 0.80;
    private double _secondaryLineOpacity = 0.74;
    private string _textBackingStyle = "SoftShadow";
    private string _lyricFontWeight = "SemiBold";
    private string _lineTransitionStyle = "SlideFade";
    private List<NamedAppearancePreset> _customAppearancePresets = [];
    private Dictionary<string, string> _hotKeys = HotKeyBindings.Normalize(null);

    public string UiThemeMode
    {
        get => _uiThemeMode;
        set => Set(ref _uiThemeMode, value is "Dark" or "Light" ? value : "System");
    }

    public double SecondaryLineScale
    {
        get => _secondaryLineScale;
        set => Set(ref _secondaryLineScale, double.IsFinite(value) ? Math.Clamp(value, 0.70, 1) : 0.80);
    }

    public double SecondaryLineOpacity
    {
        get => _secondaryLineOpacity;
        set => Set(ref _secondaryLineOpacity, double.IsFinite(value) ? Math.Clamp(value, 0.50, 1) : 0.74);
    }

    public string TextBackingStyle
    {
        get => _textBackingStyle;
        set => Set(ref _textBackingStyle, value is "None" or "CrispOutline" or "ProgressGlow" ? value : "SoftShadow");
    }

    public string LyricFontWeight
    {
        get => _lyricFontWeight;
        set => Set(ref _lyricFontWeight, value == "Normal" ? value : "SemiBold");
    }

    public string LineTransitionStyle
    {
        get => _lineTransitionStyle;
        set => Set(ref _lineTransitionStyle, value == "FadeOnly" ? value : "SlideFade");
    }

    public List<NamedAppearancePreset> CustomAppearancePresets
    {
        get => [.. _customAppearancePresets];
        set
        {
            var presets = (value ?? []).Where(p => p?.Appearance is not null && !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => p with { Name = p.Name.Trim() }).DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (!_customAppearancePresets.SequenceEqual(presets))
                Set(ref _customAppearancePresets, presets);
        }
    }

    public Dictionary<string, string> HotKeys
    {
        get => new(_hotKeys);
        set
        {
            var normalized = HotKeyBindings.Normalize(value);
            if (!_hotKeys.SequenceEqual(normalized)) Set(ref _hotKeys, normalized);
        }
    }

    public bool ShowLyrics
    {
        get => _showLyrics;
        set => Set(ref _showLyrics, value);
    }

    public bool Locked
    {
        get => _locked;
        set => Set(ref _locked, value);
    }

    public bool HoverHide
    {
        get => _hoverHide;
        set => Set(ref _hoverHide, value);
    }

    public bool HideWhenPaused
    {
        get => _hideWhenPaused;
        set => Set(ref _hideWhenPaused, value);
    }

    public bool DoubleLine
    {
        get => _doubleLine;
        set => Set(ref _doubleLine, value);
    }

    public bool ProgressiveHighlight
    {
        get => _progressiveHighlight;
        set => Set(ref _progressiveHighlight, value);
    }

    public bool ShowTrackInfoWhenNoLyrics
    {
        get => _showTrackInfoWhenNoLyrics;
        set => Set(ref _showTrackInfoWhenNoLyrics, value);
    }

    public bool ShowLyricsCredits
    {
        get => _showLyricsCredits;
        set => Set(ref _showLyricsCredits, value);
    }

    public bool ExcludeFromCapture
    {
        get => _excludeFromCapture;
        set => Set(ref _excludeFromCapture, value);
    }

    public bool CenterAlignedDragging
    {
        get => _centerAlignedDragging;
        set => Set(ref _centerAlignedDragging, value);
    }

    public bool Topmost
    {
        get => _topmost;
        set => Set(ref _topmost, value);
    }

    public double BackgroundOpacity
    {
        get => _backgroundOpacity;
        set => Set(ref _backgroundOpacity, Math.Clamp(value, 0, 1));
    }

    public double FontSize
    {
        get => _fontSize;
        set => Set(ref _fontSize, Math.Clamp(value, 16, 42));
    }

    public string FontFamilyName
    {
        get => _fontFamilyName;
        set => Set(ref _fontFamilyName, string.IsNullOrWhiteSpace(value)
            ? DefaultFontFamilyName
            : value.Trim());
    }

    public string LyricColor
    {
        get => _lyricColor;
        set => Set(ref _lyricColor, NormalizeRgb(value, DefaultLyricColor));
    }

    public string ProgressColor
    {
        get => _progressColor;
        set => Set(ref _progressColor, NormalizeRgb(value, DefaultProgressColor));
    }

    public string BackgroundColor
    {
        get => _backgroundColor;
        set => Set(ref _backgroundColor, NormalizeRgb(value, DefaultBackgroundColor));
    }

    public string LyricsDirectory
    {
        get => _lyricsDirectory;
        set => Set(ref _lyricsDirectory, string.IsNullOrWhiteSpace(value)
            ? LyricsStorage.DefaultDirectory
            : value.Trim());
    }

    public bool EnableLrclibLyrics
    {
        get => _enableLrclibLyrics;
        set => Set(ref _enableLrclibLyrics, value);
    }

    public bool EnableNetEaseLyrics
    {
        get => _enableNetEaseLyrics;
        set => Set(ref _enableNetEaseLyrics, value);
    }

    public bool EnableQqMusicLyrics
    {
        get => _enableQqMusicLyrics;
        set => Set(ref _enableQqMusicLyrics, value);
    }

    public bool EnableKugouLyrics
    {
        get => _enableKugouLyrics;
        set => Set(ref _enableKugouLyrics, value);
    }

    public bool LyricsFilterEnabled
    {
        get => _lyricsFilterEnabled;
        set => Set(ref _lyricsFilterEnabled, value);
    }

    public string LyricsFilterPatterns
    {
        get => _lyricsFilterPatterns;
        set => Set(ref _lyricsFilterPatterns, value ?? string.Empty);
    }

    public string PlayerFollowMode
    {
        get => _playerFollowMode;
        set => Set(ref _playerFollowMode, PlayerFollowModes.Normalize(value));
    }

    public string FollowedPlayerIds
    {
        get => _followedPlayerIds;
        set => Set(ref _followedPlayerIds, value?.Trim() ?? string.Empty);
    }

    public string LocalPlayerExecutablePath
    {
        get => _localPlayerExecutablePath;
        set => Set(ref _localPlayerExecutablePath, value?.Trim() ?? string.Empty);
    }

    public bool ExitWhenPlayersClose
    {
        get => _exitWhenPlayersClose;
        set => Set(ref _exitWhenPlayersClose, value);
    }

    public int OffsetMilliseconds
    {
        get => _offsetMilliseconds;
        set => Set(ref _offsetMilliseconds, Math.Clamp(value, -10_000, 10_000));
    }

    public bool AutoRepairNetEaseSync
    {
        get => _autoRepairNetEaseSync;
        set => Set(ref _autoRepairNetEaseSync, value);
    }

    public int NetEaseSyncDelayMilliseconds
    {
        get => _netEaseSyncDelayMilliseconds;
        set => Set(ref _netEaseSyncDelayMilliseconds, Math.Clamp(
            value, 0, NetEaseLyricsPositionCompensation.MaximumDelayMilliseconds));
    }

    public int NetEaseOffsetMilliseconds
    {
        get => _netEaseOffsetMilliseconds;
        set => Set(ref _netEaseOffsetMilliseconds, Math.Clamp(value,
            PlayerLyricsPositionCompensation.MinimumMilliseconds,
            PlayerLyricsPositionCompensation.MaximumMilliseconds));
    }

    public int AppleMusicSyncAdvanceMilliseconds
    {
        get => _appleMusicSyncAdvanceMilliseconds;
        set => Set(ref _appleMusicSyncAdvanceMilliseconds, Math.Clamp(
            value, 0, AppleMusicLyricsPositionCompensation.MaximumAdvanceMilliseconds));
    }

    public int QqMusicOffsetMilliseconds
    {
        get => _qqMusicOffsetMilliseconds;
        set => Set(ref _qqMusicOffsetMilliseconds, Math.Clamp(value,
            PlayerLyricsPositionCompensation.MinimumMilliseconds,
            PlayerLyricsPositionCompensation.MaximumMilliseconds));
    }

    public int KugouOffsetMilliseconds
    {
        get => _kugouOffsetMilliseconds;
        set => Set(ref _kugouOffsetMilliseconds, Math.Clamp(value,
            PlayerLyricsPositionCompensation.MinimumMilliseconds,
            PlayerLyricsPositionCompensation.MaximumMilliseconds));
    }

    public int SpotifyOffsetMilliseconds
    {
        get => _spotifyOffsetMilliseconds;
        set => Set(ref _spotifyOffsetMilliseconds, Math.Clamp(value,
            PlayerLyricsPositionCompensation.MinimumMilliseconds,
            PlayerLyricsPositionCompensation.MaximumMilliseconds));
    }

    // NetEase's quick control is a signed adjustment on top of its separately
    // editable default delay. Other players retain their own signed convention.
    public int GetPlayerLyricOffsetMilliseconds(string? sourceAppUserModelId) =>
        MusicPlayerCatalog.Identify(sourceAppUserModelId)?.Id switch
        {
            "netease" => NetEaseOffsetMilliseconds,
            "applemusic" => AppleMusicSyncAdvanceMilliseconds,
            "qqmusic" => QqMusicOffsetMilliseconds,
            "kugou" => KugouOffsetMilliseconds,
            "spotify" => SpotifyOffsetMilliseconds,
            _ => 0
        };

    public void SetPlayerLyricOffsetMilliseconds(string? sourceAppUserModelId, int value)
    {
        var offset = Math.Clamp(value,
            PlayerLyricsPositionCompensation.MinimumMilliseconds,
            PlayerLyricsPositionCompensation.MaximumMilliseconds);
        switch (MusicPlayerCatalog.Identify(sourceAppUserModelId)?.Id)
        {
            case "netease": NetEaseOffsetMilliseconds = offset; break;
            case "applemusic": AppleMusicSyncAdvanceMilliseconds = offset; break;
            case "qqmusic": QqMusicOffsetMilliseconds = offset; break;
            case "kugou": KugouOffsetMilliseconds = offset; break;
            case "spotify": SpotifyOffsetMilliseconds = offset; break;
        }
    }

    /// <summary>
    /// Per-recording lyric adjustments. Positive values advance the lyric
    /// timeline, matching OffsetMilliseconds; negative values delay it.
    /// Use the methods below to mutate this map so automatic saving is raised.
    /// </summary>
    public Dictionary<string, int> TrackLyricOffsetsMilliseconds
    {
        get => new(_trackLyricOffsetsMilliseconds, StringComparer.Ordinal);
        set
        {
            var normalized = NormalizeTrackLyricOffsets(value);
            if (DictionaryEquals(_trackLyricOffsetsMilliseconds, normalized))
            {
                return;
            }

            _trackLyricOffsetsMilliseconds = normalized;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(TrackLyricOffsetsMilliseconds)));
        }
    }

    public AppearancePreset? SavedAppearancePreset
    {
        get => _savedAppearancePreset;
        set
        {
            var normalized = value is null
                ? null
                : new AppearancePreset(
                    Math.Clamp(value.FontSize, 16, 42),
                    string.IsNullOrWhiteSpace(value.FontFamilyName)
                        ? DefaultFontFamilyName
                        : value.FontFamilyName.Trim(),
                    NormalizeRgb(value.LyricColor, DefaultLyricColor),
                    NormalizeRgb(value.ProgressColor, DefaultProgressColor),
                    NormalizeRgb(value.BackgroundColor, DefaultBackgroundColor),
                    Math.Clamp(value.BackgroundOpacity, 0, 1),
                    value.ProgressiveHighlight,
                    value.DoubleLine);
            if (Equals(_savedAppearancePreset, normalized))
            {
                return;
            }

            _savedAppearancePreset = normalized;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SavedAppearancePreset)));
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(HasSavedAppearancePreset)));
        }
    }

    [JsonIgnore]
    public bool HasSavedAppearancePreset => _savedAppearancePreset is not null;

    public NamedAppearancePreset CaptureAppearance(string name) => new(name,
        new AppearancePreset(FontSize, FontFamilyName, LyricColor, ProgressColor, BackgroundColor,
            BackgroundOpacity, ProgressiveHighlight, DoubleLine),
        SecondaryLineScale, SecondaryLineOpacity, TextBackingStyle, LyricFontWeight, LineTransitionStyle);

    public void ApplyAppearance(NamedAppearancePreset style)
    {
        SecondaryLineScale = style.SecondaryLineScale;
        SecondaryLineOpacity = style.SecondaryLineOpacity;
        TextBackingStyle = style.TextBackingStyle;
        LyricFontWeight = style.LyricFontWeight;
        LineTransitionStyle = style.LineTransitionStyle;
        ApplyColorsAndFont(style.Appearance);
    }

    public void SaveCurrentAppearancePreset()
    {
        var personal = CaptureAppearance("我的方案");
        SavedAppearancePreset = personal.Appearance;
        StorePersonalPreset(personal);
    }

    internal void StorePersonalPreset(NamedAppearancePreset personal)
    {
        var presets = CustomAppearancePresets;
        var index = presets.FindIndex(p => string.Equals(p.Name, personal.Name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) presets.Add(personal);
        else presets[index] = personal;
        CustomAppearancePresets = presets;
    }

    public bool ApplySavedAppearancePreset()
    {
        var preset = _savedAppearancePreset;
        if (preset is null)
        {
            return false;
        }

        var personal = _customAppearancePresets.Find(p => p.Name == "我的方案" && p.Appearance == preset);
        if (personal is not null) ApplyAppearance(personal);
        else ApplyColorsAndFont(preset);
        return true;
    }

    private void ApplyColorsAndFont(AppearancePreset preset)
    {
        FontSize = preset.FontSize;
        FontFamilyName = preset.FontFamilyName;
        LyricColor = preset.LyricColor;
        ProgressColor = preset.ProgressColor;
        BackgroundColor = preset.BackgroundColor;
        BackgroundOpacity = preset.BackgroundOpacity;
        ProgressiveHighlight = preset.ProgressiveHighlight;
        DoubleLine = preset.DoubleLine;
    }

    public int GetTrackLyricOffsetMilliseconds(TrackIdentity track) =>
        TrackLyricOffsetCalibration.Resolve(track, _trackLyricOffsetsMilliseconds);

    public void SetTrackLyricOffsetMilliseconds(TrackIdentity track, int value)
    {
        ArgumentNullException.ThrowIfNull(track);
        var key = TrackLyricOffsetCalibration.GetPrimaryKey(track);
        var normalizedValue = TrackLyricOffsetCalibration.Clamp(value);
        if (_trackLyricOffsetsMilliseconds.TryGetValue(key, out var current)
            && current == normalizedValue)
        {
            return;
        }

        var updated = new Dictionary<string, int>(
            _trackLyricOffsetsMilliseconds,
            StringComparer.Ordinal)
        {
            [key] = normalizedValue
        };
        TrackLyricOffsetsMilliseconds = updated;
    }

    public void ClearTrackLyricOffset(TrackIdentity track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var updated = new Dictionary<string, int>(
            _trackLyricOffsetsMilliseconds,
            StringComparer.Ordinal);
        var changed = updated.Remove(TrackLyricOffsetCalibration.GetPrimaryKey(track));
        changed |= updated.Remove(TrackLyricOffsetCalibration.GetMetadataKey(track));
        if (changed)
        {
            TrackLyricOffsetsMilliseconds = updated;
        }
    }

    public void ClearAllTrackLyricOffsets()
    {
        if (_trackLyricOffsetsMilliseconds.Count > 0)
        {
            TrackLyricOffsetsMilliseconds = [];
        }
    }

    public double? OverlayLeft
    {
        get => _overlayLeft;
        set => Set(ref _overlayLeft, value);
    }

    public double? OverlayTop
    {
        get => _overlayTop;
        set => Set(ref _overlayTop, value);
    }

    /// <summary>
    /// True keeps the lyric window's bottom edge attached to the working-area
    /// bottom while its content size changes. Null denotes a legacy saved
    /// position that has not yet been classified.
    /// </summary>
    public bool? OverlayDockedToTaskbar
    {
        get => _overlayDockedToTaskbar;
        set => Set(ref _overlayDockedToTaskbar, value);
    }

    public void ResetAppearanceSettings()
    {
        Locked = DefaultLocked;
        HoverHide = DefaultHoverHide;
        DoubleLine = DefaultDoubleLine;
        ProgressiveHighlight = DefaultProgressiveHighlight;
        ShowTrackInfoWhenNoLyrics = DefaultShowTrackInfoWhenNoLyrics;
        ExcludeFromCapture = DefaultExcludeFromCapture;
        CenterAlignedDragging = false;
        ApplyAppearance(AppearancePresets.Veil);
    }

    public void ResetLyricsDisplay()
    {
        ResetAppearanceSettings();
        NetEaseSyncDelayMilliseconds = DefaultNetEaseSyncDelayMilliseconds;
        NetEaseOffsetMilliseconds = 0;
        AppleMusicSyncAdvanceMilliseconds = DefaultAppleMusicSyncAdvanceMilliseconds;
        QqMusicOffsetMilliseconds = 0;
        KugouOffsetMilliseconds = 0;
        SpotifyOffsetMilliseconds = 0;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string NormalizeRgb(string? value, string fallback)
    {
        var hex = value?.Trim().TrimStart('#');
        if (hex?.Length == 8)
        {
            hex = hex[2..];
        }

        return hex?.Length == 6
            && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
            ? $"#{hex.ToUpperInvariant()}"
            : fallback;
    }

    private static Dictionary<string, int> NormalizeTrackLyricOffsets(
        IReadOnlyDictionary<string, int>? values)
    {
        var normalized = new Dictionary<string, int>(StringComparer.Ordinal);
        if (values is null)
        {
            return normalized;
        }

        foreach (var (rawKey, rawValue) in values)
        {
            var key = rawKey?.Trim();
            if (string.IsNullOrEmpty(key) || key.Length > 512)
            {
                continue;
            }

            normalized[key] = TrackLyricOffsetCalibration.Clamp(rawValue);
        }

        return normalized;
    }

    private static bool DictionaryEquals(
        IReadOnlyDictionary<string, int> left,
        IReadOnlyDictionary<string, int> right) =>
        left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
}

public sealed record AppearancePreset(
    double FontSize,
    string FontFamilyName,
    string LyricColor,
    string ProgressColor,
    string BackgroundColor,
    double BackgroundOpacity,
    bool ProgressiveHighlight,
    bool DoubleLine);

public sealed record NamedAppearancePreset(
    string Name, AppearancePreset Appearance, double SecondaryLineScale = 0.80,
    double SecondaryLineOpacity = 0.74, string TextBackingStyle = "SoftShadow",
    string LyricFontWeight = "SemiBold", string LineTransitionStyle = "SlideFade");

public static class PlayerFollowModes
{
    public const string Monitor = "Monitor";
    public const string Shortcut = "Shortcut";
    public const string Combined = "Combined";

    public static string Normalize(string? value)
    {
        if (string.Equals(value, Monitor, StringComparison.OrdinalIgnoreCase))
        {
            return Monitor;
        }

        return string.Equals(value, Combined, StringComparison.OrdinalIgnoreCase)
            ? Combined
            : Shortcut;
    }

    public static bool UsesMonitor(string? value) =>
        string.Equals(Normalize(value), Monitor, StringComparison.Ordinal)
        || string.Equals(Normalize(value), Combined, StringComparison.Ordinal);

    public static bool UsesShortcut(string? value) =>
        string.Equals(Normalize(value), Shortcut, StringComparison.Ordinal)
        || string.Equals(Normalize(value), Combined, StringComparison.Ordinal);

    public static string FromFeatures(bool monitorEnabled, bool shortcutEnabled) =>
        monitorEnabled && shortcutEnabled
            ? Combined
            : monitorEnabled
                ? Monitor
                : Shortcut;
}
