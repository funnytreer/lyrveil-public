namespace LyricsXQuiet.Core.Lyrics;

public static class LyricsStorage
{
    public const string OwnershipMarkerFileName = ".lyrveil-lyrics";

    public static string DefaultDirectory
    {
        get
        {
            var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (string.IsNullOrWhiteSpace(music))
            {
                music = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }

            return Path.Combine(music, "Lyrveil Lyrics");
        }
    }

    public static void EnsureOwnedDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var resolved = Path.GetFullPath(directory);
        Directory.CreateDirectory(resolved);
        var markerPath = Path.Combine(resolved, OwnershipMarkerFileName);
        if (!File.Exists(markerPath))
        {
            File.WriteAllText(
                markerPath,
                "Lyrveil managed lyrics directory v1" + Environment.NewLine);
        }
    }

    public static bool IsOwnedDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var resolved = Path.GetFullPath(directory);
        if (File.Exists(Path.Combine(resolved, OwnershipMarkerFileName)))
        {
            return true;
        }

        // The original default directory predates ownership markers. It has
        // always been dedicated to Lyrveil, so it is the only safe legacy path.
        return string.Equals(
            Path.TrimEndingDirectorySeparator(resolved),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(DefaultDirectory)),
            StringComparison.OrdinalIgnoreCase);
    }
}
