namespace Fusebox.Application.Budgeting;

/// <summary>
/// Stops calls to a failing dependency so a slow or dead one cannot stall every request.
/// Closed: calls go through. After enough failures in a row it opens: calls are refused. After
/// <c>probeInterval</c> one call is let through to test the dependency; success closes it, failure keeps it open.
/// </summary>
internal sealed class CircuitBreaker(int failureThreshold, TimeSpan probeInterval, TimeProvider time)
{
    private readonly Lock _gate = new();
    private int _failures;
    private DateTimeOffset _openedAt;
    private bool _probing;

    // Read without the lock on the hot path. Writes happen under the lock.
    private volatile bool _open;
    private volatile bool _healthy = true;

    /// <summary>True if the caller may use the dependency now. A caller that gets true must report the result.</summary>
    public bool TryAcquire()
    {
        if (!_open)
        {
            return true;
        }

        lock (_gate)
        {
            if (!_open)
            {
                return true;
            }

            if (_probing || time.GetUtcNow() - _openedAt < probeInterval)
            {
                return false;
            }

            _probing = true;
            return true;
        }
    }

    /// <summary>True while the circuit is open, so calls are being refused.</summary>
    public bool IsOpen => _open;

    /// <summary>Records a success. Returns true if this success closed an open circuit.</summary>
    public bool RecordSuccess()
    {
        if (_healthy)
        {
            return false;
        }

        lock (_gate)
        {
            var wasOpen = _open;
            _failures = 0;
            _probing = false;
            _open = false;
            _healthy = true;
            return wasOpen;
        }
    }

    /// <summary>Records a failure. Returns true if this failure opened the circuit.</summary>
    public bool RecordFailure()
    {
        lock (_gate)
        {
            _healthy = false;
            _probing = false;

            if (_open)
            {
                // A failed probe: stay open and wait a full interval before the next one.
                _openedAt = time.GetUtcNow();
                return false;
            }

            if (++_failures < failureThreshold)
            {
                return false;
            }

            _openedAt = time.GetUtcNow();
            _open = true;
            return true;
        }
    }

    /// <summary>Releases a probe whose caller gave up before learning the result.</summary>
    public void Abandon()
    {
        lock (_gate)
        {
            _probing = false;
        }
    }
}
