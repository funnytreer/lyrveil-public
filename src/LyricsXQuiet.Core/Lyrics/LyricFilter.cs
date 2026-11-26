using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace LyricsXQuiet.Core.Lyrics;

public sealed record LyricPatternError(int LineNumber, string Pattern);

public static class LyricFilter
{
    private static readonly object PatternCacheLock = new();
    private static readonly Dictionary<string, PatternSet> PatternCache = new(StringComparer.Ordinal);
    private static readonly Queue<string> PatternCacheOrder = new();
    public const string LegacyDefaultPatterns = """
        ^\s*(作词|作曲|编曲|制作人|监制|混音|母带|录音|和声|吉他|贝斯|鼓|弦乐|OP|SP|发行|出品)\s*[:：].*$
        ^\s*(Lyrics|Composer|Arranger|Producer|Mixed by|Mastered by)\s*[:：].*$
        ^\s*(纯音乐|此歌曲为没有填词的纯音乐|暂无歌词)\s*[，,。.!！]*\s*$
        """;

    public const string DefaultPatterns = """
        ^\s*(纯音乐|此歌曲为没有填词的纯音乐|暂无歌词)\s*[，,。.!！]*\s*$
        """;

    private static readonly Regex CreditLineRegex = CreateRegex(
        @"^\s*(?:(?:词|作词|填词|歌词|曲|作曲|编曲|制作人|监制|混音(?:师|工程师)?|母带(?:处理|工程师)?|录音(?:师|工程师|棚)?|和声(?:编写|设计)?|配唱制作人|人声编辑|吉他|贝斯|鼓手?|弦乐|笛子|钢琴|古筝|二胡|封面(?:绘画|设计|制作)?|视觉(?:设计|制作)?|物料统筹|统筹|企划|策划|OP|SP|发行(?:单位|公司)?|出品(?:人|单位|公司)?|主唱|演唱)(?:\s*/?\s*[A-Za-z][A-Za-z .&/'-]*)?|Lyrics?|Lyricist|Composer|Arranger|Producer|Co-Producer|Executive Producer|Mixed by|Mixing Engineer|Mastered by|Mastering Engineer|Recording Engineer|Recorded by|Vocal|Chorus|Guitar|Bass|Drummer|Drums|Strings?|Flute|Piano|Chinese Zither|Cover(?: Painting| Art| Design)?|Planning|A&R|Publisher|Released by|Label)\s*[:：].*$");

    private static readonly Regex CopyrightNoticeRegex = CreateRegex(
        @"^\s*[（(]?\s*(?:未经.{0,40}(?:许可|授权).{0,40}(?:翻唱|翻录|复制)|版权所有|Copyright).*$");

    public static LyricDocument RemoveCredits(LyricDocument document)
    {
        var lines = new List<LyricLine>(document.Lines.Count);
        foreach (var line in document.Lines)
        {
            if (IsCreditLine(line.Text))
            {
                continue;
            }

            var translation = !string.IsNullOrWhiteSpace(line.Translation)
                && IsCreditLine(line.Translation)
                    ? null
                    : line.Translation;
            lines.Add(new LyricLine(line.Start, line.Text, translation, line.Words));
        }

        return new LyricDocument(
            lines,
            document.Source,
            document.RawText,
            document.IsInstrumental);
    }

    public static bool IsLegacyDefaultPatternSet(string patterns) =>
        string.Equals(
            NormalizePatternSet(patterns),
            NormalizePatternSet(LegacyDefaultPatterns),
            StringComparison.Ordinal);

    public static LyricDocument Apply(LyricDocument document, string patterns)
    {
        var compiled = GetPatternSet(patterns);
        if (compiled.Rules.Count == 0) return document;
        var stopwatch = Stopwatch.StartNew();
        foreach (var rule in compiled.Rules)
        {
            if (compiled.TimedOutLines.ContainsKey(rule.LineNumber)) continue;
            try
            {
                // Commit one rule at a time. A timeout discards that rule's
                // partial edits, keeping the document consistent across lines.
                document = ApplyRule(document, rule.Regex, stopwatch);
            }
            catch (RegexMatchTimeoutException)
            {
                compiled.TimedOutLines.TryAdd(rule.LineNumber, 0);
            }
        }
        return document with
        {
            Lines = document.Lines.Select(line => line with
            {
                Text = line.Text.Trim(),
                Translation = line.Translation?.Trim(),
                Words = string.Equals(line.Text, line.Text.Trim(), StringComparison.Ordinal)
                    ? line.Words : Array.Empty<LyricWordTiming>()
            }).ToArray()
        };
    }

    private static LyricDocument ApplyRule(LyricDocument document, Regex filter, Stopwatch stopwatch)
    {
        var lines = new List<LyricLine>(document.Lines.Count);
        foreach (var line in document.Lines)
        {
            if (stopwatch.ElapsedMilliseconds >= 250) throw new RegexMatchTimeoutException();
            var text = filter.Replace(line.Text, string.Empty);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var translation = string.IsNullOrWhiteSpace(line.Translation)
                ? null
                : filter.Replace(line.Translation, string.Empty);
            if (string.IsNullOrWhiteSpace(translation))
            {
                translation = null;
            }

            var words = string.Equals(text, line.Text, StringComparison.Ordinal)
                ? line.Words
                : Array.Empty<LyricWordTiming>();
            lines.Add(new LyricLine(line.Start, text, translation, words));
        }

        return new LyricDocument(lines, document.Source, document.RawText, document.IsInstrumental);
    }

    public static IReadOnlyList<string> FindInvalidPatterns(string patterns)
        => FindPatternErrors(patterns)
            .Select(error => error.Pattern)
            .ToArray();

    public static IReadOnlyList<LyricPatternError> FindPatternErrors(string patterns) =>
        GetPatternSet(patterns).Errors;

    public static IReadOnlyList<int> FindTimedOutPatternLines(string patterns) =>
        GetPatternSet(patterns).TimedOutLines.Keys.Order().ToArray();

    private static PatternSet GetPatternSet(string patterns)
    {
        patterns ??= string.Empty;
        lock (PatternCacheLock)
        {
            if (PatternCache.TryGetValue(patterns, out var cached)) return cached;
            var invalid = new List<LyricPatternError>();
            var rules = new List<PatternRule>();
            foreach (var (lineNumber, pattern) in ReadPatternLines(patterns))
            {
                try { rules.Add(new(lineNumber, CreateRegex(pattern))); }
                catch (ArgumentException) { invalid.Add(new(lineNumber, pattern)); }
            }
            if (PatternCache.Count >= 8)
            {
                PatternCache.Remove(PatternCacheOrder.Dequeue());
            }
            var result = new PatternSet(rules, invalid);
            PatternCache.Add(patterns, result);
            PatternCacheOrder.Enqueue(patterns);
            return result;
        }
    }

    private sealed record PatternRule(int LineNumber, Regex Regex);
    private sealed record PatternSet(IReadOnlyList<PatternRule> Rules, IReadOnlyList<LyricPatternError> Errors)
    {
        public ConcurrentDictionary<int, byte> TimedOutLines { get; } = new();
    }

    private static IEnumerable<string> ReadPatterns(string patterns) =>
        ReadPatternLines(patterns).Select(item => item.Pattern);

    private static IEnumerable<(int LineNumber, string Pattern)> ReadPatternLines(string patterns)
    {
        var lines = (patterns ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var pattern = lines[index].Trim();
            if (!string.IsNullOrWhiteSpace(pattern))
            {
                yield return (index + 1, pattern);
            }
        }
    }

    private static string NormalizePatternSet(string patterns) =>
        string.Join('\n', ReadPatterns(patterns));

    private static Regex CreateRegex(string pattern) => new(
        pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static bool IsCreditLine(string text)
    {
        try
        {
            return CreditLineRegex.IsMatch(text)
                || CopyrightNoticeRegex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
