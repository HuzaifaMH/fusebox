namespace Fusebox.Application.Budgeting;

/// <summary>What a route does when the shared budget store cannot be reached.</summary>
public enum FailureMode
{
    /// <summary>
    /// Keep serving under a strict local limit for a bounded time, then stop. Apps stay up and exposure stays bounded.
    /// </summary>
    FailOpenDegraded,

    /// <summary>Reject calls whenever the budget cannot be verified. For routes where any unverified spend is too much.</summary>
    FailClosed,
}
