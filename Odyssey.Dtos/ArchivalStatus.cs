namespace Odyssey.Dtos;

/// <summary>
/// Shared list-filter status for resources whose only lifecycle distinction is whether they are
/// archived — budgets, contacts, currencies, transaction tags, journal entries, journal tags, task
/// tags, photos, photo tags and albums. Derived at query time from the entity's <c>Archived</c>
/// column (there is no stored status enum).
/// </summary>
public enum ArchivalStatus
{
    Active,
    Archived,
}
