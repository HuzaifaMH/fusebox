namespace Fusebox.Application.Budgeting;

/// <summary>
/// The budget cannot be verified and the call is not allowed to proceed unverified.
/// </summary>
public sealed class EnforcementUnavailableException : Exception
{
    public EnforcementUnavailableException()
    {
    }

    public EnforcementUnavailableException(string message)
        : base(message)
    {
    }

    public EnforcementUnavailableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
