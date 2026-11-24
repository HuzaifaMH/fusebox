using Fusebox.Application.Alerting;

namespace Fusebox.Infrastructure.Alerting.Notifiers;

/// <summary>What an HTTP answer from a channel means for a notification, the same way for every channel.</summary>
internal static class NotifierHttp
{
    /// <summary>
    /// A success is delivered. A rate limit, a timeout or a server error is worth trying again, and the channel's own advice on
    /// when is passed on. Any other error means the channel will never take this, for example a wrong key or address.
    /// </summary>
    public static NotifyOutcome Interpret(HttpResponseMessage response, string channel)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.IsSuccessStatusCode)
        {
            return new Delivered();
        }

        // The body is left out of the reason, since a channel may echo what it was sent.
        var reason = $"{channel} answered {(int)response.StatusCode}";
        var status = (int)response.StatusCode;

        return status is 429 or 408 || status >= 500
            ? new RetryLater(reason, RetryAfter(response))
            : new Rejected(reason);
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;

        if (header?.Delta is { } delta)
        {
            return delta;
        }

        return header?.Date is { } date && date > DateTimeOffset.UtcNow ? date - DateTimeOffset.UtcNow : null;
    }
}
