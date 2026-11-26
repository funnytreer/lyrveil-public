using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LyricsXQuiet.Core.Lyrics;

/// <summary>
/// Canonicalizes Chinese player/provider metadata only for release identity matching.
/// It does not inspect, convert, label or rank the lyric text itself.
/// </summary>
internal static class ChineseMetadataNormalizer
{
    private const uint MapSimplifiedChinese = 0x02000000;

    public static string NormalizeForMatching(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!OperatingSystem.IsWindows() || !value.Any(IsHanCharacter))
        {
            return value;
        }

        return TryMapToSimplifiedChinese(value, out var simplified)
            ? simplified
            : value;
    }

    [SupportedOSPlatform("windows")]
    private static bool TryMapToSimplifiedChinese(string value, out string result)
    {
        result = value;
        var required = LCMapStringEx(
            "zh-CN",
            MapSimplifiedChinese,
            value,
            value.Length,
            null,
            0,
            nint.Zero,
            nint.Zero,
            nint.Zero);
        if (required <= 0)
        {
            return false;
        }

        var buffer = new char[required];
        var written = LCMapStringEx(
            "zh-CN",
            MapSimplifiedChinese,
            value,
            value.Length,
            buffer,
            buffer.Length,
            nint.Zero,
            nint.Zero,
            nint.Zero);
        if (written <= 0)
        {
            return false;
        }

        if (buffer[written - 1] == '\0')
        {
            written--;
        }

        result = new string(buffer, 0, written);
        return true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringEx(
        string lpLocaleName,
        uint dwMapFlags,
        string lpSrcStr,
        int cchSrc,
        [Out] char[]? lpDestStr,
        int cchDest,
        nint lpVersionInformation,
        nint lpReserved,
        nint sortHandle);

    private static bool IsHanCharacter(char value) =>
        value is >= '\u3400' and <= '\u9FFF'
            or >= '\uF900' and <= '\uFAFF';
}
