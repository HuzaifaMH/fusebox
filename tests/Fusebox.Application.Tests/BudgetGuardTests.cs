using Fusebox.Application.Budgeting;
using Fusebox.Application.Llm;
using Fusebox.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Fusebox.Application.Tests;

public class BudgetGuardTests
{
    private const string Model = "test-model";

    // $1.00 per million input tokens and $2.00 per million output tokens.
    private static readonly ModelPrice Price = new(Money.FromDollars(1m), Money.FromDollars(2m));

    private static readonly Tenant Tenant = new(
        TenantId.Parse("acme"),
        "Acme",
        new BudgetPolicy(Money.FromDollars(10m), TimeSpan.FromHours(1)));

    private readonly FakePriceCatalog _prices = new FakePriceCatalog().With(Model, Price);
    private readonly FakeEnforcementStore _store = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-03-04T10:15:00Z"));

    private BudgetGuard CreateGuard(long defaultMaxOutputTokens = 1_000) => new(
        _prices,
        _store,
        Options.Create(new BudgetGuardOptions { DefaultMaxOutputTokens = defaultMaxOutputTokens }),
        _time,
        NullLogger<BudgetGuard>.Instance);

    private static LlmRequestInfo Request(long? maxOutput = 500, int choices = 1, long promptCharacters = 3_000, string model = Model) =>
        new(model, maxOutput, choices, promptCharacters, IsStreaming: false);

    [Fact]
    public async Task Reserves_the_worst_case_cost_of_input_estimate_plus_output_cap()
    {
        // 3,000 characters estimate to 1,000 input tokens ($0.001) and 500 output tokens ($0.001).
        var decision = await CreateGuard().ReserveAsync(Tenant, Request(), FailureMode.FailOpenDegraded, CancellationToken.None);

        var granted = Assert.IsType<BudgetGranted>(decision);
        Assert.Equal(Money.FromDollars(0.002m), granted.Reservation.Reserved);
        Assert.Equal(Money.FromDollars(0.002m), Assert.Single(_store.Charges).Amount);
    }

    [Fact]
    public async Task Uses_the_default_output_cap_when_the_request_sets_none()
    {
        var decision = await CreateGuard(defaultMaxOutputTokens: 2_000)
            .ReserveAsync(Tenant, Request(maxOutput: null), FailureMode.FailOpenDegraded, CancellationToken.None);

        // 1,000 input tokens ($0.001) plus 2,000 default output tokens ($0.004).
        Assert.Equal(Money.FromDollars(0.005m), Assert.IsType<BudgetGranted>(decision).Reservation.Reserved);
    }

    [Fact]
    public async Task Multiplies_the_output_cap_by_the_number_of_choices()
    {
        var decision = await CreateGuard().ReserveAsync(Tenant, Request(maxOutput: 500, choices: 4), FailureMode.FailOpenDegraded, CancellationToken.None);

        // 1,000 input tokens ($0.001) plus 4 x 500 output tokens ($0.004).
        Assert.Equal(Money.FromDollars(0.005m), Assert.IsType<BudgetGranted>(decision).Reservation.Reserved);
    }

    [Fact]
    public async Task The_reservation_records_the_tenant_model_and_window()
    {
        var decision = await CreateGuard().ReserveAsync(Tenant, Request(), FailureMode.FailOpenDegraded, CancellationToken.None);

        var reservation = Assert.IsType<BudgetGranted>(decision).Reservation;
        Assert.Equal(Tenant.Id, reservation.TenantId);
        Assert.Equal(Model, reservation.Model);
        Assert.Equal(DateTimeOffset.Parse("2026-03-04T10:00:00Z"), reservation.WindowStart);
    }

    [Fact]
    public async Task Denies_an_unpriced_model_without_touching_the_store()
    {
        var decision = await CreateGuard().ReserveAsync(Tenant, Request(model: "mystery"), FailureMode.FailOpenDegraded, CancellationToken.None);

        var denied = Assert.IsType<BudgetDenied>(decision);
        Assert.Equal(DenialReason.UnknownModel, denied.Reason);
        Assert.Empty(_store.Charges);
    }

    [Fact]
    public async Task Denies_a_request_that_alone_exceeds_the_whole_budget_without_touching_the_store()
    {
        // 1,000 input tokens plus 10 million output tokens is about $20, above the $10 budget.
        var decision = await CreateGuard().ReserveAsync(Tenant, Request(maxOutput: 10_000_000), FailureMode.FailOpenDegraded, CancellationToken.None);

        var denied = Assert.IsType<BudgetDenied>(decision);
        Assert.Equal(DenialReason.RequestExceedsBudget, denied.Reason);
        Assert.Null(denied.RetryAfter);
        Assert.Empty(_store.Charges);
    }

    [Fact]
    public async Task Denies_when_the_store_has_no_room_and_says_when_to_retry_and_what_remains()
    {
        _store.Grant = false;
        _store.Spent = Money.FromDollars(9.995m);

        var decision = await CreateGuard().ReserveAsync(Tenant, Request(), FailureMode.FailOpenDegraded, CancellationToken.None);

        var denied = Assert.IsType<BudgetDenied>(decision);
        Assert.Equal(DenialReason.BudgetExceeded, denied.Reason);
        Assert.Equal(TimeSpan.FromMinutes(45), denied.RetryAfter);
        Assert.Equal(Money.FromDollars(0.005m), denied.Remaining);
    }

    [Fact]
    public async Task Settles_a_billed_call_at_the_real_cost()
    {
        var reservation = await ReserveAsync();

        // 100 input tokens ($0.0001) and 50 output tokens ($0.0001).
        var result = await CreateGuard().SettleAsync(
            reservation,
            CallOutcome.Billed(new TokenUsage(100, 50)),
            CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal(Money.FromDollars(0.0002m), result.Charged);
        Assert.Equal(Money.FromDollars(0.0002m), Assert.Single(_store.Settlements).Actual);
    }

    [Fact]
    public async Task Settles_a_call_that_was_not_billed_at_zero()
    {
        var reservation = await ReserveAsync();

        var result = await CreateGuard().SettleAsync(reservation, CallOutcome.NotBilled(), CancellationToken.None);

        Assert.Equal(Money.Zero, result.Charged);
    }

    [Fact]
    public async Task Settles_a_call_with_unknown_usage_at_the_reservation()
    {
        var reservation = await ReserveAsync();

        var result = await CreateGuard().SettleAsync(reservation, CallOutcome.BilledUnknownUsage(), CancellationToken.None);

        Assert.Equal(reservation.Reserved, result.Charged);
    }

    [Fact]
    public async Task Charges_the_reservation_when_the_price_disappears_before_settlement()
    {
        var reservation = await ReserveAsync();
        _prices.Remove(Model);

        var result = await CreateGuard().SettleAsync(
            reservation,
            CallOutcome.Billed(new TokenUsage(100, 50)),
            CancellationToken.None);

        Assert.Equal(reservation.Reserved, result.Charged);
    }

    [Fact]
    public async Task Reports_when_the_reservation_was_already_settled()
    {
        var reservation = await ReserveAsync();
        _store.SettleOutcome = SettleOutcome.ReservationNotFound;

        var result = await CreateGuard().SettleAsync(reservation, CallOutcome.NotBilled(), CancellationToken.None);

        Assert.False(result.Applied);
    }

    private async Task<Reservation> ReserveAsync()
    {
        var decision = await CreateGuard().ReserveAsync(Tenant, Request(), FailureMode.FailOpenDegraded, CancellationToken.None);
        return Assert.IsType<BudgetGranted>(decision).Reservation;
    }
}
