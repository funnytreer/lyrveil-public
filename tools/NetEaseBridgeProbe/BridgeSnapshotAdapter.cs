using System.Globalization;
using System.Text.Json;

namespace LyricsXQuiet.Tools.NetEaseBridgeProbe;

internal static class BridgeSnapshotAdapter
{
    private static readonly char[] ArtistSeparators =
        ['/', '\\', ',', '，', '、', ';', '；', '&', '＆', '·', '・', '|'];

    public static bool TryCreate(JsonElement probe, out ProducerSnapshot? snapshot)
    {
        snapshot = null;
        if (!probe.TryGetProperty("ok", out var ok)
            || ok.ValueKind != JsonValueKind.True
            || !probe.TryGetProperty("track", out var track)
            || track.ValueKind != JsonValueKind.Object
            || !probe.TryGetProperty("playback", out var playback)
            || playback.ValueKind != JsonValueKind.Object
            || !TryReadScalar(track, "id", out var id)
            || !long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId)
            || numericId <= 0
            || !TryReadScalar(track, "title", out var title)
            || !TryReadScalar(track, "artist", out var artist)
            || !IsValidText(title, 512)
            || !IsValidText(artist, 4096)
            || !TryReadLong(playback, "positionMs", out var positionMs)
            || !TryReadLong(playback, "durationMs", out var durationMs)
            || positionMs < 0
            || durationMs <= 0
            || durationMs > TimeSpan.FromHours(24).TotalMilliseconds
            || positionMs - durationMs > TimeSpan.FromSeconds(2).TotalMilliseconds
            || !TryReadState(playback, out var state))
        {
            return false;
        }

        var artists = artist
            .Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();
        if (artists.Length == 0)
        {
            return false;
        }

        var album = TryReadScalar(track, "album", out var albumValue)
            ? albumValue
            : string.Empty;
        if (!IsValidText(album, 512, allowEmpty: true)
            || artists.Any(value => !IsValidText(value, 256)))
        {
            return false;
        }

        snapshot = new ProducerSnapshot(
            DateTimeOffset.UtcNow,
            new BridgeWireTrack(
                numericId.ToString(CultureInfo.InvariantCulture),
                title.Trim(),
                Array.AsReadOnly(artists),
                album.Trim(),
                durationMs),
            state,
            Math.Min(positionMs, durationMs),
            1d);
        return true;
    }

    private static bool TryReadScalar(
        JsonElement container,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!container.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        value = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            _ => string.Empty
        };
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryReadLong(
        JsonElement container,
        string propertyName,
        out long value)
    {
        value = 0;
        if (!container.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            if (property.TryGetInt64(out value))
            {
                return true;
            }

            if (property.TryGetDouble(out var number)
                && double.IsFinite(number)
                && number is >= long.MinValue and <= long.MaxValue)
            {
                value = checked((long)Math.Round(number));
                return true;
            }
        }

        return property.ValueKind == JsonValueKind.String
            && long.TryParse(
                property.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value);
    }

    private static bool TryReadState(JsonElement playback, out string state)
    {
        state = string.Empty;
        if (playback.TryGetProperty("isPlaying", out var isPlaying)
            && isPlaying.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            state = isPlaying.GetBoolean() ? "playing" : ReadInactiveState(playback);
            return true;
        }

        if (!playback.TryGetProperty("status", out var status))
        {
            return false;
        }

        if (status.ValueKind == JsonValueKind.String)
        {
            var normalized = status.GetString()?.Trim().ToLowerInvariant() ?? string.Empty;
            if (normalized.Contains("playing", StringComparison.Ordinal)
                || normalized is "play" or "started")
            {
                state = "playing";
                return true;
            }

            if (normalized.Contains("pause", StringComparison.Ordinal))
            {
                state = "paused";
                return true;
            }

            if (normalized.Contains("stop", StringComparison.Ordinal)
                || normalized.Contains("ended", StringComparison.Ordinal))
            {
                state = "stopped";
                return true;
            }
        }

        if (status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var numericState))
        {
            // Compatibility with snapshots produced before the page expression
            // normalized the Redux PlayingState enum to text.
            state = numericState switch
            {
                2 => "playing",
                1 => "paused",
                -1 or 0 or 3 or 4 => "stopped",
                _ => string.Empty
            };
        }

        return state.Length > 0;
    }

    private static string ReadInactiveState(JsonElement playback)
    {
        if (playback.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.String)
        {
            var value = status.GetString() ?? string.Empty;
            if (value.Contains("stop", StringComparison.OrdinalIgnoreCase)
                || value.Contains("ended", StringComparison.OrdinalIgnoreCase))
            {
                return "stopped";
            }
        }

        return "paused";
    }

    private static bool IsValidText(string value, int maximumLength, bool allowEmpty = false) =>
        value.Length <= maximumLength
        && (allowEmpty || !string.IsNullOrWhiteSpace(value))
        && !value.Any(char.IsControl);
}
