namespace LyricsXQuiet.Core.Playback;

public enum PlaybackConnectionState
{
    WaitingForPlayer,
    PlayerNeedsMediaSession,
    Connected
}

public sealed record PlaybackConnectionStatus(
    PlaybackConnectionState State,
    MusicPlayerDefinition? Player = null)
{
    public static PlaybackConnectionStatus Waiting { get; } =
        new(PlaybackConnectionState.WaitingForPlayer);
}
