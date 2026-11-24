namespace Fusebox.Application.Budgeting;

public enum SettleOutcome
{
    /// <summary>The reservation was replaced by the real cost.</summary>
    Applied,

    /// <summary>The reservation was unknown, already settled, or had expired. Nothing changed.</summary>
    ReservationNotFound,
}
