using Fusebox.Application.Telemetry;
using Fusebox.Gateway.Http;
using Fusebox.Gateway.Observability;
using Yarp.ReverseProxy.Model;

namespace Fusebox.Gateway.Middleware;

/// <summary>
/// Records every authenticated request when it ends, whatever became of it: forwarded, refused or failed.
/// </summary>
/// <remarks>
/// Handing the record over is a single non-blocking call, so this adds no measurable time to a request and a slow
/// or broken database can never slow a call down. Later middleware describe the request through <see cref="CallTelemetry"/>.
/// </remarks>
internal sealed class RequestTelemetryMiddleware(RequestDelegate next, ITelemetrySink sink, GatewayMetrics metrics, TimeProvider time)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var tenant = context.GetTenant();
        var telemetry = context.Telemetry();
        var startedAt = time.GetUtcNow();
        var started = time.GetTimestamp();

        try
        {
            await next(context);
        }
        catch
        {
            telemetry.Failed();
            throw;
        }
        finally
        {
            var record = telemetry.ToRecord(
                tenant.Id,
                context.GetRouteModel().Config.RouteId,
                startedAt,
                time.GetElapsedTime(started),
                context.Response.StatusCode);

            metrics.Record(record);
            sink.TryRecord(record);
        }
    }
}
