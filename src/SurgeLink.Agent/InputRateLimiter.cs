namespace SurgeLink.Agent;

/// <summary>
/// Bounds a single grant's terminal input and file mutation rate. The bucket holds one minute of
/// permits, so a short burst is allowed but sustained input is throttled.
/// </summary>
internal sealed class InputRateLimiter
{
    private readonly object _gate = new();
    private readonly int _permitsPerMinute;
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset _lastRefill;
    private double _tokens;

    public InputRateLimiter(int permitsPerMinute, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permitsPerMinute, 1);
        _permitsPerMinute = permitsPerMinute;
        _timeProvider = timeProvider;
        _tokens = permitsPerMinute;
        _lastRefill = timeProvider.GetUtcNow();
    }

    public bool TryAcquire()
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            var elapsedMinutes = (now - _lastRefill).TotalMinutes;
            if (elapsedMinutes > 0)
            {
                _tokens = Math.Min(_permitsPerMinute, _tokens + elapsedMinutes * _permitsPerMinute);
                _lastRefill = now;
            }
            else if (elapsedMinutes < 0)
            {
                _lastRefill = now;
            }

            if (_tokens < 1) return false;
            _tokens -= 1;
            return true;
        }
    }
}
