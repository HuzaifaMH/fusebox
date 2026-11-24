using Fusebox.Application.Budgeting;
using Fusebox.Application.Llm;
using Fusebox.Application.Telemetry;
using Fusebox.Domain;

namespace Fusebox.Gateway.Http;

/// <summary>
/// What the middleware learn about one request as it passes through, collected so that a single record can be
/// written when the request ends. A request that nothing marks otherwise is a plain call passed through to the vendor.
/// </summary>
internal sealed class CallTelemetry
{
    private RequestDisposition _disposition = RequestDisposition.Forwarded;
    private string? _model;
    private bool _isStreaming;
    private bool _isRepeat;
    private DenialReason? _denialReason;
    private TokenUsage? _usage;
    private Money _reserved = Money.Zero;
    private Money _cost = Money.Zero;

    /// <summary>How the request ended so far. Still <see cref="RequestDisposition.Forwarded"/> unless Fusebox answered it itself.</summary>
    public RequestDisposition Disposition => _disposition;

    /// <summary>The call's model and whether it streams, once the request body has been read.</summary>
    public void Observe(LlmRequestInfo request)
    {
        ArgumentNullException.ThrowIfNull(request);

        _model = request.Model;
        _isStreaming = request.IsStreaming;
    }

    /// <summary>The request repeats one sent a moment before.</summary>
    public void Repeat() => _isRepeat = true;

    public void Rejected() => _disposition = RequestDisposition.Rejected;

    public void Denied(DenialReason reason)
    {
        _disposition = RequestDisposition.Denied;
        _denialReason = reason;
    }

    public void Unavailable() => _disposition = RequestDisposition.Unavailable;

    public void Failed() => _disposition = RequestDisposition.Failed;

    /// <summary>The call went to the vendor and its cost was settled.</summary>
    public void Settled(Reservation reservation, CallOutcome outcome, Money charged)
    {
        ArgumentNullException.ThrowIfNull(reservation);

        _reserved = reservation.Reserved;
        _cost = charged;
        _usage = outcome.Kind == CallOutcomeKind.Billed ? outcome.Usage : null;
    }

    public RequestRecord ToRecord(TenantId tenantId, string routeId, DateTimeOffset startedAt, TimeSpan duration, int statusCode) =>
        new(
            Guid.CreateVersion7(startedAt),
            startedAt,
            tenantId,
            routeId,
            _model,
            _disposition,
            _denialReason,
            _disposition == RequestDisposition.Failed ? StatusCodes.Status500InternalServerError : statusCode,
            duration,
            _isStreaming,
            _usage,
            _cost,
            _reserved,
            _isRepeat);
}

internal static class CallTelemetryExtensions
{
    /// <summary>The request's telemetry, created on first use.</summary>
    public static CallTelemetry Telemetry(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var telemetry = context.Features.Get<CallTelemetry>();
        if (telemetry is null)
        {
            telemetry = new CallTelemetry();
            context.Features.Set(telemetry);
        }

        return telemetry;
    }
}
