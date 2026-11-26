namespace LyricsXQuiet.Core.Lyrics;

public sealed class LyricTimeline
{
    private readonly LyricDocument _document;

    public LyricTimeline(LyricDocument document)
    {
        _document = document;
    }

    public TimeSpan? FirstLineStart => _document.Lines.Count > 0 ? _document.Lines[0].Start : null;

    public LyricFrame? GetFrame(TimeSpan position, bool includeSecondaryLine = true)
    {
        var lines = _document.Lines;
        if (lines.Count == 0 || position < lines[0].Start)
        {
            return null;
        }

        var low = 0;
        var high = lines.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (lines[middle].Start <= position)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        var index = Math.Max(0, high);
        var current = lines[index];
        var lastWord = current.Words.LastOrDefault();
        var lineEnd = index + 1 < lines.Count
            ? lines[index + 1].Start
            : lastWord?.End is { } end && end > lastWord.Start
                ? end
                : current.Start + TimeSpan.FromSeconds(5);

        var secondary = string.Empty;
        if (includeSecondaryLine)
        {
            secondary = !string.IsNullOrWhiteSpace(current.Translation)
                ? current.Translation
                : index + 1 < lines.Count ? lines[index + 1].Text : string.Empty;
        }

        var hasWordTiming = current.Words.Count > 0;
        var progress = hasWordTiming
            ? GetWordProgress(current, position, lineEnd)
            : GetLineProgress(current.Start, position, lineEnd);

        return new LyricFrame(
            current.Text,
            secondary,
            Math.Clamp(progress, 0, 1),
            hasWordTiming,
            index);
    }

    private static double GetLineProgress(
        TimeSpan lineStart,
        TimeSpan position,
        TimeSpan lineEnd)
    {
        var duration = Math.Max(1, (lineEnd - lineStart).TotalMilliseconds);
        return Math.Clamp((position - lineStart).TotalMilliseconds / duration, 0, 1);
    }

    private static double GetWordProgress(LyricLine line, TimeSpan position, TimeSpan lineEnd)
    {
        if (line.Text.Length == 0 || line.Words.Count == 0 || position < line.Words[0].Start)
        {
            return 0;
        }

        var wordIndex = line.Words.Count - 1;
        for (var index = 0; index < line.Words.Count - 1; index++)
        {
            if (position < line.Words[index + 1].Start)
            {
                wordIndex = index;
                break;
            }
        }

        var word = line.Words[wordIndex];
        var nextTime = wordIndex + 1 < line.Words.Count
            ? line.Words[wordIndex + 1].Start
            : lineEnd;
        if (word.End is { } reportedEnd && reportedEnd >= word.Start)
            nextTime = reportedEnd < nextTime ? reportedEnd : nextTime;
        var nextCharacter = Math.Min(line.Text.Length, word.StartCharacter + word.Length);
        var segmentDuration = Math.Max(1, (nextTime - word.Start).TotalMilliseconds);
        var localProgress = Math.Clamp((position - word.Start).TotalMilliseconds / segmentDuration, 0, 1);
        var characterProgress = word.StartCharacter + ((nextCharacter - word.StartCharacter) * localProgress);
        return characterProgress / line.Text.Length;
    }
}
