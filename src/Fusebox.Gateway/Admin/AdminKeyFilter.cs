using System.Security.Cryptography;
using System.Text;
using Fusebox.Application.Tenancy;
using Fusebox.Gateway.Http;
using Microsoft.Extensions.Options;

namespace Fusebox.Gateway.Admin;

/// <summary>Lets a request through only if it carries the admin key in <c>X-Admin-Key</c>.</summary>
internal sealed partial class AdminKeyFilter(IApiKeyHasher hasher, IOptions<AdminOptions> options, ILogger<AdminKeyFilter> logger) : IEndpointFilter
{
    public const string HeaderName = "X-Admin-Key";

    private readonly ILogger<AdminKeyFilter> _logger = logger;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;

        if (!http.Request.Headers.TryGetValue(HeaderName, out var supplied) || supplied.Count != 1 || string.IsNullOrEmpty(supplied[0]))
        {
            LogRejected(http.Connection.RemoteIpAddress?.ToString() ?? "unknown", "no key");
            return Unauthorized(http);
        }

        // Compared as hashes in constant time, so the comparison reveals nothing about the real key.
        var suppliedHash = Encoding.ASCII.GetBytes(hasher.Hash(supplied[0]!));
        var expectedHash = Encoding.ASCII.GetBytes(options.Value.ApiKeyHash);

        if (!CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash))
        {
            LogRejected(http.Connection.RemoteIpAddress?.ToString() ?? "unknown", "wrong key");
            return Unauthorized(http);
        }

        return await next(context);
    }

    private static IResult Unauthorized(HttpContext http)
    {
        http.Response.Headers.WWWAuthenticate = HeaderName;
        return ProblemResults.Unauthorized();
    }

    [LoggerMessage(EventId = 9201, Level = LogLevel.Warning, Message = "Refused an admin request from {Address}: {Reason}")]
    private partial void LogRejected(string address, string reason);
}
