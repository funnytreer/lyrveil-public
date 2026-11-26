namespace LyricsXQuiet.Core.Playback;

public sealed class CompanionExitPolicy
{
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _restartProbePeriod;
    private readonly TimeSpan _updateWaitTimeout;
    private DateTimeOffset? _waitingForFirstPlayerSince;
    private DateTimeOffset? _playerMissingSince;
    private DateTimeOffset? _updateWaitUntil;
    private bool _hasObservedPlayer;

    public bool HasObservedPlayer => _hasObservedPlayer;
    public bool IsWaitingForUpdate => _updateWaitUntil is not null;

    public CompanionExitPolicy(
        TimeSpan startupTimeout,
        TimeSpan restartProbePeriod,
        TimeSpan updateWaitTimeout)
    {
        if (startupTimeout < TimeSpan.Zero
            || restartProbePeriod < TimeSpan.Zero
            || updateWaitTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(startupTimeout), "等待时间不能为负数。");
        }

        _startupTimeout = startupTimeout;
        _restartProbePeriod = restartProbePeriod;
        _updateWaitTimeout = updateWaitTimeout;
    }

    public bool ShouldExit(
        bool anyPlayerRunning,
        bool configurationWindowOpen,
        bool updateActivityDetected,
        DateTimeOffset now,
        bool requireObservedPlayer = false)
    {
        if (anyPlayerRunning)
        {
            _hasObservedPlayer = true;
            _waitingForFirstPlayerSince = null;
            _playerMissingSince = null;
            _updateWaitUntil = null;
            return false;
        }

        if (configurationWindowOpen)
        {
            return false;
        }

        if (_hasObservedPlayer)
        {
            _playerMissingSince ??= now;
            if (updateActivityDetected)
            {
                _updateWaitUntil ??= now + _updateWaitTimeout;
            }

            if (_updateWaitUntil is DateTimeOffset updateWaitUntil)
            {
                return now >= updateWaitUntil;
            }

            return now - _playerMissingSince >= _restartProbePeriod;
        }

        if (requireObservedPlayer) return false;
        _waitingForFirstPlayerSince ??= now;
        return now - _waitingForFirstPlayerSince >= _startupTimeout;
    }
}
