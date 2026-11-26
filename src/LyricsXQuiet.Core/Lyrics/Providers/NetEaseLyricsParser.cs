using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LyricsXQuiet.Core.Lyrics.Providers;

internal static class NetEaseLyricsParser
{
    private static readonly Regex MillisecondLineRegex = new(
        @"^\[(?<start>\d+),(?<duration>\d+)\](?<body>[^\r\n]*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex YrcWordTagRegex = new(
        @"\((?<start>\d+),(?<duration>\d+),(?<flag>\d+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex KLyricWordTagRegex = new(
        @"\((?<offset>\d+),(?<duration>\d+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BrokenLrcTimestampRegex = new(
        @"(\[\d+:\d+):(\d+\])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex InstrumentalMarkerRegex = new(
        @"^\s*(?:纯音乐(?:[，,。.!！\s]*(?:请欣赏)?)?|此歌曲为没有填词的纯音乐|instrumental)\s*[，,。.!！]*\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static LyricDocument? Parse(
        string? lrc,
        string? kLyric,
        string? yrc,
        string? translatedLrc,
        string source)
    {
        IReadOnlyList<LyricLine> lines;
        if (TryParseMillisecondLyrics(yrc, yrcFormat: true, out var yrcLines))
        {
            lines = yrcLines;
        }
        else if (TryParseMillisecondLyrics(kLyric, yrcFormat: false, out var kLyricLines))
        {
            lines = kLyricLines;
        }
        else if (!string.IsNullOrWhiteSpace(lrc))
        {
            var parsed = LrcParser.Parse(FixBrokenTimestamps(lrc), source);
            if (!parsed.HasSynchronizedLines)
            {
                return null;
            }

            lines = parsed.Lines;
        }
        else
        {
            return null;
        }

        lines = MergeTranslation(lines, translatedLrc);
        var instrumental = lines.Count > 0
            && lines.All(line => InstrumentalMarkerRegex.IsMatch(line.Text));
        var rawText = LrcSerializer.Serialize(lines);
        return new LyricDocument(lines, source, rawText, instrumental);
    }

    private static bool TryParseMillisecondLyrics(
        string? content,
        bool yrcFormat,
        out IReadOnlyList<LyricLine> lines)
    {
        lines = Array.Empty<LyricLine>();
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var parsed = new List<LyricLine>();
        foreach (Match lineMatch in MillisecondLineRegex.Matches(content))
        {
            if (!long.TryParse(
                    lineMatch.Groups["start"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var lineStartMilliseconds)
                || !long.TryParse(
                    lineMatch.Groups["duration"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var lineDurationMilliseconds))
            {
                continue;
            }

            var body = lineMatch.Groups["body"].Value;
            var (text, words) = yrcFormat
                ? ParseYrcWords(body, lineStartMilliseconds, lineDurationMilliseconds)
                : ParseKLyricWords(body, lineStartMilliseconds, lineDurationMilliseconds);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            parsed.Add(new LyricLine(
                TimeSpan.FromMilliseconds(lineStartMilliseconds),
                text,
                null,
                words));
        }

        lines = parsed
            .OrderBy(line => line.Start)
            .GroupBy(line => line.Start)
            .Select(group => group.First())
            .ToArray();
        return lines.Count > 0;
    }

    private static (string Text, IReadOnlyList<LyricWordTiming> Words) ParseYrcWords(
        string body,
        long lineStartMilliseconds,
        long lineDurationMilliseconds)
    {
        var matches = YrcWordTagRegex.Matches(body);
        if (matches.Count == 0)
        {
            return (body.TrimEnd(), Array.Empty<LyricWordTiming>());
        }

        var builder = new StringBuilder(body.Length);
        var words = new List<LyricWordTiming>(matches.Count);
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            var segmentStart = match.Index + match.Length;
            var segmentEnd = index + 1 < matches.Count ? matches[index + 1].Index : body.Length;
            if (segmentEnd <= segmentStart)
            {
                continue;
            }

            var segment = body[segmentStart..segmentEnd];
            if (!long.TryParse(
                    match.Groups["start"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var wordStartMilliseconds))
            {
                builder.Append(segment);
                continue;
            }

            // YRC normally stores an absolute start time. A few older responses
            // use an offset from the line, so accept that form as well.
            if (wordStartMilliseconds < lineStartMilliseconds
                && wordStartMilliseconds <= lineDurationMilliseconds)
            {
                wordStartMilliseconds += lineStartMilliseconds;
            }

            var characterIndex = builder.Length;
            builder.Append(segment);
            _ = long.TryParse(match.Groups["duration"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var durationMilliseconds);
            words.Add(new LyricWordTiming(
                TimeSpan.FromMilliseconds(wordStartMilliseconds),
                characterIndex,
                segment.Length,
                durationMilliseconds > 0
                    ? TimeSpan.FromMilliseconds(wordStartMilliseconds + durationMilliseconds) : null));
        }

        var text = builder.ToString().TrimEnd();
        return (text, TrimWordRanges(words, text.Length));
    }

    private static (string Text, IReadOnlyList<LyricWordTiming> Words) ParseKLyricWords(
        string body,
        long lineStartMilliseconds,
        long lineDurationMilliseconds)
    {
        var matches = KLyricWordTagRegex.Matches(body);
        if (matches.Count == 0)
        {
            return (body.TrimEnd(), Array.Empty<LyricWordTiming>());
        }

        var builder = new StringBuilder(body.Length);
        var words = new List<LyricWordTiming>(matches.Count);
        long elapsedMilliseconds = 0;
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            var segmentStart = match.Index + match.Length;
            var segmentEnd = index + 1 < matches.Count ? matches[index + 1].Index : body.Length;
            if (segmentEnd <= segmentStart)
            {
                continue;
            }

            var segment = body[segmentStart..segmentEnd];
            _ = long.TryParse(
                match.Groups["offset"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var offsetMilliseconds);
            _ = long.TryParse(
                match.Groups["duration"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var durationMilliseconds);

            var relativeStart = offsetMilliseconds > 0 ? offsetMilliseconds : elapsedMilliseconds;
            relativeStart = Math.Clamp(relativeStart, 0, Math.Max(0, lineDurationMilliseconds));
            var characterIndex = builder.Length;
            builder.Append(segment);
            words.Add(new LyricWordTiming(
                TimeSpan.FromMilliseconds(lineStartMilliseconds + relativeStart),
                characterIndex,
                segment.Length,
                durationMilliseconds > 0
                    ? TimeSpan.FromMilliseconds(lineStartMilliseconds + relativeStart + durationMilliseconds) : null));
            elapsedMilliseconds = Math.Max(elapsedMilliseconds, relativeStart) + Math.Max(0, durationMilliseconds);
        }

        var text = builder.ToString().TrimEnd();
        return (text, TrimWordRanges(words, text.Length));
    }

    private static IReadOnlyList<LyricWordTiming> TrimWordRanges(
        IEnumerable<LyricWordTiming> words,
        int textLength) =>
        words
            .Where(word => word.StartCharacter < textLength)
            .Select(word => word with
            {
                Length = Math.Min(word.Length, textLength - word.StartCharacter)
            })
            .Where(word => word.Length > 0)
            .ToArray();

    private static IReadOnlyList<LyricLine> MergeTranslation(
        IReadOnlyList<LyricLine> primaryLines,
        string? translatedLrc)
    {
        if (string.IsNullOrWhiteSpace(translatedLrc))
        {
            return primaryLines;
        }

        var translation = LrcParser.Parse(FixBrokenTimestamps(translatedLrc), "网易云翻译");
        if (!translation.HasSynchronizedLines)
        {
            return primaryLines;
        }

        return primaryLines.Select(line =>
        {
            if (!string.IsNullOrWhiteSpace(line.Translation))
            {
                return line;
            }

            var nearest = translation.Lines
                .Select(candidate => new
                {
                    Line = candidate,
                    Delta = Math.Abs((candidate.Start - line.Start).TotalMilliseconds)
                })
                .Where(candidate => candidate.Delta <= 750)
                .OrderBy(candidate => candidate.Delta)
                .FirstOrDefault();
            var translatedText = nearest?.Line.Text;
            return string.IsNullOrWhiteSpace(translatedText)
                   || string.Equals(translatedText, line.Text, StringComparison.Ordinal)
                ? line
                : line with { Translation = translatedText };
        }).ToArray();
    }

    private static string SerializeAsLrc(IReadOnlyList<LyricLine> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.Append(FormatLineTimestamp(line.Start));
            AppendWordTimedText(builder, line);
            builder.AppendLine();
            if (!string.IsNullOrWhiteSpace(line.Translation)
                && !string.Equals(line.Translation, line.Text, StringComparison.Ordinal))
            {
                builder.Append(FormatLineTimestamp(line.Start));
                builder.AppendLine(line.Translation);
            }
        }

        return builder.ToString();
    }

    private static void AppendWordTimedText(StringBuilder builder, LyricLine line)
    {
        if (line.Words.Count == 0)
        {
            builder.Append(line.Text);
            return;
        }

        var cursor = 0;
        foreach (var word in line.Words.OrderBy(word => word.StartCharacter))
        {
            var start = Math.Clamp(word.StartCharacter, cursor, line.Text.Length);
            if (start > cursor)
            {
                builder.Append(line.Text.AsSpan(cursor, start - cursor));
            }

            var length = Math.Clamp(word.Length, 0, line.Text.Length - start);
            if (length == 0)
            {
                continue;
            }

            builder.Append(FormatWordTimestamp(word.Start));
            builder.Append(line.Text.AsSpan(start, length));
            cursor = start + length;
        }

        if (cursor < line.Text.Length)
        {
            builder.Append(line.Text.AsSpan(cursor));
        }
    }

    private static string FixBrokenTimestamps(string content) =>
        BrokenLrcTimestampRegex.Replace(content, "$1.$2");

    private static string FormatLineTimestamp(TimeSpan value)
    {
        var totalMinutes = (int)value.TotalMinutes;
        return FormattableString.Invariant($"[{totalMinutes:00}:{value.Seconds:00}.{value.Milliseconds:000}]");
    }

    private static string FormatWordTimestamp(TimeSpan value)
    {
        var totalMinutes = (int)value.TotalMinutes;
        return FormattableString.Invariant($"<{totalMinutes:00}:{value.Seconds:00}.{value.Milliseconds:000}>");
    }
}
