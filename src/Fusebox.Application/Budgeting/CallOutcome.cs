using Fusebox.Domain;

namespace Fusebox.Application.Budgeting;

public enum CallOutcomeKind
{
    /// <summary>The call is known not to have been billed.</summary>
    NotBilled,

    /// <summary>The call was billed and the vendor reported its usage.</summary>
    Billed,

    /// <summary>The call was probably billed but its usage is unknown. Charged at the reservation, the safe bound.</summary>
    BilledUnknownUsage,
}

/// <summary>What happened to a forwarded call, as far as billing is concerned.</summary>
public readonly record struct CallOutcome
{
    private CallOutcome(CallOutcomeKind kind, TokenUsage usage)
    {
        Kind = kind;
        Usage = usage;
    }

    public CallOutcomeKind Kind { get; }

    public TokenUsage Usage { get; }

    public static CallOutcome NotBilled() => new(CallOutcomeKind.NotBilled, default);

    public static CallOutcome Billed(TokenUsage usage) => new(CallOutcomeKind.Billed, usage);

    public static CallOutcome BilledUnknownUsage() => new(CallOutcomeKind.BilledUnknownUsage, default);
}
