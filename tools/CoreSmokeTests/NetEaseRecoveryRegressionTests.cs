using LyricsXQuiet.Core.Playback;

internal static class NetEaseRecoveryRegressionTests
{
    public static void Run()
    {
        var now = DateTimeOffset.UtcNow;
        var paused = new PlaybackSnapshot(new TrackIdentity("Song", "Artist", "", TimeSpan.Zero, "cloudmusic.exe"),
            PlaybackState.Paused, TimeSpan.Zero, 1, now, PlaybackTimelineKind.Estimated);
        Check(NetEaseRecoveryPolicy.IsSafelyPaused(paused, now), "A fresh paused NetEase session may recover.");
        foreach (var snapshot in new[]
        {
            paused with { State = PlaybackState.Playing }, paused with { State = PlaybackState.Unknown },
            paused with { TimelineKind = PlaybackTimelineKind.Reported },
            paused with { CapturedAt = now.AddSeconds(-5) }, paused with { CapturedAt = now.AddSeconds(1) },
            paused with { Track = paused.Track with { SourceAppUserModelId = "QQMusic.exe" } }
        }) Check(!NetEaseRecoveryPolicy.IsSafelyPaused(snapshot, now),
            "Playing, unknown, stale, already precise, or another player's session must never trigger recovery.");

        var policy = new NetEaseRecoveryPolicy();
        Check(!policy.Observe("original", false, false, false, now), "Playing must not restart.");
        Check(!policy.Observe("original", false, true, false, now.AddSeconds(1)), "Pausing must wait for the grace period.");
        Check(!policy.Observe("original", false, false, false, now.AddSeconds(8)), "Resuming must cancel pending recovery.");
        Check(!policy.Observe("original", false, true, false, now.AddSeconds(10)), "A second pause must start a new grace period.");
        Check(!policy.Observe("original", false, true, true, now.AddSeconds(20)), "An active updater must prevent restart.");
        Check(!policy.Observe("original", false, true, false, now.AddSeconds(21)), "Finishing an update must restart the grace period.");
        Check(policy.Observe("original", false, true, false, now.AddSeconds(30)), "A sustained pause should recover once.");
        Check(!policy.Observe("new-pid-without-flags", false, true, false, now.AddMinutes(1)),
            "A failed recovery that returns a new process must not cause a restart loop.");
        policy.Observe(null, false, false, false, now.AddMinutes(2));
        Check(!policy.Observe("new-pid-without-flags", false, true, false, now.AddMinutes(2).AddSeconds(3)),
            "The brief absence while restarting must not rearm a failed recovery.");
        policy.Observe("healthy", true, true, false, now.AddMinutes(3));
        Check(!policy.Observe("next-update", false, true, false, now.AddMinutes(4)), "A later update must wait again.");
        Check(policy.Observe("next-update", false, true, false, now.AddMinutes(4).AddSeconds(9)),
            "Recovery must remain available after a later update loses the previously working endpoint.");
        policy.Observe(null, false, false, false, now.AddMinutes(5));
        policy.Observe(null, false, false, false, now.AddMinutes(5).AddSeconds(16));
        Check(!policy.Attempted, "A genuine exit must allow recovery on the next independent launch.");
        Console.WriteLine("NetEase recovery policy passed: pause/update gating, freshness, repeat updates, restart-loop prevention.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
