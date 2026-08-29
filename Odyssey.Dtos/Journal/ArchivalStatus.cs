namespace Odyssey.Dtos.Journal;

/// <summary>
/// List-filter status for the module's archive-only resources (journal entries, journal tags, task
/// tags), derived at query time from the entity's <c>Archived</c> column. A module-local copy so
/// Odyssey.Dtos.Journal does not reference Odyssey.Dtos.Finance.
/// </summary>
public enum ArchivalStatus
{
    Active,
    Archived,
}
