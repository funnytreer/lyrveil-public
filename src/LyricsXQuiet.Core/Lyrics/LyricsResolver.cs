using LyricsXQuiet.Core.Lyrics.Providers;
using LyricsXQuiet.Core.Playback;
using System.Security.Cryptography;
using System.Text.Json;

namespace LyricsXQuiet.Core.Lyrics;

public sealed class LyricsResolver
{
    private const string PreferredProviderName = "酷狗音乐";
    private const string CurrentAutomaticRankingPolicy = "kugou-first-v1";
    private const double PreferredProviderNearTieThreshold = 3d;
    // Provider preference comes after release identity, but before lyric-format
    // quality inside the deliberately narrow near-tie window.
    private const double PreferredProviderBonus = 6.05d;
    private const double PreferredProviderAutomaticMatchThreshold = 20d;
    // Deliberately smaller than four seconds of duration mismatch (3.2 points).
    // Word timing may break a near tie, but cannot rescue a meaningfully worse
    // recording, artist, album or version match.
    private const double WordTimingPreferenceBonus = 3d;

    private readonly LyricsCache _cache;
    private readonly IReadOnlyList<ILyricsProvider> _providers;
    private readonly Func<bool> _preferWordTimedLyrics;
    private readonly TimeSpan _automaticSearchTimeout;

    public LyricsResolver(
        LyricsCache cache,
        IEnumerable<ILyricsProvider> providers,
        Func<bool>? preferWordTimedLyrics = null,
        TimeSpan? automaticSearchTimeout = null)
    {
        _cache = cache;
        _providers = providers.ToArray();
        _preferWordTimedLyrics = preferWordTimedLyrics ?? (() => true);
        _automaticSearchTimeout = automaticSearchTimeout ?? TimeSpan.FromSeconds(6);
    }

    public async Task<LyricDocument?> ResolveAsync(
        TrackIdentity track,
        bool forceNetwork = false,
        CancellationToken cancellationToken = default) =>
        (await ResolveWithStatusAsync(track, forceNetwork, cancellationToken)).Document;

    public async Task<LyricsResolutionResult> ResolveWithStatusAsync(
        TrackIdentity track,
        bool forceNetwork = false,
        CancellationToken cancellationToken = default,
        Action<LyricDocument>? documentAvailable = null)
    {
        if (!forceNetwork)
        {
            LyricsCacheEntry? cached;
            try { cached = await _cache.TryLoadEntryAsync(track, cancellationToken); }
            catch (Exception exception) when (IsStorageFailure(exception)) { cached = null; }
            if (cached is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                documentAvailable?.Invoke(cached.Document);
                try
                {
                    if (cached.SelectionKind == LyricsCacheSelectionKind.Automatic
                        && !string.Equals(
                            cached.RankingPolicy,
                            CurrentAutomaticRankingPolicy,
                            StringComparison.Ordinal))
                    {
                        var preferredDocument = await TryUpgradeToPreferredProviderAsync(
                            track,
                            cancellationToken,
                            documentAvailable);
                        if (preferredDocument is not null)
                        {
                            return preferredDocument;
                        }
                    }

                    var upgraded = await TryUpgradeAutomaticCacheAsync(
                        track,
                        cached,
                        cancellationToken,
                        documentAvailable);
                    if (upgraded is not null)
                    {
                        return upgraded;
                    }

                    if (cached.SelectionKind == LyricsCacheSelectionKind.Automatic
                        && !string.Equals(
                            cached.RankingPolicy,
                            CurrentAutomaticRankingPolicy,
                            StringComparison.Ordinal)
                        && (IsAlreadyExactForCurrentTrack(cached, track)
                            || !HasExactProviderFor(track)))
                    {
                        return await SaveAutomaticAsync(
                            track,
                            cached.Document with
                            {
                                Source = string.IsNullOrWhiteSpace(cached.DeclaredSource)
                                    ? cached.Document.Source
                                    : cached.DeclaredSource
                            },
                            cancellationToken);
                    }
                }
                catch (LyricsSourcesUnavailableException)
                {
                    // Cached lyrics are already visible. Leave their ranking policy
                    // unchanged so a later online visit can retry the upgrade.
                }

                return new(cached.Document);
            }
        }

        var candidate = (await SearchCandidatesAsync(track, cancellationToken, automatic: true))
            .FirstOrDefault();
        if (candidate is null)
        {
            return new(null);
        }

        return await SaveAutomaticAsync(track, candidate.Document, cancellationToken, documentAvailable);
    }

    public async Task<LyricDocument?> ImportAsync(
        TrackIdentity track,
        string path,
        CancellationToken cancellationToken = default) =>
        (await ImportWithStatusAsync(track, path, cancellationToken)).Document;

    public async Task<LyricsResolutionResult> ImportWithStatusAsync(
        TrackIdentity track,
        string path,
        CancellationToken cancellationToken = default,
        Action<LyricDocument>? documentAvailable = null)
    {
        var document = await LyricsCache.ReadImportAsync(path, cancellationToken);
        return document is null ? new(null)
            : await PersistAsync(track, document, LyricsCacheSelectionKind.Imported,
                cancellationToken, documentAvailable);
    }

    public async Task<IReadOnlyList<LyricsCandidate>> SearchAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default) =>
        await SearchCandidatesAsync(track, cancellationToken, automatic: false);

    private async Task<IReadOnlyList<LyricsCandidate>> SearchCandidatesAsync(
        TrackIdentity track, CancellationToken cancellationToken, bool automatic)
    {
        var result = await SearchWithStatusAsync(track, cancellationToken, automatic);
        if (result.Candidates.Count == 0 && result.Failures.Count > 0)
            throw new LyricsSourcesUnavailableException(result.Failures);
        return result.Candidates;
    }

    public async Task<LyricsSearchResult> SearchWithStatusAsync(
        TrackIdentity track,
        CancellationToken cancellationToken = default,
        bool automatic = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(automatic ? _automaticSearchTimeout : TimeSpan.FromSeconds(30));
        var searches = _providers
            .Where(provider => provider.IsEnabled)
            .Select(provider => SearchProviderAsync(provider, track, automatic,
                deadline.Token, cancellationToken))
            .ToArray();
        var results = await Task.WhenAll(searches);
        cancellationToken.ThrowIfCancellationRequested();
        var preferWordTimedLyrics = _preferWordTimedLyrics();

        var scoredCandidates = ScoreRelevantCandidates(
            results.SelectMany(result => result.Candidates),
            track);
        var deduplicatedCandidates = OrderCandidates(
                scoredCandidates,
                preferWordTimedLyrics)
            .GroupBy(CreateDuplicateKey)
            .Select(group => OrderCandidates(
                group,
                preferWordTimedLyrics).First());
        return new LyricsSearchResult(
            OrderCandidates(deduplicatedCandidates, preferWordTimedLyrics).ToArray(),
            results.Where(result => result.Failure is not null).Select(result => result.Failure!).ToArray(),
            results.Count(result => result.Failure is null));
    }

    private static IEnumerable<LyricsCandidate> ScoreRelevantCandidates(
        IEnumerable<LyricsCandidate> candidates,
        TrackIdentity track) =>
        candidates
            .Where(candidate => LyricsMatchScorer.IsRelevant(candidate, track))
            .Select(candidate => candidate with
            {
                MatchScore = LyricsMatchScorer.Score(candidate, track)
            });

    private static IOrderedEnumerable<LyricsCandidate> OrderCandidates(
        IEnumerable<LyricsCandidate> candidates,
        bool preferWordTimedLyrics)
    {
        var candidateArray = candidates.ToArray();
        var bestIdentityScore = candidateArray.Length == 0
            ? double.PositiveInfinity
            : candidateArray.Min(candidate => candidate.MatchScore);
        return candidateArray
            .OrderBy(candidate => GetQualityAdjustedScore(
                candidate,
                preferWordTimedLyrics,
                bestIdentityScore))
            .ThenBy(candidate => candidate.MatchScore)
            .ThenByDescending(candidate =>
                preferWordTimedLyrics && LyricsMatchScorer.HasReliableWordTiming(candidate.Document))
            .ThenByDescending(candidate => candidate.Document.Lines.Any(line =>
                !string.IsNullOrWhiteSpace(line.Translation)))
            .ThenBy(candidate => candidate.Provider, StringComparer.OrdinalIgnoreCase);
    }

    private static double GetQualityAdjustedScore(
        LyricsCandidate candidate,
        bool preferWordTimedLyrics,
        double bestIdentityScore) =>
        candidate.MatchScore
        - (preferWordTimedLyrics
           && LyricsMatchScorer.HasReliableWordTiming(candidate.Document)
            ? WordTimingPreferenceBonus
            : 0)
        - (IsPreferredProvider(candidate.Provider)
           && candidate.MatchScore <= bestIdentityScore + PreferredProviderNearTieThreshold
            ? PreferredProviderBonus
            : 0);

    private static bool IsPreferredProvider(string provider) =>
        string.Equals(provider, PreferredProviderName, StringComparison.OrdinalIgnoreCase);

    private static CandidateDuplicateKey CreateDuplicateKey(LyricsCandidate candidate) =>
        !string.IsNullOrWhiteSpace(candidate.Id)
            ? new(candidate.Provider.Trim().ToUpperInvariant(), candidate.Id.Trim(), "", "", "", 0)
            : new(candidate.Provider.Trim().ToUpperInvariant(), "",
                LyricsMatchScorer.NormalizeIdentityPart(candidate.Title),
                LyricsMatchScorer.NormalizeIdentityPart(candidate.Artist),
                LyricsMatchScorer.NormalizeIdentityPart(candidate.Album),
                (long)Math.Round(candidate.Duration.TotalSeconds, MidpointRounding.AwayFromZero));

    private static async Task<ProviderSearchResult> SearchProviderAsync(
        ILyricsProvider provider,
        TrackIdentity track,
        bool automatic,
        CancellationToken deadlineToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var task = automatic ? provider.SearchAutomaticAsync(track, deadlineToken)
                : provider.SearchAsync(track, deadlineToken);
            return new(await task.WaitAsync(deadlineToken), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return new([], new(provider.Name, LyricsProviderFailureKind.Network));
        }
        catch (OperationCanceledException)
        {
            return new([], new(provider.Name, LyricsProviderFailureKind.Timeout));
        }
        catch (Exception exception) when (IsRecoverableProviderFailure(exception))
        {
            return new([], new(provider.Name, LyricsProviderFailureKind.InvalidResponse));
        }
    }

    private static bool IsRecoverableProviderFailure(Exception exception) =>
        exception is JsonException
            or InvalidDataException
            or FormatException
            or CryptographicException
            or InvalidOperationException;

    public Task<LyricsResolutionResult> SaveSelectionAsync(
        TrackIdentity track,
        LyricsCandidate candidate,
        CancellationToken cancellationToken = default,
        Action<LyricDocument>? documentAvailable = null) =>
        PersistAsync(
            track,
            candidate.Document,
            LyricsCacheSelectionKind.Manual,
            cancellationToken,
            documentAvailable);

    private Task<LyricsResolutionResult> SaveAutomaticAsync(
        TrackIdentity track,
        LyricDocument document,
        CancellationToken cancellationToken,
        Action<LyricDocument>? documentAvailable = null) =>
        PersistAsync(
            track,
            document,
            LyricsCacheSelectionKind.Automatic,
            cancellationToken,
            documentAvailable,
            CurrentAutomaticRankingPolicy);

    private async Task<LyricsResolutionResult> PersistAsync(
        TrackIdentity track,
        LyricDocument document,
        LyricsCacheSelectionKind selectionKind,
        CancellationToken cancellationToken,
        Action<LyricDocument>? documentAvailable,
        string rankingPolicy = "")
    {
        cancellationToken.ThrowIfCancellationRequested();
        documentAvailable?.Invoke(document);
        try
        {
            await _cache.SaveAsync(track, document, selectionKind, cancellationToken, rankingPolicy);
            return new(document);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return new(document, SaveFailed: true);
        }
    }

    private static bool IsStorageFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
        or System.Security.SecurityException;

    private async Task<LyricsResolutionResult?> TryUpgradeToPreferredProviderAsync(
        TrackIdentity track,
        CancellationToken cancellationToken,
        Action<LyricDocument>? documentAvailable)
    {
        if (!_providers.Any(provider => provider.IsEnabled && IsPreferredProvider(provider.Name)))
        {
            return null;
        }

        // Re-run the full cross-provider comparison once for a legacy automatic
        // cache. This prevents a merely title-similar Kugou result from replacing
        // a clearly better artist/album/version/duration match from another source.
        var selected = (await SearchCandidatesAsync(track, cancellationToken, automatic: true)).FirstOrDefault();
        if (selected is null
            || !IsPreferredProvider(selected.Provider)
            || selected.MatchScore > PreferredProviderAutomaticMatchThreshold)
        {
            return null;
        }

        return await SaveAutomaticAsync(track, selected.Document, cancellationToken, documentAvailable);
    }

    private bool HasExactProviderFor(TrackIdentity track) =>
        _providers
            .OfType<IExactSourceLyricsProvider>()
            .Any(provider => provider.CanResolveExact(track));

    private bool IsAlreadyExactForCurrentTrack(
        LyricsCacheEntry cached,
        TrackIdentity track) =>
        _providers
            .OfType<IExactSourceLyricsProvider>()
            .Any(provider =>
                provider.CanResolveExact(track)
                && string.Equals(
                    cached.DeclaredSource,
                    provider.Name,
                    StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(cached.SourceTrackId)
                && string.Equals(
                    cached.SourceTrackId,
                    track.SourceTrackId,
                    StringComparison.OrdinalIgnoreCase));

    private async Task<LyricsResolutionResult?> TryUpgradeAutomaticCacheAsync(
        TrackIdentity track,
        LyricsCacheEntry cached,
        CancellationToken cancellationToken,
        Action<LyricDocument>? documentAvailable)
    {
        if (cached.SelectionKind != LyricsCacheSelectionKind.Automatic)
        {
            return null;
        }

        // An automatic Kugou selection already represents the active ranking
        // policy. A later exact NetEase ID may improve metadata, but must not
        // silently replace the user's preferred lyric provider.
        if (IsPreferredProvider(cached.DeclaredSource))
        {
            return null;
        }

        foreach (var provider in _providers.OfType<IExactSourceLyricsProvider>())
        {
            if (!provider.CanResolveExact(track))
            {
                continue;
            }

            var alreadyExactForCurrentId = string.Equals(
                    cached.DeclaredSource,
                    provider.Name,
                    StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(cached.SourceTrackId)
                && string.Equals(
                    cached.SourceTrackId,
                    track.SourceTrackId,
                    StringComparison.OrdinalIgnoreCase);
            if (alreadyExactForCurrentId)
            {
                continue;
            }

            LyricDocument? exactDocument;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(_automaticSearchTimeout);
                exactDocument = await provider.FindExactAsync(track, deadline.Token).WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is HttpRequestException
                    or OperationCanceledException
                    or JsonException
                    or InvalidDataException
                    or FormatException
                    or CryptographicException
                    or InvalidOperationException)
            {
                exactDocument = null;
            }

            if (exactDocument is not { HasSynchronizedLines: true }
                && exactDocument is not { IsInstrumental: true })
            {
                continue;
            }

            return await SaveAutomaticAsync(track, exactDocument, cancellationToken, documentAvailable);
        }

        return null;
    }

    private readonly record struct CandidateDuplicateKey(
        string Provider,
        string Id,
        string Title,
        string Artist,
        string Album,
        long DurationSeconds);

    private sealed record ProviderSearchResult(
        IReadOnlyList<LyricsCandidate> Candidates,
        LyricsProviderFailure? Failure);
}
