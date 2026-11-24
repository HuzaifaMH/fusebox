using System.Text;
using Fusebox.Application.Detection;
using Fusebox.Application.Telemetry;
using Fusebox.Domain;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Fusebox.Application.Tests;

public class RepeatDetectorTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");
    private static readonly TenantId Globex = TenantId.Parse("globex");

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-03-04T12:00:00Z"));

    private RepeatDetector Create(Action<RepeatDetectionOptions>? configure = null)
    {
        var options = new RepeatDetectionOptions();
        configure?.Invoke(options);
        return new RepeatDetector(Options.Create(options), _time);
    }

    private static byte[] Body(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void The_first_request_is_not_a_repeat_and_the_same_one_again_is()
    {
        var detector = Create();

        Assert.False(detector.Observe(Acme, "chat", Body("hello"), declaredRetry: false));
        Assert.True(detector.Observe(Acme, "chat", Body("hello"), declaredRetry: false));
        Assert.True(detector.Observe(Acme, "chat", Body("hello"), declaredRetry: false));
    }

    [Fact]
    public void A_different_body_tenant_or_route_is_not_a_repeat()
    {
        var detector = Create();
        detector.Observe(Acme, "chat", Body("hello"), declaredRetry: false);

        Assert.False(detector.Observe(Acme, "chat", Body("hello!"), declaredRetry: false));
        Assert.False(detector.Observe(Globex, "chat", Body("hello"), declaredRetry: false));
        Assert.False(detector.Observe(Acme, "embeddings", Body("hello"), declaredRetry: false));
    }

    [Fact]
    public void A_request_is_only_a_repeat_within_the_window()
    {
        var detector = Create();
        detector.Observe(Acme, "chat", Body("hello"), declaredRetry: false);

        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.True(detector.Observe(Acme, "chat", Body("hello"), declaredRetry: false));

        // Seeing it again restarts the window, so a steady loop keeps counting.
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.True(detector.Observe(Acme, "chat", Body("hello"), declaredRetry: false));

        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.False(detector.Observe(Acme, "chat", Body("hello"), declaredRetry: false));
    }

    [Fact]
    public void A_client_that_says_it_is_retrying_is_believed()
    {
        var detector = Create();

        Assert.True(detector.Observe(Acme, "chat", Body("never seen before"), declaredRetry: true));
    }

    [Fact]
    public void Nothing_is_noticed_when_it_is_switched_off()
    {
        var detector = Create(o => o.Enabled = false);
        detector.Observe(Acme, "chat", Body("hello"), declaredRetry: false);

        Assert.False(detector.Enabled);
        Assert.False(detector.Observe(Acme, "chat", Body("hello"), declaredRetry: true));
    }

    [Fact]
    public void Memory_is_bounded_and_a_full_detector_simply_stops_remembering_new_requests()
    {
        var detector = Create(o => o.MaxTracked = 3);

        for (var i = 0; i < 10; i++)
        {
            detector.Observe(Acme, "chat", Body($"request {i}"), declaredRetry: false);
        }

        // The first three were remembered; the rest never were.
        Assert.True(detector.Observe(Acme, "chat", Body("request 0"), declaredRetry: false));
        Assert.False(detector.Observe(Acme, "chat", Body("request 9"), declaredRetry: false));
    }

    [Fact]
    public void Expired_requests_make_room_for_new_ones()
    {
        var detector = Create(o => o.MaxTracked = 2);
        detector.Observe(Acme, "chat", Body("a"), declaredRetry: false);
        detector.Observe(Acme, "chat", Body("b"), declaredRetry: false);

        _time.Advance(TimeSpan.FromSeconds(120));
        detector.Observe(Acme, "chat", Body("c"), declaredRetry: false);

        Assert.True(detector.Observe(Acme, "chat", Body("c"), declaredRetry: false));
    }

    [Fact]
    public void The_retry_header_names_come_from_configuration()
    {
        Assert.Contains("X-Stainless-Retry-Count", Create().RetryHeaders);
        Assert.Equal(["X-My-Retry"], Create(o =>
        {
            o.RetryHeaders.Clear();
            o.RetryHeaders.Add("X-My-Retry");
        }).RetryHeaders);
    }
}

public class RepeatSignalTests
{
    private static MinuteStats Minute(long requests, long forwarded, long repeats) =>
        new(DateTimeOffset.Parse("2026-03-04T12:00:00Z"), requests, forwarded, 0, 0, 0, 0, new long[16], repeats);

    [Fact]
    public void The_signal_is_the_share_of_forwarded_calls_that_repeat()
    {
        Assert.Equal(0.25, SignalExtractor.Value(Signal.RepeatRate, Minute(40, 40, 10), minForwarded: 3)!.Value, precision: 6);
    }

    [Fact]
    public void A_minute_with_too_few_calls_is_not_measured()
    {
        Assert.Null(SignalExtractor.Value(Signal.RepeatRate, Minute(2, 2, 2), minForwarded: 3));
    }

    [Fact]
    public void Merging_minutes_adds_up_their_repeats()
    {
        var merged = MinuteStats.Merge([Minute(10, 10, 4), Minute(20, 20, 6)]);

        Assert.Equal(10, merged.Repeats);
    }

    [Fact]
    public void The_signal_has_a_name_a_format_and_thresholds_but_no_fixed_limit()
    {
        Assert.Equal("Repeated requests", AnomalyDescription.Name(Signal.RepeatRate));
        Assert.Equal("85%", AnomalyDescription.Format(Signal.RepeatRate, 0.85));
        Assert.Null(new DetectionOptions().For(Signal.RepeatRate).FixedThreshold);
    }
}
