namespace LyricsXQuiet.Tools.NetEaseBridgeProbe;

internal sealed class ProbeException : Exception
{
    public ProbeException(string message)
        : base(message)
    {
    }

    public ProbeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
