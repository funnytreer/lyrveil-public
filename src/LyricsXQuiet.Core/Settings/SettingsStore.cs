using System.Text.Json;
using System.Text;
using LyricsXQuiet.Core.Lyrics;

namespace LyricsXQuiet.Core.Settings;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly object _writeLock = new();

    public string FilePath => _filePath;

    public SettingsStore(string? rootDirectory = null)
    {
        var root = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LyricsXQuiet");
        _filePath = Path.Combine(root, "settings.json");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return new AppSettings();
        }

        try
        {
            string json;
            // Replacing a complete settings snapshot can overlap a monitor process reading the previous one.
            using (var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                json = await reader.ReadToEndAsync(cancellationToken);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                ?? new AppSettings();
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var hasSchemaVersion = TryGetPropertyIgnoreCase(
                root,
                nameof(AppSettings.SettingsSchemaVersion),
                out var schemaVersionElement);
            var persistedSchemaVersion = hasSchemaVersion
                && schemaVersionElement.ValueKind == JsonValueKind.Number
                && schemaVersionElement.TryGetInt32(out var parsedSchemaVersion)
                    ? parsedSchemaVersion
                    : 0;
            if (!hasSchemaVersion
                && TryGetPropertyIgnoreCase(
                    root,
                    nameof(AppSettings.NetEaseSyncDelayMilliseconds),
                    out var legacyDelay)
                && legacyDelay.ValueKind == JsonValueKind.Number
                && legacyDelay.TryGetInt32(out var milliseconds)
                && milliseconds == 30)
            {
                // Builds before schema v1 serialized their then-default 30 ms.
                // Migrate only that exact legacy sentinel; every other value is
                // treated as a deliberate user adjustment and is preserved.
                settings.NetEaseSyncDelayMilliseconds = AppSettings.DefaultNetEaseSyncDelayMilliseconds;
            }

            if (persistedSchemaVersion < 2
                && LyricFilter.IsLegacyDefaultPatternSet(settings.LyricsFilterPatterns))
            {
                // Credits now have a dedicated, understandable setting. Only
                // migrate the exact old built-in set; user-authored patterns are
                // always preserved verbatim.
                settings.LyricsFilterPatterns = LyricFilter.DefaultPatterns;
            }

            if (persistedSchemaVersion < 3
                && TryGetPropertyIgnoreCase(
                    root,
                    nameof(AppSettings.AppleMusicSyncAdvanceMilliseconds),
                    out var legacyAppleMusicAdvance)
                && legacyAppleMusicAdvance.ValueKind == JsonValueKind.Number
                && legacyAppleMusicAdvance.TryGetInt32(out var appleMusicMilliseconds)
                && appleMusicMilliseconds == 80)
            {
                // 80 ms was the former built-in baseline. Migrate only that
                // exact value; every other persisted value is a user choice.
                settings.AppleMusicSyncAdvanceMilliseconds = 300;
            }

            settings.FollowedPlayerIds = MigrateFollowedPlayers(persistedSchemaVersion, settings.PlayerFollowMode, settings.FollowedPlayerIds);

            if (persistedSchemaVersion < 5
                && !TryGetPropertyIgnoreCase(root, nameof(AppSettings.NetEaseOffsetMilliseconds), out _)
                && settings.NetEaseSyncDelayMilliseconds is 0 or 120)
            {
                // Zero and the former built-in 120 ms now adopt the one-second
                // baseline. Other calibrated delays become the user's default
                // directly; the new quick-panel adjustment starts at zero, so
                // no saved correction is applied twice. Schema v5 preserves an
                // explicitly disabled (zero) baseline on subsequent loads.
                settings.NetEaseSyncDelayMilliseconds = AppSettings.DefaultNetEaseSyncDelayMilliseconds;
            }

            if (persistedSchemaVersion < 6 && settings.SavedAppearancePreset is { } legacyPreset)
            {
                settings.StorePersonalPreset(new NamedAppearancePreset(
                    "我的方案", legacyPreset, 1, 1, "ProgressGlow", "Normal", "FadeOnly"));
            }

            settings.SettingsSchemaVersion = AppSettings.CurrentSettingsSchemaVersion;
            return settings;
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
    }

    // The login listener needs only these fields. Avoid materializing appearance data, lyric filters
    // or the potentially large per-track calibration dictionary for a process that never draws UI.
    public MonitorConfiguration? ReadMonitorConfiguration()
    {
        if (!File.Exists(_filePath)) return new(PlayerFollowModes.Shortcut, "", "", false);
        try
        {
            using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            string? ReadString(string name) => TryGetPropertyIgnoreCase(root, name, out var item) && item.ValueKind == JsonValueKind.String
                ? item.GetString()?.Trim() : null;
            var version = TryGetPropertyIgnoreCase(root, nameof(AppSettings.SettingsSchemaVersion), out var schema)
                && schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var n) ? n : 0;
            var mode = PlayerFollowModes.Normalize(ReadString(nameof(AppSettings.PlayerFollowMode)));
            var ids = ReadString(nameof(AppSettings.FollowedPlayerIds)) ?? AppSettings.DefaultFollowedPlayerIds;
            var repair = TryGetPropertyIgnoreCase(root, nameof(AppSettings.AutoRepairNetEaseSync), out var flag) && flag.ValueKind == JsonValueKind.True;
            return new(mode, MigrateFollowedPlayers(version, mode, ids), ReadString(nameof(AppSettings.LocalPlayerExecutablePath)) ?? "", repair);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static string MigrateFollowedPlayers(int schema, string mode, string ids) =>
        schema < 4 && PlayerFollowModes.UsesMonitor(mode) && string.Equals(ids, "netease", StringComparison.OrdinalIgnoreCase)
            ? AppSettings.DefaultFollowedPlayerIds : ids;

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        if (element.TryGetProperty(propertyName, out value)) return true;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        SaveNow(settings, cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Persists small, interaction-critical changes before returning. The
    /// settings document is tiny, and doing this synchronously prevents a
    /// forced restart immediately after a drag from losing the final position.
    /// </summary>
    public void SaveNow(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        lock (_writeLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $"settings-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
                for (int attempt = 0; ; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        File.Move(temporaryPath, _filePath, true);
                        break;
                    }
                    catch (Exception e) when (attempt < 3 && OperatingSystem.IsWindows()
                        && (e is IOException or UnauthorizedAccessException) && (e.HResult & 0xffff) is 5 or 32 or 33)
                    {
                        // An external reader may briefly hold the destination without delete sharing.
                        Thread.Sleep(15 * (attempt + 1));
                    }
                }
            }
            finally
            {
                try { File.Delete(temporaryPath); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
