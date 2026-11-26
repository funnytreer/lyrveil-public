using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using LyricsXQuiet.Core.Settings;

namespace LyricsXQuiet.App.State;

public sealed class AppState : INotifyPropertyChanged
{
    private string _trackDisplay = "等待播放器";
    private string _playerDisplay = "尚未检测到媒体会话";
    private string _lyricsStatus = "播放音乐后会自动匹配歌词";
    private bool _showNetEasePreciseSyncHelp;
    private string _netEasePreciseSyncTitle = "网易云播放进度未连接";
    private string _netEasePreciseSyncDescription =
        "当前歌词靠本地计时，中途打开或拖动进度后可能对不上。请完全退出网易云，再从词间生成的组合快捷方式启动；网易云自行开机启动或更新后重启可能没有启用精确进度。";
    private string _netEasePreciseSyncActionText = "查看启用方式…";
    private string _netEaseRecoveryStatus = "";

    public string NetEaseRecoveryStatus
    {
        get => _netEaseRecoveryStatus;
        set => Set(ref _netEaseRecoveryStatus, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private string _activePlayerId = "";
    private string _sourceHealth = "尚未检查";
    private string _calibrationPrompt = "";
    public string ActivePlayerId { get => _activePlayerId; set => Set(ref _activePlayerId, value); }
    public string SourceHealth { get => _sourceHealth; set => Set(ref _sourceHealth, value); }
    public string CalibrationPrompt { get => _calibrationPrompt; set => Set(ref _calibrationPrompt, value); }

    private string _syncBadge = "等待播放器";
    private string _timingBadge = "等待歌词";
    public string SyncBadge { get => _syncBadge; set => Set(ref _syncBadge, value); }
    public string TimingBadge { get => _timingBadge; set => Set(ref _timingBadge, value); }

    public string TrackDisplay
    {
        get => _trackDisplay;
        set => Set(ref _trackDisplay, value);
    }

    public string PlayerDisplay
    {
        get => _playerDisplay;
        set => Set(ref _playerDisplay, value);
    }

    public string LyricsStatus
    {
        get => _lyricsStatus;
        set => Set(ref _lyricsStatus, value);
    }

    public bool ShowNetEasePreciseSyncHelp
    {
        get => _showNetEasePreciseSyncHelp;
        set => Set(ref _showNetEasePreciseSyncHelp, value);
    }

    public string NetEasePreciseSyncTitle
    {
        get => _netEasePreciseSyncTitle;
        set => Set(ref _netEasePreciseSyncTitle, value);
    }

    public string NetEasePreciseSyncDescription
    {
        get => _netEasePreciseSyncDescription;
        set => Set(ref _netEasePreciseSyncDescription, value);
    }

    public string NetEasePreciseSyncActionText
    {
        get => _netEasePreciseSyncActionText;
        set => Set(ref _netEasePreciseSyncActionText, value);
    }

    private void Set(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void Set(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class AppViewModel
{
    public AppViewModel(AppSettings settings, AppState state)
    {
        Settings = settings;
        State = state;
        FontFamilies = LyricFontCatalog.GetInstalledFonts();

        var canonicalSelection = FontFamilies.FirstOrDefault(name =>
            string.Equals(name, settings.FontFamilyName, StringComparison.OrdinalIgnoreCase));
        settings.FontFamilyName = canonicalSelection ?? FontFamilies[0];
    }

    public AppSettings Settings { get; }

    public AppState State { get; }

    public IReadOnlyList<string> FontFamilies { get; }

    public string VersionDisplay { get; } =
        typeof(AppViewModel).Assembly.GetName().Version?.ToString(3) ?? "未知";
}

internal static class LyricFontCatalog
{
    private const string DefaultFontFamilyName = "Microsoft YaHei UI";
    internal const string EmbeddedFallbackFontFamilyName = "Noto Sans CJK SC";

    private static readonly string[] PreferredFontFamilies =
    [
        DefaultFontFamilyName,
        EmbeddedFallbackFontFamilyName,
        "MiSans",
        "DengXian",
        "Microsoft JhengHei UI",
        "Yu Gothic UI",
        "Meiryo UI",
        "Malgun Gothic",
        "Noto Sans SC",
        "Noto Serif SC",
        "Source Han Sans SC",
        "Source Han Serif SC",
        "LXGW WenKai",
        "LXGW WenKai Lite",
        "IBM Plex Sans SC",
        "Sarasa Gothic SC",
        "Sarasa UI SC"
    ];

    private static readonly char[] RequiredLatinCharacters =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789".ToCharArray();

    private static readonly char[][] LyricScriptProbes =
    [
        "歌词音樂你我天地".ToCharArray(),
        "日本語かなカナ".ToCharArray(),
        "한국어사랑".ToCharArray()
    ];

    public static IReadOnlyList<string> GetInstalledFonts()
    {
        var installedFonts = Fonts.SystemFontFamilies
            .GroupBy(font => font.Source, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var suitableFonts = PreferredFontFamilies
            .Where(name => string.Equals(
                    name,
                    EmbeddedFallbackFontFamilyName,
                    StringComparison.OrdinalIgnoreCase)
                || (installedFonts.TryGetValue(name, out var fontFamily)
                    && SupportsLyricText(fontFamily)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (suitableFonts.Count == 0)
        {
            suitableFonts.AddRange(installedFonts.Values
                .Where(SupportsLyricText)
                .Select(font => font.Source)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .Take(6));
        }

        if (suitableFonts.Count == 0)
        {
            suitableFonts.Add(DefaultFontFamilyName);
        }

        return suitableFonts;
    }

    private static bool SupportsLyricText(System.Windows.Media.FontFamily fontFamily)
    {
        try
        {
            var typeface = new Typeface(
                fontFamily,
                System.Windows.FontStyles.Normal,
                System.Windows.FontWeights.Normal,
                System.Windows.FontStretches.Normal);
            if (!typeface.TryGetGlyphTypeface(out var glyphTypeface) || glyphTypeface.Symbol)
            {
                return false;
            }

            var glyphs = glyphTypeface.CharacterToGlyphMap;
            return RequiredLatinCharacters.All(character => glyphs.ContainsKey(character))
                && LyricScriptProbes.Any(probe =>
                    probe.All(character => glyphs.ContainsKey(character)));
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }
}

internal static class LyricFontResolver
{
    private const string DefaultFontFamilyName = "Microsoft YaHei UI";
    private const string EmbeddedFallbackFontReference =
        "./Assets/Fonts/#Noto Sans CJK SC";

    private static readonly Uri ApplicationBaseUri = new("pack://application:,,,/");

    private static readonly string[] FallbackFontFamilies =
    [
        EmbeddedFallbackFontReference,
        "Segoe UI",
        DefaultFontFamilyName,
        "Microsoft JhengHei UI",
        "Yu Gothic UI",
        "Malgun Gothic",
        "Global User Interface"
    ];

    public static System.Windows.Media.FontFamily Create(string? primaryFontFamilyName)
    {
        var fontFamilies = new[] { ResolveFontReference(primaryFontFamilyName) }
            .Concat(FallbackFontFamilies)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return new System.Windows.Media.FontFamily(
            ApplicationBaseUri,
            string.Join(", ", fontFamilies));
    }

    private static string? ResolveFontReference(string? fontFamilyName) =>
        string.Equals(
            fontFamilyName,
            LyricFontCatalog.EmbeddedFallbackFontFamilyName,
            StringComparison.OrdinalIgnoreCase)
            ? EmbeddedFallbackFontReference
            : fontFamilyName;
}
