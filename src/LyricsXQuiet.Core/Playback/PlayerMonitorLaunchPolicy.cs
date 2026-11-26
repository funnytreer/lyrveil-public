namespace LyricsXQuiet.Core.Playback;

public sealed class PlayerMonitorLaunchPolicy
{
    private readonly TimeSpan _startupConfirmationTimeout;
    private readonly TimeSpan _initialRetryDelay;
    private readonly TimeSpan _maximumRetryDelay;
    private readonly TimeSpan _stableRunResetPeriod;
    private int _consecutiveFailures;
    private DateTimeOffset? _nextTransitionAt;
    private DateTimeOffset? _runningSince;

    public PlayerMonitorLaunchPolicy(
        TimeSpan startupConfirmationTimeout,
        TimeSpan initialRetryDelay,
        TimeSpan maximumRetryDelay,
        TimeSpan stableRunResetPeriod)
    {
        if (startupConfirmationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startupConfirmationTimeout),
                "启动确认时间必须大于零。");
        }

        if (initialRetryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialRetryDelay),
                "首次重试等待时间必须大于零。");
        }

        if (maximumRetryDelay < initialRetryDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRetryDelay),
                "最大重试等待时间不能短于首次重试等待时间。");
        }

        if (stableRunResetPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stableRunResetPeriod),
                "稳定运行确认时间必须大于零。");
        }

        _startupConfirmationTimeout = startupConfirmationTimeout;
        _initialRetryDelay = initialRetryDelay;
        _maximumRetryDelay = maximumRetryDelay;
        _stableRunResetPeriod = stableRunResetPeriod;
    }

    public PlayerMonitorLaunchState State { get; private set; }

    public int ConsecutiveFailures => _consecutiveFailures;

    public DateTimeOffset? NextTransitionAt => _nextTransitionAt;

    public void SuppressUntilAllPlayersExit()
    {
        Reset();
        State = PlayerMonitorLaunchState.SuppressedUntilAllPlayersExit;
    }

    public PlayerMonitorLaunchDecision Observe(
        bool playerRunning,
        bool mainInstanceRunning,
        DateTimeOffset now,
        bool anyOtherSupportedPlayerRunning = false)
    {
        if (State == PlayerMonitorLaunchState.SuppressedUntilAllPlayersExit && (playerRunning || anyOtherSupportedPlayerRunning))
            return PlayerMonitorLaunchDecision.None;

        if (!playerRunning)
        {
            Reset();
            return PlayerMonitorLaunchDecision.None;
        }

        if (mainInstanceRunning)
        {
            if (State != PlayerMonitorLaunchState.Running)
            {
                _runningSince = now;
            }

            State = PlayerMonitorLaunchState.Running;
            _nextTransitionAt = null;
            if (_runningSince is DateTimeOffset runningSince
                && now - runningSince >= _stableRunResetPeriod)
            {
                _consecutiveFailures = 0;
            }

            return PlayerMonitorLaunchDecision.None;
        }

        if (State == PlayerMonitorLaunchState.Running)
        {
            ScheduleRetry(now);
            return PlayerMonitorLaunchDecision.None;
        }

        if (State == PlayerMonitorLaunchState.AwaitingConfirmation)
        {
            if (now < _nextTransitionAt)
            {
                return PlayerMonitorLaunchDecision.None;
            }

            ScheduleRetry(now);
            return PlayerMonitorLaunchDecision.None;
        }

        if (State == PlayerMonitorLaunchState.RetryDelay
            && now < _nextTransitionAt)
        {
            return PlayerMonitorLaunchDecision.None;
        }

        State = PlayerMonitorLaunchState.AwaitingConfirmation;
        _nextTransitionAt = now + _startupConfirmationTimeout;
        return PlayerMonitorLaunchDecision.StartMainInstance;
    }

    public void ReportLaunchFailure(DateTimeOffset now)
    {
        if (State == PlayerMonitorLaunchState.AwaitingConfirmation)
        {
            ScheduleRetry(now);
        }
    }

    private void ScheduleRetry(DateTimeOffset now)
    {
        _consecutiveFailures++;
        State = PlayerMonitorLaunchState.RetryDelay;
        _nextTransitionAt = now + GetRetryDelay(_consecutiveFailures);
        _runningSince = null;
    }

    private TimeSpan GetRetryDelay(int failureCount)
    {
        var ticks = _initialRetryDelay.Ticks;
        for (var index = 1; index < failureCount; index++)
        {
            if (ticks >= _maximumRetryDelay.Ticks / 2)
            {
                return _maximumRetryDelay;
            }

            ticks *= 2;
        }

        return TimeSpan.FromTicks(Math.Min(ticks, _maximumRetryDelay.Ticks));
    }

    private void Reset()
    {
        State = PlayerMonitorLaunchState.Idle;
        _consecutiveFailures = 0;
        _nextTransitionAt = null;
        _runningSince = null;
    }
}

public enum PlayerMonitorLaunchState
{
    Idle,
    AwaitingConfirmation,
    RetryDelay,
    Running,
    SuppressedUntilAllPlayersExit
}

public enum PlayerMonitorLaunchDecision
{
    None,
    StartMainInstance
}
