using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LyricsXQuiet.Core.Lyrics;

public static partial class LrcParser
{
    private static readonly Regex LineTimestampRegex = LineTimestampPattern();
    private static readonly Regex WordTimestampRegex = WordTimestampPattern();
    private static readonly Regex OffsetRegex = OffsetPattern();
    private static readonly Regex SourceRegex = SourcePattern();
    private static readonly Regex InstrumentalMarkerRegex = InstrumentalMarkerPattern();

    public static LyricDocument Parse(string content, string source = "local")
    {
        ArgumentNullException.ThrowIfNull(content);

        var offset = ReadOffset(content);
        var parsedLines = new List<ParsedLine>();

        foreach (var rawLine in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var timestampMatches = LineTimestampRegex.Matches(rawLine);
            if (timestampMatches.Count == 0)
            {
                continue;
            }

            var body = LineTimestampRegex.Replace(rawLine, string.Empty).Trim();
            var (text, words) = ParseWords(body, offset);
            if (string.IsNullOrWhiteSpace(text) && words.Count == 0)
            {
                continue;
            }

            foreach (Match timestampMatch in timestampMatches)
            {
                if (!TryParseTimestamp(timestampMatch, out var timestamp))
                {
                    continue;
                }

                parsedLines.Add(new ParsedLine(
                    ClampTime(timestamp + offset),
                    text,
                    words));
            }
        }

        var lines = parsedLines
            .OrderBy(line => line.Start)
            .GroupBy(line => line.Start)
            .Select(group =>
            {
                var candidates = group.Where(line => !string.IsNullOrWhiteSpace(line.Text)).ToArray();
                var primary = candidates.FirstOrDefault() ?? group.First();
                var translation = candidates
                    .Skip(1)
                    .Select(line => line.Text)
                    .FirstOrDefault(text => !string.Equals(text, primary.Text, StringComparison.Ordinal));

                return new LyricLine(
                    primary.Start,
                    primary.Text,
                    translation,
                    primary.Words);
            })
            .ToArray();

        var isInstrumental = lines.Length > 0
            && lines.All(line => InstrumentalMarkerRegex.IsMatch(line.Text));
        var declaredSource = SourceRegex.Match(content).Groups["source"].Value.Trim();
        var effectiveSource = string.IsNullOrWhiteSpace(declaredSource)
            ? source
            : string.Equals(source, "本地缓存", StringComparison.Ordinal)
                ? $"本地缓存 · {declaredSource}"
                : source;
        return new LyricDocument(lines, effectiveSource, content, isInstrumental);
    }

    private static (string Text, IReadOnlyList<LyricWordTiming> Words) ParseWords(
        string body,
        TimeSpan offset)
    {
        var matches = WordTimestampRegex.Matches(body);
        if (matches.Count == 0)
        {
            return (body, Array.Empty<LyricWordTiming>());
        }

        var builder = new StringBuilder();
        var words = new List<LyricWordTiming>();
        var prefix = body[..matches[0].Index];
        builder.Append(prefix);

        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            var segmentStart = match.Index + match.Length;
            var segmentEnd = index + 1 < matches.Count ? matches[index + 1].Index : body.Length;
            var segment = body[segmentStart..segmentEnd];
            if (!TryParseTimestamp(match, out var wordStart))
            {
                builder.Append(segment);
                continue;
            }

            var characterIndex = builder.Length;
            builder.Append(segment);
            if (segment.Length > 0)
            {
                TimeSpan? wordEnd = null;
                if (index + 1 < matches.Count)
                {
                    var next = matches[index + 1];
                    var nextEnd = next.Index + next.Length;
                    var isEndMarker = nextEnd == body.Length
                        || index + 2 < matches.Count && matches[index + 2].Index == nextEnd;
                    if (isEndMarker && TryParseTimestamp(next, out var end) && end > wordStart)
                        wordEnd = ClampTime(end + offset);
                }
                words.Add(new LyricWordTiming(
                    ClampTime(wordStart + offset),
                    characterIndex,
                    segment.Length,
                    wordEnd));
            }
        }

        return (builder.ToString().TrimEnd(), words);
    }

    private static TimeSpan ReadOffset(string content)
    {
        var match = OffsetRegex.Match(content);
        return match.Success && int.TryParse(
            match.Groups["offset"].Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var milliseconds)
            ? TimeSpan.FromMilliseconds(milliseconds)
            : TimeSpan.Zero;
    }

    private static bool TryParseTimestamp(Match match, out TimeSpan timestamp)
    {
        timestamp = TimeSpan.Zero;
        if (!int.TryParse(match.Groups["minute"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minute)
            || !double.TryParse(
                match.Groups["second"].Value.Replace(':', '.'),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var second))
        {
            return false;
        }

        timestamp = TimeSpan.FromMinutes(minute) + TimeSpan.FromSeconds(second);
        return true;
    }

    private static TimeSpan ClampTime(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private sealed record ParsedLine(
        TimeSpan Start,
        string Text,
        IReadOnlyList<LyricWordTiming> Words);

    [GeneratedRegex(@"\[(?<minute>\d{1,3}):(?<second>\d{1,2}(?:[\.:]\d{1,3})?)\]", RegexOptions.Compiled)]
    private static partial Regex LineTimestampPattern();

    [GeneratedRegex(@"<(?<minute>\d{1,3}):(?<second>\d{1,2}(?:[\.:]\d{1,3})?)>", RegexOptions.Compiled)]
    private static partial Regex WordTimestampPattern();

    [GeneratedRegex(@"\[offset:(?<offset>[+-]?\d+)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex OffsetPattern();

    [GeneratedRegex(@"^[\uFEFF \t]*\[source:(?<source>[^\]\r\n]+)\][ \t\r]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex SourcePattern();

    [GeneratedRegex(
        @"^\s*(纯音乐(?:[，,。.!！\s]*(?:请欣赏)?)?|此歌曲为没有填词的纯音乐|instrumental)\s*[，,。.!！]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex InstrumentalMarkerPattern();
}
