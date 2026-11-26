using System.Text;
using LyricsXQuiet.Core.Lyrics;
using LyricsXQuiet.Core.Playback;

namespace LyricsXQuiet.App.State;

internal sealed class LyricsCandidateViewModel
{
    public LyricsCandidateViewModel(
        LyricsCandidate candidate,
        TrackIdentity matchTrack,
        bool isRecommended)
    {
        Candidate = candidate;
        Title = candidate.Title;
        RecommendationText = isRecommended ? "首选" : string.Empty;
        RecommendationSummary = LyricsCandidateDisplay.GetRecommendationSummary(
            candidate,
            matchTrack);
        var duration = candidate.Duration > TimeSpan.Zero
            ? $"{(int)candidate.Duration.TotalMinutes:00}:{candidate.Duration.Seconds:00}"
            : "时长未知";
        ArtistAndDuration = string.IsNullOrWhiteSpace(candidate.Artist)
            ? duration
            : $"{candidate.Artist} · {duration}";
        AlbumAndProvider = string.IsNullOrWhiteSpace(candidate.Album)
            ? candidate.Provider
            : $"{candidate.Album} · {candidate.Provider}";
        QualitySummary = LyricsCandidateDisplay.GetQualitySummary(candidate);
        AccessibilityName = (isRecommended
                ? $"首选依据，{RecommendationSummary}，"
                : string.Empty)
            + LyricsCandidateDisplay.GetAccessibilityName(candidate, duration);

    }

    public LyricsCandidate Candidate { get; }

    public string Title { get; }

    public string Provider => Candidate.Provider;

    public string RecommendationText { get; }

    public string RecommendationSummary { get; }

    public string ArtistAndDuration { get; }

    public string AlbumAndProvider { get; }

    public string QualitySummary { get; }

    public string AccessibilityName { get; }

    public string Preview => BuildPreview(Candidate.Document);

    private static string BuildPreview(LyricDocument document)
    {
        var builder = new StringBuilder();
        foreach (var line in document.Lines.Take(32))
        {
            builder.Append('[')
                .Append((int)line.Start.TotalMinutes)
                .Append(':')
                .Append(line.Start.Seconds.ToString("00"))
                .Append("] ")
                .AppendLine(line.Text);
            if (!string.IsNullOrWhiteSpace(line.Translation))
            {
                builder.Append("     ").AppendLine(line.Translation);
            }
        }

        return builder.ToString().TrimEnd();
    }
}
