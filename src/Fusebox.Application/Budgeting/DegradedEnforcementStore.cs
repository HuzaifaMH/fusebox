using System.Collections.Concurrent;
using Fusebox.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fusebox.Application.Budgeting;

/// <summary>
/// Keeps the gateway serving when the shared budget store is unreachable, without giving up enforcement.
/// </summary>
/// <remarks>
/// While the store answers, every call goes straight to it. When it fails repeatedly, a circuit breaker stops
/// the gateway asking it and the gateway enters degraded mode: each tenant gets a strict share of its budget
/// that this instance enforces on its own, using the last known config. After a time limit it stops serving
/// unverified calls. Routes set to <see cref="FailureMode.FailClosed"/> never serve unverified.
/// Listeners are told when degraded mode starts, ends or runs out.
/// </remarks>
public sealed partial class DegradedEnforcementStore : IEnforcementStore, IEnforcementStatus
{
    private readonly IEnforcementStore _store;
    private readonly IEnforcementStore _localStore;
    private readonly DegradedModeOptions _options;
    private readonly TimeProvider _time;
    private readonly IEnforcementOutageListener[] _listeners;
    private readonly ILogger<DegradedEnforcementStore> _logger;
    private readonly CircuitBreaker _breaker;

    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<TenantId, long> _unverifiedMicroDollars = new();
    private DateTimeOffset _outageStartedAt;
    private bool _limitReported;
    private long _unverifiedCalls;
    private volatile bool _inOutage;

    /// <param name="store">The shared store, such as Redis.</param>
    /// <param name="localStore">A store private to this instance, used for the local limit while degraded.</param>
    public DegradedEnforcementStore(
        IEnforcementStore store,
        IEnforcementStore localStore,
        IOptions<DegradedModeOptions> options,
        TimeProvider time,
        IEnumerable<IEnforcementOutageListener> listeners,
        ILogger<DegradedEnforcementStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(listeners);

        _store = store;
        _localStore = localStore;
        _options = options.Value;
        _time = time;
        _listeners = [.. listeners];
        _logger = logger;
        _breaker = new CircuitBreaker(_options.FailureThreshold, _options.ProbeInterval, time);
    }

    /// <inheritdoc />
    public bool IsDegraded => _inOutage;

    public async ValueTask<ReserveResult> TryReserveAsync(BudgetCharge charge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(charge);

        Exception? failure = null;

        if (_breaker.TryAcquire())
        {
            try
            {
                var result = await WithTimeoutAsync(_store.TryReserveAsync(charge, cancellationToken), cancellationToken);
                StoreAnswered();
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller left; that says nothing about the store.
                _breaker.Abandon();
                throw;
            }
#pragma warning disable CA1031 // Any failure to reach the store, whatever its type, means the same: it did not answer.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                failure = exception;
                StoreFailed(exception);
            }
        }

        return await ReserveLocallyAsync(charge, failure, cancellationToken);
    }

    public async ValueTask<SettleOutcome> SettleAsync(SettleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A hold taken locally during an outage is settled locally, whatever state the store is in now.
        if (await _localStore.SettleAsync(request, cancellationToken) == SettleOutcome.Applied)
        {
            var delta = request.Actual.MicroDollars - request.Reservation.Reserved.MicroDollars;
            _unverifiedMicroDollars.AddOrUpdate(request.Reservation.TenantId, delta, (_, current) => current + delta);
            return SettleOutcome.Applied;
        }

        if (!_breaker.TryAcquire())
        {
            throw new EnforcementUnavailableException("The budget store is unreachable, so the call could not be settled.");
        }

        try
        {
            var outcome = await WithTimeoutAsync(_store.SettleAsync(request, cancellationToken), cancellationToken);
            StoreAnswered();
            return outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _breaker.Abandon();
            throw;
        }
#pragma warning disable CA1031 // See TryReserveAsync.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // The hold stays in the store until it expires, which counts against the budget: the safe direction.
            StoreFailed(exception);
            throw;
        }
    }

    // The call keeps running after a timeout. If it lands in the store later it leaves a hold that expires on its own.
    private async ValueTask<T> WithTimeoutAsync<T>(ValueTask<T> call, CancellationToken cancellationToken) =>
        await call.AsTask().WaitAsync(_options.StoreTimeout, _time, cancellationToken);

    private async ValueTask<ReserveResult> ReserveLocallyAsync(
        BudgetCharge charge,
        Exception? failure,
        CancellationToken cancellationToken)
    {
        if (charge.FailureMode == FailureMode.FailClosed)
        {
            LogRejectedUnverified(charge.TenantId, "the route is fail-closed");
            throw new EnforcementUnavailableException(
                "The budget store is unreachable and this route does not allow unverified calls.", failure);
        }

        if (HasReachedLimit(_time.GetUtcNow()))
        {
            LogRejectedUnverified(charge.TenantId, "degraded mode has run out");
            throw new EnforcementUnavailableException(
                "The budget store has been unreachable for longer than degraded mode allows.", failure);
        }

        var localCharge = charge with { Policy = LocalPolicy(charge.Policy) };
        var result = await _localStore.TryReserveAsync(localCharge, cancellationToken);

        if (result.Granted)
        {
            Interlocked.Increment(ref _unverifiedCalls);
            _unverifiedMicroDollars.AddOrUpdate(
                charge.TenantId, charge.Amount.MicroDollars, (_, current) => current + charge.Amount.MicroDollars);
            LogServedLocally(charge.TenantId);
        }

        return result;
    }

    // Rounds down so the local share is never more than asked for.
    private BudgetPolicy LocalPolicy(BudgetPolicy policy) =>
        new(Money.FromMicroDollars((long)decimal.Floor(policy.Limit.MicroDollars * _options.LocalBudgetFraction)), policy.Window);

    private void StoreAnswered()
    {
        _breaker.RecordSuccess();

        if (_inOutage)
        {
            EndOutage();
        }
    }

    private void StoreFailed(Exception exception)
    {
        LogStoreFailed(exception);

        if (_breaker.RecordFailure())
        {
            BeginOutage(exception.Message);
        }
    }

    private void BeginOutage(string reason)
    {
        var now = _time.GetUtcNow();

        lock (_gate)
        {
            if (_inOutage)
            {
                return;
            }

            _outageStartedAt = now;
            _limitReported = false;
            _inOutage = true;
        }

        Notify(listener => listener.OutageStarted(now, reason));
    }

    private void EndOutage()
    {
        OutageSummary summary;
        var now = _time.GetUtcNow();

        lock (_gate)
        {
            if (!_inOutage)
            {
                return;
            }

            _inOutage = false;

            var spend = new Dictionary<TenantId, Money>();
            foreach (var tenantId in _unverifiedMicroDollars.Keys)
            {
                if (_unverifiedMicroDollars.TryRemove(tenantId, out var microDollars) && microDollars > 0)
                {
                    spend[tenantId] = Money.FromMicroDollars(microDollars);
                }
            }

            summary = new OutageSummary(_outageStartedAt, now, Interlocked.Exchange(ref _unverifiedCalls, 0), spend);
        }

        Notify(listener => listener.OutageEnded(summary));
    }

    // True once degraded mode has run past its limit. Tells listeners the first time.
    private bool HasReachedLimit(DateTimeOffset now)
    {
        DateTimeOffset startedAt;
        bool firstTime;

        lock (_gate)
        {
            if (!_inOutage || now - _outageStartedAt <= _options.MaxDuration)
            {
                return false;
            }

            startedAt = _outageStartedAt;
            firstTime = !_limitReported;
            _limitReported = true;
        }

        if (firstTime)
        {
            Notify(listener => listener.OutageLimitReached(startedAt, now));
        }

        return true;
    }

#pragma warning disable CA1031 // A listener must never be able to break a request.
    private void Notify(Action<IEnforcementOutageListener> call)
    {
        foreach (var listener in _listeners)
        {
            try
            {
                call(listener);
            }
            catch (Exception exception)
            {
                LogListenerFailed(exception, listener.GetType().Name);
            }
        }
    }
#pragma warning restore CA1031

    [LoggerMessage(EventId = 1201, Level = LogLevel.Warning,
        Message = "The budget store did not answer")]
    private partial void LogStoreFailed(Exception exception);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Debug,
        Message = "Allowed a call for tenant {TenantId} on the local limit, without the shared store")]
    private partial void LogServedLocally(TenantId tenantId);

    [LoggerMessage(EventId = 1203, Level = LogLevel.Debug,
        Message = "Rejected a call for tenant {TenantId} because the budget store is unreachable and {Reason}")]
    private partial void LogRejectedUnverified(TenantId tenantId, string reason);

    [LoggerMessage(EventId = 1204, Level = LogLevel.Error,
        Message = "The outage listener {Listener} failed")]
    private partial void LogListenerFailed(Exception exception, string listener);
}
