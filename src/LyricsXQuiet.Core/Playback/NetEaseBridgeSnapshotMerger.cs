using System.Globalization;

namespace LyricsXQuiet.Core.Playback;

public static class NetEaseBridgeSnapshotMerger
{
    public const int CurrentProtocolVersion = 1;

    public static readonly TimeSpan DefaultMaximumAge = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaximumPositionOverrun = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumTrackDurationDifference = TimeSpan.FromSeconds(3);
    private static readonly char[] ArtistSeparators =
        ['/', '\\', ',', '，', '、', ';', '；', '&', '＆', '·', '・', '|'];

    public static PlaybackSnapshot Merge(
        PlaybackSnapshot nativeSnapshot,
        NetEaseBridgeSnapshot? bridgeSnapshot,
        DateTimeOffset now,
        TimeSpan? maximumAge = null)
    {
        ArgumentNullException.ThrowIfNull(nativeSnapshot);

        if (bridgeSnapshot is null
            || MusicPlayerCatalog.Identify(nativeSnapshot.Track.SourceAppUserModelId)?.Id
                != "netease"
            || !IsUsable(bridgeSnapshot, now, maximumAge ?? DefaultMaximumAge)
            || !TracksMatch(nativeSnapshot.Track, bridgeSnapshot.Track))
        {
            return nativeSnapshot;
        }

        var duration = TimeSpan.FromMilliseconds(bridgeSnapshot.Track.DurationMs);
        var position = TimeSpan.FromMilliseconds(bridgeSnapshot.PositionMs);
        if (position > duration)
        {
            position = duration;
        }

        var track = nativeSnapshot.Track with
        {
            Duration = duration,
            SourceTrackId = $"netease:{bridgeSnapshot.Track.Id}"
        };

        return new PlaybackSnapshot(
            track,
            bridgeSnapshot.State,
            position,
            bridgeSnapshot.TimelineFrozen
                ? 0d
                : bridgeSnapshot.Rate > 0
                    ? bridgeSnapshot.Rate
                    : 1d,
            bridgeSnapshot.ObservedAt,
            PlaybackTimelineKind.Reported);
    }

    public static bool IsUsable(
        NetEaseBridgeSnapshot snapshot,
        DateTimeOffset now,
        TimeSpan maximumAge)
    {
        if (snapshot.Version != CurrentProtocolVersion
            || snapshot.Sequence < 0
            || maximumAge <= TimeSpan.Zero
            || snapshot.ObservedAt > now + MaximumFutureSkew
            || now - snapshot.ObservedAt > maximumAge
            || snapshot.Track is null
            || string.IsNullOrWhiteSpace(snapshot.Track.Title)
            || snapshot.Track.Title.Length > 512
            || snapshot.Track.Title.Any(char.IsControl)
            || snapshot.Track.Album is null
            || snapshot.Track.Album.Length > 512
            || snapshot.Track.Album.Any(char.IsControl)
            || snapshot.Track.Artists is null
            || snapshot.Track.Artists.Count is 0 or > 16
            || snapshot.Track.Artists.Any(artist =>
                string.IsNullOrWhiteSpace(artist)
                || artist.Length > 256
                || artist.Any(char.IsControl))
            || snapshot.Track.Id is null
            || snapshot.Track.Id.Length > 32
            || !long.TryParse(
                snapshot.Track.Id,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var trackId)
            || trackId <= 0
            || snapshot.Track.DurationMs <= 0
            || snapshot.Track.DurationMs > MaximumDuration.TotalMilliseconds
            || snapshot.PositionMs < 0
            || snapshot.PositionMs - snapshot.Track.DurationMs
                > MaximumPositionOverrun.TotalMilliseconds
            || !double.IsFinite(snapshot.Rate)
            || snapshot.Rate is < 0 or > 8
            || snapshot.State is not (PlaybackState.Playing
                or PlaybackState.Paused
                or PlaybackState.Stopped))
        {
            return false;
        }

        return true;
    }

    private static bool TracksMatch(TrackIdentity nativeTrack, NetEaseBridgeTrack bridgeTrack)
    {
        if (!string.Equals(
                TrackIdentity.NormalizeForMatching(nativeTrack.Title),
                TrackIdentity.NormalizeForMatching(bridgeTrack.Title),
                StringComparison.Ordinal))
        {
            return false;
        }

        if (!OptionalSourceTrackIdMatches(nativeTrack.SourceTrackId, bridgeTrack.Id)
            || !OptionalAlbumMatches(nativeTrack.Album, bridgeTrack.Album)
            || !OptionalDurationMatches(nativeTrack.Duration, bridgeTrack.DurationMs))
        {
            return false;
        }

        var nativeArtist = TrackIdentity.NormalizeForMatching(nativeTrack.Artist);
        if (string.IsNullOrEmpty(nativeArtist))
        {
            return false;
        }

        var bridgeArtists = bridgeTrack.Artists
            .Select(TrackIdentity.NormalizeForMatching)
            .Where(artist => artist.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (bridgeArtists.Length == 0)
        {
            return false;
        }

        if (bridgeArtists.Contains(nativeArtist, StringComparer.Ordinal)
            || string.Equals(nativeArtist, string.Concat(bridgeArtists), StringComparison.Ordinal))
        {
            return true;
        }

        var nativeArtistParts = nativeTrack.Artist
            .Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(TrackIdentity.NormalizeForMatching)
            .Where(artist => artist.Length > 0);
        return nativeArtistParts.Any(part => bridgeArtists.Contains(part, StringComparer.Ordinal));
    }

    private static bool OptionalSourceTrackIdMatches(
        string nativeSourceTrackId,
        string bridgeTrackId)
    {
        if (string.IsNullOrWhiteSpace(nativeSourceTrackId))
        {
            return true;
        }

        return SourceTrackIdParser.TryGetNetEaseSongId(nativeSourceTrackId.Trim(), out var nativeTrackId)
            && long.TryParse(
                bridgeTrackId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedBridgeTrackId)
            && nativeTrackId == parsedBridgeTrackId;
    }

    private static bool OptionalAlbumMatches(string nativeAlbum, string bridgeAlbum) =>
        string.IsNullOrWhiteSpace(nativeAlbum)
        || string.IsNullOrWhiteSpace(bridgeAlbum)
        || string.Equals(
            TrackIdentity.NormalizeForMatching(nativeAlbum),
            TrackIdentity.NormalizeForMatching(bridgeAlbum),
            StringComparison.Ordinal);

    private static bool OptionalDurationMatches(TimeSpan nativeDuration, long bridgeDurationMs) =>
        nativeDuration <= TimeSpan.Zero
        || bridgeDurationMs <= 0
        || Math.Abs(nativeDuration.TotalMilliseconds - bridgeDurationMs)
            <= MaximumTrackDurationDifference.TotalMilliseconds;
}
