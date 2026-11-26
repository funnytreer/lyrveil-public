using System.Text;

namespace LyricsXQuiet.Core.Playback;

public sealed record TrackIdentity(
    string Title,
    string Artist,
    string Album,
    TimeSpan Duration,
    string SourceAppUserModelId = "",
    string SourceTrackId = "")
{
    public bool IsUsable => !string.IsNullOrWhiteSpace(Title);

    public string StableKey => IsAppleMusicSource
        ? string.Join('|',
            NormalizeForMatching(Title),
            NormalizeForMatching(Artist),
            NormalizeForMatching(Album))
        : string.Join('|',
            NormalizeForMatching(Title),
            NormalizeForMatching(Artist),
            Math.Round(Duration.TotalSeconds));

    // StableKey intentionally tolerates gradual metadata enrichment. Lyric operations need a
    // stricter identity so two releases with the same title, artist and rounded duration cannot
    // share an in-flight request merely because their albums differ.
    public string LyricsOperationKey => string.Join('|',
        NormalizeForMatching(Title),
        NormalizeForMatching(Artist),
        NormalizeForMatching(Album),
        Math.Round(Duration.TotalSeconds));

    // Calibration remains separate because it may use a source-specific ID,
    // while lyric caches also need a metadata fallback across integrations.
    public string LyricCalibrationKey =>
        TrackLyricOffsetCalibration.GetPrimaryKey(this);

    public string DisplayName => string.IsNullOrWhiteSpace(Artist)
        ? Title
        : $"{Title} · {Artist}";

    private bool IsAppleMusicSource =>
        SourceAppUserModelId.Contains("applemusic", StringComparison.OrdinalIgnoreCase)
        || SourceAppUserModelId.Contains("apple music", StringComparison.OrdinalIgnoreCase);

    internal static string NormalizeForMatching(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
