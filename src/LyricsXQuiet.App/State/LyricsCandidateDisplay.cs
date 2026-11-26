using LyricsXQuiet.Core.Lyrics;
using LyricsXQuiet.Core.Playback;

namespace LyricsXQuiet.App.State;

internal static class LyricsCandidateDisplay
{
    public static string GetSearchStatus(LyricsSearchResult result, string? recommendation)
    {
        var failure = result.Failures.Count == 0 ? string.Empty
            : $"{string.Join("、", result.Failures.Select(item => item.Provider).Distinct())}暂时不可用，请稍后重试。";
        if (result.Candidates.Count == 0)
            return failure.Length > 0 ? $"歌词来源请求失败。{failure}"
                : result.SuccessfulSources == 0 ? "请先在设置中启用至少一个歌词来源。"
                : "没有找到同步歌词，可以修改歌名或歌手后重试。";
        return $"找到 {result.Candidates.Count} 个版本；首选依据：{recommendation}。{failure}";
    }

    public static string GetQualitySummary(LyricsCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (candidate.Document.IsInstrumental)
        {
            return "纯音乐";
        }

        var hasWordTiming = candidate.Document.Lines.Any(line => line.Words.Count > 0);
        var hasTranslation = candidate.Document.Lines.Any(line =>
            !string.IsNullOrWhiteSpace(line.Translation));
        return string.Join(
            " · ",
            new[]
            {
                hasWordTiming ? "逐字" : "逐行",
                hasTranslation ? "含翻译" : string.Empty
            }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    public static string GetAccessibilityName(LyricsCandidate candidate, string duration)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var parts = new[]
        {
            candidate.Title,
            candidate.Artist,
            candidate.Album,
            duration,
            candidate.Provider,
            GetQualitySummary(candidate)
        };
        return string.Join("，", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    public static string GetRecommendationSummary(
        LyricsCandidate candidate,
        TrackIdentity track)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(track);

        var assessment = LyricsMatchScorer.Assess(candidate, track);
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(track.Artist))
        {
            parts.Add(!assessment.HasCandidateArtist
                ? "歌手未知"
                : assessment.ArtistMatches
                    ? "歌手相符"
                    : "歌手不同");
        }

        if (assessment.HasComparableAlbum)
        {
            parts.Add(assessment.AlbumMatches ? "专辑相符" : "专辑不同");
        }

        if (assessment.HasComparableDuration)
        {
            var differenceSeconds = (int)Math.Round(
                assessment.DurationDifference.TotalSeconds,
                MidpointRounding.AwayFromZero);
            parts.Add(differenceSeconds <= 1 ? "时长相符" : $"时长差 {differenceSeconds} 秒");
        }

        if (LyricsMatchScorer.HasReliableWordTiming(candidate.Document))
        {
            parts.Add("逐字");
        }

        if (string.Equals(candidate.Provider, "酷狗音乐", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("酷狗");
        }

        return parts.Count == 0 ? "综合信息最接近" : string.Join(" · ", parts);
    }
}
