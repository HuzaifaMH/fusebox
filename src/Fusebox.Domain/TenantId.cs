namespace Fusebox.Domain;

/// <summary>
/// Identifies the owner of a budget (a customer, team or app).
/// Restricted to letters, digits, '-' and '_' so it is always safe inside cache keys and log lines.
/// </summary>
public sealed record TenantId
{
    public const int MaxLength = 64;

    private TenantId(string value) => Value = value;

    public string Value { get; }

    /// <exception cref="ArgumentException">The value is empty, too long, or contains invalid characters.</exception>
    public static TenantId Parse(string value)
    {
        if (!TryParse(value, out var id))
        {
            throw new ArgumentException(
                $"A tenant id must be 1-{MaxLength} characters of letters, digits, '-' or '_'.",
                nameof(value));
        }

        return id;
    }

    public static bool TryParse(string? value, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TenantId? id)
    {
        id = null;

        if (string.IsNullOrEmpty(value) || value.Length > MaxLength || !value.All(IsAllowed))
        {
            return false;
        }

        id = new TenantId(value);
        return true;
    }

    public override string ToString() => Value;

    private static bool IsAllowed(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_';
}
