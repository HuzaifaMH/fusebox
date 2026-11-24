using System.Globalization;
using Fusebox.Application.Telemetry;
using Fusebox.Application.Upstream;
using Fusebox.Gateway.Http;
using Fusebox.Gateway.Observability;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

namespace Fusebox.Gateway.Middleware;

/// <summary>
/// Cuts a vendor off when it keeps failing, so callers get an immediate answer instead of each waiting for a timeout, and
/// so no budget is held for calls that cannot succeed. After a pause one call is let through to see whether it is back.
/// </summary>
/// <remarks>
/// Only the vendor's own failures count: a 5xx answer, or no answer at all. A refusal by Fusebox (over budget, bad request) and a
/// caller hanging up say nothing about the vendor, so they neither open nor close the circuit. Calls are never retried, because
/// a second attempt at a call the vendor already started would be billed twice.
/// </remarks>
internal sealed class UpstreamBreakerMiddleware(RequestDelegate next, UpstreamBreakers breakers, GatewayMetrics metrics)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!breakers.Enabled || context.GetRouteModel().Cluster?.ClusterId is not { } cluster)
        {
            await next(context);
            return;
        }

        if (!breakers.TryAcquire(cluster))
        {
            metrics.ShortCircuited(cluster);
            context.Telemetry().Unavailable();
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(breakers.OpenFor.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            await ProblemResults.UpstreamUnavailable().ExecuteAsync(context);
            return;
        }

        try
        {
            await next(context);
        }
        catch
        {
            // Something inside the gateway failed, which says nothing about the vendor.
            breakers.Abandon(cluster);
            throw;
        }

        Report(context, cluster);
    }

    private void Report(HttpContext context, string cluster)
    {
        var error = context.Features.Get<IForwarderErrorFeature>()?.Error ?? ForwarderError.None;

        if (error != ForwarderError.None)
        {
            if (CausedByVendor(error))
            {
                breakers.Failed(cluster, error.ToString());
            }
            else
            {
                breakers.Abandon(cluster);
            }

            return;
        }

        // If Fusebox answered itself, nothing was learned about the vendor.
        if (context.Telemetry().Disposition != RequestDisposition.Forwarded)
        {
            breakers.Abandon(cluster);
            return;
        }

        if (context.Response.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            breakers.Failed(cluster, string.Create(CultureInfo.InvariantCulture, $"HTTP {context.Response.StatusCode}"));
            return;
        }

        breakers.Succeeded(cluster);
    }

    // The vendor could not be reached, did not answer in time, or answered with something unreadable. Anything that
    // ends with "Client" or "Canceled" is the caller going away.
    private static bool CausedByVendor(ForwarderError error) =>
        error is ForwarderError.Request
            or ForwarderError.RequestTimedOut
            or ForwarderError.RequestBodyDestination
            or ForwarderError.ResponseHeaders
            or ForwarderError.ResponseBodyDestination;
}
