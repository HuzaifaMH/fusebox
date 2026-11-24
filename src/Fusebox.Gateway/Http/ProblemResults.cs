using Fusebox.Application.Budgeting;

namespace Fusebox.Gateway.Http;

/// <summary>
/// Every error Fusebox itself returns, as an RFC 9457 problem document with a stable "type"
/// that clients can switch on. Errors from the upstream API are passed through untouched.
/// </summary>
internal static class ProblemResults
{
    private const string TypePrefix = "urn:fusebox:problem:";

    public static IResult Unauthorized() => Results.Problem(
        statusCode: StatusCodes.Status401Unauthorized,
        title: "Authentication required",
        detail: "Send your Fusebox API key as 'Authorization: Bearer <key>'.",
        type: TypePrefix + "unauthorized");

    public static IResult InvalidRequest(string detail) => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: "Invalid request",
        detail: detail,
        type: TypePrefix + "invalid-request");

    public static IResult PayloadTooLarge(long limitBytes) => Results.Problem(
        statusCode: StatusCodes.Status413PayloadTooLarge,
        title: "Request body too large",
        detail: $"The request body may be at most {limitBytes} bytes.",
        type: TypePrefix + "payload-too-large");

    public static IResult EnforcementUnavailable() => Results.Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "Budget enforcement unavailable",
        detail: "Fusebox could not verify the budget for this request, so it was not forwarded. Retry shortly.",
        type: TypePrefix + "enforcement-unavailable");

    public static IResult UpstreamUnavailable() => Results.Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "Upstream API unavailable",
        detail: "The API this call goes to has been failing, so Fusebox did not forward it and held no budget for it. Retry shortly.",
        type: TypePrefix + "upstream-unavailable");

    public static IResult Misconfigured() => Results.Problem(
        statusCode: StatusCodes.Status500InternalServerError,
        title: "Route is not configured for metering",
        detail: "This route has no valid Fusebox metering configuration, so it was not forwarded.",
        type: TypePrefix + "route-misconfigured");

    public static IResult Denied(BudgetDenied denied)
    {
        ArgumentNullException.ThrowIfNull(denied);

        return denied.Reason switch
        {
            DenialReason.BudgetExceeded => Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Budget exceeded",
                detail: "This tenant has no budget left in the current window.",
                type: TypePrefix + "budget-exceeded",
                extensions: new Dictionary<string, object?>
                {
                    ["remainingUsd"] = denied.Remaining.ToDollars(),
                    ["retryAfterSeconds"] = RetryAfterSeconds(denied),
                }),

            DenialReason.RequestExceedsBudget => Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Request exceeds budget",
                detail: "This request could cost more than the tenant's entire budget window, so waiting will not help. Lower max_tokens or shorten the prompt.",
                type: TypePrefix + "request-exceeds-budget"),

            DenialReason.UnknownModel => Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Model is not priced",
                detail: "Fusebox has no price for this model, so its cost cannot be bounded.",
                type: TypePrefix + "unknown-model"),

            _ => throw new ArgumentOutOfRangeException(nameof(denied), denied.Reason, "Unhandled denial reason."),
        };
    }

    public static int RetryAfterSeconds(BudgetDenied denied) =>
        denied.RetryAfter is { } wait ? (int)Math.Ceiling(wait.TotalSeconds) : 0;
}
