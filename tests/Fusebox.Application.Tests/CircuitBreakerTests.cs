using Fusebox.Application.Budgeting;
using Microsoft.Extensions.Time.Testing;

namespace Fusebox.Application.Tests;

public class CircuitBreakerTests
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-03-04T10:15:00Z"));

    private CircuitBreaker Create(int failureThreshold = 3) => new(failureThreshold, ProbeInterval, _time);

    private static void Fail(CircuitBreaker breaker, int times)
    {
        for (var i = 0; i < times; i++)
        {
            Assert.True(breaker.TryAcquire());
            breaker.RecordFailure();
        }
    }

    [Fact]
    public void Lets_calls_through_while_closed()
    {
        var breaker = Create();

        Assert.True(breaker.TryAcquire());
        Assert.True(breaker.TryAcquire());
    }

    [Fact]
    public void Stays_closed_until_the_failure_threshold_is_reached()
    {
        var breaker = Create(failureThreshold: 3);

        Fail(breaker, 2);

        Assert.True(breaker.TryAcquire());
    }

    [Fact]
    public void Reports_the_failure_that_opens_it_and_only_that_one()
    {
        var breaker = Create(failureThreshold: 3);

        Assert.False(breaker.RecordFailure());
        Assert.False(breaker.RecordFailure());
        Assert.True(breaker.RecordFailure());
        Assert.False(breaker.RecordFailure());
    }

    [Fact]
    public void Refuses_calls_while_open()
    {
        var breaker = Create();
        Fail(breaker, 3);

        Assert.False(breaker.TryAcquire());

        _time.Advance(ProbeInterval - TimeSpan.FromMilliseconds(1));
        Assert.False(breaker.TryAcquire());
    }

    [Fact]
    public void A_success_resets_the_count_of_failures_in_a_row()
    {
        var breaker = Create(failureThreshold: 3);

        Fail(breaker, 2);
        breaker.RecordSuccess();
        Fail(breaker, 2);

        Assert.True(breaker.TryAcquire());
    }

    [Fact]
    public void Lets_one_probe_through_after_the_interval()
    {
        var breaker = Create();
        Fail(breaker, 3);
        _time.Advance(ProbeInterval);

        Assert.True(breaker.TryAcquire());
        Assert.False(breaker.TryAcquire());
    }

    [Fact]
    public void Closes_when_the_probe_succeeds()
    {
        var breaker = Create();
        Fail(breaker, 3);
        _time.Advance(ProbeInterval);
        Assert.True(breaker.TryAcquire());

        breaker.RecordSuccess();

        Assert.True(breaker.TryAcquire());
        Assert.True(breaker.TryAcquire());
    }

    [Fact]
    public void Stays_open_and_waits_a_full_interval_again_when_the_probe_fails()
    {
        var breaker = Create();
        Fail(breaker, 3);
        _time.Advance(ProbeInterval);
        Assert.True(breaker.TryAcquire());

        Assert.False(breaker.RecordFailure());

        Assert.False(breaker.TryAcquire());
        _time.Advance(ProbeInterval);
        Assert.True(breaker.TryAcquire());
    }

    [Fact]
    public void Allows_another_probe_when_the_first_caller_gave_up()
    {
        var breaker = Create();
        Fail(breaker, 3);
        _time.Advance(ProbeInterval);
        Assert.True(breaker.TryAcquire());

        breaker.Abandon();

        Assert.True(breaker.TryAcquire());
    }
}
