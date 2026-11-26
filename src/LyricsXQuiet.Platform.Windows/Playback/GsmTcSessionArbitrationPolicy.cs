using LyricsXQuiet.Core.Playback;

namespace LyricsXQuiet.Platform.Windows.Playback;

/// <summary>
/// Pure priority policy used when more than one supported GSMTC session exists.
/// Strong playback signals are represented by higher-order bits so weaker hints
/// can refine a choice without ever overturning them.
/// </summary>
public static class GsmTcSessionArbitrationPolicy
{
    /// <summary>
    /// Determines whether a candidate is Windows' current GSMTC session.
    /// The native projected object identity is authoritative. Source AUMID is
    /// intentionally only a fallback when it identifies exactly one candidate;
    /// otherwise two sessions published by the same app would both look current.
    /// </summary>
    public static bool IsWindowsCurrentSession(
        GsmTcCurrentSessionIdentitySignals signals) =>
        signals.IsSameNativeSession
        || signals.SourceAppUserModelIdMatches
            && signals.MatchingSourceSessionCount == 1;

    public static int GetScore(GsmTcSessionArbitrationSignals signals)
    {
        var score = 0;

        // A recent Core Audio sample remains the strongest evidence because it
        // repairs players that publish an incorrect paused/stopped GSMTC state.
        if (signals.IsAudiblyPlaying)
        {
            score |= 1 << 3;
        }

        // An explicit Playing state must outrank every combination of the weaker
        // timeline and Windows-current hints when no audio sample is available.
        if (signals.ReportedState == PlaybackState.Playing)
        {
            score |= 1 << 2;
        }

        if (signals.HasUsableTimeline)
        {
            score |= 1 << 1;
        }

        if (signals.IsWindowsCurrent)
        {
            score |= 1;
        }

        return score;
    }
}

public readonly record struct GsmTcCurrentSessionIdentitySignals(
    bool IsSameNativeSession,
    bool SourceAppUserModelIdMatches,
    int MatchingSourceSessionCount);

public readonly record struct GsmTcSessionArbitrationSignals(
    PlaybackState ReportedState,
    bool IsAudiblyPlaying,
    bool HasUsableTimeline,
    bool IsWindowsCurrent);
