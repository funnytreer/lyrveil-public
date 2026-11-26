namespace LyricsXQuiet.Core.Playback;

/// <summary>
/// Apple Music for Windows currently exposes whole-second GSMTC positions while
/// refreshing the timeline several times per second. The first observation of a
/// new second is therefore late by up to one refresh interval. Estimate the real
/// second boundary as the midpoint between the last observation of the previous
/// value and the first observation of the new value.
/// </summary>
public sealed class AppleMusicTimelineQuantizationCompensator
{
    private static readonly TimeSpan MinimumObservationInterval =
        TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan MaximumObservationInterval =
        TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan MaximumAdvance =
        TimeSpan.FromMilliseconds(250);

    private string _trackKey = string.Empty;
    private TimeSpan _lastReportedPosition;
    private DateTimeOffset _lastObservationAt;
    private TimeSpan _estimatedAdvance;
    private bool _hasObservation;

    public TimeSpan Compensate(
        TrackIdentity track,
        PlaybackState state,
        TimeSpan reportedPosition,
        DateTimeOffset timelineUpdatedAt,
        DateTimeOffset capturedAt)
    {
        if (!IsAppleMusic(track.SourceAppUserModelId)
            || reportedPosition < TimeSpan.Zero
            || timelineUpdatedAt > capturedAt
            || !IsWholeSecond(reportedPosition))
        {
            Reset();
            return reportedPosition;
        }

        if (!string.Equals(_trackKey, track.StableKey, StringComparison.Ordinal))
        {
            Reset();
            _trackKey = track.StableKey;
        }

        if (state != PlaybackState.Playing)
        {
            Remember(reportedPosition, timelineUpdatedAt);
            _estimatedAdvance = TimeSpan.Zero;
            return reportedPosition;
        }

        if (!_hasObservation)
        {
            Remember(reportedPosition, timelineUpdatedAt);
            return reportedPosition;
        }

        if (timelineUpdatedAt <= _lastObservationAt)
        {
            return reportedPosition + _estimatedAdvance;
        }

        if (reportedPosition == _lastReportedPosition)
        {
            _lastObservationAt = timelineUpdatedAt;
            return reportedPosition + _estimatedAdvance;
        }

        var positionStep = reportedPosition - _lastReportedPosition;
        var observationInterval = timelineUpdatedAt - _lastObservationAt;
        if (positionStep == TimeSpan.FromSeconds(1)
            && observationInterval >= MinimumObservationInterval
            && observationInterval <= MaximumObservationInterval)
        {
            _estimatedAdvance = TimeSpan.FromTicks(Math.Min(
                observationInterval.Ticks / 2,
                MaximumAdvance.Ticks));
        }
        else
        {
            // A large discontinuity is a seek, and a fractional/irregular step is
            // not the whole-second quantization pattern this class compensates.
            _estimatedAdvance = TimeSpan.Zero;
        }

        Remember(reportedPosition, timelineUpdatedAt);
        return reportedPosition + _estimatedAdvance;
    }

    private void Remember(TimeSpan position, DateTimeOffset observedAt)
    {
        _lastReportedPosition = position;
        _lastObservationAt = observedAt;
        _hasObservation = true;
    }

    private void Reset()
    {
        _trackKey = string.Empty;
        _lastReportedPosition = TimeSpan.Zero;
        _lastObservationAt = default;
        _estimatedAdvance = TimeSpan.Zero;
        _hasObservation = false;
    }

    private static bool IsWholeSecond(TimeSpan position) =>
        position.Ticks % TimeSpan.TicksPerSecond == 0;

    private static bool IsAppleMusic(string sourceAppUserModelId) =>
        sourceAppUserModelId.Contains("applemusic", StringComparison.OrdinalIgnoreCase)
        || sourceAppUserModelId.Contains("apple music", StringComparison.OrdinalIgnoreCase);
}
