namespace LyricsXQuiet.Core.Settings;

public static class AppearancePresets
{
    private static NamedAppearancePreset Create(string name, double size, string text, string progress, string background,
        double opacity, double scale, string backing = "SoftShadow", string weight = "SemiBold", string transition = "SlideFade",
        double secondaryOpacity = 0.74) => new(name,
            new AppearancePreset(size, "Microsoft YaHei UI", text, progress, background, opacity, true, true),
            scale, secondaryOpacity, backing, weight, transition);

    public static NamedAppearancePreset Veil { get; } = Create("词间薄幕", 24, "#F8FAFC", "#5EEAD4", "#0E1318", .52, .8);
    public static IReadOnlyList<NamedAppearancePreset> BuiltIn { get; } = Array.AsReadOnly(new[]
    {
        Create("经典原版", 24, "#FFFFFF", "#33FFDD", "#000000", .55, 1, "ProgressGlow", "Normal", "FadeOnly", 1),
        Veil,
        Create("无界清墨", 26, "#FFFFFF", "#5EEAD4", "#000000", 0, .8, "CrispOutline"),
        Create("影院双行", 28, "#F1F5F9", "#FDE047", "#0A0D10", .68, .78),
        Create("夜读柔光", 22, "#CBD5E1", "#38BDF8", "#11161B", .42, .82, weight: "Normal", transition: "FadeOnly")
    });
}
