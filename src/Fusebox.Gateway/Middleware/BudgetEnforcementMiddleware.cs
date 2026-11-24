using Fusebox.Application.Budgeting;
using Fusebox.Application.Llm;
using Fusebox.Application.Telemetry;
using Fusebox.Domain;
using Fusebox.Gateway.Configuration;
using Fusebox.Gateway.Http;
using Fusebox.Gateway.Proxy;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

namespace Fusebox.Gateway.Middleware;

/// <summary>
/// Holds the worst-case cost of an LLM call against the tenant's budget before forwarding it,
/// and settles the real cost once the call finishes.
/// </summary>
/// <remarks>
/// When the budget cannot be verified at all, the call is rejected with 503. Whether the gateway first keeps
/// serving on a local limit is decided below this class, per route, by the enforcement store.
/// Streamed responses are settled from the usage event at the end of the stream. If the stream ends without one
/// (for example the caller disconnected), the call is charged at its reservation.
/// </remarks>
internal sealed partial class BudgetEnforcementMiddleware(
    RequestDelegate next,
    IBudgetGuard guard,
    LlmProtocolCatalog protocols,
    RepeatDetector repeats,
    IOptions<GatewayOptions> options,
    ILogger<BudgetEnforcementMiddleware> logger)
{
    private const int BufferThresholdBytes = 64 * 1024;

    private readonly ILogger<BudgetEnforcementMiddleware> _logger = logger;
    private readonly GatewayOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var route = context.GetRouteModel().Config;
        var telemetry = context.Telemetry();

        if (IsExplicitlyUnmetered(route))
        {
            await next(context);
            return;
        }

        if (!TryResolveProtocol(route, out var protocol) || !TryResolveFailureMode(route, out var failureMode))
        {
            telemetry.Failed();
            await ProblemResults.Misconfigured().ExecuteAsync(context);
            return;
        }

        var tenant = context.GetTenant();

        var body = await TryReadBodyAsync(context);
        if (body is null)
        {
            LogBodyTooLarge(tenant.Id);
            telemetry.Rejected();
            await ProblemResults.PayloadTooLarge(_options.MaxRequestBodyBytes).ExecuteAsync(context);
            return;
        }

        if (!protocol.TryParseRequest(body.Value, out var request))
        {
            LogInvalidBody(tenant.Id, protocol.Name);
            telemetry.Rejected();
            await ProblemResults.InvalidRequest($"The request body is not a valid '{protocol.Name}' request.").ExecuteAsync(context);
            return;
        }

        telemetry.Observe(request);

        if (repeats.Enabled && repeats.Observe(tenant.Id, route.RouteId, body.Value.Span, DeclaresRetry(context.Request.Headers, repeats.RetryHeaders)))
        {
            telemetry.Repeat();
        }

        var decision = await TryReserveAsync(context, tenant, request, failureMode);

        switch (decision)
        {
            case BudgetGranted granted:
                await ForwardAndSettleAsync(context, granted.Reservation, protocol, request, body.Value);
                break;

            case BudgetDenied denied:
                telemetry.Denied(denied.Reason);
                context.Response.Headers.RetryAfter = ProblemResults.RetryAfterSeconds(denied).ToString(System.Globalization.CultureInfo.InvariantCulture);
                await ProblemResults.Denied(denied).ExecuteAsync(context);
                break;

            default:
                telemetry.Unavailable();
                await ProblemResults.EnforcementUnavailable().ExecuteAsync(context);
                break;
        }
    }

    // A client that says it is retrying (a retry count above zero) is believed, whether or not the body matches an earlier call.
    private static bool DeclaresRetry(IHeaderDictionary headers, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            if (headers.TryGetValue(name, out var value) && int.TryParse(value.ToString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) && count > 0)
            {
                return true;
            }
        }

        return false;
    }

    private async Task ForwardAndSettleAsync(
        HttpContext context,
        Reservation reservation,
        ILlmProtocol protocol,
        LlmRequestInfo request,
        ReadOnlyMemory<byte> requestBody)
    {
        var originalBody = context.Response.Body;
        var outcome = CallOutcome.NotBilled();
        CapturingStream? capture = null;
        SseUsageStream? stream = null;

        if (request.IsStreaming)
        {
            if (!request.ClientRequestedStreamUsage)
            {
                AskVendorToReportStreamUsage(context, protocol, requestBody);
            }

            stream = new SseUsageStream(
                originalBody, protocol, hideUsageEvent: !request.ClientRequestedStreamUsage, _options.MaxStreamEventBytes);
            context.Response.Body = stream;
        }
        else
        {
            capture = new CapturingStream(originalBody, _options.MaxCapturedResponseBytes);
            context.Response.Body = capture;
        }

        try
        {
            await next(context);

            if (stream is not null)
            {
                await stream.CompleteAsync(context.RequestAborted);
            }

            outcome = DetermineOutcome(context, reservation, protocol, capture, stream);
        }
        catch
        {
            // The call may have been billed even though it did not complete, for example when the client
            // hung up mid-response. Once the response has started, assume it was; the reservation is the safe bound.
            outcome = context.Response.HasStarted ? CallOutcome.BilledUnknownUsage() : CallOutcome.NotBilled();
            throw;
        }
        finally
        {
            context.Response.Body = originalBody;
            var charged = await SettleAsync(reservation, outcome);
            context.Telemetry().Settled(reservation, outcome, charged);
        }
    }

    private CallOutcome DetermineOutcome(
        HttpContext context,
        Reservation reservation,
        ILlmProtocol protocol,
        CapturingStream? capture,
        SseUsageStream? stream)
    {
        var forwarderError = context.Features.Get<IForwarderErrorFeature>()?.Error ?? ForwarderError.None;

        if (forwarderError != ForwarderError.None)
        {
            return WasNeverSent(forwarderError) ? CallOutcome.NotBilled() : CallOutcome.BilledUnknownUsage();
        }

        // An error from the vendor (bad request, rate limit, outage) is not billed.
        if (context.Response.StatusCode is < 200 or >= 300)
        {
            return CallOutcome.NotBilled();
        }

        if (stream is not null)
        {
            if (stream.Usage is { } streamed)
            {
                return CallOutcome.Billed(streamed);
            }

            LogUsageUnreadable(reservation.Id, stream.Overflowed ? "a streamed event was too large to read" : "the stream had no usage event");
            return CallOutcome.BilledUnknownUsage();
        }

        if (capture is null)
        {
            return CallOutcome.BilledUnknownUsage();
        }

        if (capture.Truncated || context.Response.Headers.ContentEncoding.Count > 0)
        {
            LogUsageUnreadable(reservation.Id, "the response was too large or compressed");
            return CallOutcome.BilledUnknownUsage();
        }

        if (!protocol.TryExtractUsage(capture.Captured, out var usage))
        {
            LogUsageUnreadable(reservation.Id, "the response had no usage block");
            return CallOutcome.BilledUnknownUsage();
        }

        return CallOutcome.Billed(usage);
    }

    // Streamed responses report usage only if the vendor is asked to, so the forwarded body is changed to ask.
    private static void AskVendorToReportStreamUsage(HttpContext context, ILlmProtocol protocol, ReadOnlyMemory<byte> requestBody)
    {
        var rewritten = protocol.WithStreamUsage(requestBody);

        context.Request.Body = new MemoryStream(rewritten.ToArray());
        context.Request.ContentLength = rewritten.Length;
    }

    // Failures before the request was fully sent mean the vendor never started work.
    private static bool WasNeverSent(ForwarderError error) =>
        error is ForwarderError.Request or ForwarderError.RequestBodyClient or ForwarderError.RequestBodyCanceled;

    // Settlement is best-effort: it must never turn an already-served response into a failure. If it fails,
    // the reservation expires by TTL and stays counted against the budget, which is the safe direction.
#pragma warning disable CA1031 // Do not catch general exception types
    // Returns what was charged. If settling failed the hold stays counted, so that is what the call cost the budget.
    private async Task<Money> SettleAsync(Reservation reservation, CallOutcome outcome)
    {
        // Deliberately not tied to the client's connection: the vendor bills the call either way.
        using var timeout = new CancellationTokenSource(_options.SettlementTimeout);

        try
        {
            return (await guard.SettleAsync(reservation, outcome, timeout.Token)).Charged;
        }
        catch (Exception exception)
        {
            LogSettlementFailed(exception, reservation.TenantId, reservation.Id);
            return reservation.Reserved;
        }
    }

    private async Task<BudgetDecision?> TryReserveAsync(
        HttpContext context,
        Tenant tenant,
        LlmRequestInfo request,
        FailureMode failureMode)
    {
        try
        {
            return await guard.ReserveAsync(tenant, request, failureMode, context.RequestAborted);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogEnforcementUnavailable(exception, tenant.Id);
            return null;
        }
    }
#pragma warning restore CA1031

    private async Task<ReadOnlyMemory<byte>?> TryReadBodyAsync(HttpContext context)
    {
        var limit = _options.MaxRequestBodyBytes;

        if (context.Request.ContentLength > limit)
        {
            return null;
        }

        // Buffering lets the body be read here and then replayed to the upstream by YARP.
        context.Request.EnableBuffering(BufferThresholdBytes, limit);

        try
        {
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
            context.Request.Body.Position = 0;
            return buffer.ToArray();
        }
        catch (IOException)
        {
            // Raised when a body without a declared length grows past the buffer limit.
            return null;
        }
    }

    private bool TryResolveProtocol(Yarp.ReverseProxy.Configuration.RouteConfig route, out ILlmProtocol protocol)
    {
        protocol = null!;

        if (route.Metadata is null || !route.Metadata.TryGetValue(FuseboxMetadata.Protocol, out var name))
        {
            LogRouteNotMetered(route.RouteId);
            return false;
        }

        if (!protocols.TryGet(name, out var found))
        {
            LogUnknownProtocol(route.RouteId, name);
            return false;
        }

        protocol = found;
        return true;
    }

    // Absent means the default. A value that is present but not understood is a mistake, so the route is not served.
    private bool TryResolveFailureMode(Yarp.ReverseProxy.Configuration.RouteConfig route, out FailureMode failureMode)
    {
        failureMode = FailureMode.FailOpenDegraded;

        if (route.Metadata is null || !route.Metadata.TryGetValue(FuseboxMetadata.FailureMode, out var value))
        {
            return true;
        }

        if (Enum.TryParse(value, ignoreCase: true, out failureMode) && Enum.IsDefined(failureMode))
        {
            return true;
        }

        LogUnknownFailureMode(route.RouteId, value);
        return false;
    }

    private static bool IsExplicitlyUnmetered(Yarp.ReverseProxy.Configuration.RouteConfig route) =>
        route.Metadata is not null
        && route.Metadata.TryGetValue(FuseboxMetadata.Metering, out var metering)
        && string.Equals(metering, FuseboxMetadata.MeteringNone, StringComparison.OrdinalIgnoreCase);

    [LoggerMessage(EventId = 3101, Level = LogLevel.Error,
        Message = "Route {RouteId} has neither a '" + FuseboxMetadata.Protocol + "' nor a '" + FuseboxMetadata.Metering + "=" + FuseboxMetadata.MeteringNone + "' setting, so it was not forwarded")]
    private partial void LogRouteNotMetered(string routeId);

    [LoggerMessage(EventId = 3102, Level = LogLevel.Error,
        Message = "Route {RouteId} names the unknown protocol '{Protocol}', so it was not forwarded")]
    private partial void LogUnknownProtocol(string routeId, string protocol);

    [LoggerMessage(EventId = 3109, Level = LogLevel.Error,
        Message = "Route {RouteId} has the unknown failure mode '{FailureMode}', so it was not forwarded")]
    private partial void LogUnknownFailureMode(string routeId, string failureMode);

    [LoggerMessage(EventId = 3103, Level = LogLevel.Information,
        Message = "Rejected an oversized request body for tenant {TenantId}")]
    private partial void LogBodyTooLarge(TenantId tenantId);

    [LoggerMessage(EventId = 3104, Level = LogLevel.Information,
        Message = "Rejected an invalid '{Protocol}' request body for tenant {TenantId}")]
    private partial void LogInvalidBody(TenantId tenantId, string protocol);

    [LoggerMessage(EventId = 3105, Level = LogLevel.Error,
        Message = "The enforcement store failed, so the request for tenant {TenantId} was rejected")]
    private partial void LogEnforcementUnavailable(Exception exception, TenantId tenantId);

    [LoggerMessage(EventId = 3107, Level = LogLevel.Warning,
        Message = "Could not read usage for reservation {ReservationId} because {Reason}; charging the reservation")]
    private partial void LogUsageUnreadable(Guid reservationId, string reason);

    [LoggerMessage(EventId = 3108, Level = LogLevel.Error,
        Message = "Settlement failed for tenant {TenantId}, reservation {ReservationId}; the reservation stays counted until it expires")]
    private partial void LogSettlementFailed(Exception exception, TenantId tenantId, Guid reservationId);
}
