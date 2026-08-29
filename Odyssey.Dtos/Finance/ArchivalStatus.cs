namespace Odyssey.Dtos.Finance;

/// <summary>
/// Shared list-filter status for resources whose only lifecycle distinction is whether they are
/// archived — budgets, contacts, currencies and transaction tags. Derived at query time from
/// the entity's <c>Archived</c> column (there is no stored status enum).
/// </summary>
public enum ArchivalStatus
{
    Active,
    Archived,
}
