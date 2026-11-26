using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LyricsXQuiet.Core.Playback;

namespace LyricsXQuiet.Core.Lyrics;

public static partial class LyricsMatchScorer
{
    private static readonly string[] StrongVersionMarkers =
    [
        "伴奏", "纯音乐", "instrumental", "karaoke", "off vocal"
    ];

    private static readonly string[] VersionMarkers =
    [
        "现场", "現場", "演唱会", "演唱會", "巡演", "巡回", "巡迴", "音乐节", "音樂節",
        "live", "concert", "tour",
        "remix", "混音", "cover", "翻唱", "dj", "demo", "伴奏",
        "instrumental", "karaoke", "off vocal", "acoustic", "unplugged", "radio edit",
        "extended mix", "sped up", "slowed", "reverb"
    ];

    [Flags]
    private enum VersionTraits
    {
        None = 0,
        Live = 1 << 0,
        Remix = 1 << 1,
        Acoustic = 1 << 2,
        Instrumental = 1 << 3,
        Cover = 1 << 4,
        Demo = 1 << 5,
        Dj = 1 << 6,
        AlternateEdit = 1 << 7
    }

    /// <summary>
    /// Determines whether a search result can reasonably be another version of the requested song.
    /// Artist and duration deliberately do not participate here: covers and live recordings must remain
    /// available for manual selection. Those fields are used only to order the surviving candidates.
    /// </summary>
    public static bool IsRelevant(LyricsCandidate candidate, TrackIdentity track)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(track);

        if (!candidate.Document.HasSynchronizedLines || candidate.Document.IsInstrumental)
        {
            return false;
        }

        return IsRelevantTitle(candidate.Title, track.Title);
    }

    public static bool IsRelevantTitle(string candidateTitle, string requestedTitle)
    {
        if (string.IsNullOrWhiteSpace(candidateTitle) || string.IsNullOrWhiteSpace(requestedTitle))
        {
            return false;
        }

        var candidateAliases = BuildTitleAliases(candidateTitle);
        var requestedAliases = BuildTitleAliases(requestedTitle);
        if (candidateAliases.Overlaps(requestedAliases))
        {
            return true;
        }

        // Chinese-script canonicalization is intentionally limited to explicit live releases.
        // It exists so metadata such as “倒帶 - 演唱會版” can match “倒带 (Live)”, not as a
        // general simplified/traditional lyric preference or candidate-ranking rule.
        if (!HasLiveVersionMarker(candidateTitle) || !HasLiveVersionMarker(requestedTitle))
        {
            return false;
        }

        return BuildTitleAliases(ChineseMetadataNormalizer.NormalizeForMatching(candidateTitle))
            .Overlaps(BuildTitleAliases(
                ChineseMetadataNormalizer.NormalizeForMatching(requestedTitle)));
    }

    internal static string NormalizeIdentityPart(string value) => Normalize(value);

    internal static string GetSearchTitle(string value)
    {
        // Providers are much more likely to find the correct release when the query contains the
        // song's base title rather than a translated version label. Remove bracketed annotations
        // only when they really describe a version; ordinary subtitles remain part of the query.
        var withoutVersionAnnotations = BracketedTextRegex().Replace(value, match =>
            IsVersionDescription(match.Groups["content"].Value) ? " " : match.Value);
        var stripped = StripVersionSuffix(withoutVersionAnnotations);
        var searchTitle = string.IsNullOrWhiteSpace(stripped) ? value.Trim() : stripped;
        return HasLiveVersionMarker(value)
            ? ChineseMetadataNormalizer.NormalizeForMatching(searchTitle)
            : searchTitle;
    }

    internal static double ScoreMetadata(
        string title,
        string artist,
        string album,
        TimeSpan duration,
        TrackIdentity track)
    {
        var requestedVersion = DetectVersionTraits(track.Title, track.Artist, track.Album);
        var candidateVersion = DetectVersionTraits(title, artist, album);
        var compareChineseLiveMetadata = requestedVersion.HasFlag(VersionTraits.Live)
            && candidateVersion.HasFlag(VersionTraits.Live);
        var score = TitleDistance(title, track.Title, compareChineseLiveMetadata) * 60;
        if (!string.IsNullOrWhiteSpace(track.Artist))
        {
            score += ArtistDistance(artist, track.Artist) * 30;

            // Same-title covers remain available for manual selection, but a completely different
            // singer must not outrank a credible spelling/alias of the requested artist merely
            // because its duration happens to be closer. Missing artist metadata receives a
            // smaller penalty so providers with incomplete records can still be useful fallbacks.
            if (!ArtistsStronglyMatch(artist, track.Artist))
            {
                score += string.IsNullOrWhiteSpace(artist) ? 60 : 120;
            }
        }

        if (!string.IsNullOrWhiteSpace(track.Album)
            && !string.IsNullOrWhiteSpace(album))
        {
            var albumDistance = TextDistance(album, track.Album, compareChineseLiveMetadata);
            var albumWeight = requestedVersion.HasFlag(VersionTraits.Live) ? 28d : 14d;
            score += albumDistance * albumWeight;

            if (ReleaseNamesStronglyMatch(album, track.Album, compareChineseLiveMetadata))
            {
                score -= requestedVersion.HasFlag(VersionTraits.Live) ? 14 : 5;
            }

            if (requestedVersion.HasFlag(VersionTraits.Live)
                && candidateVersion.HasFlag(VersionTraits.Live)
                && HaveConflictingReleaseYears(album, track.Album))
            {
                score += 18;
            }
        }

        if (track.Duration > TimeSpan.Zero)
        {
            score += DurationMismatchPenalty(duration, track.Duration);
        }

        score += VersionCompatibilityPenalty(requestedVersion, candidateVersion);
        return score;
    }

    public static double Score(LyricsCandidate candidate, TrackIdentity track)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(track);

        if (!candidate.Document.HasSynchronizedLines || candidate.Document.IsInstrumental)
        {
            return 10_000;
        }

        // This score represents release identity only. Lyrics quality is deliberately applied as a
        // later tie-breaker so word timing can never outweigh title, artist, album, version or duration.
        return ScoreMetadata(
            candidate.Title,
            candidate.Artist,
            candidate.Album,
            candidate.Duration,
            track);
    }

    public static LyricsMatchAssessment Assess(
        LyricsCandidate candidate,
        TrackIdentity track)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(track);

        var requestedVersion = DetectVersionTraits(track.Title, track.Artist, track.Album);
        var candidateVersion = DetectVersionTraits(
            candidate.Title,
            candidate.Artist,
            candidate.Album);
        var compareChineseLiveMetadata = requestedVersion.HasFlag(VersionTraits.Live)
            && candidateVersion.HasFlag(VersionTraits.Live);
        var hasCandidateArtist = !string.IsNullOrWhiteSpace(candidate.Artist);
        var hasComparableAlbum = !string.IsNullOrWhiteSpace(track.Album)
            && !string.IsNullOrWhiteSpace(candidate.Album);
        var hasComparableDuration = track.Duration > TimeSpan.Zero
            && candidate.Duration > TimeSpan.Zero;
        return new LyricsMatchAssessment(
            hasCandidateArtist,
            hasCandidateArtist
                && (!string.IsNullOrWhiteSpace(track.Artist)
                    ? ArtistsStronglyMatch(candidate.Artist, track.Artist)
                    : true),
            hasComparableAlbum,
            hasComparableAlbum
                && ReleaseNamesStronglyMatch(
                    candidate.Album,
                    track.Album,
                    compareChineseLiveMetadata),
            hasComparableDuration,
            hasComparableDuration
                ? (candidate.Duration - track.Duration).Duration()
                : TimeSpan.Zero);
    }

    public static bool HasReliableWordTiming(LyricDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var lyricLines = document.Lines
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .ToArray();
        if (lyricLines.Length == 0)
        {
            return false;
        }

        var timedLines = lyricLines.Count(line => line.Words.Count > 0);
        // A single malformed enhanced-LRC tag must not make an ordinary line-timed file outrank a
        // genuine KRC/YRC document. Intros and interludes may legitimately have no word tags, so a
        // majority of timed lyric lines is sufficient.
        return timedLines > 0 && timedLines * 2 >= lyricLines.Length;
    }

    private static double VersionCompatibilityPenalty(
        VersionTraits requested,
        VersionTraits candidate)
    {
        if (requested == VersionTraits.None)
        {
            var penalty = candidate.HasFlag(VersionTraits.Live) ? 58d : 0d;
            penalty += candidate.HasFlag(VersionTraits.Instrumental) ? 80d : 0d;
            penalty += (candidate & ~(VersionTraits.Live | VersionTraits.Instrumental)) != 0
                ? 36d
                : 0d;
            return penalty;
        }

        var missingRequestedTraits = requested & ~candidate;
        var unexpectedCandidateTraits = candidate & ~requested;
        var score = CountTraits(missingRequestedTraits) * 48d;
        score += CountTraits(unexpectedCandidateTraits) * 24d;
        if (requested.HasFlag(VersionTraits.Live)
            && !candidate.HasFlag(VersionTraits.Live))
        {
            score += 28;
        }

        return score;
    }

    private static VersionTraits DetectVersionTraits(string title, string artist, string album)
    {
        // Version information is not consistently stored by players and lyric providers. Some put
        // it in the title, others in the album, and remixes/covers are sometimes expressed only in
        // the artist credit. Treat the three fields as one release identity for version matching.
        var value = $"{title} {artist} {album}"
            .Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant();
        var latinWords = LatinWordRegex().Matches(value)
            .Select(match => match.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var traits = VersionTraits.None;

        if (ContainsAny(
                value,
                "现场", "現場", "演唱会", "演唱會", "巡演", "巡回", "巡迴", "音乐节", "音樂節")
            || latinWords.Overlaps(["live", "concert", "tour", "festival"]))
        {
            traits |= VersionTraits.Live;
        }

        if (ContainsAny(value, "混音")
            || latinWords.Overlaps(["remix", "remixed", "mix"]))
        {
            traits |= VersionTraits.Remix;
        }

        if (ContainsAny(value, "不插电")
            || latinWords.Overlaps(["acoustic", "unplugged"]))
        {
            traits |= VersionTraits.Acoustic;
        }

        if (ContainsAny(value, "伴奏", "纯音乐", "卡拉ok", "卡拉 OK")
            || latinWords.Overlaps(["instrumental", "karaoke", "backing"]))
        {
            traits |= VersionTraits.Instrumental;
        }

        if (ContainsAny(value, "翻唱") || latinWords.Contains("cover"))
        {
            traits |= VersionTraits.Cover;
        }

        if (latinWords.Contains("demo"))
        {
            traits |= VersionTraits.Demo;
        }

        if (latinWords.Contains("dj") || ContainsAny(value, "dj版", "DJ版"))
        {
            traits |= VersionTraits.Dj;
        }

        if (ContainsAny(value, "加速版", "慢速版")
            || latinWords.Overlaps(["radio", "edit", "extended", "sped", "slowed", "reverb"]))
        {
            traits |= VersionTraits.AlternateEdit;
        }

        return traits;
    }

    private static bool ContainsAny(string value, params string[] markers) =>
        markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static int CountTraits(VersionTraits traits) =>
        System.Numerics.BitOperations.PopCount((uint)traits);

    private static bool ReleaseNamesStronglyMatch(
        string candidateAlbum,
        string requestedAlbum,
        bool canonicalizeChinese)
    {
        var candidate = Normalize(candidateAlbum, canonicalizeChinese);
        var requested = Normalize(requestedAlbum, canonicalizeChinese);
        return candidate.Length > 0
            && requested.Length > 0
            && (string.Equals(candidate, requested, StringComparison.Ordinal)
                || candidate.Contains(requested, StringComparison.Ordinal)
                || requested.Contains(candidate, StringComparison.Ordinal));
    }

    private static bool HaveConflictingReleaseYears(string candidateAlbum, string requestedAlbum)
    {
        var candidateYears = ReleaseYearRegex().Matches(candidateAlbum)
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);
        var requestedYears = ReleaseYearRegex().Matches(requestedAlbum)
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);
        return candidateYears.Count > 0
            && requestedYears.Count > 0
            && !candidateYears.Overlaps(requestedYears);
    }

    private static double TextDistance(
        string candidate,
        string requested,
        bool canonicalizeChinese = false)
    {
        var left = Normalize(candidate, canonicalizeChinese);
        var right = Normalize(requested, canonicalizeChinese);
        if (left.Length == 0 || right.Length == 0)
        {
            return left.Length == right.Length ? 0 : 1;
        }

        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 0;
        }

        if (left.Contains(right, StringComparison.Ordinal)
            || right.Contains(left, StringComparison.Ordinal))
        {
            return 0.12;
        }

        return (double)LevenshteinDistance(left, right) / Math.Max(left.Length, right.Length);
    }

    private static double ArtistDistance(string candidate, string requested) =>
        ArtistsStronglyMatch(candidate, requested)
            ? 0
            : TextDistance(candidate, requested, canonicalizeChinese: true);

    private static double DurationMismatchPenalty(TimeSpan candidate, TimeSpan requested)
    {
        if (candidate <= TimeSpan.Zero)
        {
            // Missing duration is less trustworthy than a close measured duration, but remains a
            // usable fallback because some providers legitimately omit this field.
            return 20;
        }

        var difference = Math.Abs(candidate.TotalSeconds - requested.TotalSeconds);
        var penalty = difference * 0.8;

        // Do not flatten every large mismatch into the same capped score. A two-minute recording
        // and a six-minute recording with the same title are almost certainly different songs or
        // arrangements, and must rank much lower than a candidate only a few seconds away.
        var substantialMismatch = Math.Max(20, requested.TotalSeconds * 0.08);
        var severeMismatch = Math.Max(45, requested.TotalSeconds * 0.18);
        if (difference > substantialMismatch)
        {
            penalty += 40;
        }

        if (difference > severeMismatch)
        {
            penalty += 60;
        }

        return Math.Min(400, penalty);
    }

    private static bool ArtistsStronglyMatch(string candidate, string requested)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(requested))
        {
            return false;
        }

        var candidateAliases = BuildArtistAliases(candidate);
        var requestedAliases = BuildArtistAliases(requested);
        if (candidateAliases.Overlaps(requestedAliases))
        {
            return true;
        }

        // Han artist names occasionally include an attached group or stage-name suffix that is
        // not covered by the explicit suffix list. Latin names never use substring matching:
        // “Artist” and “Other Artist” are different performers even though one contains the other.
        return candidateAliases.Any(candidateAlias =>
            requestedAliases.Any(requestedAlias =>
            {
                var shorter = candidateAlias.Length <= requestedAlias.Length
                    ? candidateAlias
                    : requestedAlias;
                return ArtistHanSegmentRegex().IsMatch(shorter)
                    && IsMeaningfulArtistAlias(shorter)
                    && (candidateAlias.Contains(requestedAlias, StringComparison.Ordinal)
                        || requestedAlias.Contains(candidateAlias, StringComparison.Ordinal));
            }));
    }

    private static HashSet<string> BuildArtistAliases(string value)
    {
        var canonical = ChineseMetadataNormalizer.NormalizeForMatching(value)
            .Normalize(NormalizationForm.FormKC)
            .ToLowerInvariant();
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var full = Normalize(canonical);
        if (full.Length > 0)
        {
            aliases.Add(full);
        }

        // Add complete credited performers so ordinary metadata such as
        // “Artist feat. Guest” still matches the primary artist without relying on unsafe
        // substring comparison.
        foreach (var credit in ArtistCreditSeparatorRegex().Split(canonical))
        {
            var alias = Normalize(credit);
            if (IsMeaningfulArtistAlias(alias))
            {
                aliases.Add(alias);
            }
        }

        // Providers frequently append a Latin stage name while players expose an abbreviation,
        // for example “声无哀乐SWAL” versus “聲無哀樂乐队THEWEAPONS”. The shared Han-name core is
        // a safer identity signal than attempting to guess a relationship between the two Latin
        // aliases. Generic group suffixes are removed only for artist matching, never lyric text.
        foreach (Match match in ArtistHanSegmentRegex().Matches(canonical))
        {
            var segment = Normalize(match.Value);
            if (segment.Length >= 2)
            {
                aliases.Add(segment);
            }

            var withoutGroupSuffix = StripArtistGroupSuffix(segment);
            if (withoutGroupSuffix.Length >= 2)
            {
                aliases.Add(withoutGroupSuffix);
            }
        }

        return aliases;
    }

    private static string StripArtistGroupSuffix(string value)
    {
        string[] suffixes = ["合唱团", "合唱队", "组合", "乐团", "乐队"];
        foreach (var suffix in suffixes)
        {
            if (value.EndsWith(suffix, StringComparison.Ordinal)
                && value.Length > suffix.Length)
            {
                return value[..^suffix.Length];
            }
        }

        return value;
    }

    private static bool IsMeaningfulArtistAlias(string value) =>
        ArtistHanSegmentRegex().IsMatch(value)
            ? value.Length >= 2
            : value.Length >= 4;

    private static double TitleDistance(
        string candidate,
        string requested,
        bool canonicalizeChinese) => Math.Min(
        TextDistance(candidate, requested, canonicalizeChinese),
        TextDistance(
            StripVersionSuffix(candidate),
            StripVersionSuffix(requested),
            canonicalizeChinese));

    private static HashSet<string> BuildTitleAliases(string title)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        AddAlias(aliases, title);

        var withoutAnnotations = BracketedTextRegex().Replace(title, " ");
        AddAlias(aliases, withoutAnnotations);

        foreach (Match match in BracketedTextRegex().Matches(title))
        {
            var content = match.Groups["content"].Value;
            if (!IsVersionDescription(content))
            {
                AddAlias(aliases, content);
            }
        }

        foreach (var segment in AlternateTitleSeparatorRegex().Split(title))
        {
            AddAlias(aliases, BracketedTextRegex().Replace(segment, " "));
        }

        return aliases;
    }

    private static void AddAlias(ISet<string> aliases, string value)
    {
        var normalized = Normalize(value);
        if (normalized.Length > 0)
        {
            aliases.Add(normalized);
        }

        var withoutVersion = StripVersionSuffix(value);
        normalized = Normalize(withoutVersion);
        if (normalized.Length > 0)
        {
            aliases.Add(normalized);
        }
    }

    private static string StripVersionSuffix(string value)
    {
        var previous = value;
        while (true)
        {
            var stripped = EnglishVersionSuffixRegex().Replace(previous, string.Empty);
            stripped = ChineseVersionSuffixRegex().Replace(stripped, string.Empty).Trim();
            if (string.Equals(previous, stripped, StringComparison.Ordinal))
            {
                return stripped;
            }

            previous = stripped;
        }
    }

    private static bool IsVersionDescription(string value)
    {
        return VersionMarkers.Any(marker =>
            value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string value, bool canonicalizeChinese = false)
    {
        var metadata = canonicalizeChinese
            ? ChineseMetadataNormalizer.NormalizeForMatching(value)
            : value;
        var normalized = metadata
            .Normalize(NormalizationForm.FormKC)
            .ToLower(CultureInfo.InvariantCulture);
        return NonLetterOrDigitRegex().Replace(normalized, string.Empty);
    }

    private static bool HasLiveVersionMarker(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var latinWords = LatinWordRegex().Matches(normalized)
            .Select(match => match.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ContainsAny(
                normalized,
                "现场", "現場", "演唱会", "演唱會", "巡演", "巡回", "巡迴", "音乐节", "音樂節")
            || latinWords.Overlaps(["live", "concert", "tour", "festival"]);
    }

    private static int LevenshteinDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var substitution = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+", RegexOptions.Compiled)]
    private static partial Regex NonLetterOrDigitRegex();

    [GeneratedRegex(@"[a-z]+", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex LatinWordRegex();

    [GeneratedRegex(@"[\u3400-\u9FFF\uF900-\uFAFF]+", RegexOptions.Compiled)]
    private static partial Regex ArtistHanSegmentRegex();

    [GeneratedRegex(
        @"\s*(?:,|，|、|/|／|;|；|&|＆|\+|\b(?:feat(?:uring)?|ft|with)\.?\b)\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ArtistCreditSeparatorRegex();

    [GeneratedRegex(@"(?<!\d)(?:19|20)\d{2}(?!\d)", RegexOptions.Compiled)]
    private static partial Regex ReleaseYearRegex();

    [GeneratedRegex(@"[\(\[（【](?<content>[^\)\]）】]+)[\)\]）】]", RegexOptions.Compiled)]
    private static partial Regex BracketedTextRegex();

    [GeneratedRegex(@"\s*(?:/|／|\||｜)\s*", RegexOptions.Compiled)]
    private static partial Regex AlternateTitleSeparatorRegex();

    [GeneratedRegex(
        @"(?:(?:\s*[-–—·/|]\s*)(?:live(?:\s+(?:version|ver\.?))?|remix(?:ed)?|instrumental|karaoke|off\s*vocal|backing\s*track|acoustic(?:\s+version)?|unplugged|cover|demo|radio\s+edit|extended(?:\s+mix)?|sped\s+up|slowed(?:\s+down)?|reverb|dj(?:\s+(?:version|mix))?)|\s+(?:live\s+(?:version|ver\.?)|remix(?:ed)?|instrumental|karaoke|off\s*vocal|backing\s*track|acoustic(?:\s+version)?|unplugged|cover|demo|radio\s+edit|extended(?:\s+mix)?|sped\s+up|slowed(?:\s+down)?|reverb|dj(?:\s+(?:version|mix))?))\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex EnglishVersionSuffixRegex();

    [GeneratedRegex(
        @"(?:\s*[-–—·/|]\s*|\s*)(?:现[场場](?:版)?|演唱[会會](?:版)?|巡演(?:版)?|巡[回迴](?:版)?|音[乐樂]节(?:版)?|混音(?:版)?|伴奏(?:版)?|[纯純]音[乐樂](?:版)?|卡拉\s*OK(?:版)?|翻唱(?:版)?|原声(?:版)?|不插[电電](?:版)?|加速(?:版)?|慢速(?:版)?|DJ(?:版)?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ChineseVersionSuffixRegex();
}
