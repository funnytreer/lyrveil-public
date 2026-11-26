namespace LyricsXQuiet.Core.Playback;

public sealed record MusicPlayerDefinition(
    string Id,
    string DisplayName,
    string ProcessName);

public static class MusicPlayerCatalog
{
    private static readonly HashSet<string> BlockedLocalProcessNames = new(
        [
            "chrome", "msedge", "firefox", "brave", "opera", "vivaldi",
            "arc", "thorium", "lyricsxquiet.app"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static ConfiguredLocalPlayerEntry? _configuredLocalPlayer;

    private static readonly PlayerEntry[] Entries =
    [
        new(
            new MusicPlayerDefinition("netease", "网易云音乐", "cloudmusic"),
            ["cloudmusic", "neteasecloudmusic"]),
        new(
            new MusicPlayerDefinition("qqmusic", "QQ 音乐", "QQMusic"),
            ["qqmusic"]),
        new(
            new MusicPlayerDefinition("kugou", "酷狗音乐", "KuGou"),
            ["kugou", "kgmusic"]),
        new(
            new MusicPlayerDefinition("applemusic", "Apple Music", "AppleMusic"),
            ["applemusic", "apple music"]),
        new(
            new MusicPlayerDefinition("spotify", "Spotify", "Spotify"),
            ["spotify"])
    ];

    public static IReadOnlyList<MusicPlayerDefinition> SupportedPlayers { get; } =
        Array.AsReadOnly(Entries.Select(entry => entry.Player).ToArray());

    public static MusicPlayerDefinition? ConfiguredLocalPlayer =>
        Volatile.Read(ref _configuredLocalPlayer)?.Player;

    public static IReadOnlyList<MusicPlayerDefinition> DetectablePlayers
    {
        get
        {
            var local = ConfiguredLocalPlayer;
            return local is null
                ? SupportedPlayers
                : SupportedPlayers.Append(local).ToArray();
        }
    }

    public static void ConfigureLocalPlayer(string? executablePath)
    {
        var configured = TryCreateLocalPlayer(executablePath);
        Volatile.Write(ref _configuredLocalPlayer, configured);
    }

    public static bool IsLocalPlayerExecutableAllowed(string? executablePath) =>
        TryCreateLocalPlayer(executablePath) is not null;

    public static MusicPlayerDefinition? Identify(string? sourceAppUserModelId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppUserModelId))
        {
            return null;
        }

        foreach (var entry in Entries)
        {
            if (entry.SourceTokens.Any(token =>
                sourceAppUserModelId.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                return entry.Player;
            }
        }

        var local = Volatile.Read(ref _configuredLocalPlayer);
        return local is not null && local.Matches(sourceAppUserModelId)
            ? local.Player
            : null;
    }

    public static bool IsSupported(string? sourceAppUserModelId) =>
        Identify(sourceAppUserModelId) is not null;

    public static IReadOnlyList<MusicPlayerDefinition> SelectByIds(string? serializedIds)
    {
        var selectedIds = (serializedIds ?? string.Empty)
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return SupportedPlayers
            .Where(player => selectedIds.Contains(player.Id))
            .ToArray();
    }

    public static IReadOnlyList<MusicPlayerDefinition> SelectForMonitoring(string? serializedIds)
    {
        var selected = SelectByIds(serializedIds);
        var local = ConfiguredLocalPlayer;
        return local is null
            ? selected
            : selected.Append(local).ToArray();
    }

    public static string SerializeIds(IEnumerable<string> playerIds)
    {
        var requestedIds = playerIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return string.Join(';', SupportedPlayers
            .Where(player => requestedIds.Contains(player.Id))
            .Select(player => player.Id));
    }

    private static ConfiguredLocalPlayerEntry? TryCreateLocalPlayer(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        string resolvedPath;
        try
        {
            resolvedPath = Path.GetFullPath(executablePath.Trim());
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!string.Equals(
                Path.GetExtension(resolvedPath),
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var processName = Path.GetFileNameWithoutExtension(resolvedPath).Trim();
        if (processName.Length == 0)
        {
            return null;
        }

        if (BlockedLocalProcessNames.Contains(processName))
        {
            return null;
        }

        return new ConfiguredLocalPlayerEntry(
            new MusicPlayerDefinition("local", "本地播放器", processName),
            processName);
    }

    private sealed record ConfiguredLocalPlayerEntry(
        MusicPlayerDefinition Player,
        string ProcessName)
    {
        public bool Matches(string sourceAppUserModelId)
        {
            if (string.Equals(
                    sourceAppUserModelId,
                    ProcessName,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    sourceAppUserModelId,
                    $"{ProcessName}.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var normalizedSource = NormalizeSessionToken(sourceAppUserModelId);
            var normalizedProcess = NormalizeSessionToken(ProcessName);
            return normalizedProcess.Length >= 4
                && normalizedSource.Contains(normalizedProcess, StringComparison.Ordinal);
        }

        private static string NormalizeSessionToken(string value) => new(
            value.ToLowerInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray());
    }

    private sealed record PlayerEntry(
        MusicPlayerDefinition Player,
        IReadOnlyList<string> SourceTokens);
}
