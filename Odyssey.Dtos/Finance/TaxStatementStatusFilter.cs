namespace Odyssey.Dtos.Finance;

/// <summary>
/// List-filter status for tax statements. Combines the stored <see cref="TaxStatementStatus"/> values
/// (New/Approved/Flagged, which apply only to non-archived statements) with the derived
/// <see cref="Archived"/> bucket (from the <c>Archived</c> column). Archived statements are hidden by
/// default and returned only when <see cref="Archived"/> is explicitly requested. The stored members
/// share <see cref="TaxStatementStatus"/>'s values so they map by value.
/// </summary>
public enum TaxStatementStatusFilter
{
    New = 0,
    Approved = 1,
    Flagged = 2,
    Archived = 3,
}
