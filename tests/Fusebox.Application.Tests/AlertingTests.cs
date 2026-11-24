using Fusebox.Application.Alerting;
using Fusebox.Application.Budgeting;
using Fusebox.Application.Detection;
using Fusebox.Application.Diagnosis;
using Fusebox.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static Fusebox.Application.Tests.AlertSamples;

namespace Fusebox.Application.Tests;

public class AlertPolicyTests
{
    private readonly AlertOptions _options = new();

    private static AlertRecord Open(AlertSeverity severity = AlertSeverity.Warning, string revision = "detected") =>
        new(Guid.NewGuid(), "anomaly:acme:openai-chat:spend", AlertKind.Anomaly, severity, "t", "s", new Dictionary<string, string>(), revision, Now.AddMinutes(-10), Now.AddMinutes(-1));

    private AlertAction Decide(AlertSignal signal, AlertRecord? open = null, DateTimeOffset? lastResolved = null) =>
        AlertPolicy.Decide(signal, open, lastResolved, Now, _options);

    [Fact]
    public void A_new_incident_opens_an_alert()
    {
        Assert.Equal(AlertAction.Open, Decide(SampleSignal()));
    }

    [Fact]
    public void Raising_an_incident_that_is_already_open_says_nothing_new()
    {
        Assert.Equal(AlertAction.Ignore, Decide(SampleSignal(), Open()));
    }

    [Fact]
    public void A_more_severe_signal_escalates_the_open_alert()
    {
        Assert.Equal(AlertAction.Escalate, Decide(SampleSignal(severity: AlertSeverity.Critical), Open(AlertSeverity.Warning)));
    }

    [Fact]
    public void A_less_severe_signal_never_downgrades_an_open_alert()
    {
        Assert.Equal(AlertAction.Ignore, Decide(SampleSignal(severity: AlertSeverity.Info), Open(AlertSeverity.Critical)));
        Assert.Equal(AlertAction.Ignore, Decide(SampleSignal(severity: AlertSeverity.Warning), Open(AlertSeverity.Critical)));
    }

    [Fact]
    public void A_new_revision_updates_the_open_alert_but_only_when_the_revision_changed()
    {
        Assert.Equal(AlertAction.Update, Decide(SampleSignal(revision: "diagnosed-template"), Open(revision: "detected")));
        Assert.Equal(AlertAction.Ignore, Decide(SampleSignal(revision: "detected"), Open(revision: "detected")));
    }

    [Fact]
    public void Escalation_takes_precedence_over_an_update()
    {
        Assert.Equal(AlertAction.Escalate, Decide(SampleSignal(severity: AlertSeverity.Critical, revision: "diagnosed-template"), Open()));
    }

    [Theory]
    [InlineData(AlertSeverity.Warning, 14, AlertAction.Ignore)]
    [InlineData(AlertSeverity.Warning, 16, AlertAction.Open)]
    [InlineData(AlertSeverity.Critical, 4, AlertAction.Ignore)]
    [InlineData(AlertSeverity.Critical, 6, AlertAction.Open)]
    public void A_recently_resolved_incident_cannot_raise_a_new_alert_until_its_cooldown_is_over(AlertSeverity severity, int minutesSinceResolved, AlertAction expected)
    {
        var decision = Decide(SampleSignal(severity: severity), lastResolved: Now.AddMinutes(-minutesSinceResolved));

        Assert.Equal(expected, decision);
    }

    [Fact]
    public void A_critical_alert_has_a_shorter_cooldown_than_a_warning()
    {
        Assert.True(AlertPolicy.Cooldown(AlertSeverity.Critical, _options) < AlertPolicy.Cooldown(AlertSeverity.Warning, _options));
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 40)]
    [InlineData(7, 300)]
    [InlineData(40, 300)]
    public void Waits_twice_as_long_after_each_failure_up_to_a_ceiling(int attempts, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), AlertPolicy.Backoff(attempts, _options));
    }

    [Fact]
    public void Never_waits_less_than_the_first_backoff_even_for_a_nonsense_attempt_count()
    {
        Assert.Equal(_options.FirstBackoff, AlertPolicy.Backoff(0, _options));
        Assert.Equal(_options.FirstBackoff, AlertPolicy.Backoff(-3, _options));
    }
}

public class AlertRouterTests
{
    private static AlertRouter Create(AlertOptions options, params string[] channels) =>
        new(Options.Create(options), channels.Select(c => new FakeNotifier(c)));

    [Fact]
    public void Sends_critical_alerts_to_pagerduty_and_the_rest_to_slack_by_default()
    {
        var router = Create(new AlertOptions(), Channels.PagerDuty, Channels.Slack);

        Assert.Equal([Channels.PagerDuty], router.ChannelsFor(AlertSeverity.Critical));
        Assert.Equal([Channels.Slack], router.ChannelsFor(AlertSeverity.Warning));
        Assert.Equal([Channels.Slack], router.ChannelsFor(AlertSeverity.Info));
    }

    [Fact]
    public void Never_drops_a_critical_alert_because_pagerduty_is_not_set_up()
    {
        var router = Create(new AlertOptions(), Channels.Slack);

        Assert.Equal([Channels.Slack], router.ChannelsFor(AlertSeverity.Critical));
    }

    [Fact]
    public void Uses_whatever_channel_exists_when_the_configured_one_does_not()
    {
        var router = Create(new AlertOptions(), Channels.Webhook);

        Assert.Equal([Channels.Webhook], router.ChannelsFor(AlertSeverity.Warning));
        Assert.Equal([Channels.Webhook], router.ChannelsFor(AlertSeverity.Critical));
    }

    [Fact]
    public void Has_nowhere_to_send_only_when_no_channel_is_available_at_all()
    {
        Assert.Empty(Create(new AlertOptions()).ChannelsFor(AlertSeverity.Critical));
    }

    [Fact]
    public void Can_route_one_severity_to_several_channels()
    {
        var options = new AlertOptions();
        options.Routing["Critical"] = [Channels.PagerDuty, Channels.Slack];

        var router = Create(options, Channels.PagerDuty, Channels.Slack);

        Assert.Equal([Channels.PagerDuty, Channels.Slack], router.ChannelsFor(AlertSeverity.Critical));
    }

    [Fact]
    public void Matches_channel_and_severity_names_without_regard_to_case()
    {
        var options = new AlertOptions { Routing = new Dictionary<string, List<string>> { ["critical"] = ["SLACK"] } };

        Assert.Equal([Channels.Slack], Create(options, Channels.Slack, Channels.PagerDuty).ChannelsFor(AlertSeverity.Critical));
    }
}

public class AlertServiceTests
{
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeAlertStore _store = new(Now);
    private readonly AlertOptions _options = new();

    private AlertService Create(params string[] channels) => new(
        _store,
        new AlertRouter(Options.Create(_options), channels.Select(c => new FakeNotifier(c))),
        Options.Create(_options),
        _time,
        NullLogger<AlertService>.Instance);

    [Fact]
    public async Task Opens_an_alert_and_saves_a_notification_for_the_channel_in_the_same_step()
    {
        await Create(Channels.Slack).RaiseAsync(SampleSignal(), CancellationToken.None);

        var alert = Assert.Single(_store.Open);
        var delivery = Assert.Single(_store.Deliveries);
        Assert.Equal("anomaly:acme:openai-chat:spend", alert.Fingerprint);
        Assert.Equal(AlertEvent.Trigger, delivery.Request.Event);
        Assert.Equal(Channels.Slack, delivery.Request.Channel);
        Assert.Equal("pending", delivery.Status);
        Assert.Equal(alert.Id, delivery.Request.Notification.AlertId);
        Assert.Equal("Spend jumped.", delivery.Request.Notification.Summary);
        Assert.Equal(alert.Fingerprint, delivery.Request.Notification.Fingerprint);
    }

    [Fact]
    public async Task Raising_the_same_incident_again_creates_nothing_more()
    {
        var service = Create(Channels.Slack);

        await service.RaiseAsync(SampleSignal(), CancellationToken.None);
        await service.RaiseAsync(SampleSignal(), CancellationToken.None);
        await service.RaiseAsync(SampleSignal(), CancellationToken.None);

        Assert.Single(_store.Open);
        Assert.Single(_store.Deliveries);
    }

    [Fact]
    public async Task Sends_a_critical_alert_to_every_channel_configured_for_it()
    {
        _options.Routing["Critical"] = [Channels.PagerDuty, Channels.Slack];

        await Create(Channels.PagerDuty, Channels.Slack).RaiseAsync(SampleSignal(severity: AlertSeverity.Critical), CancellationToken.None);

        Assert.Equal([Channels.PagerDuty, Channels.Slack], _store.Deliveries.Select(d => d.Request.Channel));
    }

    [Fact]
    public async Task Escalates_when_the_incident_gets_worse_and_pages_the_channel_for_the_new_severity()
    {
        var service = Create(Channels.PagerDuty, Channels.Slack);
        await service.RaiseAsync(SampleSignal(severity: AlertSeverity.Warning), CancellationToken.None);

        await service.RaiseAsync(SampleSignal(severity: AlertSeverity.Critical), CancellationToken.None);

        Assert.Equal(AlertSeverity.Critical, Assert.Single(_store.Open).Severity);
        var escalation = _store.Deliveries.Last();
        Assert.Equal(AlertEvent.Escalate, escalation.Request.Event);
        Assert.Equal(Channels.PagerDuty, escalation.Request.Channel);
        Assert.Equal(AlertSeverity.Critical, escalation.Request.Notification.Severity);
    }

    [Fact]
    public async Task Does_not_notify_again_when_the_incident_gets_milder()
    {
        var service = Create(Channels.Slack, Channels.PagerDuty);
        await service.RaiseAsync(SampleSignal(severity: AlertSeverity.Critical), CancellationToken.None);
        var before = _store.Deliveries.Count;

        await service.RaiseAsync(SampleSignal(severity: AlertSeverity.Warning), CancellationToken.None);

        Assert.Equal(before, _store.Deliveries.Count);
        Assert.Equal(AlertSeverity.Critical, Assert.Single(_store.Open).Severity);
    }

    [Fact]
    public async Task Sends_an_update_when_there_is_something_new_to_say_such_as_a_diagnosis()
    {
        var service = Create(Channels.Slack);
        await service.RaiseAsync(SampleSignal(revision: "detected", summary: "A diagnosis is on its way."), CancellationToken.None);

        await service.RaiseAsync(SampleSignal(revision: "diagnosed-template", summary: "Spend rose 5x because calls went up."), CancellationToken.None);

        Assert.Equal("Spend rose 5x because calls went up.", Assert.Single(_store.Open).Summary);
        Assert.Equal([AlertEvent.Trigger, AlertEvent.Update], _store.Deliveries.Select(d => d.Request.Event));
        Assert.Equal("Spend rose 5x because calls went up.", _store.Deliveries.Last().Request.Notification.Summary);
    }

    [Fact]
    public async Task Resolving_tells_the_channels_and_closes_the_alert()
    {
        var service = Create(Channels.Slack);
        await service.RaiseAsync(SampleSignal(), CancellationToken.None);

        await service.ResolveAsync("anomaly:acme:openai-chat:spend", Now.AddMinutes(20), "It ended after 20 minutes.", CancellationToken.None);

        Assert.Empty(_store.Open);
        Assert.Equal(Now.AddMinutes(20), _store.Resolved["anomaly:acme:openai-chat:spend"]);
        var resolve = _store.Deliveries.Last();
        Assert.Equal(AlertEvent.Resolve, resolve.Request.Event);
        Assert.Equal("It ended after 20 minutes.", resolve.Request.Notification.Note);
        Assert.Equal("resolved", resolve.Request.Revision);
    }

    [Fact]
    public async Task Resolving_something_that_is_not_open_does_nothing()
    {
        await Create(Channels.Slack).ResolveAsync("anomaly:nobody", Now, null, CancellationToken.None);

        Assert.Empty(_store.Deliveries);
    }

    [Fact]
    public async Task Resolves_through_the_channels_the_alert_had_reached_not_the_ones_it_started_on()
    {
        var service = Create(Channels.PagerDuty, Channels.Slack);
        await service.RaiseAsync(SampleSignal(severity: AlertSeverity.Warning), CancellationToken.None);
        await service.RaiseAsync(SampleSignal(severity: AlertSeverity.Critical), CancellationToken.None);

        await service.ResolveAsync("anomaly:acme:openai-chat:spend", Now, null, CancellationToken.None);

        // It was escalated to critical, so the page is closed on PagerDuty.
        Assert.Equal(Channels.PagerDuty, _store.Deliveries.Last().Request.Channel);
        Assert.Equal(AlertEvent.Resolve, _store.Deliveries.Last().Request.Event);
    }

    [Fact]
    public async Task A_resolved_incident_cannot_raise_a_new_alert_until_the_cooldown_has_passed()
    {
        var service = Create(Channels.Slack);
        await service.RaiseAsync(SampleSignal(), CancellationToken.None);
        await service.ResolveAsync("anomaly:acme:openai-chat:spend", Now, null, CancellationToken.None);

        await service.RaiseAsync(SampleSignal(), CancellationToken.None);
        Assert.Empty(_store.Open);

        _time.Advance(TimeSpan.FromMinutes(16));
        await service.RaiseAsync(SampleSignal(), CancellationToken.None);
        Assert.Single(_store.Open);
    }

    [Fact]
    public async Task Finds_the_other_instances_alert_when_it_loses_a_race_to_open_one()
    {
        _store.RaceWith = new AlertRecord(
            Guid.NewGuid(), "anomaly:acme:openai-chat:spend", AlertKind.Anomaly, AlertSeverity.Warning, "t", "s", new Dictionary<string, string>(), "detected", Now, Now);

        await Create(Channels.Slack).RaiseAsync(SampleSignal(), CancellationToken.None);

        // The winner's alert stands, and this instance added nothing of its own.
        Assert.Single(_store.Open);
        Assert.Empty(_store.Deliveries);
    }

    [Fact]
    public async Task Still_saves_the_alert_when_there_is_no_channel_so_it_is_not_lost_from_the_record()
    {
        await Create().RaiseAsync(SampleSignal(), CancellationToken.None);

        Assert.Single(_store.Open);
        Assert.Empty(_store.Deliveries);
    }
}

public class AlertDispatcherTests
{
    private readonly FakeAlertStore _store = new(Now);
    private readonly AlertOptions _options = new() { NotifyTimeout = TimeSpan.FromMilliseconds(300) };
    private readonly FakeNotifier _slack = new(Channels.Slack);

    private AlertDispatcher Create(params INotifier[] notifiers) =>
        new(_store, notifiers, Options.Create(_options), NullLogger<AlertDispatcher>.Instance);

    private async Task<StoredDelivery> QueueAsync(string channel = Channels.Slack)
    {
        var service = new AlertService(
            _store,
            new AlertRouter(Options.Create(_options), [new FakeNotifier(channel)]),
            Options.Create(_options),
            new FakeTimeProvider(Now),
            NullLogger<AlertService>.Instance);

        await service.RaiseAsync(SampleSignal(), CancellationToken.None);
        return _store.Deliveries.Single();
    }

    [Fact]
    public async Task Sends_what_is_due_and_marks_it_sent_only_after_the_channel_accepted_it()
    {
        var delivery = await QueueAsync();

        var taken = await Create(_slack).DispatchAsync(Now, CancellationToken.None);

        Assert.Equal(1, taken);
        Assert.Equal("sent", delivery.Status);
        Assert.Equal(Now, delivery.SentAt);
        Assert.Equal(delivery.Request.Notification, Assert.Single(_slack.Received));
    }

    [Fact]
    public async Task Does_not_send_what_is_not_due_yet()
    {
        var delivery = await QueueAsync();
        delivery.NextAttemptAt = Now.AddMinutes(1);

        Assert.Equal(0, await Create(_slack).DispatchAsync(Now, CancellationToken.None));
        Assert.Empty(_slack.Received);
    }

    [Fact]
    public async Task A_channel_that_is_down_delays_the_notification_instead_of_losing_it()
    {
        var delivery = await QueueAsync();
        _slack.Outcomes.Enqueue(new RetryLater("Slack answered 503"));

        await Create(_slack).DispatchAsync(Now, CancellationToken.None);

        Assert.Equal("pending", delivery.Status);
        Assert.Equal(Now.AddSeconds(5), delivery.NextAttemptAt);
        Assert.Equal("Slack answered 503", delivery.Error);
    }

    [Fact]
    public async Task Keeps_retrying_with_longer_pauses_until_the_channel_accepts()
    {
        var delivery = await QueueAsync();
        var dispatcher = Create(_slack);
        var now = Now;

        for (var failure = 0; failure < 4; failure++)
        {
            _slack.Outcomes.Enqueue(new RetryLater("down"));
            await dispatcher.DispatchAsync(now, CancellationToken.None);
            Assert.Equal("pending", delivery.Status);
            now = delivery.NextAttemptAt;
        }

        // Pauses so far: 5, 10, 20 and 40 seconds. The fifth try works.
        Assert.Equal(Now.AddSeconds(75), now);
        await dispatcher.DispatchAsync(now, CancellationToken.None);
        Assert.Equal("sent", delivery.Status);
        Assert.Equal(5, delivery.Attempts);
    }

    [Fact]
    public async Task Honours_a_channels_own_advice_to_wait_longer_than_the_usual_pause()
    {
        var delivery = await QueueAsync();
        _slack.Outcomes.Enqueue(new RetryLater("rate limited", TimeSpan.FromSeconds(90)));

        await Create(_slack).DispatchAsync(Now, CancellationToken.None);

        Assert.Equal(Now.AddSeconds(90), delivery.NextAttemptAt);
    }

    [Fact]
    public async Task Does_not_let_a_channel_ask_for_a_shorter_pause_than_the_backoff()
    {
        var delivery = await QueueAsync();
        _slack.Outcomes.Enqueue(new RetryLater("rate limited", TimeSpan.FromMilliseconds(1)));

        await Create(_slack).DispatchAsync(Now, CancellationToken.None);

        Assert.Equal(Now.AddSeconds(5), delivery.NextAttemptAt);
    }

    [Fact]
    public async Task Gives_up_on_a_notification_the_channel_will_never_accept_and_keeps_the_reason()
    {
        var delivery = await QueueAsync();
        _slack.Outcomes.Enqueue(new Rejected("invalid routing key"));

        await Create(_slack).DispatchAsync(Now, CancellationToken.None);

        Assert.Equal("failed", delivery.Status);
        Assert.Equal("invalid routing key", delivery.Error);
        Assert.Equal(0, await Create(_slack).DispatchAsync(Now.AddHours(1), CancellationToken.None));
    }

    [Fact]
    public async Task Treats_an_exception_from_a_channel_as_a_reason_to_retry()
    {
        var delivery = await QueueAsync();
        _slack.Failure = new HttpRequestException("connection refused");

        await Create(_slack).DispatchAsync(Now, CancellationToken.None);

        Assert.Equal("pending", delivery.Status);
        Assert.Contains("HttpRequestException", delivery.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Treats_a_channel_that_does_not_answer_in_time_as_a_reason_to_retry()
    {
        var delivery = await QueueAsync();
        _slack.Hang = true;

        await Create(_slack).DispatchAsync(Now, CancellationToken.None);

        Assert.Equal("pending", delivery.Status);
        Assert.Contains("did not answer", delivery.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lets_a_cancellation_from_the_caller_through()
    {
        await QueueAsync();
        _slack.Hang = true;
        using var cancellation = new CancellationTokenSource();
        var pending = Create(_slack).DispatchAsync(Now, cancellation.Token).AsTask();

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Gives_up_on_a_notification_for_a_channel_that_is_not_configured()
    {
        var delivery = await QueueAsync(channel: Channels.PagerDuty);

        await Create(_slack).DispatchAsync(Now, CancellationToken.None);

        Assert.Equal("failed", delivery.Status);
        Assert.Contains("not configured", delivery.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_notification_that_is_being_sent_is_not_sent_again_by_another_round_but_comes_due_again_if_the_sender_dies()
    {
        var delivery = await QueueAsync();
        var first = await _store.ClaimDueAsync(Now, 10, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.Single(first);

        // Another round while the first is still holding it finds nothing.
        Assert.Empty(await _store.ClaimDueAsync(Now.AddMinutes(1), 10, TimeSpan.FromMinutes(2), CancellationToken.None));

        // The sender died without reporting, so after the hold it is taken again.
        Assert.Single(await _store.ClaimDueAsync(Now.AddMinutes(3), 10, TimeSpan.FromMinutes(2), CancellationToken.None));
        Assert.Equal(2, delivery.Attempts);
    }

    [Fact]
    public async Task Sends_at_most_one_batch_at_a_time()
    {
        _options.BatchSize = 2;
        var service = new AlertService(_store, new AlertRouter(Options.Create(_options), [_slack]), Options.Create(_options), new FakeTimeProvider(Now), NullLogger<AlertService>.Instance);
        for (var i = 0; i < 5; i++)
        {
            await service.RaiseAsync(SampleSignal(fingerprint: $"anomaly:t{i}:openai-chat:spend"), CancellationToken.None);
        }

        Assert.Equal(2, await Create(_slack).DispatchAsync(Now, CancellationToken.None));
        Assert.Equal(2, await Create(_slack).DispatchAsync(Now, CancellationToken.None));
        Assert.Equal(1, await Create(_slack).DispatchAsync(Now, CancellationToken.None));
    }

    [Fact]
    public async Task Reports_when_a_round_last_completed_so_a_heartbeat_can_tell_it_is_alive()
    {
        var dispatcher = Create(_slack);
        Assert.Null(dispatcher.LastRoundAt);

        await dispatcher.DispatchAsync(Now, CancellationToken.None);

        Assert.Equal(Now, dispatcher.LastRoundAt);
    }
}

public class AlertSignalsTests
{
    private readonly AlertOptions _options = new();

    [Fact]
    public void An_anomaly_alert_waits_for_its_diagnosis_for_a_short_while()
    {
        var young = SampleAnomaly(openedAt: Now.AddSeconds(-30));
        var waited = SampleAnomaly(openedAt: Now.AddSeconds(-50));

        Assert.Null(AlertSignals.ForAnomaly(young, _options, Now));
        Assert.NotNull(AlertSignals.ForAnomaly(waited, _options, Now));
    }

    [Fact]
    public void An_anomaly_that_has_its_diagnosis_does_not_wait()
    {
        var signal = AlertSignals.ForAnomaly(SampleAnomaly(openedAt: Now.AddSeconds(-5), diagnosis: SampleDiagnosis()), _options, Now);

        Assert.NotNull(signal);
    }

    [Fact]
    public void Carries_the_diagnosis_as_the_summary_and_the_next_step_as_a_detail()
    {
        var signal = AlertSignals.ForAnomaly(SampleAnomaly(diagnosis: SampleDiagnosis()), _options, Now)!;

        Assert.Equal("Spend rose 5x because calls per minute went up.", signal.Summary);
        Assert.Equal("Look for a retry loop.", signal.Details["Suggested next step"]);
        Assert.Contains("Worked out from the numbers", signal.Details["Explanation"], StringComparison.Ordinal);
        Assert.Equal("diagnosed-template", signal.Revision);
    }

    [Fact]
    public void Labels_an_llm_explanation_as_ai_suggested()
    {
        var signal = AlertSignals.ForAnomaly(SampleAnomaly(diagnosis: SampleDiagnosis(NarrationSource.Llm)), _options, Now)!;

        Assert.StartsWith("AI-suggested", signal.Details["Explanation"], StringComparison.Ordinal);
        Assert.Equal("diagnosed-llm", signal.Revision);
    }

    [Fact]
    public void Without_a_diagnosis_says_what_happened_and_that_one_is_coming()
    {
        var signal = AlertSignals.ForAnomaly(SampleAnomaly(), _options, Now)!;

        Assert.Contains("Spend per minute reached $1.00", signal.Summary, StringComparison.Ordinal);
        Assert.Contains("baseline of $0.20", signal.Summary, StringComparison.Ordinal);
        Assert.Contains("diagnosis is on its way", signal.Summary, StringComparison.Ordinal);
        Assert.Equal("detected", signal.Revision);
        Assert.DoesNotContain("Suggested next step", signal.Details.Keys);
    }

    [Theory]
    [InlineData(5, AlertSeverity.Warning)]
    [InlineData(9.9, AlertSeverity.Warning)]
    [InlineData(10, AlertSeverity.Critical)]
    [InlineData(40, AlertSeverity.Critical)]
    public void Becomes_critical_only_when_the_signal_is_far_above_its_baseline(double ratio, AlertSeverity expected)
    {
        Assert.Equal(expected, AlertSignals.ForAnomaly(SampleAnomaly(peakRatio: ratio), _options, Now)!.Severity);
    }

    [Fact]
    public void Names_the_incident_the_same_way_every_time_so_it_can_be_deduplicated()
    {
        var first = AlertSignals.ForAnomaly(SampleAnomaly(peakRatio: 5), _options, Now)!;
        var later = AlertSignals.ForAnomaly(SampleAnomaly(peakRatio: 12, diagnosis: SampleDiagnosis()), _options, Now)!;

        Assert.Equal("anomaly:acme:openai-chat:spend", first.Fingerprint);
        Assert.Equal(first.Fingerprint, later.Fingerprint);
    }

    [Fact]
    public void Gives_different_incidents_different_fingerprints()
    {
        Assert.NotEqual(
            AlertSignals.AnomalyFingerprint(SampleAnomaly().Series, Signal.Spend),
            AlertSignals.AnomalyFingerprint(SampleAnomaly().Series, Signal.Volume));
        Assert.NotEqual(
            AlertSignals.AnomalyFingerprint(SampleAnomaly(tenant: "acme").Series, Signal.Spend),
            AlertSignals.AnomalyFingerprint(SampleAnomaly(tenant: "globex").Series, Signal.Spend));
    }

    [Fact]
    public void Says_how_long_an_ended_anomaly_lasted()
    {
        var note = AlertSignals.AnomalyEndedNote(SampleAnomaly(isOpen: false, openedAt: Now.AddMinutes(-25), closedAt: Now));

        Assert.Equal("The anomaly ended after 25 minutes. It peaked at $1.00.", note);
    }

    [Fact]
    public void A_budget_alert_says_how_many_calls_were_blocked_and_for_whom()
    {
        var signal = AlertSignals.ForBudget(new BudgetBlock(TenantId.Parse("acme"), 1_234, Now), _options, Now);

        Assert.Equal("budget:acme", signal.Fingerprint);
        Assert.Equal(AlertKind.BudgetExhausted, signal.Kind);
        Assert.Equal(AlertSeverity.Critical, signal.Severity);
        Assert.Contains("1,234 calls from acme were blocked in the last 5 minutes", signal.Summary, StringComparison.Ordinal);
        Assert.Equal("1,234", signal.Details["Calls blocked"]);
    }

    [Fact]
    public void The_severity_of_a_budget_alert_can_be_changed()
    {
        _options.BudgetSeverity = AlertSeverity.Warning;

        Assert.Equal(AlertSeverity.Warning, AlertSignals.ForBudget(new BudgetBlock(TenantId.Parse("acme"), 1, Now), _options, Now).Severity);
    }

    [Fact]
    public void An_enforcement_outage_is_critical_and_explains_what_it_means()
    {
        var degraded = AlertSignals.ForDegradedEnforcement(Now, "connection refused");
        var failClosed = AlertSignals.ForFailClosedEnforcement(Now.AddMinutes(-5), Now);

        Assert.Equal(AlertSeverity.Critical, degraded.Severity);
        Assert.Equal(AlertSeverity.Critical, failClosed.Severity);
        Assert.Contains("no longer verified", degraded.Summary, StringComparison.Ordinal);
        Assert.Equal("connection refused", degraded.Details["Reason"]);
        Assert.Contains("rejecting", failClosed.Title, StringComparison.Ordinal);
        Assert.Contains("5 minutes", failClosed.Summary, StringComparison.Ordinal);
        Assert.NotEqual(degraded.Fingerprint, failClosed.Fingerprint);
    }

    [Fact]
    public void When_the_store_is_back_says_what_was_spent_without_verification()
    {
        var summary = new OutageSummary(
            Now.AddMinutes(-7),
            Now,
            1_240,
            new Dictionary<TenantId, Money>
            {
                [TenantId.Parse("acme")] = Money.FromDollars(2.5m),
                [TenantId.Parse("globex")] = Money.FromDollars(0.75m),
            });

        var note = AlertSignals.OutageEndedNote(summary);

        Assert.Equal(
            "The budget store was unreachable for 7 minutes. 1,240 calls were allowed without verification, about $3.250000 in total (largest: acme $2.500000, globex $0.750000).",
            note);
    }

    [Theory]
    [InlineData(20, "20 seconds")]
    [InlineData(0, "1 seconds")]
    [InlineData(300, "5 minutes")]
    [InlineData(7200, "2 hours")]
    public void Writes_a_duration_the_way_a_person_would_say_it(int seconds, string expected)
    {
        Assert.Equal(expected, AlertSignals.Duration(TimeSpan.FromSeconds(seconds)));
    }
}

public class AlertingOutageListenerTests
{
    private readonly RecordingInbox _inbox = new();

    [Fact]
    public void Raises_a_critical_alert_when_degraded_mode_starts()
    {
        new AlertingOutageListener(_inbox).OutageStarted(Now, "timeout");

        var raise = Assert.IsType<QueuedRaise>(Assert.Single(_inbox.Items));
        Assert.Equal(AlertSignals.EnforcementDegradedFingerprint, raise.Signal.Fingerprint);
        Assert.Equal(AlertSeverity.Critical, raise.Signal.Severity);
    }

    [Fact]
    public void Raises_another_when_the_outage_outlasts_degraded_mode()
    {
        new AlertingOutageListener(_inbox).OutageLimitReached(Now.AddMinutes(-6), Now);

        var raise = Assert.IsType<QueuedRaise>(Assert.Single(_inbox.Items));
        Assert.Equal(AlertSignals.EnforcementFailClosedFingerprint, raise.Signal.Fingerprint);
    }

    [Fact]
    public void Resolves_both_alerts_with_a_note_when_the_store_is_back()
    {
        var summary = new OutageSummary(Now.AddMinutes(-3), Now, 10, new Dictionary<TenantId, Money>());

        new AlertingOutageListener(_inbox).OutageEnded(summary);

        var resolves = _inbox.Items.Cast<QueuedResolve>().ToList();
        Assert.Equal(2, resolves.Count);
        Assert.Contains(resolves, r => r.Fingerprint == AlertSignals.EnforcementDegradedFingerprint);
        Assert.Contains(resolves, r => r.Fingerprint == AlertSignals.EnforcementFailClosedFingerprint);
        Assert.All(resolves, r => Assert.Contains("unreachable for 3 minutes", r.Note, StringComparison.Ordinal));
    }
}

public class AlertSourceRunnerTests
{
    private readonly FakeTimeProvider _time = new(Now);
    private readonly FakeAlertStore _store = new(Now);
    private readonly FakeAlertFeed _feed = new();
    private readonly AlertOptions _options = new();

    private (AlertSourceRunner Runner, AlertService Service) Create()
    {
        var notifiers = new[] { new FakeNotifier(Channels.Slack), new FakeNotifier(Channels.PagerDuty) };
        var service = new AlertService(_store, new AlertRouter(Options.Create(_options), notifiers), Options.Create(_options), _time, NullLogger<AlertService>.Instance);

        return (new AlertSourceRunner(_feed, _store, service, Options.Create(_options), NullLogger<AlertSourceRunner>.Instance), service);
    }

    [Fact]
    public async Task Raises_an_alert_for_an_open_anomaly_once_it_has_waited_for_its_diagnosis()
    {
        _feed.Anomalies.Add(SampleAnomaly(openedAt: Now.AddSeconds(-60)));

        await Create().Runner.RunAsync(Now, CancellationToken.None);

        var alert = Assert.Single(_store.Open);
        Assert.Equal("anomaly:acme:openai-chat:spend", alert.Fingerprint);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
    }

    [Fact]
    public async Task Waits_for_a_young_anomaly_to_be_diagnosed()
    {
        _feed.Anomalies.Add(SampleAnomaly(openedAt: Now.AddSeconds(-10)));

        await Create().Runner.RunAsync(Now, CancellationToken.None);

        Assert.Empty(_store.Open);
    }

    [Fact]
    public async Task Sends_the_diagnosis_as_an_update_when_it_arrives_later()
    {
        var anomaly = SampleAnomaly(openedAt: Now.AddSeconds(-60));
        _feed.Anomalies.Add(anomaly);
        var (runner, _) = Create();
        await runner.RunAsync(Now, CancellationToken.None);

        _feed.Anomalies[0] = anomaly with { Diagnosis = SampleDiagnosis() };
        await runner.RunAsync(Now.AddSeconds(15), CancellationToken.None);

        Assert.Equal([AlertEvent.Trigger, AlertEvent.Update], _store.Deliveries.Select(d => d.Request.Event));
        Assert.Equal("Spend rose 5x because calls per minute went up.", _store.Deliveries.Last().Request.Notification.Summary);
    }

    [Fact]
    public async Task Repeating_a_round_changes_nothing()
    {
        _feed.Anomalies.Add(SampleAnomaly(openedAt: Now.AddSeconds(-60), diagnosis: SampleDiagnosis()));
        var (runner, _) = Create();

        await runner.RunAsync(Now, CancellationToken.None);
        var deliveries = _store.Deliveries.Count;
        await runner.RunAsync(Now.AddSeconds(15), CancellationToken.None);
        await runner.RunAsync(Now.AddSeconds(30), CancellationToken.None);

        Assert.Equal(deliveries, _store.Deliveries.Count);
        Assert.Single(_store.Open);
    }

    [Fact]
    public async Task Escalates_to_a_page_when_the_anomaly_climbs_past_the_critical_ratio()
    {
        var anomaly = SampleAnomaly(openedAt: Now.AddSeconds(-60), peakRatio: 5, diagnosis: SampleDiagnosis());
        _feed.Anomalies.Add(anomaly);
        var (runner, _) = Create();
        await runner.RunAsync(Now, CancellationToken.None);

        _feed.Anomalies[0] = anomaly with { PeakRatio = 14, PeakValue = 2_800_000 };
        await runner.RunAsync(Now.AddSeconds(15), CancellationToken.None);

        Assert.Equal(AlertSeverity.Critical, Assert.Single(_store.Open).Severity);
        var page = _store.Deliveries.Last();
        Assert.Equal(AlertEvent.Escalate, page.Request.Event);
        Assert.Equal(Channels.PagerDuty, page.Request.Channel);
    }

    [Fact]
    public async Task Resolves_the_alert_when_the_anomaly_has_ended_and_says_how_long_it_lasted()
    {
        var anomaly = SampleAnomaly(openedAt: Now.AddMinutes(-20), diagnosis: SampleDiagnosis());
        _feed.Anomalies.Add(anomaly);
        var (runner, _) = Create();
        await runner.RunAsync(Now, CancellationToken.None);

        _feed.Anomalies[0] = anomaly with { IsOpen = false, ClosedAt = Now.AddMinutes(2) };
        await runner.RunAsync(Now.AddMinutes(3), CancellationToken.None);

        Assert.Empty(_store.Open);
        var resolve = _store.Deliveries.Last();
        Assert.Equal(AlertEvent.Resolve, resolve.Request.Event);
        Assert.Contains("ended after 22 minutes", resolve.Request.Notification.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ended_anomaly_that_never_had_an_alert_does_nothing()
    {
        _feed.Anomalies.Add(SampleAnomaly(isOpen: false, openedAt: Now.AddMinutes(-20), closedAt: Now.AddMinutes(-2)));

        await Create().Runner.RunAsync(Now, CancellationToken.None);

        Assert.Empty(_store.Deliveries);
    }

    [Fact]
    public async Task Alerts_when_a_tenants_calls_are_being_blocked_for_budget()
    {
        _feed.Blocks.Add(new BudgetBlock(TenantId.Parse("acme"), 40, Now.AddMinutes(-1)));

        await Create().Runner.RunAsync(Now, CancellationToken.None);

        var alert = Assert.Single(_store.Open);
        Assert.Equal("budget:acme", alert.Fingerprint);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal(Channels.PagerDuty, Assert.Single(_store.Deliveries).Request.Channel);
    }

    [Fact]
    public async Task Resolves_a_budget_alert_once_calls_are_no_longer_blocked()
    {
        _feed.Blocks.Add(new BudgetBlock(TenantId.Parse("acme"), 40, Now));
        var (runner, _) = Create();
        await runner.RunAsync(Now, CancellationToken.None);

        _feed.Blocks.Clear();
        await runner.RunAsync(Now.AddMinutes(6), CancellationToken.None);

        Assert.Empty(_store.Open);
        Assert.Equal(AlertEvent.Resolve, _store.Deliveries.Last().Request.Event);
    }

    [Fact]
    public async Task Keeps_a_budget_alert_open_while_calls_are_still_being_blocked()
    {
        _feed.Blocks.Add(new BudgetBlock(TenantId.Parse("acme"), 40, Now));
        var (runner, _) = Create();

        await runner.RunAsync(Now, CancellationToken.None);
        await runner.RunAsync(Now.AddMinutes(1), CancellationToken.None);

        Assert.Single(_store.Open);
        Assert.Single(_store.Deliveries);
    }

    [Fact]
    public async Task Leaves_budgets_alone_when_budget_alerts_are_switched_off()
    {
        _options.BudgetAlerts = false;
        _feed.Blocks.Add(new BudgetBlock(TenantId.Parse("acme"), 40, Now));

        await Create().Runner.RunAsync(Now, CancellationToken.None);

        Assert.Empty(_store.Open);
    }

    [Fact]
    public async Task Does_nothing_when_another_instance_is_already_looking()
    {
        _feed.Anomalies.Add(SampleAnomaly(openedAt: Now.AddMinutes(-5)));
        _feed.LockAvailable = false;

        await Create().Runner.RunAsync(Now, CancellationToken.None);

        Assert.Empty(_store.Open);
    }
}
