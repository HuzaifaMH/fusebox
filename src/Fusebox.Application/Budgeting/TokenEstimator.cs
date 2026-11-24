namespace Fusebox.Application.Budgeting;

/// <summary>
/// A cheap, deliberately pessimistic estimate of input tokens, used only to size a reservation.
/// Billing always uses the vendor-reported usage, never this estimate.
/// </summary>
internal static class TokenEstimator
{
    // English averages about 4 characters per token. Three over-estimates, which is the safe direction.
    private const long CharactersPerToken = 3;

    public static long FromCharacters(long characters) =>
        (Math.Max(characters, 0) + CharactersPerToken - 1) / CharactersPerToken;
}
