using System.Collections.Concurrent;
using Fusebox.Application.Budgeting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fusebox.Application.Upstream;

/// <summary>Settings for the upstream circuit breakers. Section <c>Fusebox:Upstream:Breaker</c>.</summary>
/// <remarks>A vendor is named as in the proxy's cluster configuration, such as "openai".</remarks>
public sealed class UpstreamBreakerOptions
{
    public const string SectionName = "Fusebox:Upstream:Breaker";

    /// <summary>
    /// When on, a vendor that keeps failing is cut off for a while: calls to it are refused at once, without holding any
    /// budget, instead of every caller waiting for a timeout. Turn off to pass every call through whatever the vendor is doing.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How many calls in a row must fail (a 5xx answer, or no answer) before the vendor is cut off.</summary>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>How long the vendor stays cut off before one call is let through to see whether it is back.</summary>
    public TimeSpan OpenFor { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Port: told when a vendor is cut off for failing and when it comes back, so someone can be alerted.</summary>
/// <remarks>Methods run on the request path, so implementations must return quickly, for example by queueing the work.</remarks>
public interface IUpstreamHealthListener
{
    /// <summary>Calls to this vendor have failed repeatedly, so it is now cut off.</summary>
    void CircuitOpened(string cluster, DateTimeOffset at, string lastFailure);

    /// <summary>The vendor answered a test call, so it is no longer cut off.</summary>
    void CircuitClosed(string cluster, DateTimeOffset openedAt, DateTimeOffset at);
}

/// <summary>
/// One circuit breaker per vendor. Each vendor's health is judged on its own, so one failing does not cut off another.
/// </summary>
/// <remarks>
/// A call that fails because the caller hung up says nothing about the vendor and should be <see cref="Abandon"/>ed, not counted.
/// </remarks>
public sealed partial class UpstreamBreakers
{
    private readonly UpstreamBreakerOptions _options;
    private readonly TimeProvider _time;
    private readonly IUpstreamHealthListener[] _listeners;
    private readonly ILogger<UpstreamBreakers> _logger;
    private readonly ConcurrentDictionary<string, Circuit> _circuits = new(StringComparer.Ordinal);

    public UpstreamBreakers(
        IOptions<UpstreamBreakerOptions> options,
        TimeProvider time,
        IEnumerable<IUpstreamHealthListener> listeners,
        ILogger<UpstreamBreakers> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(listeners);

        _options = options.Value;
        _time = time;
        _listeners = [.. listeners];
        _logger = logger;
    }

    public bool Enabled => _options.Enabled;

    /// <summary>How long a cut-off vendor stays cut off between test calls, for telling callers when to try again.</summary>
    public TimeSpan OpenFor => _options.OpenFor;

    /// <summary>True if a call may go to the vendor now. A caller that gets true must report the result.</summary>
    public bool TryAcquire(string cluster) => CircuitFor(cluster).Breaker.TryAcquire();

    public void Succeeded(string cluster)
    {
        var circuit = CircuitFor(cluster);

        if (!circuit.Breaker.RecordSuccess())
        {
            return;
        }

        var openedAt = circuit.OpenedAt;
        var now = _time.GetUtcNow();
        LogClosed(cluster, now - openedAt);
        Notify(listener => listener.CircuitClosed(cluster, openedAt, now));
    }

    public void Failed(string cluster, string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);

        var circuit = CircuitFor(cluster);
        circuit.LastFailure = reason;

        if (!circuit.Breaker.RecordFailure())
        {
            return;
        }

        var now = _time.GetUtcNow();
        circuit.OpenedAt = now;
        LogOpened(cluster, _options.FailureThreshold, reason);
        Notify(listener => listener.CircuitOpened(cluster, now, reason));
    }

    /// <summary>The caller gave up before learning how the vendor answered.</summary>
    public void Abandon(string cluster) => CircuitFor(cluster).Breaker.Abandon();

    /// <summary>Whether each vendor seen so far is cut off right now.</summary>
    public IReadOnlyDictionary<string, bool> Snapshot() => _circuits.ToDictionary(pair => pair.Key, pair => pair.Value.Breaker.IsOpen, StringComparer.Ordinal);

    private Circuit CircuitFor(string cluster) =>
        _circuits.GetOrAdd(cluster, _ => new Circuit(new CircuitBreaker(_options.FailureThreshold, _options.OpenFor, _time)));

#pragma warning disable CA1031 // A listener that fails must never stop a request.
    private void Notify(Action<IUpstreamHealthListener> notify)
    {
        foreach (var listener in _listeners)
        {
            try
            {
                notify(listener);
            }
            catch (Exception exception)
            {
                LogListenerFailed(exception);
            }
        }
    }
#pragma warning restore CA1031

    private sealed class Circuit(CircuitBreaker breaker)
    {
        public CircuitBreaker Breaker { get; } = breaker;

        public DateTimeOffset OpenedAt { get; set; }

        public string LastFailure { get; set; } = string.Empty;
    }

    [LoggerMessage(EventId = 5101, Level = LogLevel.Error, Message = "Upstream {Cluster} failed {Failures} calls in a row (last: {Reason}); calls to it are now refused until a test call succeeds")]
    private partial void LogOpened(string cluster, int failures, string reason);

    [LoggerMessage(EventId = 5102, Level = LogLevel.Information, Message = "Upstream {Cluster} answered a test call after {Outage}; calls to it flow again")]
    private partial void LogClosed(string cluster, TimeSpan outage);

    [LoggerMessage(EventId = 5103, Level = LogLevel.Error, Message = "A listener for upstream health failed")]
    private partial void LogListenerFailed(Exception exception);
}
