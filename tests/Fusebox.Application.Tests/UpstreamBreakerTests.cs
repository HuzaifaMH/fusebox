using Fusebox.Application.Alerting;
using Fusebox.Application.Upstream;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Fusebox.Application.Tests;

public class UpstreamBreakersTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-03-04T12:00:00Z");

    private readonly FakeTimeProvider _time = new(T0);
    private readonly RecordingListener _listener = new();

    private UpstreamBreakers Create(int threshold = 3, TimeSpan? openFor = null, bool enabled = true) =>
        new(
            Options.Create(new UpstreamBreakerOptions { Enabled = enabled, FailureThreshold = threshold, OpenFor = openFor ?? TimeSpan.FromSeconds(30) }),
            _time,
            [_listener],
            NullLogger<UpstreamBreakers>.Instance);

    private static void Fail(UpstreamBreakers breakers, string cluster, int times)
    {
        for (var i = 0; i < times; i++)
        {
            Assert.True(breakers.TryAcquire(cluster));
            breakers.Failed(cluster, "HTTP 503");
        }
    }

    [Fact]
    public void Calls_go_through_until_enough_fail_in_a_row()
    {
        var breakers = Create(threshold: 3);

        Fail(breakers, "openai", 2);

        Assert.True(breakers.TryAcquire("openai"));
        Assert.Empty(_listener.Opened);
    }

    [Fact]
    public void The_vendor_is_cut_off_after_the_threshold_and_the_listener_is_told_once()
    {
        var breakers = Create(threshold: 3);

        Fail(breakers, "openai", 3);

        Assert.False(breakers.TryAcquire("openai"));
        var opened = Assert.Single(_listener.Opened);
        Assert.Equal(("openai", T0, "HTTP 503"), opened);
        Assert.True(breakers.Snapshot()["openai"]);
    }

    [Fact]
    public void A_success_in_between_starts_the_count_again()
    {
        var breakers = Create(threshold: 3);

        Fail(breakers, "openai", 2);
        breakers.Succeeded("openai");
        Fail(breakers, "openai", 2);

        Assert.True(breakers.TryAcquire("openai"));
        Assert.Empty(_listener.Opened);
    }

    [Fact]
    public void Each_vendor_is_judged_on_its_own()
    {
        var breakers = Create(threshold: 2);

        Fail(breakers, "openai", 2);

        Assert.False(breakers.TryAcquire("openai"));
        Assert.True(breakers.TryAcquire("anthropic"));
    }

    [Fact]
    public void After_the_pause_one_test_call_goes_through_and_success_closes_the_circuit()
    {
        var breakers = Create(threshold: 2);
        Fail(breakers, "openai", 2);

        _time.Advance(TimeSpan.FromSeconds(31));

        Assert.True(breakers.TryAcquire("openai"));
        Assert.False(breakers.TryAcquire("openai"));

        breakers.Succeeded("openai");

        Assert.True(breakers.TryAcquire("openai"));
        var closed = Assert.Single(_listener.Closed);
        Assert.Equal(("openai", T0, T0.AddSeconds(31)), closed);
        Assert.False(breakers.Snapshot()["openai"]);
    }

    [Fact]
    public void A_failed_test_call_keeps_it_cut_off_without_telling_anyone_again()
    {
        var breakers = Create(threshold: 2);
        Fail(breakers, "openai", 2);

        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.True(breakers.TryAcquire("openai"));
        breakers.Failed("openai", "HTTP 502");

        Assert.False(breakers.TryAcquire("openai"));
        Assert.Single(_listener.Opened);
        Assert.Empty(_listener.Closed);
    }

    [Fact]
    public void Successes_while_nothing_is_wrong_tell_nobody()
    {
        var breakers = Create();

        breakers.Succeeded("openai");
        breakers.Succeeded("openai");

        Assert.Empty(_listener.Closed);
    }

    [Fact]
    public void A_test_call_that_is_abandoned_lets_the_next_one_try()
    {
        var breakers = Create(threshold: 2);
        Fail(breakers, "openai", 2);
        _time.Advance(TimeSpan.FromSeconds(31));

        Assert.True(breakers.TryAcquire("openai"));
        breakers.Abandon("openai");

        Assert.True(breakers.TryAcquire("openai"));
    }

    [Fact]
    public void A_listener_that_throws_does_not_stop_the_others_or_the_caller()
    {
        var second = new RecordingListener();
        var breakers = new UpstreamBreakers(
            Options.Create(new UpstreamBreakerOptions { FailureThreshold = 2 }),
            _time,
            [new ThrowingListener(), second],
            NullLogger<UpstreamBreakers>.Instance);

        Fail(breakers, "openai", 2);

        Assert.Single(second.Opened);
    }

    [Fact]
    public void The_breakers_can_be_switched_off()
    {
        Assert.False(Create(enabled: false).Enabled);
        Assert.True(Create().Enabled);
    }

    private sealed class RecordingListener : IUpstreamHealthListener
    {
        public List<(string Cluster, DateTimeOffset At, string Reason)> Opened { get; } = [];

        public List<(string Cluster, DateTimeOffset OpenedAt, DateTimeOffset At)> Closed { get; } = [];

        public void CircuitOpened(string cluster, DateTimeOffset at, string lastFailure) => Opened.Add((cluster, at, lastFailure));

        public void CircuitClosed(string cluster, DateTimeOffset openedAt, DateTimeOffset at) => Closed.Add((cluster, openedAt, at));
    }

    private sealed class ThrowingListener : IUpstreamHealthListener
    {
        public void CircuitOpened(string cluster, DateTimeOffset at, string lastFailure) => throw new InvalidOperationException("listener broke");

        public void CircuitClosed(string cluster, DateTimeOffset openedAt, DateTimeOffset at) => throw new InvalidOperationException("listener broke");
    }
}

public class UpstreamAlertTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-03-04T12:00:00Z");

    private readonly RecordingInbox _inbox = new();

    private AlertingUpstreamListener Create() => new(_inbox, Microsoft.Extensions.Options.Options.Create(new UpstreamBreakerOptions { OpenFor = TimeSpan.FromSeconds(30) }));

    [Fact]
    public void A_vendor_being_cut_off_raises_a_critical_alert_named_after_it()
    {
        Create().CircuitOpened("openai", T0, "HTTP 503");

        var signal = Assert.IsType<QueuedRaise>(Assert.Single(_inbox.Items)).Signal;
        Assert.Equal("upstream:openai", signal.Fingerprint);
        Assert.Equal(AlertKind.UpstreamUnavailable, signal.Kind);
        Assert.Equal(AlertSeverity.Critical, signal.Severity);
        Assert.Contains("openai", signal.Title, StringComparison.Ordinal);
        Assert.Equal("HTTP 503", signal.Details["Last failure"]);
        Assert.Contains("30 seconds", signal.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_vendor_coming_back_resolves_it_with_a_note_on_how_long_it_was_out()
    {
        Create().CircuitClosed("openai", T0, T0.AddMinutes(4));

        var resolve = Assert.IsType<QueuedResolve>(Assert.Single(_inbox.Items));
        Assert.Equal("upstream:openai", resolve.Fingerprint);
        Assert.Contains("4 minutes", resolve.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_vendors_have_two_separate_alerts()
    {
        Assert.NotEqual(AlertSignals.UpstreamFingerprint("openai"), AlertSignals.UpstreamFingerprint("anthropic"));
    }
}
