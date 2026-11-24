using System.Net.Http.Headers;
using Fusebox.Application.Tenancy;
using Fusebox.Gateway.Http;

namespace Fusebox.Gateway.Middleware;

/// <summary>
/// Identifies the calling tenant from "Authorization: Bearer &lt;Fusebox key&gt;".
/// Requests without a valid key never reach enforcement or the upstream API.
/// </summary>
internal sealed partial class TenantAuthenticationMiddleware(
    RequestDelegate next,
    ITenantAuthenticator authenticator,
    ILogger<TenantAuthenticationMiddleware> logger)
{
    private static readonly Func<ILogger, string, IDisposable?> TenantScope =
        LoggerMessage.DefineScope<string>("Tenant {TenantId}");

    private readonly ILogger<TenantAuthenticationMiddleware> _logger = logger;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var tenant = await authenticator.AuthenticateAsync(ReadBearerToken(context.Request), context.RequestAborted);

        if (tenant is null)
        {
            LogRejected(context.Request.Path);
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await ProblemResults.Unauthorized().ExecuteAsync(context);
            return;
        }

        context.SetTenant(tenant);

        // Every log line written while this request is processed carries the tenant.
        using (TenantScope(_logger, tenant.Id.Value))
        {
            await next(context);
        }
    }

    private static string? ReadBearerToken(HttpRequest request) =>
        AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out var header)
        && string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            ? header.Parameter
            : null;

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information,
        Message = "Rejected a request without a valid API key to {Path}")]
    private partial void LogRejected(PathString path);
}
