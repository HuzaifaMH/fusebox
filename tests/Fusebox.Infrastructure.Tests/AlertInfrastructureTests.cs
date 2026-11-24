using Fusebox.Application.Alerting;
using Fusebox.Infrastructure.Alerting;
using Fusebox.Infrastructure.Alerting.Postgres;
using Fusebox.Infrastructure.Catalog;
using Fusebox.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Fusebox.Infrastructure.Tests;

/// <summary>The alert store on a real PostgreSQL: dedupe, claiming, retries and the lock that keeps one source running.</summary>
[Collection(SharedPostgres.Name)]
public class PostgresAlertStoreTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-03-04T12:00:00Z");

    private readonly List<IAsyncDisposable> _disposables = [];

    public async Task InitializeAsync()
    {
        if (postgres.ConnectionString is not null)
        {
            await using var connection = new NpgsqlConnection(postgres.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("DROP TABLE IF EXISTS alert_deliveries, alerts CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }
    }

    private PostgresAlertStore CreateStore()
    {
        var store = new PostgresAlertStore(Options.Create(new TelemetryOptions
        {
            Store = TelemetryStoreKind.Postgres,
            Postgres = new PostgresOptions { ConnectionString = postgres.ConnectionString! },
        }));
        _disposables.Add(store);
        return store;
    }

    private static AlertRecord Record(string fingerprint = "anomaly:acme:openai-chat:spend", AlertSeverity severity = AlertSeverity.Warning, string revision = "detected") =>
        new(
            Guid.NewGuid(),
            fingerprint,
            AlertKind.Anomaly,
            severity,
            "Spend anomaly",
            "Spend jumped.",
            new Dictionary<string, string> { ["Tenant"] = "acme" },
            revision,
            T0,
            T0);

    private static DeliveryRequest Delivery(AlertRecord alert, string channel = Channels.Slack, AlertEvent kind = AlertEvent.Trigger, string? revision = null) =>
        new(
            channel,
            revision ?? alert.Revision,
            new AlertNotification(kind, alert.Id, alert.Fingerprint, alert.Kind, alert.Severity, alert.Title, alert.Summary, alert.Details, alert.OpenedAt, alert.UpdatedAt));

    [SkippableFact]
    public async Task An_alert_is_saved_with_its_notifications_and_found_again()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        var alert = Record();

        Assert.True(await store.TryOpenAsync(alert, [Delivery(alert)], CancellationToken.None));

        var found = await store.FindOpenAsync(alert.Fingerprint, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(alert.Id, found.Id);
        Assert.Equal("acme", found.Details["Tenant"]);
        Assert.Equal(AlertSeverity.Warning, found.Severity);
        Assert.Single(await store.FindOpenByKindAsync(AlertKind.Anomaly, CancellationToken.None));
    }

    [SkippableFact]
    public async Task An_alert_about_a_failing_vendor_is_accepted_by_the_database_and_read_back()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        var alert = Record("upstream:openai") with { Kind = AlertKind.UpstreamUnavailable, Severity = AlertSeverity.Critical };

        Assert.True(await store.TryOpenAsync(alert, [Delivery(alert)], CancellationToken.None));

        var found = await store.FindOpenAsync("upstream:openai", CancellationToken.None);
        Assert.Equal(AlertKind.UpstreamUnavailable, found!.Kind);
        Assert.Single(await store.FindOpenByKindAsync(AlertKind.UpstreamUnavailable, CancellationToken.None));
    }

    [SkippableFact]
    public async Task A_second_alert_with_the_same_fingerprint_loses_and_leaves_no_notifications()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        var first = Record();
        var second = Record();

        Assert.True(await store.TryOpenAsync(first, [Delivery(first)], CancellationToken.None));
        Assert.False(await store.TryOpenAsync(second, [Delivery(second)], CancellationToken.None));

        var claimed = await store.ClaimDueAsync(T0, 10, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.Single(claimed);
        Assert.Equal(first.Id, claimed[0].Notification.AlertId);
    }

    [SkippableFact]
    public async Task Resolving_closes_the_alert_and_remembers_when()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        var alert = Record();
        await store.TryOpenAsync(alert, [], CancellationToken.None);

        await store.ResolveAsync(alert.Id, T0.AddMinutes(10), [Delivery(alert, kind: AlertEvent.Resolve, revision: "resolved")], CancellationToken.None);

        Assert.Null(await store.FindOpenAsync(alert.Fingerprint, CancellationToken.None));
        Assert.Equal(T0.AddMinutes(10), await store.FindLastResolvedAsync(alert.Fingerprint, CancellationToken.None));

        // The same incident can be opened again once the old one is closed.
        var again = Record();
        Assert.True(await store.TryOpenAsync(again, [], CancellationToken.None));
    }

    [SkippableFact]
    public async Task An_update_changes_the_alert_and_queues_a_notification_once_per_revision()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        var alert = Record();
        await store.TryOpenAsync(alert, [Delivery(alert)], CancellationToken.None);

        var updated = alert with { Severity = AlertSeverity.Critical, Revision = "diagnosed", UpdatedAt = T0.AddMinutes(1) };
        var escalation = Delivery(updated, Channels.PagerDuty, AlertEvent.Escalate);
        await store.UpdateAsync(updated, [escalation], CancellationToken.None);
        await store.UpdateAsync(updated, [escalation], CancellationToken.None);

        var found = await store.FindOpenAsync(alert.Fingerprint, CancellationToken.None);
        Assert.Equal(AlertSeverity.Critical, found!.Severity);
        Assert.Equal("diagnosed", found.Revision);

        var claimed = await store.ClaimDueAsync(T0.AddMinutes(2), 10, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.Equal(2, claimed.Count);
    }

    [SkippableFact]
    public async Task A_claimed_notification_is_held_and_comes_back_if_nobody_confirms_it()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        var alert = Record();
        await store.TryOpenAsync(alert, [Delivery(alert)], CancellationToken.None);

        var first = await store.ClaimDueAsync(T0, 10, TimeSpan.FromMinutes(2), CancellationToken.None);
        var concurrent = await store.ClaimDueAsync(T0.AddSeconds(30), 10, TimeSpan.FromMinutes(2), CancellationToken.None);
        var afterHold = await store.ClaimDueAsync(T0.AddMinutes(3), 10, TimeSpan.FromMinutes(2), CancellationToken.None);

        Assert.Single(first);
        Assert.Equal(1, first[0].Attempts);
        Assert.Empty(concurrent);
        Assert.Single(afterHold);
        Assert.Equal(2, afterHold[0].Attempts);
    }

    [SkippableFact]
    public async Task Two_dispatchers_never_claim_the_same_notification()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var storeA = CreateStore();
        var storeB = CreateStore();

        for (var i = 0; i < 20; i++)
        {
            var alert = Record($"anomaly:t{i}:r:spend");
            await storeA.TryOpenAsync(alert, [Delivery(alert)], CancellationToken.None);
        }

        var results = await Task.WhenAll(
            Task.Run(async () => await storeA.ClaimDueAsync(T0, 20, TimeSpan.FromMinutes(2), CancellationToken.None)),
            Task.Run(async () => await storeB.ClaimDueAsync(T0, 20, TimeSpan.FromMinutes(2), CancellationToken.None)));

        var ids = results.SelectMany(r => r.Select(d => d.Id)).ToList();
        Assert.Equal(20, ids.Count);
        Assert.Equal(20, ids.Distinct().Count());
    }

    [SkippableFact]
    public async Task Sent_retried_and_failed_notifications_leave_the_queue_correctly()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        var alert = Record();
        await store.TryOpenAsync(alert, [Delivery(alert, Channels.Slack), Delivery(alert, Channels.PagerDuty), Delivery(alert, Channels.Webhook)], CancellationToken.None);

        var claimed = await store.ClaimDueAsync(T0, 10, TimeSpan.FromMinutes(2), CancellationToken.None);
        var slack = claimed.Single(d => d.Channel == Channels.Slack);
        var pagerDuty = claimed.Single(d => d.Channel == Channels.PagerDuty);
        var webhook = claimed.Single(d => d.Channel == Channels.Webhook);

        await store.MarkSentAsync(slack.Id, T0, CancellationToken.None);
        await store.ScheduleRetryAsync(pagerDuty.Id, T0.AddSeconds(30), "busy", CancellationToken.None);
        await store.MarkFailedAsync(webhook.Id, "refused", CancellationToken.None);

        Assert.Empty(await store.ClaimDueAsync(T0.AddSeconds(10), 10, TimeSpan.FromMinutes(2), CancellationToken.None));

        var later = await store.ClaimDueAsync(T0.AddSeconds(31), 10, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.Equal(Channels.PagerDuty, Assert.Single(later).Channel);
    }

    [SkippableFact]
    public async Task Only_one_instance_runs_the_alert_sources_at_a_time()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var holder = CreateStore();
        var other = CreateStore();
        var otherRan = false;
        var gate = new TaskCompletionSource();

        var holding = holder.TryRunExclusivelyAsync(async _ => await gate.Task, CancellationToken.None).AsTask();
        await Task.Delay(300);

        var ran = await other.TryRunExclusivelyAsync(_ =>
        {
            otherRan = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        gate.SetResult();
        Assert.True(await holding);
        Assert.False(ran);
        Assert.False(otherRan);

        Assert.True(await other.TryRunExclusivelyAsync(_ => Task.CompletedTask, CancellationToken.None));
    }
}

public class AlertOptionsValidatorTests
{
    private static ValidateOptionsResult Validate(Action<AlertOptions> configure, TelemetryStoreKind store = TelemetryStoreKind.Postgres)
    {
        var options = new AlertOptions { Enabled = true, Slack = new SlackOptions { WebhookUrl = "https://slack.test/h" } };
        configure(options);
        var telemetry = Options.Create(new TelemetryOptions { Store = store });
        return new AlertOptionsValidator(telemetry).Validate(null, options);
    }

    [Fact]
    public void A_disabled_alerting_section_is_always_valid() =>
        Assert.True(new AlertOptionsValidator(Options.Create(new TelemetryOptions())).Validate(null, new AlertOptions { Enabled = false }).Succeeded);

    [Fact]
    public void A_sensible_setup_is_valid() => Assert.True(Validate(_ => { }).Succeeded);

    [Fact]
    public void Alerting_needs_the_database_that_keeps_alerts() =>
        Assert.True(Validate(_ => { }, TelemetryStoreKind.None).Failed);

    [Fact]
    public void Alerting_needs_somewhere_to_send_alerts() =>
        Assert.True(Validate(o => o.Slack.WebhookUrl = string.Empty).Failed);

    [Fact]
    public void A_channel_address_must_be_a_web_address() =>
        Assert.True(Validate(o => o.Slack.WebhookUrl = "not a url").Failed);

    [Fact]
    public void Routing_to_an_unknown_channel_is_refused() =>
        Assert.True(Validate(o => o.Routing["Critical"] = ["carrier-pigeon"]).Failed);

    [Fact]
    public void Routing_for_an_unknown_severity_is_refused() =>
        Assert.True(Validate(o => o.Routing["Catastrophic"] = ["slack"]).Failed);

    [Fact]
    public void A_claim_shorter_than_the_send_timeout_is_refused() =>
        Assert.True(Validate(o =>
        {
            o.ClaimDuration = TimeSpan.FromSeconds(5);
            o.NotifyTimeout = TimeSpan.FromSeconds(15);
        }).Failed);

    [Fact]
    public void A_heartbeat_that_is_too_frequent_is_refused() =>
        Assert.True(Validate(o => o.Heartbeat = new HeartbeatOptions { Url = "https://hc.test/ping", Interval = TimeSpan.FromSeconds(1) }).Failed);
}

public class AlertJobTests
{
    [Fact]
    public async Task The_heartbeat_is_sent_only_while_alert_delivery_is_alive()
    {
        var time = new FakeTimeProvider(AlertTimes.Start);
        var handler = new StubHandler((_, _) => NotifierSamples.Reply(System.Net.HttpStatusCode.OK));
        using var client = new HttpClient(handler);
        var options = NotifierSamples.Options(o => o.Heartbeat = new HeartbeatOptions { Url = "https://hc.test/ping", Interval = TimeSpan.FromMinutes(1) });
        var dispatcher = AlertTimes.Dispatcher(time, options);
        var sender = new HeartbeatSender(client, dispatcher, options, NullLogger<HeartbeatSender>.Instance);

        // No round has completed yet: Fusebox is not known to be alive.
        Assert.False(await sender.PingAsync(time.GetUtcNow(), CancellationToken.None));
        Assert.Empty(handler.Calls);

        await dispatcher.DispatchAsync(time.GetUtcNow(), CancellationToken.None);
        Assert.True(await sender.PingAsync(time.GetUtcNow(), CancellationToken.None));
        Assert.Single(handler.Calls);

        // Delivery stops completing rounds; the pings stop with it.
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.False(await sender.PingAsync(time.GetUtcNow(), CancellationToken.None));
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task A_monitor_that_refuses_the_ping_is_reported_not_thrown()
    {
        var time = new FakeTimeProvider(AlertTimes.Start);
        using var client = new HttpClient(new StubHandler((_, _) => throw new HttpRequestException("down")));
        var options = NotifierSamples.Options(o => o.Heartbeat = new HeartbeatOptions { Url = "https://hc.test/ping" });
        var dispatcher = AlertTimes.Dispatcher(time, options);
        await dispatcher.DispatchAsync(time.GetUtcNow(), CancellationToken.None);

        Assert.False(await new HeartbeatSender(client, dispatcher, options, NullLogger<HeartbeatSender>.Instance).PingAsync(time.GetUtcNow(), CancellationToken.None));
    }

    [Fact]
    public async Task A_failing_round_does_not_stop_the_source_job()
    {
        var time = new FakeTimeProvider(AlertTimes.Start);
        var options = NotifierSamples.Options();
        var feed = new ThrowingFeed();
        var service = AlertTimes.Service(time, options);
        var runner = new AlertSourceRunner(feed, new FlakyStore(0), service, options, NullLogger<AlertSourceRunner>.Instance);
        var job = new AlertSourceJob(runner, options, time, NullLogger<AlertSourceJob>.Instance);

        await job.TickAsync(CancellationToken.None);
        await job.TickAsync(CancellationToken.None);

        Assert.Equal(2, feed.Calls);
    }

    [Fact]
    public async Task A_queued_alert_is_saved_after_the_database_comes_back()
    {
        var time = new FakeTimeProvider(AlertTimes.Start);
        var options = NotifierSamples.Options();
        var store = new FlakyStore(failures: 2);
        var service = AlertTimes.Service(time, options, store);
        var writer = new AlertInboxWriter(new AlertInbox(options), service, options, time, NullLogger<AlertInboxWriter>.Instance);

        var save = writer.SaveAsync(new QueuedRaise(AlertTimes.Signal()), CancellationToken.None);
        while (!save.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(5));
            await Task.Yield();
        }

        await save;
        Assert.Equal(3, store.OpenAttempts);
        Assert.Single(store.Opened);
    }

    [Fact]
    public void The_inbox_takes_nothing_when_alerting_is_off()
    {
        var inbox = new AlertInbox(Options.Create(new AlertOptions { Enabled = false }));

        Assert.False(inbox.TryPost(new QueuedRaise(AlertTimes.Signal())));
    }
}

internal static class AlertTimes
{
    public static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-03-04T12:00:00Z");

    public static AlertSignal Signal() =>
        new("anomaly:acme:openai-chat:spend", AlertKind.Anomaly, AlertSeverity.Warning, "Spend anomaly", "Spend jumped.", new Dictionary<string, string>(), "detected", Start);

    public static AlertService Service(TimeProvider time, IOptions<AlertOptions> options, IAlertStore? store = null)
    {
        var router = new AlertRouter(options, [new NoopNotifier(Channels.Slack)]);
        return new AlertService(store ?? new FlakyStore(0), router, options, time, NullLogger<AlertService>.Instance);
    }

    public static AlertDispatcher Dispatcher(TimeProvider time, IOptions<AlertOptions> options) =>
        new(new FlakyStore(0), [new NoopNotifier(Channels.Slack)], options, NullLogger<AlertDispatcher>.Instance);
}

internal sealed class NoopNotifier(string channel) : INotifier
{
    public string Channel { get; } = channel;

    public ValueTask<NotifyOutcome> NotifyAsync(AlertNotification notification, CancellationToken cancellationToken) => ValueTask.FromResult<NotifyOutcome>(new Delivered());
}

internal sealed class ThrowingFeed : IAlertFeed
{
    public int Calls { get; private set; }

    public ValueTask<bool> TryRunExclusivelyAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        Calls++;
        throw new InvalidOperationException("database is down");
    }

    public ValueTask<IReadOnlyList<AnomalyAlertState>> LoadAnomaliesAsync(DateTimeOffset since, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<IReadOnlyList<BudgetBlock>> LoadBudgetBlocksAsync(DateTimeOffset since, CancellationToken cancellationToken) => throw new NotSupportedException();
}

/// <summary>An alert store that fails the first few times it is asked to open an alert.</summary>
internal sealed class FlakyStore(int failures) : IAlertStore
{
    private int _remaining = failures;

    public int OpenAttempts { get; private set; }

    public List<AlertRecord> Opened { get; } = [];

    public ValueTask<AlertRecord?> FindOpenAsync(string fingerprint, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Opened.FirstOrDefault(a => a.Fingerprint == fingerprint));

    public ValueTask<IReadOnlyList<AlertRecord>> FindOpenByKindAsync(AlertKind kind, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<AlertRecord>>([.. Opened.Where(a => a.Kind == kind)]);

    public ValueTask<DateTimeOffset?> FindLastResolvedAsync(string fingerprint, CancellationToken cancellationToken) => ValueTask.FromResult<DateTimeOffset?>(null);

    public ValueTask<bool> TryOpenAsync(AlertRecord alert, IReadOnlyList<DeliveryRequest> deliveries, CancellationToken cancellationToken)
    {
        OpenAttempts++;
        if (_remaining > 0)
        {
            _remaining--;
            throw new InvalidOperationException("database is down");
        }

        Opened.Add(alert);
        return ValueTask.FromResult(true);
    }

    public ValueTask UpdateAsync(AlertRecord alert, IReadOnlyList<DeliveryRequest> deliveries, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask ResolveAsync(Guid alertId, DateTimeOffset at, IReadOnlyList<DeliveryRequest> deliveries, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<IReadOnlyList<PendingDelivery>> ClaimDueAsync(DateTimeOffset now, int limit, TimeSpan claimFor, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<PendingDelivery>>([]);

    public ValueTask MarkSentAsync(Guid deliveryId, DateTimeOffset at, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask ScheduleRetryAsync(Guid deliveryId, DateTimeOffset nextAttemptAt, string reason, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask MarkFailedAsync(Guid deliveryId, string reason, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
