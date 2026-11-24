using Fusebox.Application.Llm;
using Fusebox.Application.Pricing;
using Fusebox.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fusebox.Application.Budgeting;

internal sealed partial class BudgetGuard(
    IPriceCatalog prices,
    IEnforcementStore store,
    IOptions<BudgetGuardOptions> options,
    TimeProvider time,
    ILogger<BudgetGuard> logger) : IBudgetGuard
{
    private readonly ILogger<BudgetGuard> _logger = logger;

    public async ValueTask<BudgetDecision> ReserveAsync(
        Tenant tenant,
        LlmRequestInfo request,
        FailureMode failureMode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(request);

        var now = time.GetUtcNow();

        var price = await prices.FindAsync(tenant.Id, request.Model, cancellationToken);
        if (price is null)
        {
            LogDenied(tenant.Id, DenialReason.UnknownModel, request.Model);
            return new BudgetDenied(DenialReason.UnknownModel, RetryAfter: null, Remaining: Money.Zero);
        }

        var worstCase = WorstCaseCost(price, request);

        if (worstCase > tenant.Budget.Limit)
        {
            LogDenied(tenant.Id, DenialReason.RequestExceedsBudget, request.Model);
            return new BudgetDenied(DenialReason.RequestExceedsBudget, RetryAfter: null, tenant.Budget.Limit);
        }

        var charge = new BudgetCharge(tenant.Id, Guid.NewGuid(), worstCase, tenant.Budget, now, failureMode);
        var result = await store.TryReserveAsync(charge, cancellationToken);

        if (!result.Granted)
        {
            LogDenied(tenant.Id, DenialReason.BudgetExceeded, request.Model);
            return new BudgetDenied(
                DenialReason.BudgetExceeded,
                result.Window.RemainingAt(now),
                tenant.Budget.Limit.SubtractOrZero(result.Spent));
        }

        var reservation = new Reservation(charge.ReservationId, tenant.Id, result.Window.Start, worstCase, request.Model);
        LogGranted(tenant.Id, reservation.Id, request.Model, worstCase.MicroDollars);

        return new BudgetGranted(reservation);
    }

    public async ValueTask<SettlementResult> SettleAsync(
        Reservation reservation,
        CallOutcome outcome,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation);

        var actual = await ActualCostAsync(reservation, outcome, cancellationToken);
        var settled = await store.SettleAsync(new SettleRequest(reservation, actual), cancellationToken);

        if (settled == SettleOutcome.ReservationNotFound)
        {
            LogSettlementSkipped(reservation.TenantId, reservation.Id);
            return new SettlementResult(actual, Applied: false);
        }

        LogSettled(reservation.TenantId, reservation.Id, outcome.Kind, reservation.Reserved.MicroDollars, actual.MicroDollars);
        return new SettlementResult(actual, Applied: true);
    }

    private Money WorstCaseCost(ModelPrice price, LlmRequestInfo request)
    {
        var outputPerChoice = request.MaxOutputTokens ?? options.Value.DefaultMaxOutputTokens;
        var usage = new TokenUsage(
            TokenEstimator.FromCharacters(request.PromptCharacters),
            checked(outputPerChoice * Math.Max(request.Choices, 1)));

        return price.CostOf(usage);
    }

    private async ValueTask<Money> ActualCostAsync(
        Reservation reservation,
        CallOutcome outcome,
        CancellationToken cancellationToken)
    {
        switch (outcome.Kind)
        {
            case CallOutcomeKind.NotBilled:
                return Money.Zero;

            case CallOutcomeKind.Billed:
                var price = await prices.FindAsync(reservation.TenantId, reservation.Model, cancellationToken);
                if (price is not null)
                {
                    return price.CostOf(outcome.Usage);
                }

                // The price vanished between reserve and settle. Charging the hold is the safe bound.
                LogPriceMissingAtSettlement(reservation.Model);
                return reservation.Reserved;

            default:
                return reservation.Reserved;
        }
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information,
        Message = "Request denied for tenant {TenantId}: {Reason} (model {Model})")]
    private partial void LogDenied(TenantId tenantId, DenialReason reason, string model);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Debug,
        Message = "Reserved {ReservedMicroDollars} micro-dollars for tenant {TenantId}, reservation {ReservationId}, model {Model}")]
    private partial void LogGranted(TenantId tenantId, Guid reservationId, string model, long reservedMicroDollars);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information,
        Message = "Settled reservation {ReservationId} for tenant {TenantId}: outcome {Outcome}, reserved {ReservedMicroDollars}, charged {ChargedMicroDollars} micro-dollars")]
    private partial void LogSettled(TenantId tenantId, Guid reservationId, CallOutcomeKind outcome, long reservedMicroDollars, long chargedMicroDollars);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Warning,
        Message = "Reservation {ReservationId} for tenant {TenantId} was not found at settlement; it was already settled or expired")]
    private partial void LogSettlementSkipped(TenantId tenantId, Guid reservationId);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Warning,
        Message = "No price for model {Model} at settlement; charging the reservation")]
    private partial void LogPriceMissingAtSettlement(string model);
}
