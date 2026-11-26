using System.Globalization;
using System.Text;

namespace LyricsXQuiet.Core.Lyrics;

/// <summary>
/// Serializes the provider-neutral lyric model to the enhanced LRC format that
/// <see cref="LrcParser"/> can read again. Provider-specific payloads such as
/// KRC/YRC must never be written directly into the user-facing .lrc cache.
/// </summary>
public static class LrcSerializer
{
    public static string Serialize(LyricDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Serialize(document.Lines);
    }

    public static string Serialize(IReadOnlyList<LyricLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var builder = new StringBuilder();
        foreach (var line in lines.OrderBy(line => line.Start))
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
            if (word.End is { } end && end > word.Start)
            {
                // An empty enhanced-LRC segment terminates this word. The next
                // start tag can then retain a pause without extending the word.
                builder.Append(FormatWordTimestamp(end));
            }
            cursor = start + length;
        }

        if (cursor < line.Text.Length)
        {
            builder.Append(line.Text.AsSpan(cursor));
        }
    }

    private static string FormatLineTimestamp(TimeSpan value)
    {
        var clamped = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        var totalMinutes = (int)clamped.TotalMinutes;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[{totalMinutes:00}:{clamped.Seconds:00}.{clamped.Milliseconds:000}]");
    }

    private static string FormatWordTimestamp(TimeSpan value)
    {
        var clamped = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        var totalMinutes = (int)clamped.TotalMinutes;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"<{totalMinutes:00}:{clamped.Seconds:00}.{clamped.Milliseconds:000}>");
    }
}
