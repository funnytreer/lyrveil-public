using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace LyricsXQuiet.Core.Lyrics.Providers;

internal static class LyricsProviderRequests
{
    // A failed candidate does not discard successful candidates. If nothing
    // could be retrieved, preserve the failure instead of reporting a false miss.
    public static async Task<IReadOnlyList<LyricsCandidate>> CollectAsync(
        IEnumerable<Func<Task<IReadOnlyList<LyricsCandidate>>>> requests,
        CancellationToken cancellationToken)
    {
        var batches = await Task.WhenAll(requests.Select(async request =>
        {
            try { return (Candidates: await request(), Failure: (Exception?)null); }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                && exception is HttpRequestException or OperationCanceledException
                    or JsonException or InvalidDataException or FormatException)
            {
                return (Candidates: (IReadOnlyList<LyricsCandidate>)Array.Empty<LyricsCandidate>(), Failure: exception);
            }
        }));
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = batches.SelectMany(batch => batch.Candidates).ToArray();
        if (candidates.Length == 0 && batches.FirstOrDefault(batch => batch.Failure is not null).Failure is { } failure)
            ExceptionDispatchInfo.Capture(failure).Throw();
        return candidates;
    }
}
