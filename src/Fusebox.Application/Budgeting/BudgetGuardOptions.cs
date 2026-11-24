using System.ComponentModel.DataAnnotations;

namespace Fusebox.Application.Budgeting;

public sealed class BudgetGuardOptions
{
    /// <summary>
    /// The output-token cap assumed when a request sets none. It sizes the reservation,
    /// so a smaller value blocks fewer requests but trusts callers' real output to stay near it.
    /// </summary>
    [Range(1, 10_000_000)]
    public long DefaultMaxOutputTokens { get; set; } = 4_096;
}
